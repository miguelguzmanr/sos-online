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
    /// <summary>
    /// Added to the frame number when reseeding the RNG and when setting the game clock's frame count on a load
    /// release (spike B: a replay reproduces another run's frames).
    /// </summary>
    public int RngFrameOffset;
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

    /// <summary>
    /// Optional: while it returns true, frames don't advance (Stage 3: a joiner's area-entry save is still loading;
    /// frame L must not run before that load reaches its end).
    /// </summary>
    public Func<bool> Hold;

    /// <summary>Newest frame whose system input (sent by the host only) has arrived: how far the session has got.</summary>
    public int NewestKnownFrame { get; private set; } = -1;

    /// <summary>First frame a slot no longer takes part in (int.MaxValue = still in).</summary>
    private readonly int[] _leaveFrame = new int[InputOverride.SlotCount];

    /// <summary>First frame a slot takes part in (0 for the slots present at the start).</summary>
    private readonly int[] _joinFrame = new int[InputOverride.SlotCount];

    /// <summary>The frame this run started on (0, or the join frame on a PC that dropped in).</summary>
    public int StartFrame { get; private set; }

    /// <summary>
    /// Stage 3 joiner that is still catching up: its own slot's input comes from the host's log (a guest that
    /// re-syncs by replay was in the session at the area entry), never from this PC's devices, and its own slot
    /// leaving in the log doesn't stop this run. Cleared on the merge frame message.
    /// </summary>
    public bool Observer;

    /// <summary>Slot <paramref name="slot"/> stops sending input from <paramref name="frame"/> on (same on every peer).</summary>
    public void SetLeaveFrame(int slot, int frame)
    {
        if (slot <= 0 || slot >= InputOverride.SlotCount) return;
        _leaveFrame[slot] = Math.Min(_leaveFrame[slot], frame);
    }

    private int _endFrame = int.MaxValue;
    private Action _onEnd;

    /// <summary>
    /// Runs <paramref name="onEnd"/> (which stops lockstep) at the start of <paramref name="frame"/>, before anything of
    /// that frame runs: every peer ends after the same last frame.
    /// </summary>
    public void EndAt(int frame, Action onEnd)
    {
        if (frame < Frame) frame = Frame;
        _endFrame = frame;
        _onEnd = onEnd;
    }

    public bool InSessionAt(int slot, int frame) => Occupied[slot] && frame >= _joinFrame[slot] && frame < _leaveFrame[slot];

    /// <summary>
    /// Drop-in: slot <paramref name="slot"/> takes part from <paramref name="frame"/> on (same on every peer). Its
    /// first <see cref="Delay"/> frames are empty input, like the first frames of a run: the joiner's own input
    /// starts at <paramref name="frame"/> + Delay.
    /// </summary>
    public void AddSlot(int slot, int frame)
    {
        if (slot <= 0 || slot >= InputOverride.SlotCount) return;
        Occupied[slot] = true;
        _joinFrame[slot] = frame;
        _leaveFrame[slot] = int.MaxValue;
        for (int f = frame; f < frame + Delay; f++) Slots(f)[slot] = InputSnapshot.Empty;
        Plugin.Log.LogInfo($"[lockstep] slot {slot} joins at frame {frame}");
    }

    /// <summary>Every input buffered for <paramref name="from"/> and later (host: brings a joiner up to date).</summary>
    public IEnumerable<(int frame, InputSnapshot[] channels)> BufferedFrom(int from)
    {
        foreach (var kv in _frames)
            if (kv.Key >= from) yield return (kv.Key, kv.Value);
    }

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

    /// <param name="startFrame">
    /// 0 for a new run. A PC dropping into a running session starts on the join frame: its own first Delay frames
    /// are empty, everyone else's input comes from the host.
    /// </param>
    public void Start(int localSlot, bool isHost, bool[] occupied, int delay, int seed, int startFrame = 0)
    {
        Seed = seed;
        LocalSlot = localSlot;
        IsHost = isHost;
        Occupied = (bool[])occupied.Clone();
        Delay = Math.Max(1, delay);
        Frame = StartFrame = startFrame;
        Observer = false;
        RngFrameOffset = 0;
        _endFrame = int.MaxValue;
        _onEnd = null;
        for (int i = 0; i < _leaveFrame.Length; i++) { _leaveFrame[i] = int.MaxValue; _joinFrame[i] = 0; }
        _frames.Clear();
        _times.Clear();
        _scheduled.Clear();
        AppliedFrame = startFrame - 1;
        NewestKnownFrame = startFrame - 1;
        Hold = null;
        LookaheadWaitMs = 0;
        _lastLocal = _lastSystem = null;
        MaxStallMs = LastStallMs = 0;

        for (int f = startFrame; f < startFrame + Delay; f++)
        {
            var ch = Slots(f);
            if (startFrame == 0)
                for (int c = 0; c < InputOverride.ChannelCount; c++) ch[c] = InputSnapshot.Empty;
            else ch[localSlot] = InputSnapshot.Empty;
        }
        if (startFrame > 0) _joinFrame[localSlot] = startFrame;

        GameBridge.ResetSlotPlayers();
        RemapPlayers();
        InputOverride.ResetHold();
        InputOverride.SetFrame(startFrame == 0 ? Slots(0) : EmptyFrame(), advance: false);
        InputOverride.Active = true;
        Running = true;
        Plugin.Log.LogInfo($"[lockstep] started at frame {startFrame}: slot {localSlot}, host={isHost}, delay={Delay}, occupied={string.Join(",", occupied)}");
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
        GameClock.Stop();
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

    private static InputSnapshot[] EmptyFrame()
    {
        var ch = new InputSnapshot[InputOverride.ChannelCount];
        for (int c = 0; c < ch.Length; c++) ch[c] = InputSnapshot.Empty;
        return ch;
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
        if (system != null)
        {
            ch[InputOverride.SystemChannel] = system;
            if (frame > NewestKnownFrame) NewestKnownFrame = frame;
        }
    }

    /// <summary>Stage 3: a logged frame from the host, every channel at once (slots not in the session are ignored).</summary>
    public void OnRemoteFrame(int frame, InputSnapshot[] channels)
    {
        if (!Running || frame < Frame) return;
        for (int s = 0; s < InputOverride.SlotCount; s++)
            if (channels[s] != null && InSessionAt(s, frame)) OnRemoteInput(frame, s, channels[s], null);
        if (channels[InputOverride.SystemChannel] != null) OnRemoteInput(frame, -1, null, channels[InputOverride.SystemChannel]);
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

        if (!Observer && Frame >= _leaveFrame[LocalSlot]) { Stop("left the session"); LeftSession?.Invoke(); return; }

        if (Frame >= _endFrame)
        {
            var end = _onEnd;
            _endFrame = int.MaxValue;
            _onEnd = null;
            end?.Invoke();
            if (!Running) return;
        }

        if (Hold != null && Hold()) return;

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
        // A slot's first Delay frames are empty input on every PC (AddSlot); a Stage 3 joiner that is still catching
        // up isn't in the session at all, and its own input must not drive its copy of the game either.
        bool localIn = !Observer && InSessionAt(LocalSlot, target) && target >= _joinFrame[LocalSlot] + Delay;
        if (localIn) ch[LocalSlot] = local;
        if (system != null) ch[InputOverride.SystemChannel] = system;
        if (localIn || system != null) SendInput?.Invoke(target, LocalSlot, local, system);

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
        GameClock.Tick();
        _times[Frame] = UnityEngine.Time.time;
        _times.Remove(Frame - HistoryFrames);
        // Reseed every frame: whatever consumed UnityEngine.Random a variable number of times last frame
        // (async loading, visibility-driven effects) can't carry divergence into this one.
        DeterministicRandom.InternalSeeding = true;
        try { UnityEngine.Random.InitState(unchecked(Seed ^ ((Frame + RngFrameOffset) * 486187739))); }
        finally { DeterministicRandom.InternalSeeding = false; }
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
