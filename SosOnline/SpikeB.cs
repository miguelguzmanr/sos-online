using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SosOnline.Game;
using SosOnline.Lockstep;

namespace SosOnline;

/// <summary>
/// Spike B harness: does loading the area-entry autosave reproduce the live state at area entry?
///   --sosonline-entryload=K        the K-th level load of the run is the area entry (default 1)
///   --sosonline-saveatentry        autosave on frame C (first frame saving is allowed after L) and copy the file to
///                                  BepInEx/SosOnline/spike-b-autosave.sos (its save-folder name goes to .name)
///   --sosonline-record=FILE        append each applied frame's slot 0 and system input to FILE
///   --sosonline-replay=FILE        feed FILE's inputs instead of local ones, aligned on L:
///   --sosonline-replayfrom=L_A     recorded frame L_A + k is fed on this run's frame L + k, and the RNG is reseeded
///                                  as on frame L_A + k (pass the recording run's --sosonline-seed too)
///   --sosonline-encounterat=N      start the nearest encounter on frame L + N
///   --sosonline-partyatentry=FILE  record (with --sosonline-saveatentry) or apply (with --sosonline-replay) the
///                                  party's exact positions on frame L
///   --sosonline-sandboxfile=F1,F2  copy files into the test sandbox (each named as in F.name) before the saves load
/// L is the frame the entry load ends on: the loading barrier's release frame, known a few frames in advance.
/// </summary>
internal static class SpikeB
{
    private static int _entryLoad = 1;
    private static int _L = -1, _C = -1;
    private static bool _saveAtEntry, _savePending;
    private static DateTime _saveStartedUtc;
    private static int _encounterAt = -1;
    private static BinaryWriter _record;
    private static Dictionary<int, (InputSnapshot local, InputSnapshot sys)> _replay;
    private static int _replayFrom = -1;
    private static int _replayed;

    public static int L => _L;
    public static bool Replaying => _replayBytes != null;
    private static byte[] _replayBytes;
    private static string _replayPath;

    /// <summary>Parsed on first use: snapshots are sized by the action table, which is built after startup.</summary>
    private static void ParseReplay()
    {
        if (_replay != null || !ActionTable.Ready) return;
        _replay = new Dictionary<int, (InputSnapshot, InputSnapshot)>();
        using var r = new BinaryReader(new MemoryStream(_replayBytes));
        int active = 0;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            int f = r.ReadInt32();
            var local = InputSnapshot.Read(r);
            var sys = InputSnapshot.Read(r);
            if (!local.IsIdle() || !sys.IsIdle()) active++;
            _replay[f] = (local, sys);
        }
        Plugin.Log.LogInfo($"[spikeb] replaying {_replay.Count} frames ({active} with input) from {_replayPath}, recorded frame {_replayFrom} = this run's L");
    }

    public static void Init()
    {
        if (TestHarness.TryArg("entryload", out var k)) _entryLoad = int.Parse(k);
        _saveAtEntry = TestHarness.TryArg("saveatentry", out _);
        if (TestHarness.TryArg("encounterat", out var e)) _encounterAt = int.Parse(e);
        if (TestHarness.TryArg("record", out var rec))
        {
            _record = new BinaryWriter(File.Create(rec));
            Plugin.Log.LogInfo($"[spikeb] recording inputs to {rec}");
        }
        if (TestHarness.TryArg("replay", out var rep))
        {
            _replayFrom = TestHarness.TryArg("replayfrom", out var rf) ? int.Parse(rf) : 0;
            _replayBytes = File.ReadAllBytes(rep);
            _replayPath = rep;
        }
        // The recording run writes its game clock at the entry load's release; the replay starts its clock there too.
        var clockFile = Path.Combine(TestHarness.OutDir, "spike-b-clock.txt");
        Net.Session.ReleaseClock = (seq, timeBase) =>
        {
            if (seq != _entryLoad) return timeBase;
            if (_record != null) File.WriteAllText(clockFile, timeBase.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            if (Replaying && File.Exists(clockFile))
            {
                timeBase = double.Parse(File.ReadAllText(clockFile), System.Globalization.CultureInfo.InvariantCulture);
                Plugin.Log.LogInfo($"[spikeb] clock at L = {timeBase:R}, as recorded");
            }
            return timeBase;
        };
        Net.Session.LoadReleaseScheduled += (seq, frame) =>
        {
            if (seq != _entryLoad) return;
            _L = frame;
            Plugin.Log.LogInfo($"[spikeb] entry load {seq} ends on f{frame} (L)");
            if (Replaying)
            {
                // Same RNG stream as the recorded run from L on (needs the same --sosonline-seed).
                Net.Session.Instance.Engine.RngFrameOffset = _replayFrom - frame;
                Plugin.Log.LogInfo($"[spikeb] RNG frame offset {_replayFrom - frame}");
            }
        };
    }

    /// <summary>--sosonline-sandboxfile: puts a save file into the sandbox before the game reads it.</summary>
    public static void CopyIntoSandbox()
    {
        if (!TestHarness.TryArg("sandboxfile", out var list)) return;
        foreach (var src in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = File.Exists(src + ".name") ? File.ReadAllText(src + ".name").Trim() : Path.GetFileName(src);
            File.Copy(src, Path.Combine(SaveMirror.OwnDir, name), true);
            Plugin.Log.LogInfo($"[spikeb] copied {src} into the sandbox as {name}");
        }
    }

    /// <summary>Replay: recorded input for target frame <paramref name="frame"/> (neutral before L).</summary>
    public static InputSnapshot Filter(int frame, int channel, InputSnapshot snap)
    {
        if (channel != 0 && channel != InputOverride.SystemChannel) return snap;
        ParseReplay();
        if (_replay == null || _L < 0 || frame < _L) return new InputSnapshot();
        if (!_replay.TryGetValue(_replayFrom + frame - _L, out var rec)) return new InputSnapshot();
        if (channel == 0) _replayed++;
        return channel == 0 ? rec.local : rec.sys;
    }

    /// <summary>After the inputs of <paramref name="frame"/> were applied (every frame).</summary>
    public static void OnFrame(int frame)
    {
        if (_record != null)
        {
            _record.Write(frame);
            (InputOverride.Current[0] ?? InputSnapshot.Empty).Write(_record);
            (InputOverride.Current[InputOverride.SystemChannel] ?? InputSnapshot.Empty).Write(_record);
            if (frame % 300 == 0) _record.Flush();
        }
        if (_L < 0 || frame < _L) return;
        if (frame == _L)
        {
            Plugin.Log.LogInfo($"[spikeb] L=f{frame} loading={GameBridge.LoadingLevel} level={GameBridge.CurrentLevelId}");
            if (TestHarness.TryArg("partyatentry", out var partyFile)) PartyAtEntry(partyFile);
        }
        if (_C < 0 && GameBridge.CanSaveNow)
        {
            _C = frame;
            Plugin.Log.LogInfo($"[spikeb] C=f{frame} (L+{frame - _L})");
            if (_saveAtEntry)
            {
                _saveStartedUtc = DateTime.UtcNow.AddSeconds(-1);
                int slot = GameBridge.AutoSave();
                _savePending = slot >= 0;
                Plugin.Log.LogInfo($"[spikeb] autosave at C -> slot {slot}");
            }
        }
        if (_savePending && !GameBridge.Saving) CopyAutosave(frame);
        if (_encounterAt >= 0 && frame == _L + _encounterAt)
        {
            Plugin.Log.LogInfo($"[spikeb] starting encounter at f{frame} (L+{_encounterAt})");
            TestHarness.StartNearestEncounter();
        }
        if (Replaying && frame % 600 == 0) Plugin.Log.LogInfo($"[spikeb] f{frame}: replayed {_replayed} recorded frames");
    }

    /// <summary>Writes (recording run) or applies (replay) the exact party positions at L.</summary>
    private static void PartyAtEntry(string path)
    {
        var list = GameBridge.Party?.LeaderAndFollowers;
        if (list == null) return;
        if (!Replaying)
        {
            var lines = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                var p = c.transform.position;
                lines.Add($"{c.gameObject.name}|{BitConverter.SingleToInt32Bits(p.x)}|{BitConverter.SingleToInt32Bits(p.y)}|{BitConverter.SingleToInt32Bits(p.z)}");
            }
            File.WriteAllLines(path, lines);
            Plugin.Log.LogInfo($"[spikeb] wrote {lines.Count} party positions at L to {path}");
            return;
        }
        if (!File.Exists(path)) { Plugin.Log.LogError($"[spikeb] {path} missing"); return; }
        var moved = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Split('|');
            var pos = new UnityEngine.Vector3(BitConverter.Int32BitsToSingle(int.Parse(t[1])), BitConverter.Int32BitsToSingle(int.Parse(t[2])), BitConverter.Int32BitsToSingle(int.Parse(t[3])));
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null || c.gameObject.name != t[0]) continue;
                var d = c.transform.position - pos;
                moved.Add($"{t[0]} {d.x:G3},{d.y:G3},{d.z:G3}");
                c.transform.position = pos;
            }
        }
        Plugin.Log.LogInfo($"[spikeb] applied party positions at L, corrections: {string.Join("; ", moved)}");
    }

    private static void CopyAutosave(int frame)
    {
        _savePending = false;
        var newest = new DirectoryInfo(SaveMirror.OwnDir).GetFiles("*.sos")
            .Where(f => f.LastWriteTimeUtc >= _saveStartedUtc && !f.Name.Contains("Options") && !f.Name.Contains("Index"))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest == null) { Plugin.Log.LogError($"[spikeb] f{frame}: no autosave file written since C"); return; }
        string dst = Path.Combine(TestHarness.OutDir, "spike-b-autosave.sos");
        File.Copy(newest.FullName, dst, true);
        File.WriteAllText(dst + ".name", newest.Name);
        // The save index lists which slots and autosaves exist; without it the copy is invisible to the game.
        var index = Path.Combine(SaveMirror.OwnDir, "SOS_SaveDataIndex.sos");
        if (File.Exists(index))
        {
            string idst = Path.Combine(TestHarness.OutDir, "spike-b-index.sos");
            File.Copy(index, idst, true);
            File.WriteAllText(idst + ".name", "SOS_SaveDataIndex.sos");
        }
        Plugin.Log.LogInfo($"[spikeb] f{frame}: saved {newest.Name} ({newest.Length} bytes) to {dst}");
    }

    public static void Close() => _record?.Flush();
}
