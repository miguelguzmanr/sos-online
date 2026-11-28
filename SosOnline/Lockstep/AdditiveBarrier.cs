using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using SosOnline.Game;
using SosOnline.Net;

namespace SosOnline.Lockstep;

/// <summary>
/// Loading barrier for levels loaded during play, next to the level that is already there: the golem's throw on
/// Evermist Island (<c>EvermistYeet</c>, loaded over the world map), world map sections and the like. They load with
/// the game's <see cref="LevelLoader"/> (asynchronous I/O), and every step after the I/O ran on the frame this PC
/// finished it: on 2026-11-27 two guests activated <c>EvermistYeet</c> 30 frames before the host, and the throw
/// swapped Zale's world map character on a different frame on each PC (findings §23).
///
/// The game's request comes from synced game logic, so its frame is the same on every PC and names the load. Then:
/// <list type="bullet">
/// <item><b>activate</b>: the loader activates each scene as soon as it is loaded (<c>LevelLoader.ActivateScenes</c>,
/// from each scene's ready callback), so the scenes' objects started a few frames apart: a wandering NPC of the new
/// level was 0.1 m off after the throw. Activation waits until every scene is loaded here, the host picks a frame
/// once every PC is there, and on it every PC holds game time (as during a level load) and activates them.</item>
/// <item><b>start</b>: Unity activates them in its own time (a time slice per frame), so the level's start
/// (<c>InitializeCurrentLevel</c>) is held too and runs on a second agreed frame, where game time runs again from a
/// clock base the host picked (each PC held a different number of frames).</item>
/// <item><b>init</b>: the level's initializers done (<c>ValidateInitializationDone</c>, which tells the owner the
/// level is loaded); held the same way when it doesn't follow the start at once.</item>
/// </list>
/// The rest of the game keeps running meanwhile and waits for the load to end on every PC, as in local play.
/// Level loads (<see cref="GameBridge.LoadingLevel"/>) have their own barrier (<see cref="LoadBarrier"/>).
/// </summary>
internal static class AdditiveBarrier
{
    /// <summary>A held step runs anyway after this long (a PC that never gets there, or a key the PCs don't share).</summary>
    private const double HoldLimitSeconds = 20;

    private sealed class Held
    {
        public Action Run;
        public IntPtr Loader;
        public int Frame;
        public double Since;
    }

    // Loader → frame of its request during lockstep, outside a level load, until its load is over.
    private static readonly Dictionary<IntPtr, int> _requests = new();
    private static readonly Dictionary<string, Held> _held = new();
    // Loaders whose activation was asked for before every scene was loaded here.
    private static readonly Dictionary<IntPtr, LevelLoader> _wantActivate = new();
    private static bool _timeHeld;
    // Releases scheduled on this PC: key → frame.
    private static readonly Dictionary<string, int> _scheduled = new();
    // Keys whose release frame came before this PC got there (a joiner replaying, or a late PC).
    private static readonly HashSet<string> _releasedEarly = new();
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static bool _releasing;
    // --sosonline-noadditive: no barrier (to reproduce the split).
    private static bool Off => TestHarness.TryArg("noadditive", out _);

    private static double Now => _clock.Elapsed.TotalSeconds;
    private static LockstepEngine Engine => Session.Instance.Engine;
    private static bool Active => InputOverride.Active && Engine.Running;

    /// <summary>Steps held on this PC, waiting for their release frame.</summary>
    public static int HeldCount => _held.Count;

    /// <summary>Steps that ran without their agreed frame (a late PC, or one held past the limit).</summary>
    public static int LateRuns { get; private set; }

    public static void Install()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>)
            (Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>)OnSceneLoaded;
    }

    // When each scene really became active (Unity activates them in its own time once allowed): compared between PCs.
    private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        try
        {
            if (!Active || GameBridge.LoadingLevel) return;
            Plugin.Log.LogInfo($"[additive] scene {scene.name} active at f{Engine.AppliedFrame}");
        }
        catch { }
    }

    /// <summary>Lockstep started or stopped: run whatever is held (the game would wait forever) and forget the rest.</summary>
    public static void Reset(string why)
    {
        foreach (var (key, h) in _held.ToList())
        {
            Plugin.Log.LogInfo($"[additive] {key}: {why}, running it now");
            Run(key, h);
        }
        _held.Clear();
        foreach (var l in _wantActivate.Values.ToList())
        {
            _releasing = true;
            try { l.ActivateScenes(); } catch { }
            finally { _releasing = false; }
        }
        _wantActivate.Clear();
        EndTimeHold(double.NaN, 0);
        _requests.Clear();
        _scheduled.Clear();
        _releasedEarly.Clear();
    }

    /// <summary>
    /// The step <paramref name="key"/> runs on <paramref name="frame"/> on this PC (the host's pick, or the area log's
    /// for a joiner replaying).
    /// </summary>
    public static void Schedule(string key, int frame, double timeBase)
    {
        if (!Engine.Running) return;
        if (_scheduled.ContainsKey(key)) return;
        _scheduled[key] = frame;
        if (frame < Engine.Frame)
        {
            // Too late to run on its frame: run it now (or when this PC gets there).
            Plugin.Log.LogError($"[additive] {key}: release frame f{frame} already passed (now f{Engine.Frame})");
            Release(key, frame, timeBase);
            return;
        }
        // As for a level load: the frame before game time runs again, line up the physics phase on every PC.
        if (key.EndsWith("/start") && frame - 1 >= Engine.Frame)
            Engine.Schedule(frame - 1, () => { if (_timeHeld) GameClock.AlignPhysicsPhase(); });
        Engine.Schedule(frame, () => Release(key, frame, timeBase));
    }

    private static void Release(string key, int frame, double timeBase)
    {
        _scheduled.Remove(key);
        if (_held.Remove(key, out var h))
        {
            Plugin.Log.LogInfo($"[additive] {key}: released at f{Engine.AppliedFrame} (held {Engine.AppliedFrame - h.Frame} frames)");
            Run(key, h);
        }
        else _releasedEarly.Add(key);
        if (key.EndsWith("/start")) EndTimeHold(timeBase, frame);
    }

    /// <summary>Game time runs again from <paramref name="timeBase"/> (NaN: from where it is, lockstep stopping).</summary>
    private static void EndTimeHold(double timeBase, int frame)
    {
        if (!_timeHeld) return;
        _timeHeld = false;
        if (!double.IsNaN(timeBase)) GameClock.SetBase(timeBase, frame + Engine.RngFrameOffset);
        GameClock.EndLoadHold();
        Plugin.Log.LogInfo($"[additive] f{frame}: game time runs again");
    }

    /// <summary>On the activation's release frame: game time stands still while Unity activates the scenes.</summary>
    private static void Activate(LevelLoader l)
    {
        if (!_timeHeld && GameClock.HoldForLoad()) _timeHeld = true;
        l.ActivateScenes();
    }

    private static void Run(string key, Held h)
    {
        _releasing = true;
        try { h.Run(); }
        catch (Exception e) { Plugin.Log.LogWarning($"[additive] {key}: {e.Message}"); }
        finally { _releasing = false; }
    }

    /// <summary>
    /// A joiner replaying (or following the session before it merges) reached a release frame whose step it hasn't
    /// got to yet: its engine waits, game time standing still, until it has. Everyone in the session reported the
    /// step before the host picked the frame, so only a PC outside the session can be here.
    /// </summary>
    public static bool Waiting(int frame)
    {
        foreach (var (key, at) in _scheduled)
            if (at == frame && !_held.ContainsKey(key)) return true;
        return false;
    }

    /// <summary>A joiner waited too long for its steps of <paramref name="frame"/>: they run whenever it gets there.</summary>
    public static void GiveUp(int frame)
    {
        foreach (var (key, at) in _scheduled.ToList())
        {
            if (at != frame || _held.ContainsKey(key)) continue;
            _scheduled.Remove(key);
            _releasedEarly.Add(key);
            Plugin.Log.LogError($"[additive] {key}: this PC didn't get there by f{frame} within {HoldLimitSeconds:F0} s; it runs when it does");
        }
    }

    /// <summary>Every applied frame: a step held past the limit runs anyway, so the game never waits forever.</summary>
    public static void Update()
    {
        foreach (var (ptr, l) in _wantActivate.ToList())
        {
            bool loaded;
            try { loaded = (l.scenesNotReadyForActivation?.Count ?? 0) == 0; } catch { loaded = true; }
            if (!loaded) continue;
            _wantActivate.Remove(ptr);
            var loader = l;
            if (!Gate(loader, "activate", () => Activate(loader)))
            {
                _releasing = true;
                try { loader.ActivateScenes(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[additive] activate: {e.Message}"); }
                finally { _releasing = false; }
            }
        }
        if (_held.Count == 0) return;
        double now = Now;
        foreach (var (key, h) in _held.ToList())
        {
            if (now - h.Since < HoldLimitSeconds) continue;
            _held.Remove(key);
            LateRuns++;
            Plugin.Log.LogError($"[additive] {key}: no release after {HoldLimitSeconds:F0} s (held since f{h.Frame}); running it now");
            Run(key, h);
        }
    }

    private static string Name(LevelLoader l)
    {
        try { return string.IsNullOrEmpty(l.mainSceneName) ? "level" : l.mainSceneName; } catch { return "level"; }
    }

    /// <summary>
    /// Returns true if the step is held (the original call is skipped); it runs again, through
    /// <paramref name="run"/>, on the release frame.
    /// </summary>
    private static bool Gate(LevelLoader l, string step, Action run)
    {
        if (_releasing || Off || !Active) return false;
        if (!_requests.TryGetValue(l.Pointer, out int requested)) return false;
        string key = $"{Name(l)}@f{requested}/{step}";
        int frame = Engine.AppliedFrame;
        if (_releasedEarly.Remove(key))
        {
            LateRuns++;
            Plugin.Log.LogError($"[additive] {key}: got here at f{frame}, after its release frame; running it now");
            return false;
        }
        if (_held.ContainsKey(key)) return true; // called again before the release (the game re-checks): still held
        _held[key] = new Held { Run = run, Loader = l.Pointer, Frame = frame, Since = Now };
        Plugin.Log.LogInfo($"[additive] {key}: reached at f{frame}, held");
        Session.Instance.ReportAdditive(key, frame);
        return true;
    }

    private static bool Ours(LevelLoader l) => NativeCode.Is<LevelLoader>(l);

    // The request: its frame names the load on every PC. LevelLoader.LoadLevel takes a LevelReference (a struct), and
    // a detour with a struct parameter crashed the runtime; its first steps take no parameters and run in the same
    // call (the parallel loading steps, then the scenes). The first one of a load counts.
    private static void Requested(LevelLoader l)
    {
        if (!Ours(l)) return;
        if (!Active || GameBridge.LoadingLevel) { _requests.Remove(l.Pointer); return; }
        if (_requests.ContainsKey(l.Pointer)) return;
        int frame = Engine.AppliedFrame;
        _requests[l.Pointer] = frame;
        Plugin.Log.LogInfo($"[additive] f{frame} load requested");
    }

    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.LoadLocalization))]
    private static class RequestLocalizationPatch { private static void Prefix(LevelLoader __instance) => Requested(__instance); }

    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.LoadPortraits))]
    private static class RequestPortraitsPatch { private static void Prefix(LevelLoader __instance) => Requested(__instance); }

    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.LoadAdditiveLevelSoundBanks))]
    private static class RequestBanksPatch { private static void Prefix(LevelLoader __instance) => Requested(__instance); }

    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.LoadCharacterSoundBanks))]
    private static class RequestCharacterBanksPatch { private static void Prefix(LevelLoader __instance) => Requested(__instance); }

    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.LoadClasses))]
    private static class RequestClassesPatch { private static void Prefix(LevelLoader __instance) => Requested(__instance); }

    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.StartLoadingScenes))]
    private static class RequestScenesPatch { private static void Prefix(LevelLoader __instance) => Requested(__instance); }

    // Each scene's ready callback (and the owner, for a load that waits for it) asks for activation; it waits here
    // until every scene is loaded on this PC (Update), then for the agreed frame.
    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.ActivateScenes))]
    private static class ActivatePatch
    {
        private static bool Prefix(LevelLoader __instance)
        {
            if (!Ours(__instance) || _releasing || Off || !Active) return true;
            var ptr = __instance.Pointer;
            if (!_requests.TryGetValue(ptr, out int requested)) return true;
            if (_held.ContainsKey($"{Name(__instance)}@f{requested}/activate")) return false;
            if (_wantActivate.TryAdd(ptr, __instance))
                Plugin.Log.LogInfo($"[additive] {Name(__instance)}@f{requested}: activation asked at f{Engine.AppliedFrame}, waits for every scene");
            return false;
        }
    }

    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.InitializeCurrentLevel))]
    private static class StartPatch
    {
        private static bool Prefix(LevelLoader __instance)
        {
            if (!Ours(__instance)) return true;
            var l = __instance;
            return !Gate(l, "start", () => l.InitializeCurrentLevel());
        }
    }

    [HarmonyPatch(typeof(LevelLoader), nameof(LevelLoader.ValidateInitializationDone))]
    private static class InitDonePatch
    {
        private static bool Prefix(LevelLoader __instance)
        {
            if (!Ours(__instance)) return true;
            int toDo;
            try { toDo = __instance.initializationsToDo; } catch { return true; }
            if (Active && _requests.ContainsKey(__instance.Pointer))
                Plugin.Log.LogInfo($"[additive] {Name(__instance)}: initializers to do {toDo} at f{Engine.AppliedFrame}");
            if (toDo > 0) return true;
            var l = __instance;
            return !Gate(l, "init", () => l.ValidateInitializationDone());
        }

        // The load is over (not held here): the loader's next request (it is pooled) names a new load.
        private static void Postfix(LevelLoader __instance)
        {
            if (!Ours(__instance)) return;
            try
            {
                if (__instance.initializationsToDo > 0) return;
                var ptr = __instance.Pointer;
                if (!_held.Values.Any(h => h.Loader == ptr)) _requests.Remove(ptr);
            }
            catch { }
        }
    }
}
