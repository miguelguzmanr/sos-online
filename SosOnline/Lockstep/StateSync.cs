using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SosOnline.Game;
using UnityEngine;

namespace SosOnline.Lockstep;

/// <summary>
/// A description of the simulation at one lockstep frame, split into named components (level, party, stats, gold,
/// inventory, combat, NPCs) so a mismatch says what diverged. Party positions are also kept as numbers, for the
/// guests' in-place drift correction.
/// </summary>
internal sealed class StateSnapshot
{
    public int Frame;
    /// <summary>Captured while a level was loading: not compared (scene loads and unloads finish at different times).</summary>
    public bool Loading;
    /// <summary>(component name, value). Same order on every peer.</summary>
    public readonly List<(string key, string value)> Components = new();
    /// <summary>Per-component hashes. A snapshot read from the network has only these, not the values.</summary>
    public readonly List<(string key, uint hash)> Hashes = new();
    public readonly List<(string name, Vector3 pos)> Characters = new();
    /// <summary>Facing of each entry of <see cref="Characters"/> (-1 unknown). Corrected in place like positions, not hashed.</summary>
    public readonly List<int> Angles = new();

    public static double CaptureMsTotal { get; private set; }
    public static int CaptureCount { get; private set; }
    public static double CaptureMsMax { get; private set; }

    public static StateSnapshot CaptureLocal(int frame)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = new StateSnapshot { Frame = frame, Loading = GameBridge.LoadingLevel };
        s.Add("lvl", () => GameBridge.CurrentLevelId);
        s.Add("scenes", () => GameBridge.ActiveSceneName);
        s.Add("party", s.CaptureParty);
        s.Add("stats", CaptureStats);
        s.Add("gold", CaptureCurrency);
        s.Add("inv", CaptureInventory);
        s.Add("combat", CaptureCombat);
        s.Add("npcs", CaptureNpcs);
        foreach (var (k, v) in s.Components) s.Hashes.Add((k, Fnv(v)));
        double ms = sw.Elapsed.TotalMilliseconds;
        CaptureMsTotal += ms;
        CaptureCount++;
        if (ms > CaptureMsMax) CaptureMsMax = ms;
        return s;
    }

    private void Add(string key, Func<string> capture)
    {
        string v;
        try { v = capture() ?? ""; }
        catch (Exception e) { v = "!" + e.GetType().Name; }
        Components.Add((key, v));
    }

    private static string P(float f) => f.ToString("F2", CultureInfo.InvariantCulture);
    private static string P1(float f) => f.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// Leader and followers, by name. Positions and facing go to <see cref="Characters"/>/<see cref="Angles"/>: the
    /// guest corrects those in place. Facing used to be part of this hash, and a one-step facing difference after a
    /// drop-in forced a full re-sync again and again (2026-11-15).
    /// </summary>
    private string CaptureParty()
    {
        var list = GameBridge.Party?.LeaderAndFollowers;
        if (list == null) return "";
        var parts = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == null) continue;
            int angle = -1;
            try { angle = c.lookDirectionController?.CurrentLookAngle ?? -1; } catch { }
            Characters.Add((c.gameObject.name, c.transform.position));
            Angles.Add(angle);
            parts.Add(c.gameObject.name);
        }
        return string.Join(" ", parts);
    }

    /// <summary>Current party: HP and MP of each member.</summary>
    private static string CaptureStats()
    {
        var ids = GameBridge.Party?.CurrentParty;
        var stats = GameBridge.Stats;
        if (ids == null || stats == null) return "";
        var parts = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var data = stats.GetCharacterData(id);
            if (data == null) continue;
            parts.Add($"{id.characterId}:{data.currentHP}/{data.currentSP}");
        }
        return string.Join(",", parts);
    }

    private static string CaptureCurrency()
    {
        var dict = GameBridge.Currency?.currencyQuantities;
        if (dict == null) return "";
        var values = new List<int>();
        foreach (var kv in dict) values.Add(kv.Value);
        values.Sort();
        return string.Join(",", values);
    }

    /// <summary>Kinds of items owned / total quantity.</summary>
    private static string CaptureInventory()
    {
        var dict = GameBridge.Inventory?.ownedInventoryItems?.dictionary;
        if (dict == null) return "";
        int kinds = 0;
        long total = 0;
        foreach (var kv in dict) { kinds++; total += kv.Value; }
        return $"{kinds}/{total}";
    }

    /// <summary>Whether an encounter runs, and each combat actor's HP.</summary>
    private static string CaptureCombat()
    {
        var cm = GameBridge.Combat;
        if (cm == null || !cm.IsAnEncounterRunning()) return "off";
        var actors = cm.CurrentEncounter?.AllActors;
        if (actors == null) return "on";
        var parts = new List<string>();
        for (int i = 0; i < actors.Count; i++)
        {
            var a = actors[i];
            if (a == null) continue;
            int hp = -1;
            try { hp = a.CombatTarget?.GetCurrentHP() ?? -1; } catch { }
            parts.Add($"{a.gameObject.name}:{hp}");
        }
        return "on " + string.Join(",", parts);
    }

    /// <summary>Every active NPC or enemy in the level: name and position, sorted.</summary>
    private static string CaptureNpcs()
    {
        var all = UnityEngine.Object.FindObjectsOfType<NPCController>();
        var parts = new List<string>();
        foreach (var n in all)
        {
            if (n == null || !n.isActiveAndEnabled) continue;
            var p = n.transform.position;
            parts.Add($"{n.gameObject.name}@({P1(p.x)},{P1(p.y)},{P1(p.z)})");
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(" ", parts);
    }

    public int AngleAt(int i) => i < Angles.Count ? Angles[i] : -1;

    public string Get(string key)
    {
        foreach (var (k, v) in Components) if (k == key) return v;
        return null;
    }

    public uint? HashOf(string key)
    {
        foreach (var (k, h) in Hashes) if (k == key) return h;
        return null;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Frame);
        w.Write(Loading);
        w.Write((byte)Hashes.Count);
        foreach (var (k, h) in Hashes) { w.Write(k); w.Write(h); }
        w.Write((byte)Characters.Count);
        for (int i = 0; i < Characters.Count; i++)
        {
            var (name, pos) = Characters[i];
            w.Write(name);
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
            w.Write((short)AngleAt(i));
        }
    }

    public static StateSnapshot Read(BinaryReader r)
    {
        var s = new StateSnapshot { Frame = r.ReadInt32(), Loading = r.ReadBoolean() };
        int c = r.ReadByte();
        for (int i = 0; i < c; i++) s.Hashes.Add((r.ReadString(), r.ReadUInt32()));
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            s.Characters.Add((r.ReadString(), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())));
            s.Angles.Add(r.ReadInt16());
        }
        return s;
    }

    public uint Hash()
    {
        uint h = 2166136261;
        foreach (var (_, ch) in Hashes) h = (h ^ ch) * 16777619;
        foreach (var (name, pos) in Characters)
        {
            h = (h ^ Fnv(name)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.x * 100)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.y * 100)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.z * 100)) * 16777619;
        }
        return h;
    }

    /// <summary>One <c>key=[value]</c> group per component, so a diff shows which one differs.</summary>
    public override string ToString()
    {
        var parts = new List<string> { Loading ? $"f{Frame} loading" : $"f{Frame}" };
        foreach (var (k, v) in Components) parts.Add($"{k}=[{v}]");
        var pos = new List<string>();
        for (int i = 0; i < Characters.Count; i++)
        {
            var (name, p) = Characters[i];
            pos.Add($"{name}@({P(p.x)},{P(p.y)},{P(p.z)})/{AngleAt(i)}");
        }
        parts.Add($"pos=[{string.Join(" ", pos)}]");
        return string.Join(" ", parts);
    }

    public static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s ?? "") h = (h ^ c) * 16777619;
        return h;
    }
}

/// <summary>
/// "Host fixes": the host periodically sends its state; guests compare it to their own state at that same frame.
/// Small positional drift (floating point, physics) is corrected in place by applying the difference.
/// Any other difference is reported as a desync that names the components which differ.
/// </summary>
internal sealed class StateSync
{
    /// <summary>Snapshot interval in frames (--sosonline-stateevery).</summary>
    public static int Interval = 30;

    private readonly Dictionary<int, StateSnapshot> _localHistory = new();
    private readonly Dictionary<int, StateSnapshot> _pendingHost = new();

    public int Corrections { get; private set; }
    public int Checks { get; private set; }
    public int SkippedLoading { get; private set; }
    public int DesyncStreak { get; private set; }
    public bool Desynced => DesyncStreak >= 2;
    public string LastDesyncReason { get; private set; } = "";

    public void Reset()
    {
        _localHistory.Clear();
        _pendingHost.Clear();
        Corrections = Checks = DesyncStreak = SkippedLoading = 0;
        LastDesyncReason = "";
    }

    /// <summary>Called by every peer at the end of input application for <paramref name="frame"/>.</summary>
    public StateSnapshot OnFrame(int frame)
    {
        if (frame % Interval != 0) return null;
        var local = StateSnapshot.CaptureLocal(frame);
        _localHistory[frame] = local;
        _localHistory.Remove(frame - Math.Max(Interval * 20, 600));
        if (_pendingHost.Remove(frame, out var host)) Compare(host, local);
        return local;
    }

    /// <summary>Guest side: a host snapshot arrived.</summary>
    public void OnHostSnapshot(StateSnapshot host)
    {
        if (_localHistory.TryGetValue(host.Frame, out var local)) Compare(host, local);
        else _pendingHost[host.Frame] = host;
    }

    private void Compare(StateSnapshot host, StateSnapshot local)
    {
        if (host.Loading || local.Loading) { SkippedLoading++; return; }
        Checks++;
        var reasons = new List<string>();
        foreach (var (key, hostHash) in host.Hashes)
        {
            var localHash = local.HashOf(key);
            if (localHash == null) reasons.Add($"{key} missing locally");
            else if (localHash != hostHash) reasons.Add($"{key} differs (local [{Trim(local.Get(key))}])");
        }
        bool sameLevel = host.HashOf("lvl") == local.HashOf("lvl") && host.HashOf("scenes") == local.HashOf("scenes");

        // Positional corrections, applied as a delta so movement since that frame is preserved. Facing is set to the
        // host's only if this character hasn't turned since that frame (a later turn came from synced input).
        int corrected = 0, turned = 0;
        if (sameLevel)
        {
            var party = GameBridge.Party?.LeaderAndFollowers;
            for (int hi = 0; hi < host.Characters.Count; hi++)
            {
                var (name, hostPos) = host.Characters[hi];
                int li = local.Characters.FindIndex(c => c.name == name);
                if (li < 0) { reasons.Add($"character {name} missing locally"); continue; }
                var delta = hostPos - local.Characters[li].pos;
                bool move = delta.sqrMagnitude >= 0.0004f; // >= 2 cm
                if (delta.sqrMagnitude > 25f) { reasons.Add($"{name} off by {delta.magnitude:F1}m"); }
                int hostAngle = host.AngleAt(hi), localAngle = local.AngleAt(li);
                bool turn = hostAngle >= 0 && localAngle >= 0 && hostAngle != localAngle;
                if ((!move && !turn) || party == null) continue;
                for (int i = 0; i < party.Count; i++)
                {
                    var c = party[i];
                    if (c == null || c.gameObject.name != name) continue;
                    if (move) { c.transform.position += delta; corrected++; }
                    if (turn)
                    {
                        try
                        {
                            var look = c.lookDirectionController;
                            if (look != null && look.CurrentLookAngle == localAngle) { look.SetAngle(hostAngle, true, true); turned++; }
                        }
                        catch { }
                    }
                }
            }
        }
        if (corrected > 0 || turned > 0)
        {
            Corrections += corrected + turned;
            Plugin.Log.LogInfo($"[sync] frame {host.Frame}: corrected {corrected} character position(s), {turned} facing(s)");
        }

        if (reasons.Count > 0)
        {
            DesyncStreak++;
            LastDesyncReason = string.Join("; ", reasons);
            Plugin.Log.LogWarning($"[sync] frame {host.Frame} mismatch #{DesyncStreak}: {LastDesyncReason}\n  local: {local}");
        }
        else DesyncStreak = 0;
    }

    private static string Trim(string v) => v == null ? "" : v.Length > 160 ? v[..160] + "…" : v;
}
