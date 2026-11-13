using System;
using System.Collections.Generic;
using System.IO;
using SosOnline.Game;
using UnityEngine;

namespace SosOnline.Lockstep;

/// <summary>A compact description of the simulation at one lockstep frame.</summary>
internal sealed class StateSnapshot
{
    public int Frame;
    public uint SceneHash;
    public string Scenes = "";
    public float RngProbe;
    public readonly List<(string name, Vector3 pos)> Characters = new();

    public static StateSnapshot CaptureLocal(int frame)
    {
        var s = new StateSnapshot { Frame = frame };
        s.Scenes = GameBridge.ActiveSceneName;
        s.SceneHash = Fnv(s.Scenes);

        // Probe the RNG without consuming it.
        var st = UnityEngine.Random.state;
        s.RngProbe = UnityEngine.Random.value;
        UnityEngine.Random.state = st;

        try
        {
            var party = GameBridge.Party;
            var list = party?.LeaderAndFollowers;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null) continue;
                    s.Characters.Add((c.gameObject.name, c.transform.position));
                }
        }
        catch { }
        return s;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Frame);
        w.Write(SceneHash);
        w.Write(Scenes);
        w.Write(RngProbe);
        w.Write((byte)Characters.Count);
        foreach (var (name, pos) in Characters)
        {
            w.Write(name);
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
        }
    }

    public static StateSnapshot Read(BinaryReader r)
    {
        var s = new StateSnapshot
        {
            Frame = r.ReadInt32(),
            SceneHash = r.ReadUInt32(),
            Scenes = r.ReadString(),
            RngProbe = r.ReadSingle(),
        };
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
            s.Characters.Add((r.ReadString(), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())));
        return s;
    }

    public uint Hash()
    {
        uint h = SceneHash;
        h = (h ^ BitConverter.SingleToUInt32Bits(RngProbe)) * 16777619;
        foreach (var (name, pos) in Characters)
        {
            h = (h ^ Fnv(name)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.x * 100)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.y * 100)) * 16777619;
            h = (h ^ (uint)Mathf.RoundToInt(pos.z * 100)) * 16777619;
        }
        return h;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        foreach (var (name, pos) in Characters) parts.Add($"{name}@({pos.x:F2},{pos.y:F2},{pos.z:F2})");
        return $"f{Frame} scenes=[{Scenes}] rng={RngProbe:F6} {string.Join(" ", parts)}";
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
/// Divergence that can't be patched (different scene, RNG stream) is reported as a desync.
/// </summary>
internal sealed class StateSync
{
    public const int Interval = 30;

    private readonly Dictionary<int, StateSnapshot> _localHistory = new();
    private readonly Dictionary<int, StateSnapshot> _pendingHost = new();

    public int Corrections { get; private set; }
    public int Checks { get; private set; }
    public int DesyncStreak { get; private set; }
    public bool Desynced => DesyncStreak >= 2;
    public string LastDesyncReason { get; private set; } = "";

    public void Reset()
    {
        _localHistory.Clear();
        _pendingHost.Clear();
        Corrections = Checks = DesyncStreak = 0;
        LastDesyncReason = "";
    }

    /// <summary>Called by every peer at the end of input application for <paramref name="frame"/>.</summary>
    public StateSnapshot OnFrame(int frame)
    {
        if (frame % Interval != 0) return null;
        var local = StateSnapshot.CaptureLocal(frame);
        _localHistory[frame] = local;
        _localHistory.Remove(frame - Interval * 20);
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
        Checks++;
        var reasons = new List<string>();
        if (host.SceneHash != local.SceneHash) reasons.Add($"scenes host=[{host.Scenes}] local=[{local.Scenes}]");
        if (Math.Abs(host.RngProbe - local.RngProbe) > 1e-7f) reasons.Add("rng stream diverged");

        // Positional corrections, applied as a delta so movement since that frame is preserved.
        int corrected = 0;
        if (host.SceneHash == local.SceneHash)
        {
            var party = GameBridge.Party?.LeaderAndFollowers;
            foreach (var (name, hostPos) in host.Characters)
            {
                int li = local.Characters.FindIndex(c => c.name == name);
                if (li < 0) { reasons.Add($"character {name} missing locally"); continue; }
                var delta = hostPos - local.Characters[li].pos;
                if (delta.sqrMagnitude < 0.0004f) continue; // < 2 cm
                if (delta.sqrMagnitude > 25f) { reasons.Add($"{name} off by {delta.magnitude:F1}m"); }
                if (party == null) continue;
                for (int i = 0; i < party.Count; i++)
                {
                    var c = party[i];
                    if (c == null || c.gameObject.name != name) continue;
                    c.transform.position += delta;
                    corrected++;
                }
            }
        }
        if (corrected > 0)
        {
            Corrections += corrected;
            Plugin.Log.LogInfo($"[sync] frame {host.Frame}: corrected {corrected} character position(s)");
        }

        if (reasons.Count > 0)
        {
            DesyncStreak++;
            LastDesyncReason = string.Join("; ", reasons);
            Plugin.Log.LogWarning($"[sync] frame {host.Frame} mismatch #{DesyncStreak}: {LastDesyncReason}\n  host : {host}\n  local: {local}");
        }
        else DesyncStreak = 0;
    }
}
