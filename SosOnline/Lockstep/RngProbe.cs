using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Runtime;
using SosOnline.Net;

namespace SosOnline.Lockstep;

/// <summary>
/// --sosonline-rngprobe: runtime detector for unwrapped RNG calls during lockstep.
///
/// Detours UnityEngine.Random native icalls (so inlined, generic, and folded callers are all caught) and logs any
/// call made while lockstep is active outside <see cref="DeterministicRandom"/>, plus Harmony hooks on
/// <see cref="Il2CppSystem.Random"/> constructors and <c>Next</c> overloads (virtual dispatch).
/// </summary>
internal static unsafe class RngProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float RangeFloatFn(float min, float max);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RangeIntFn(int min, int max);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float ValueFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VecOutFn(IntPtr outVec);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InitStateFn(int seed);

    private static RangeFloatFn _rangeFloat;
    private static RangeIntFn _rangeInt;
    private static ValueFn _value;
    private static VecOutFn _unitCircle, _insideSphere, _onSphere;
    private static InitStateFn _initState;

    private static readonly List<object> KeepAlive = new();
    private static readonly Dictionary<(string kind, string caller), int> Counts = new();
    private static readonly object Gate = new();
    private static bool _installed;

    public static void Install(Harmony harmony)
    {
        if (_installed) return;
        _installed = true;
        int icalls = 0, sys = 0;

        try
        {
            _rangeFloat = Detour<RangeFloatFn>("UnityEngine.Random::Range(System.Single,System.Single)", (min, max) =>
            {
                RecordUnity("Random.Range(float)");
                return _rangeFloat(min, max);
            }, ref icalls);

            _rangeInt = Detour<RangeIntFn>("UnityEngine.Random::RandomRangeInt(System.Int32,System.Int32)", (min, max) =>
            {
                RecordUnity("Random.Range(int)");
                return _rangeInt(min, max);
            }, ref icalls);

            _value = Detour<ValueFn>("UnityEngine.Random::get_value()", () =>
            {
                RecordUnity("Random.value");
                return _value();
            }, ref icalls);

            _unitCircle = Detour<VecOutFn>("UnityEngine.Random::GetRandomUnitCircle(UnityEngine.Vector2&)", v =>
            {
                RecordUnity("Random.insideUnitCircle");
                _unitCircle(v);
            }, ref icalls);

            _insideSphere = Detour<VecOutFn>("UnityEngine.Random::get_insideUnitSphere_Injected(UnityEngine.Vector3&)", v =>
            {
                RecordUnity("Random.insideUnitSphere");
                _insideSphere(v);
            }, ref icalls);

            _onSphere = Detour<VecOutFn>("UnityEngine.Random::get_onUnitSphere_Injected(UnityEngine.Vector3&)", v =>
            {
                RecordUnity("Random.onUnitSphere");
                _onSphere(v);
            }, ref icalls);

            _initState = Detour<InitStateFn>("UnityEngine.Random::InitState(System.Int32)", seed =>
            {
                if (!DeterministicRandom.InternalSeeding) RecordUnity("Random.InitState", ignoreScope: true);
                _initState(seed);
            }, ref icalls);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[rngprobe] icall hook error: {e.Message}");
        }

        try
        {
            var pre = new HarmonyMethod(typeof(RngProbe).GetMethod(nameof(OnSystemRandom), BindingFlags.Static | BindingFlags.NonPublic));
            var sysType = typeof(Il2CppSystem.Random);
            foreach (var c in sysType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (c.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(IntPtr)) continue;
                if (NativeCode.IsFolded(c)) { Plugin.Log.LogWarning($"[rngprobe] System.Random.{c} folded; skipped"); continue; }
                harmony.Patch(c, prefix: pre);
                sys++;
            }
            foreach (var m in sysType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.Name == "Next"))
            {
                if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[rngprobe] System.Random.{m} folded; skipped"); continue; }
                harmony.Patch(m, prefix: pre);
                sys++;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[rngprobe] System.Random hook error: {e.Message}");
        }

        Plugin.Log.LogInfo($"[rngprobe] active ({icalls} Unity icalls, {sys} System.Random hooks)");
    }

    private static T Detour<T>(string icall, T replacement, ref int count) where T : Delegate
    {
        var target = IL2CPP.il2cpp_resolve_icall(icall);
        if (target == IntPtr.Zero) { Plugin.Log.LogWarning($"[rngprobe] icall not found: {icall}"); return null; }
        KeepAlive.Add(replacement);
        KeepAlive.Add(INativeDetour.CreateAndApply(target, replacement, out T original));
        KeepAlive.Add(original);
        count++;
        return original;
    }

    private static bool LockstepRunning => InputOverride.Active && Session.Instance.Engine.Running && Session.Instance.Engine.AppliedFrame >= 0;

    private static void RecordUnity(string kind, bool ignoreScope = false)
    {
        if (!LockstepRunning) return;
        if (!ignoreScope && DeterministicRandom.InScope) return;
        Record(kind, $"f{Session.Instance.Engine.AppliedFrame}");
    }

    private static void OnSystemRandom(MethodBase __originalMethod)
    {
        string sig = __originalMethod?.Name == ".ctor"
            ? $"System.Random.ctor({__originalMethod.GetParameters().Length})"
            : $"System.Random.{__originalMethod?.Name}({__originalMethod?.GetParameters().Length ?? 0})";
        string when = LockstepRunning ? $"f{Session.Instance.Engine.AppliedFrame}" : "pre";
        Record(sig, when);
    }

    private static void Record(string kind, string when)
    {
        long anchor = 0;
        string caller = NativeCode.CallerChainFromStack(&anchor);
        lock (Gate)
        {
            var key = (kind, caller);
            Counts.TryGetValue(key, out int prev);
            Counts[key] = prev + 1;
            if (prev == 0) Plugin.Log.LogInfo($"[rngprobe] {when} {kind}: {caller}");
        }
    }

    public static void LogSummary()
    {
        if (!_installed) return;
        lock (Gate)
        {
            Plugin.Log.LogInfo($"[rngprobe] summary ({Counts.Count} unique caller(s)):");
            foreach (var kv in Counts.OrderByDescending(x => x.Value))
                Plugin.Log.LogInfo($"[rngprobe] {kv.Value}x {kv.Key.kind}: {kv.Key.caller}");
        }
    }
}
