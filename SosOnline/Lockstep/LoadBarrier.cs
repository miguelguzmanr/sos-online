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
