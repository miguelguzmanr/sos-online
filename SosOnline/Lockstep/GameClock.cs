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
/// <item><c>time</c>, <c>timeAsDouble</c>: local time plus an offset fixed on an agreed lockstep frame. Re-based on
/// each level load's release, which also absorbs frames one PC ran with another time scale before lockstep took over
/// (a title screen paused because its window had no focus).</item>
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

    private static DoubleFn _timeD, _unscaledD;
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
            _timeD = Detour<DoubleFn>("get_timeAsDouble", () => _timeD == null ? 0 : _timeD() + (Set ? _offset : 0));
            Detour<FloatFn>("get_time", () => (float)(_timeD() + (Set ? _offset : 0)));
            _unscaledD = Detour<DoubleFn>("get_unscaledTimeAsDouble", () => _unscaledD == null ? 0 : Unscaled());
            Detour<FloatFn>("get_unscaledTime", () => (float)Unscaled());
            _unscaledDelta = Detour<FloatFn>("get_unscaledDeltaTime", () => _ticking ? Step : _unscaledDelta == null ? 0 : _unscaledDelta());
            // Unity smooths deltaTime over each PC's own frame history; the game uses it for timers (the dash).
            _smoothDelta = Detour<FloatFn>("get_smoothDeltaTime", () => _ticking ? UnityEngine.Time.deltaTime : _smoothDelta == null ? 0 : _smoothDelta());
            _frame = Detour<IntFn>("get_frameCount", () => _frame == null ? 0 : _frame() + (Set ? _frameOffset : 0));
            Plugin.Log.LogInfo("[clock] time getters hooked");
        }
        catch (Exception e)
        {
            Enabled = false;
            Plugin.Log.LogError($"[clock] could not hook the time getters, sessions use each PC's own clock: {e}");
        }
    }

    private static double Unscaled() => _ticking ? _unscaled : _unscaledD() + (Set ? _unscaledOffset : 0);

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
        _frameOffset = FrameBase + lockstepFrame - _frame();
        Set = true;
        _ticking = true;
        Plugin.Log.LogInfo($"[clock] f{lockstepFrame}: game time {before:F3} -> {timeBase:F3}");
    }

    /// <summary>Lockstep applied a frame (every PC, once per frame).</summary>
    public static void Tick()
    {
        if (_ticking) _unscaled += Step;
    }

    /// <summary>Lockstep stopped: unscaled time follows the wall clock again, from where the session clock is.</summary>
    public static void Stop()
    {
        if (!_ticking || _unscaledD == null) return;
        _ticking = false;
        _unscaledOffset = _unscaled - _unscaledD();
    }

    /// <summary>
    /// Host: a base for a frame <paramref name="framesAhead"/> frames from now, when every PC's clock already agrees
    /// to within a few frames (a level load's release): a quarter second ahead of this PC's clock then.
    /// </summary>
    public static double BaseIn(int framesAhead) => Math.Round(Now + framesAhead * (double)Step + 0.25, 2);

    /// <summary>A base every PC can move to without going back in time: whole seconds, two ahead of the latest clock.</summary>
    public static double NextBase(double latestClock) => Math.Ceiling(Math.Max(latestClock, Now)) + 2;
}
