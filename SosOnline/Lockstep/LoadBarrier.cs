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
