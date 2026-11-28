using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SosOnline.Game;
using SosOnline.Lockstep;

namespace SosOnline.Net;

/// <summary>
/// Host, Stage 3: everything a joiner needs to rebuild the current area by replay instead of making everyone reload.
///
/// On the frame a level load is released (L), every PC autosaves. The host keeps a copy of that save file, the state
/// that the save does not hold (party positions, game clock, slot → player mapping, which co-op players are
/// playing), and from L on every frame's inputs. A joiner loads the save, starts lockstep on frame L and replays the
/// inputs (spike B: identical to the live game, battles included, once the game clock is shared).
///
/// The log is valid until the next level load. Roster changes the inputs don't reproduce (a Stage 3 merge, a player
/// whose connection dropped) are logged with their frames in <see cref="Roster"/>, and the joiner applies them on
/// the same frames, and so are the characters the host gives back (<see cref="Characters"/>). A leave chosen in the (synced) pause menu is input, so the replay reproduces it by itself. A
/// Stage 2 drop-in reloads everyone, which starts a new log at its load release.
/// </summary>
internal sealed class AreaLog
{
    public bool Valid { get; private set; }
    public string InvalidReason { get; private set; } = "no area entry yet";

    public int L { get; private set; } = -1;
    public int Seq { get; private set; }
    public double ClockBase { get; private set; }
    public int SaveSlot { get; private set; } = -1;
    public PartySpawn Party { get; private set; }
    public int[] PlayerIndex { get; private set; }
    public bool[] Occupied { get; private set; }
    public List<int> Playing { get; private set; }

    /// <summary>The autosave and the save index as written on frame L (later autosaves overwrite the slot).</summary>
    public List<(string name, byte[] data)> SaveFiles { get; } = new();

    /// <summary>Roster changes on frames after L, in the order they were decided: (frame, slot, joins or leaves).</summary>
    public List<(int frame, int slot, bool join)> Roster { get; } = new();

    /// <summary>
    /// Characters the host gave back on frames after L (a guest that drops in again gets the one it played before):
    /// (frame, slot, character). Not input either, so a joiner applies them on their frames like roster changes.
    /// </summary>
    public List<(int frame, int slot, string character)> Characters { get; } = new();

    /// <summary>Frames after L on which a level-up started (its barrier's release): a replay starts it there too.</summary>
    public List<int> LevelUps { get; } = new();

    /// <summary>The host's state snapshots since L: a joiner checks its replay against them as it goes.</summary>
    public List<StateSnapshot> Snapshots { get; } = new();

    // Frame L + i's inputs, serialized: a channel mask, then each present channel's snapshot.
    private readonly List<byte[]> _frames = new();
    private bool _savePending;
    private DateTime _saveStartedUtc;

    public int LastFrame => L + _frames.Count - 1;
    public int FrameCount => _frames.Count;

    /// <summary>A level load was released on frame <paramref name="frame"/>: the old log is of no more use.</summary>
    public void OnLoadReleased(int seq, int frame, double clockBase)
    {
        Invalidate("level load");
        Seq = seq;
        L = frame;
        ClockBase = clockBase;
        _frames.Clear();
        SaveFiles.Clear();
        Snapshots.Clear();
        // The occupied slots recorded at L cover changes up to L; one decided earlier for a later frame still applies.
        Roster.RemoveAll(e => e.frame <= frame);
        Characters.RemoveAll(e => e.frame <= frame);
        LevelUps.RemoveAll(f => f <= frame);
    }

    public void LogLevelUp(int frame)
    {
        LevelUps.Add(frame);
        if (L >= 0) Plugin.Log.LogInfo($"[catchup] level-up start logged: f{frame}");
    }

    /// <summary>Host: slot <paramref name="slot"/> joins (or leaves) the session on <paramref name="frame"/>.</summary>
    public void LogRoster(int frame, int slot, bool join)
    {
        Roster.Add((frame, slot, join));
        if (L >= 0) Plugin.Log.LogInfo($"[catchup] roster change logged: P{slot + 1} {(join ? "joins" : "leaves")} at f{frame}");
    }

    /// <summary>Host: slot <paramref name="slot"/> gets <paramref name="character"/> back on <paramref name="frame"/>.</summary>
    public void LogCharacter(int frame, int slot, string character)
    {
        Characters.Add((frame, slot, character));
        if (L >= 0) Plugin.Log.LogInfo($"[catchup] character change logged: P{slot + 1} plays {character} from f{frame}");
    }

    public void WriteRoster(BinaryWriter w)
    {
        w.Write(Roster.Count);
        foreach (var (frame, slot, join) in Roster) { w.Write(frame); w.Write((byte)slot); w.Write(join); }
        w.Write(Characters.Count);
        foreach (var (frame, slot, character) in Characters) { w.Write(frame); w.Write((byte)slot); w.Write(character); }
        w.Write(LevelUps.Count);
        foreach (int frame in LevelUps) w.Write(frame);
    }

    public static (List<(int frame, int slot, bool join)> roster, List<(int frame, int slot, string character)> characters,
        List<int> levelUps) ReadRoster(BinaryReader r)
    {
        var list = new List<(int, int, bool)>();
        for (int n = r.ReadInt32(); n > 0; n--) list.Add((r.ReadInt32(), r.ReadByte(), r.ReadBoolean()));
        var chars = new List<(int, int, string)>();
        for (int n = r.ReadInt32(); n > 0; n--) chars.Add((r.ReadInt32(), r.ReadByte(), r.ReadString()));
        var levelUps = new List<int>();
        for (int n = r.ReadInt32(); n > 0; n--) levelUps.Add(r.ReadInt32());
        return (list, chars, levelUps);
    }

    /// <summary>A new lockstep run starts: its frames count from its own start, so nothing of the old run applies.</summary>
    public void Clear(string reason)
    {
        Invalidate(reason);
        L = -1;
        _frames.Clear();
        SaveFiles.Clear();
        Snapshots.Clear();
        Roster.Clear();
        Characters.Clear();
        LevelUps.Clear();
    }

    public void Invalidate(string reason)
    {
        if (Valid) Plugin.Log.LogInfo($"[catchup] area log from f{L} invalid: {reason}");
        Valid = false;
        InvalidReason = reason;
        _savePending = false;
    }

    /// <summary>
    /// Every PC, on frame L after the load's release: autosave. The host also records what the save doesn't hold.
    /// Whether saving is allowed here is the same on every PC (same game state).
    /// </summary>
    public static bool SaveAtEntry(int frame, bool isHost, AreaLog log, LockstepEngine engine)
    {
        var saves = GameBridge.Saves;
        bool canSave = saves != null && !GameBridge.LoadingLevel && GameBridge.Party?.Leader != null && !GameBridge.InBattle && saves.CanSave() && !saves.Saving;
        if (!canSave)
        {
            log?.Invalidate("saving not allowed at the area entry");
            return false;
        }
        var started = DateTime.UtcNow.AddSeconds(-1);
        int slot = GameBridge.AutoSave();
        if (!isHost || log == null) return slot >= 0;
        if (slot < 0) { log.Invalidate("area entry autosave failed"); return false; }
        log.SaveSlot = slot;
        log._saveStartedUtc = started;
        log._savePending = true;
        log.Party = GameBridge.CaptureParty();
        log.PlayerIndex = (int[])GameBridge.SlotPlayerIndex.Clone();
        log.Occupied = new bool[InputOverride.SlotCount];
        log.Playing = new List<int>();
        for (int s = 0; s < InputOverride.SlotCount; s++)
        {
            log.Occupied[s] = engine.InSessionAt(s, frame);
            if (s > 0 && GameBridge.IsPlaying(s)) log.Playing.Add(s);
        }
        Plugin.Log.LogInfo($"[catchup] f{frame} area entry: autosave slot {slot}, clock {log.ClockBase:R}, playing {string.Join(",", log.Playing.Select(s => $"P{s + 1}"))}, party {log.Party}");
        return true;
    }

    /// <summary>Host, every applied frame: log its inputs; once the entry autosave is written, keep a copy of it.</summary>
    public void OnFrame(int frame, InputSnapshot[] channels)
    {
        if (L < 0 || frame < L) return;
        if (_savePending && !GameBridge.Saving) KeepSave(frame);
        if (frame != L + _frames.Count) return; // not contiguous (shouldn't happen)
        if (_frames.Count >= MaxFrames) { Invalidate("too long since the area entry"); return; }
        _frames.Add(Serialize(channels));
    }

    /// <summary>A replay longer than this (frames, 5 minutes) would keep a joiner waiting too long: a slower PC replays
    /// only a few hundred frames a second, and 51,000 frames (2026-11-26) never finished. A reload in place is quicker.</summary>
    public const int MaxFrames = 60 * 300;

    private void KeepSave(int frame)
    {
        _savePending = false;
        SaveFiles.Clear();
        var dir = SaveMirror.OwnDir;
        string name = $"Autosave_SOS_SaveSlot{SaveSlot}.sos";
        var path = Path.Combine(dir, name);
        if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < _saveStartedUtc)
        {
            // Unknown naming: take the newest save written since the autosave started.
            var newest = new DirectoryInfo(dir).GetFiles("*.sos")
                .Where(f => f.LastWriteTimeUtc >= _saveStartedUtc && !f.Name.Contains("Options") && !f.Name.Contains("Index"))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) { Invalidate("area entry autosave not found on disk"); return; }
            path = newest.FullName;
            name = newest.Name;
        }
        SaveFiles.Add((name, File.ReadAllBytes(path)));
        var index = Path.Combine(dir, "SOS_SaveDataIndex.sos");
        if (File.Exists(index)) SaveFiles.Add(("SOS_SaveDataIndex.sos", File.ReadAllBytes(index)));
        Valid = true;
        InvalidReason = "";
        Plugin.Log.LogInfo($"[catchup] f{frame} kept {name} ({SaveFiles[0].data.Length} bytes) from the area entry at f{L}");
    }

    private static byte[] Serialize(InputSnapshot[] channels)
    {
        using var ms = new MemoryStream(16);
        using var w = new BinaryWriter(ms);
        byte mask = 0;
        for (int c = 0; c < channels.Length; c++) if (channels[c] != null) mask |= (byte)(1 << c);
        w.Write(mask);
        for (int c = 0; c < channels.Length; c++) channels[c]?.Write(w);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Writes frames [from, from + count) as one message body: first frame, count, then each frame.</summary>
    public void WriteFrames(BinaryWriter w, int from, int count)
    {
        w.Write(from);
        w.Write(count);
        for (int f = from; f < from + count; f++) w.Write(_frames[f - L]);
    }

    /// <summary>Joiner: reads a block written by <see cref="WriteFrames"/>, as (frame, channels).</summary>
    public static IEnumerable<(int frame, InputSnapshot[] channels)> ReadFrames(BinaryReader r)
    {
        int from = r.ReadInt32();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            byte mask = r.ReadByte();
            var ch = new InputSnapshot[InputOverride.ChannelCount];
            for (int c = 0; c < ch.Length; c++) if ((mask & (1 << c)) != 0) ch[c] = InputSnapshot.Read(r);
            yield return (from + i, ch);
        }
    }
}
