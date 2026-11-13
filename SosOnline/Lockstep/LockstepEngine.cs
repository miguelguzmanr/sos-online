using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Rewired;
using SosOnline.Game;

namespace SosOnline.Lockstep;

/// <summary>
/// Deterministic lockstep over Rewired input.
///
/// Every Unity frame (right after Rewired polls hardware) each peer:
///   1. captures its local input and schedules it for frame <c>Frame + Delay</c>, sending it to the others;
///   2. waits until every occupied slot's input for <c>Frame</c> is present;
///   3. feeds those inputs to the game through <see cref="InputOverride"/>.
/// Combined with a fixed time step this makes all peers run the same simulation.
/// </summary>
internal sealed class LockstepEngine
{
    public bool Running { get; private set; }
    public int Frame { get; private set; }
    public int Delay { get; private set; }
    public int LocalSlot { get; private set; }
    public bool IsHost { get; private set; }
    public int Seed { get; private set; }
    public bool[] Occupied { get; private set; } = new bool[InputOverride.SlotCount];

    public float LastStallMs { get; private set; }
    public float MaxStallMs { get; private set; }
    public int StallTimeoutMs = 20000;

    /// <summary>(frame, slot, slotInput, systemInputOrNull)</summary>
    public Action<int, int, InputSnapshot, InputSnapshot> SendInput;
    /// <summary>Pumps the network; returns false if the session broke.</summary>
    public Func<bool> PollNetwork;
    /// <summary>Called after the inputs of a frame have been applied.</summary>
    public Action<int> FrameApplied;
    public Action<string> Failed;

    /// <summary>Called while waiting for input: (slots still missing for the current frame, ms waited).</summary>
    public Action<List<int>, long> Stalled;

    /// <summary>This peer's own slot reached its leave frame.</summary>
    public Action LeftSession;

    /// <summary>First frame a slot no longer takes part in (int.MaxValue = still in).</summary>
    private readonly int[] _leaveFrame = new int[InputOverride.SlotCount];

    /// <summary>Slot <paramref name="slot"/> stops sending input from <paramref name="frame"/> on (same on every peer).</summary>
    public void SetLeaveFrame(int slot, int frame)
    {
        if (slot <= 0 || slot >= InputOverride.SlotCount) return;
        _leaveFrame[slot] = Math.Min(_leaveFrame[slot], frame);
    }

    public bool InSessionAt(int slot, int frame) => Occupied[slot] && frame < _leaveFrame[slot];

    /// <summary>Optional test-harness hook that edits local input before it is sent.</summary>
    public Func<int, int, InputSnapshot, InputSnapshot> LocalInputFilter;

    private readonly Dictionary<int, InputSnapshot[]> _frames = new();
    private readonly Dictionary<int, float> _times = new();

    /// <summary>How many past frames of input and time are kept (for client-side timing).</summary>
    public const int HistoryFrames = 300;

    /// <summary>The frame whose inputs the game is currently running with.</summary>
    public int AppliedFrame { get; private set; } = -1;

    /// <summary>Time.time at the start of a past frame (NaN if unknown).</summary>
    public float TimeAt(int frame) => _times.TryGetValue(frame, out var t) ? t : float.NaN;

    /// <summary>
    /// Input of a frame up to <see cref="Delay"/> frames ahead of the current one. Every peer captured those inputs
    /// before simulating the current frame, so they exist; this waits for them to arrive if needed.
    /// Returns null if the session broke or the frame is outside the reachable range.
    /// </summary>
    public InputSnapshot Peek(int frame, int channel)
    {
        if (!Running || frame > AppliedFrame + Delay || frame < AppliedFrame - HistoryFrames + 1) return null;
        if (!Complete(frame))
        {
            var sw = Stopwatch.StartNew();
            while (!Complete(frame))
            {
                if (PollNetwork != null && !PollNetwork()) return null;
                if (!Running) return null;
                if (sw.ElapsedMilliseconds > StallTimeoutMs) { Fail("timed out waiting for timing input"); return null; }
                Thread.Sleep(0);
            }
            LookaheadWaitMs += (float)sw.Elapsed.TotalMilliseconds;
        }
        return _frames[frame][channel] ?? InputSnapshot.Empty;
    }

    /// <summary>Total time spent waiting for look-ahead input (diagnostics).</summary>
    public float LookaheadWaitMs { get; private set; }

    /// <summary>Removes a press from a future frame (it was already used retroactively). Deterministic on all peers.</summary>
    public void Consume(int frame, int channel, int actionIndex)
    {
        if (!_frames.TryGetValue(frame, out var ch) || ch[channel] == null || actionIndex < 0) return;
        var copy = new InputSnapshot();
        System.Array.Copy(ch[channel].Bits, copy.Bits, copy.Bits.Length);
        System.Array.Copy(ch[channel].Axis, copy.Axis, copy.Axis.Length);
        System.Array.Copy(ch[channel].AxisRaw, copy.AxisRaw, copy.AxisRaw.Length);
        copy.Bits[actionIndex] &= ~(ActionBits.ButtonDown | ActionBits.ShortPress | ActionBits.DoublePressHold);
        ch[channel] = copy;
    }
    private readonly SortedDictionary<int, List<Action>> _scheduled = new();
    private InputSnapshot _lastLocal, _lastSystem;

    public void Start(int localSlot, bool isHost, bool[] occupied, int delay, int seed)
    {
        Seed = seed;
        LocalSlot = localSlot;
        IsHost = isHost;
        Occupied = (bool[])occupied.Clone();
        Delay = Math.Max(1, delay);
        Frame = 0;
        for (int i = 0; i < _leaveFrame.Length; i++) _leaveFrame[i] = int.MaxValue;
        _frames.Clear();
        _times.Clear();
        _scheduled.Clear();
        AppliedFrame = -1;
        LookaheadWaitMs = 0;
        _lastLocal = _lastSystem = null;
        MaxStallMs = LastStallMs = 0;

        for (int f = 0; f < Delay; f++)
        {
            var ch = Slots(f);
            for (int c = 0; c < InputOverride.ChannelCount; c++) ch[c] = InputSnapshot.Empty;
        }

        GameBridge.ResetSlotPlayers();
        RemapPlayers();
        InputOverride.SetFrame(Slots(0));
        InputOverride.Active = true;
        Running = true;
        Plugin.Log.LogInfo($"[lockstep] started: slot {localSlot}, host={isHost}, delay={Delay}, occupied={string.Join(",", occupied)}");
    }

    /// <summary>Points every lockstep channel at the Rewired player its slot currently drives.</summary>
    public static void RemapPlayers()
    {
        InputOverride.ClearMapping();
        for (int s = 0; s < InputOverride.SlotCount; s++)
        {
            var p = GameBridge.PlayerForSlot(s);
            if (p != null) InputOverride.MapPlayer(p.id, s);
        }
        InputOverride.MapPlayer(ReInput.players.GetSystemPlayer().id, InputOverride.SystemChannel);
    }

    public void Stop(string reason)
    {
        if (!Running) return;
        Running = false;
        InputOverride.Active = false;
        _frames.Clear();
        _scheduled.Clear();
        Plugin.Log.LogInfo($"[lockstep] stopped at frame {Frame}: {reason}");
    }

    public void Schedule(int frame, Action action)
    {
        if (frame < Frame) frame = Frame;
        if (!_scheduled.TryGetValue(frame, out var list)) _scheduled[frame] = list = new List<Action>();
        list.Add(action);
    }

    private InputSnapshot[] Slots(int frame)
    {
        if (!_frames.TryGetValue(frame, out var ch)) _frames[frame] = ch = new InputSnapshot[InputOverride.ChannelCount];
        return ch;
    }

    /// <summary>Network input from a peer (or relayed by the host).</summary>
    public void OnRemoteInput(int frame, int slot, InputSnapshot input, InputSnapshot system)
    {
        if (!Running || frame < Frame) return;
        var ch = Slots(frame);
        if (slot >= 0 && slot < InputOverride.SlotCount && frame < _leaveFrame[slot]) ch[slot] = input;
        if (system != null) ch[InputOverride.SystemChannel] = system;
    }

    private List<int> MissingSlots(int frame)
    {
        var missing = new List<int>();
        _frames.TryGetValue(frame, out var ch);
        for (int s = 0; s < InputOverride.SlotCount; s++)
            if (InSessionAt(s, frame) && (ch == null || ch[s] == null)) missing.Add(s);
        return missing;
    }

    private bool Complete(int frame)
    {
        if (!_frames.TryGetValue(frame, out var ch)) return false;
        for (int s = 0; s < InputOverride.SlotCount; s++)
            if (InSessionAt(s, frame) && ch[s] == null) return false;
        return ch[InputOverride.SystemChannel] != null;
    }

    /// <summary>Runs once per Unity frame, right after Rewired's Update.</summary>
    public void Tick()
    {
        if (!Running) return;

        if (Frame >= _leaveFrame[LocalSlot]) { Stop("left the session"); LeftSession?.Invoke(); return; }

        // 1. Capture and send local input for a future frame.
        int target = Frame + Delay;
        var local = InputCapture.Capture(GameBridge.PlayerForSlot(LocalSlot), _lastLocal);
        _lastLocal = local;
        InputSnapshot system = null;
        if (IsHost)
        {
            system = InputCapture.Capture(ReInput.players.GetSystemPlayer(), _lastSystem);
            _lastSystem = system;
        }
        if (LocalInputFilter != null)
        {
            local = LocalInputFilter(target, LocalSlot, local);
            if (system != null) system = LocalInputFilter(target, InputOverride.SystemChannel, system);
        }
        var ch = Slots(target);
        ch[LocalSlot] = local;
        if (system != null) ch[InputOverride.SystemChannel] = system;
        SendInput?.Invoke(target, LocalSlot, local, system);

        // 2. Wait for everyone's input for this frame.
        if (!Complete(Frame))
        {
            var sw = Stopwatch.StartNew();
            while (!Complete(Frame))
            {
                if (PollNetwork != null && !PollNetwork()) { Fail("connection lost"); return; }
                if (!Running) return;
                if (sw.ElapsedMilliseconds > StallTimeoutMs) { Fail($"no input from peers for {StallTimeoutMs / 1000}s"); return; }
                if (Stalled != null && sw.ElapsedMilliseconds > 500 && sw.ElapsedMilliseconds % 250 < 2) Stalled(MissingSlots(Frame), sw.ElapsedMilliseconds);
                Thread.Sleep(1);
            }
            LastStallMs = (float)sw.Elapsed.TotalMilliseconds;
            if (LastStallMs > MaxStallMs) MaxStallMs = LastStallMs;
        }
        else LastStallMs = 0;

        // 3. Apply.
        InputOverride.SetFrame(_frames[Frame]);
        AppliedFrame = Frame;
        _times[Frame] = UnityEngine.Time.time;
        _times.Remove(Frame - HistoryFrames);
        // Reseed every frame: whatever consumed UnityEngine.Random a variable number of times last frame
        // (async loading, visibility-driven effects) can't carry divergence into this one.
        UnityEngine.Random.InitState(unchecked(Seed ^ (Frame * 486187739)));
        if (_scheduled.Remove(Frame, out var actions))
            foreach (var a in actions)
                try { a(); } catch (Exception e) { Plugin.Log.LogError($"[lockstep] scheduled action at {Frame} failed: {e}"); }
        FrameApplied?.Invoke(Frame);
        _frames.Remove(Frame - HistoryFrames);
        Frame++;
    }

    private void Fail(string reason)
    {
        Stop(reason);
        Failed?.Invoke(reason);
    }
}
