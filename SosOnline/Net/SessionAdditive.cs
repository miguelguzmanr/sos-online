using System.Collections.Generic;
using System.IO;
using System.Linq;
using SosOnline.Lockstep;

namespace SosOnline.Net;

/// <summary>
/// The loading barrier for levels loaded during play (<see cref="AdditiveBarrier"/>): every PC reports when it got to
/// a held loading step, and the host picks the frame every PC runs it on, as for level loads.
/// </summary>
internal sealed partial class Session
{
    // Host: step key → slot → frame that PC got there.
    private readonly Dictionary<string, Dictionary<int, int>> _additiveReports = new();
    private bool _additiveTimeHeld;
    private double _additiveWaitSince;

    /// <summary>This PC got to a held step (<see cref="AdditiveBarrier"/>).</summary>
    public void ReportAdditive(string key, int frame)
    {
        // A joiner replaying (or not merged yet) takes the session's release frames from the area log and the host.
        if (_catchUp != null || Engine.Observer) return;
        if (IsHost) { AddAdditiveReport(LocalSlot, key, frame); return; }
        var (w, ms) = Protocol.Begin(Msg.AdditiveReached);
        w.Write(key);
        w.Write(frame);
        Send(HostId, ms, true);
    }

    private void OnAdditiveReached(Peer peer, BinaryReader r)
    {
        string key = r.ReadString();
        int frame = r.ReadInt32();
        if (peer == null || peer.Pending) return;
        AddAdditiveReport(peer.Slot, key, frame);
    }

    private void OnAdditiveRelease(BinaryReader r)
    {
        string key = r.ReadString();
        int at = r.ReadInt32();
        double timeBase = r.ReadDouble();
        AdditiveBarrier.Schedule(key, at, timeBase);
    }

    private void AddAdditiveReport(int slot, string key, int frame)
    {
        if (!_additiveReports.TryGetValue(key, out var bySlot)) _additiveReports[key] = bySlot = new Dictionary<int, int>();
        bySlot[slot] = frame;
        TryReleaseAdditive();
    }

    /// <summary>Host: each step every PC in the session got to runs on one frame, a little ahead, everywhere.</summary>
    private void TryReleaseAdditive()
    {
        if (!IsHost || !Engine.Running || _additiveReports.Count == 0) return;
        int now = Engine.AppliedFrame;
        foreach (var (key, bySlot) in _additiveReports.ToList())
        {
            bool all = true;
            for (int s = 0; s < InputOverride.SlotCount; s++)
                if (Engine.InSessionAt(s, now) && !bySlot.ContainsKey(s)) { all = false; break; }
            if (!all) continue;
            _additiveReports.Remove(key);
            // As for level loads: guests run at most Delay + 1 frames ahead of the host's applied frame.
            int release = now + Engine.Delay + 3;
            double timeBase = GameClock.BaseIn(release - now);
            Plugin.Log.LogInfo($"[additive] {key}: reached at {string.Join(", ", bySlot.OrderBy(kv => kv.Key).Select(kv => $"P{kv.Key + 1} f{kv.Value}"))}; release at f{release}");
            foreach (var peer in _peers.Values.Where(p => !p.Pending || p.CatchUp == CatchUpStep.Running))
            {
                var (w, ms) = Protocol.Begin(Msg.AdditiveRelease);
                w.Write(key);
                w.Write(release);
                w.Write(timeBase);
                Send(peer.Id, ms, true);
            }
            _area.LogAdditive(release, key, timeBase);
            AdditiveBarrier.Schedule(key, release, timeBase);
        }
    }

    private void ResetAdditiveBarrier(string why)
    {
        _additiveReports.Clear();
        AdditiveBarrier.Reset(why);
        EndAdditiveTimeHold();
    }

    /// <summary>
    /// Engine hold for a joiner replaying: a release frame whose step this PC hasn't got to yet. Game time stands
    /// still meanwhile, as during a level load's hold.
    /// </summary>
    private bool HoldForAdditive()
    {
        if (_catchUp == null || GameBridgeLoading) { EndAdditiveTimeHold(); return false; }
        if (!AdditiveBarrier.Waiting(Engine.Frame)) { EndAdditiveTimeHold(); return false; }
        if (!_additiveTimeHeld)
        {
            _additiveWaitSince = Now;
            _additiveTimeHeld = GameClock.HoldForLoad();
            Plugin.Log.LogInfo($"[additive] f{Engine.Frame}: waiting for this PC's load to get to its release frame");
        }
        else if (Now - _additiveWaitSince > 20)
        {
            AdditiveBarrier.GiveUp(Engine.Frame);
            EndAdditiveTimeHold();
            return false;
        }
        return true;
    }

    private static bool GameBridgeLoading => SosOnline.Game.GameBridge.LoadingLevel;

    private void EndAdditiveTimeHold()
    {
        if (!_additiveTimeHeld) return;
        _additiveTimeHeld = false;
        GameClock.EndLoadHold();
    }
}
