using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;

namespace SosOnline.Lockstep;

/// <summary>
/// One game clock for every PC in a session (finding 8 of the drop-in plan).
///
/// Each PC's <c>Time.time</c> counts from the moment that game started, so the same lockstep frame has a different
/// absolute time on every PC. The game measures durations against absolute times and compares them in float
/// arithmetic, whose rounding depends on the magnitude. A duration of exactly N frames can then end one frame earlier
/// on one PC than on another: the party's dash into a battle (0.1 s anticipation = 6 frames) computed progress
/// 1.0000004 on one PC and 0.9999998 on the other, and the whole battle ran a frame apart from there.
///
/// While a session clock is set, Unity's own time getters (the native icalls, so every caller is covered) read
/// identically on every PC:
/// <list type="bullet">
/// <item><c>time</c>, <c>timeAsDouble</c>: an agreed base on an agreed lockstep frame plus every frame's step since
/// (<see cref="SessionTime"/>). Re-based on each level load's release, which also absorbs frames one PC ran with
/// another time scale before lockstep took over (a title screen paused because its window had no focus).</item>
/// <item><c>fixedTime</c>: the base minus the aligned physics phase, plus whole fixed steps since.</item>
/// <item><c>unscaledTime</c>, <c>unscaledDeltaTime</c>: wall-clock time otherwise. During lockstep, 1/60 s per frame.</item>
/// <item><c>smoothDeltaTime</c>: Unity's average of each PC's own recent frame times. During lockstep, <c>deltaTime</c>
/// (the game times the dash into a battle with it).</item>
/// <item><c>frameCount</c>: <see cref="FrameBase"/> + lockstep frame.</item>
/// </list>
/// The clock is kept after the session ends so time never runs backwards; a new base is only ever set ahead of every
/// PC's clock.
/// </summary>
internal static class GameClock
{
    /// <summary>frameCount on lockstep frame 0. Far above any real frame count, so setting it never goes backwards.</summary>
    public const int FrameBase = 10_000_000;

    /// <summary>Unscaled time per lockstep frame: the fixed step <see cref="Game.GameBridge.LockTime"/> sets.</summary>
    private const float Step = 1f / 60f;

    public static bool Enabled = true;
    public static bool Set { get; private set; }

    private static double _offset;
    private static int _frameOffset;
    // Unscaled time: counted per lockstep frame while one runs (_ticking), local wall-clock time + offset otherwise.
    private static bool _ticking;
    private static double _unscaled, _unscaledOffset;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float FloatFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate double DoubleFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();

    private static DoubleFn _timeD, _unscaledD, _realD;

    /// <summary>
    /// realtimeSinceStartup during lockstep: the session's unscaled time plus this constant (the same on every PC), so
    /// it reads identically everywhere and starts above any real value a PC had (under ~14 h of play). It was the
    /// game's wall clock: a PC waiting for the network kept counting it while frames stood still, so real-time waits
    /// (WaitForSecondsRealtime in the battle's turn logic) ended on different frames, and the next actor differed
    /// (tests/battle-turns.sh with lag, f2756, 2026-11-15).
    /// </summary>
    private const double RealShift = 50000;
    private static double _realOffset;

    /// <summary>
    /// timeSinceLevelLoad while a session clock is set: game time since the last base, i.e. since the last level
    /// load's release, which is the same on every PC. Unity counts it from each PC's own load end, about 4 s apart
    /// in a test session (2026-11-15).
    /// </summary>
    private static DoubleFn _levelD;
    private static double _levelStart;

    /// <summary>
    /// fixedTime trails time by whatever physics time a PC skipped before (frames longer than maximumDeltaTime, e.g.
    /// while loading): about 4 s more on one test PC than the other, with the same phase within a frame. Inside a
    /// physics step Unity's time also reads fixedTime, so FixedUpdate code saw times 4 s apart on the two PCs. During
    /// a session both read the synced time minus the shared phase: whole skipped steps are added back
    /// (<see cref="_fixedShift"/>, set with each base).
    /// </summary>
    private static DoubleFn _fixedD;
    private static double _fixedShift;
    private delegate byte BoolFn();
    private static BoolFn _inFixed, _focusedOrig;

    private static bool InFixedStep()
    {
        try { return _inFixed != null && _inFixed() != 0; } catch { return false; }
    }
    private static FloatFn _unscaledDelta, _smoothDelta;
    private static IntFn _frame;
    private static readonly List<object> _keepAlive = new();

    public static void Install()
    {
        if (!Enabled) return;
        try
        {
            // The original (trampoline) is only known once the detour is applied: until then, read 0. Any exception
            // thrown in here would crash the game, so the hooks stay trivial.
            var inFixed = IL2CPP.il2cpp_resolve_icall("UnityEngine.Time::get_inFixedTimeStep()");
            if (inFixed != IntPtr.Zero) _inFixed = Marshal.GetDelegateForFunctionPointer<BoolFn>(inFixed);
            _timeD = Detour<DoubleFn>("get_timeAsDouble", Time);
            Detour<FloatFn>("get_time", () => (float)Time());
            _fixedD = Detour<DoubleFn>("get_fixedTimeAsDouble", FixedTime);
            Detour<FloatFn>("get_fixedTime", () => (float)FixedTime());
            _unscaledD = Detour<DoubleFn>("get_unscaledTimeAsDouble", () => _unscaledD == null ? 0 : Unscaled());
            Detour<FloatFn>("get_unscaledTime", () => (float)Unscaled());
            _unscaledDelta = Detour<FloatFn>("get_unscaledDeltaTime", () => _ticking ? Step : _unscaledDelta == null ? 0 : _unscaledDelta());
            // Unity smooths deltaTime over each PC's own frame history; the game uses it for timers (the dash).
            _smoothDelta = Detour<FloatFn>("get_smoothDeltaTime", () => _ticking ? UnityEngine.Time.deltaTime : _smoothDelta == null ? 0 : _smoothDelta());
            _frame = Detour<IntFn>("get_frameCount", () => _frame == null ? 0 : _frame() + (Set ? _frameOffset : 0));
            _realD = Detour<DoubleFn>("get_realtimeSinceStartupAsDouble", () => _realD == null ? 0 : Real());
            _levelD = Detour<DoubleFn>("get_timeSinceLevelLoadAsDouble", () => _levelD == null ? 0 : Level());
            Detour<FloatFn>("get_timeSinceLevelLoad", () => (float)Level());
            Detour<FloatFn>("get_realtimeSinceStartup", () => (float)Real());
            // Application.isFocused: the game's saving (among others) waits while the window isn't focused. During
            // lockstep every PC must behave the same whichever window has focus (a save completed only on the PC
            // whose player answered, 2026-11-15).
            var focused = IL2CPP.il2cpp_resolve_icall("UnityEngine.Application::get_isFocused()");
            if (focused != IntPtr.Zero)
            {
                BoolFn replacement = () => InputOverride.Active ? (byte)1 : (_focusedOrig == null ? (byte)1 : _focusedOrig());
                _keepAlive.Add(replacement);
                _keepAlive.Add(INativeDetour.CreateAndApply(focused, replacement, out _focusedOrig));
                _keepAlive.Add(_focusedOrig);
            }
            Plugin.Log.LogInfo("[clock] time getters hooked");
        }
        catch (Exception e)
        {
            Enabled = false;
            Plugin.Log.LogError($"[clock] could not hook the time getters, sessions use each PC's own clock: {e}");
        }
    }

    // Session time, while lockstep ticks. Local time plus an offset read the same to about 1e-14 only: each PC's
    // Unity sums its frame steps onto its own, larger clock, and the rounding depends on the magnitude
    // (45.42000099129975 vs 45.420000991299744 at f1417, 2026-11-16). Every PC's frame step is the same float
    // (captureDeltaTime x timeScale), so the session sums those steps itself, from the base.
    private static double _session, _lastLocal, _fixedSession, _fixedLocal0, _fixedDt;
    private static int _lastFrameSeen = int.MinValue;

    /// <summary>
    /// Adds the steps of the frames since the last call. Only outside a physics step (local time reads fixedTime
    /// inside one). The difference of two local times is the step plus rounding far below a float's precision:
    /// rounding it to float gives the exact step.
    /// </summary>
    private static void Advance()
    {
        int f = _frame();
        if (f == _lastFrameSeen) return;
        double now = _timeD();
        _session += (float)(now - _lastLocal);
        _lastLocal = now;
        _lastFrameSeen = f;
    }

    private static double SessionTime()
    {
        if (InFixedStep()) return SessionFixed();
        Advance();
        return _session;
    }

    private static double SessionFixed() =>
        _fixedDt > 0 ? _fixedSession + Math.Round((_fixedD() - _fixedLocal0) / _fixedDt) * _fixedDt : _fixedD() + _offset + _fixedShift;

    private static double Time() => _timeD == null ? 0 : _ticking ? SessionTime() : _timeD() + (Set ? _offset + (InFixedStep() ? _fixedShift : 0) : 0);

    private static double FixedTime() => _fixedD == null ? 0 : _ticking ? SessionFixed() : _fixedD() + (Set ? _offset + _fixedShift : 0);

    /// <summary>Once per Unity frame (Rewired's Update, outside physics): keeps the step sum unbroken.</summary>
    public static void OnFrame()
    {
        if (_ticking && _timeD != null && _frame != null && !InFixedStep()) Advance();
    }

    private static double Unscaled() => _ticking ? _unscaled : _unscaledD() + (Set ? _unscaledOffset : 0);

    private static double Level() => _ticking && _timeD != null ? Math.Max(0, Time() - _levelStart) : _levelD();

    private static double Real() => _ticking ? _unscaled + RealShift : _realD() + _realOffset;

    private static T Detour<T>(string getter, T replacement) where T : Delegate
    {
        var target = IL2CPP.il2cpp_resolve_icall($"UnityEngine.Time::{getter}()");
        if (target == IntPtr.Zero) throw new InvalidOperationException($"icall Time.{getter} not found");
        _keepAlive.Add(replacement);
        _keepAlive.Add(INativeDetour.CreateAndApply(target, replacement, out T original));
        _keepAlive.Add(original);
        return original;
    }

    /// <summary>This PC's game time now (the session clock if one is set).</summary>
    public static double Now => UnityEngine.Time.timeAsDouble;

    /// <summary>
    /// On lockstep frame <paramref name="lockstepFrame"/>, on every PC: from now on game time (scaled and unscaled)
    /// reads <paramref name="timeBase"/> plus the time that passes, and frameCount reads FrameBase + lockstep frame.
    /// </summary>
    public static void SetBase(double timeBase, int lockstepFrame)
    {
        if (!Enabled || _timeD == null) return;
        double before = Now;
        _offset = timeBase - _timeD();
        _unscaled = timeBase;
        _levelStart = timeBase;
        if (_aligning)
        {
            _aligning = false;
            UnityEngine.Time.captureDeltaTime = Step;
        }
        if (_fixedD != null)
        {
            // Whole fixed steps fixedTime trails time by; the rest (the phase) is the same on every PC.
            double dt = UnityEngine.Time.fixedDeltaTime, lag = _timeD() - _fixedD();
            _fixedShift = dt > 0 ? Math.Floor(lag / dt + 1e-6) * dt : 0;
        }
        _frameOffset = FrameBase + lockstepFrame - _frame();
        double phaseNow = _fixedD != null && UnityEngine.Time.fixedDeltaTime > 0 ? Mod(_timeD() - _fixedD(), UnityEngine.Time.fixedDeltaTime) : double.NaN;
        _session = timeBase;
        _lastLocal = _timeD();
        _lastFrameSeen = _frame();
        // The phase is aligned before each load release (the same on every PC to well within 1e-5 s).
        _fixedDt = double.IsNaN(phaseNow) ? 0 : UnityEngine.Time.fixedDeltaTime;
        _fixedLocal0 = _fixedD != null ? _fixedD() : 0;
        _fixedSession = double.IsNaN(phaseNow) ? 0 : timeBase - Math.Round(phaseNow, 5);
        Set = true;
        _ticking = true;
        Plugin.Log.LogInfo($"[clock] f{lockstepFrame}: game time {before:F3} -> {timeBase:F3}, physics phase {phaseNow:F5}");
    }

    /// <summary>
    /// Physics runs every fixedDeltaTime (0.02 s) against 1/60 s frames, so one frame in six gets no physics step;
    /// which one depends on the phase between time and fixedTime. Boundaries are 1/300 s apart, and two PCs' phases
    /// were ~0.001 s apart: about one session in three the PCs stepped physics on different frames and battles split
    /// (tests/battle-turns.sh, 2026-11-15). On the frame before each load release (the game is held in loading),
    /// every PC stretches that one frame so its phase lands on <see cref="TargetPhase"/>, midway between boundaries.
    /// </summary>
    private const double TargetPhase = 0.005;
    private static bool _aligning;
    private static int _alignTicks;

    /// <summary>Call on the frame before a load release: the next frame's step brings the physics phase to target.</summary>
    public static void AlignPhysicsPhase()
    {
        if (!Enabled || _timeD == null || _fixedD == null) return;
        try
        {
            double dt = UnityEngine.Time.fixedDeltaTime;
            float scale = UnityEngine.Time.timeScale;
            if (dt <= 0 || scale <= 0) { Plugin.Log.LogWarning($"[clock] physics phase not aligned (time scale {scale})"); return; }
            double phase = Mod(_timeD() - _fixedD(), dt);
            // Game time advances captureDeltaTime * timeScale: pick the step whose scaled length lands on the target.
            double extra = Mod(TargetPhase - phase - Step * scale, dt);
            UnityEngine.Time.captureDeltaTime = (float)(Step + extra / scale);
            _aligning = true;
            _alignTicks = 0;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[clock] physics phase not aligned: {e.Message}"); }
    }

    private static double Mod(double x, double m) => ((x % m) + m) % m;

    /// <summary>Game time per frame while a level load is held at the barrier: next to nothing, but not 0 (0 turns the fixed step off).</summary>
    private const float HoldStep = 0.0001f;
    private static bool _holding;

    /// <summary>
    /// A level load reached its end on this PC and waits for the release frame: game time almost stands still until
    /// then. The level's objects already run, and each PC waits a different number of frames (the slowest loader
    /// decides): NPC patrols timed in game time walked 14 frames longer on one PC than the other, which split every NPC
    /// on Mountain Trail (2026-11-26). A replaying joiner waits far longer than the live PCs did.
    /// </summary>
    public static void HoldForLoad()
    {
        if (!Enabled || _holding) return;
        try { UnityEngine.Time.captureDeltaTime = HoldStep; _holding = true; }
        catch (Exception e) { Plugin.Log.LogWarning($"[clock] time not held: {e.Message}"); }
    }

    /// <summary>The held load was released (or dropped): frames take their usual step again.</summary>
    public static void EndLoadHold()
    {
        if (!_holding) return;
        _holding = false;
        if (_aligning) return; // the stretched step is already set; SetBase restores Step
        try { UnityEngine.Time.captureDeltaTime = Step; } catch { }
    }

    /// <summary>Lockstep applied a frame (every PC, once per frame).</summary>
    public static void Tick()
    {
        // The stretched step lasts one frame; restored by SetBase, or here if no release followed.
        if (_aligning && ++_alignTicks > 1) { _aligning = false; UnityEngine.Time.captureDeltaTime = Step; }
        if (_ticking) _unscaled += Step;
    }

    /// <summary>Lockstep stopped: unscaled time follows the wall clock again, from where the session clock is.</summary>
    public static void Stop()
    {
        if (!_ticking || _unscaledD == null) return;
        // Time carries on from the session's value, on the local clock again.
        if (_timeD != null && !InFixedStep()) { Advance(); _offset = _session - _timeD(); }
        _ticking = false;
        _unscaledOffset = _unscaled - _unscaledD();
        if (_realD != null) _realOffset = _unscaled + RealShift - _realD(); // carry on from the session's value
    }

    /// <summary>
    /// Host: a base for a frame <paramref name="framesAhead"/> frames from now, when every PC's clock already agrees
    /// to within a few frames (a level load's release): a quarter second ahead of this PC's clock then.
    /// </summary>
    public static double BaseIn(int framesAhead) => Math.Round(Now + framesAhead * (double)Step + 0.25, 2);

    /// <summary>A base every PC can move to without going back in time: whole seconds, two ahead of the latest clock.</summary>
    public static double NextBase(double latestClock) => Math.Ceiling(Math.Max(latestClock, Now)) + 2;
}
