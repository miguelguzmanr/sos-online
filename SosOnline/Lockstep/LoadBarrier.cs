using System;
using HarmonyLib;
using SosOnline.Game;
using SosOnline.Net;

namespace SosOnline.Lockstep;

/// <summary>
/// Loading barrier. Levels finish loading a few frames apart on different PCs (asynchronous I/O), and a PC that
/// finishes first would simulate the level for those frames on its own. During lockstep the game's last loading step,
/// <c>LevelManager.EndCurrentLevelLoading</c>, is held on every PC. The host collects the frame each PC reached it,
/// picks a release frame, and every PC runs that step on the release frame.
/// </summary>
internal static class LoadBarrier
{
    private static bool _releasing;

    /// <summary>Runs the held loading step now (called on the release frame, on every PC).</summary>
    public static void Release()
    {
        _releasing = true;
        try { GameBridge.Levels?.EndCurrentLevelLoading(); }
        finally { _releasing = false; }
    }

    /// <summary>
    /// Game time stands nearly still from the moment a level's scenes load, not only once the load reaches its end.
    /// The scenes' objects run from the frame they appear, and each PC loads them on its own frame (asynchronous I/O)
    /// and reaches the end on another: the boulder goats' animators on Mountain Trail ran 20 frames at full speed on
    /// one PC and 16 on the other before the hold, so at the release their animations were a fraction of a cycle
    /// apart and the goats later walked off on different frames (3 of 4 battle runs split, 2026-11-27).
    /// </summary>
    public static void Install()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>)
            (Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>)OnSceneLoaded;
    }

    private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        try
        {
            if (!InputOverride.Active || !Session.Instance.Engine.Running || !GameBridge.LoadingLevel) return;
            if (GameClock.HoldForLoad())
                Plugin.Log.LogInfo($"[barrier] scene {scene.name} loaded at f{Session.Instance.Engine.AppliedFrame}: game time held until the release");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[barrier] scene load hold: {e.Message}"); }
    }

    /// <summary>
    /// Idle animation timers (<see cref="IdleBreakHandler"/>) stand still until a level load is released. They start
    /// counting as soon as a level's objects exist, before the barrier, and the barrier holds each PC for a different
    /// number of frames: on Mountain Trail one PC counted 14 frames more than the other, and a boulder goat stopped to
    /// graze on that PC only (f572, 2026-11-26), which split every NPC after it.
    /// </summary>
    [HarmonyPatch(typeof(IdleBreakHandler), "LateUpdate")]
    private static class IdleBreakFreezePatch
    {
        private static bool Prefix(IdleBreakHandler __instance)
        {
            if (!InputOverride.Active || !NativeCode.Is<IdleBreakHandler>(__instance)) return true;
            return !GameBridge.LoadingLevel;
        }
    }

    [HarmonyPatch(typeof(LevelManager), nameof(LevelManager.EndCurrentLevelLoading))]
    private static class EndLoadingPatch
    {
        private static bool Prefix(LevelManager __instance)
        {
            if (_releasing || !InputOverride.Active || !NativeCode.Is<LevelManager>(__instance)) return true;
            return !Session.Instance.HoldLevelLoadEnd();
        }
    }
}
