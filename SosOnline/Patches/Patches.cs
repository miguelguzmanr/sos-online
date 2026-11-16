using HarmonyLib;
using Rewired;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;

namespace SosOnline.Patches;

/// <summary>Drives the lockstep engine from Rewired's own Update, right after hardware has been polled.</summary>
[HarmonyPatch(typeof(InputManager_Base), nameof(InputManager_Base.DoUpdate))]
internal static class RewiredUpdatePatch
{
    private static void Postfix(InputManager_Base __instance, UpdateLoopType __0)
    {
        if (__0 != UpdateLoopType.Update || !NativeCode.Is<InputManager_Base>(__instance)) return;
        if (!ActionTable.Ready) ActionTable.TryBuild();
        Session.Instance.Engine.Tick();
    }
}

/// <summary>
/// While lockstep runs, input devices are routed by the mod. The game's automatic (re)assignment, which reacts to
/// local hardware events, would otherwise change game state on one peer only.
/// </summary>
internal static class InputManagerPatches
{
    private static bool Running => InputOverride.Active;

    /// <summary>Skip the original only for a real object of that class (never for code-sharing strangers).</summary>
    private static bool Skip<T>(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase instance) where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        => Running && NativeCode.Is<T>(instance);

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.AssignAllControllersToSystemPlayer))]
    private static class P1 { private static bool Prefix(InputManager __instance) => !Skip<InputManager>(__instance); }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.AutoAssignUnassignedControllers))]
    private static class P2 { private static bool Prefix(InputManager __instance) => !Skip<InputManager>(__instance); }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.StartAssignFirstPlayerJoystickCoroutine))]
    private static class P3 { private static bool Prefix(InputManager __instance) => !Skip<InputManager>(__instance); }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.OnControllerConnected))]
    private static class P4
    {
        private static bool Prefix(InputManager __instance, ControllerStatusChangedEventArgs __0)
        {
            if (!Skip<InputManager>(__instance)) return true;
            // Hot-plugged pads still work for the local player, without the game noticing.
            try
            {
                if (__0 != null && __0.controllerType == ControllerType.Joystick)
                    ControllerRouting.RouteNewJoystick(GameBridge.PlayerForSlot(Session.Instance.LocalSlot), ReInput.controllers.GetJoystick(__0.controllerId));
            }
            catch { }
            return false;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.OnControllerPreDisconnect))]
    private static class P5 { private static bool Prefix(InputManager __instance) => !Skip<InputManager>(__instance); }

    [HarmonyPatch(typeof(SinglePlayerPlusManager), nameof(SinglePlayerPlusManager.OnControllerDisconnected))]
    private static class P6 { private static bool Prefix(SinglePlayerPlusManager __instance) => !Skip<SinglePlayerPlusManager>(__instance); }

    [HarmonyPatch(typeof(TitleScreen), nameof(TitleScreen.AssignFirstPlayerController))]
    private static class P7 { private static bool Prefix(TitleScreen __instance) => !Skip<TitleScreen>(__instance); }
}

/// <summary>
/// Manual saves never finished in lockstep: after Yes on "Overwrite this save data?", SaveManager.UpdateDataAsync
/// started and the write never followed, on every PC but (in real sessions) the one whose player pressed Yes. The
/// guest then stayed paused in the save menu and re-synced in a loop (2026-11-15).
/// Found by bisecting in tests/save-sync.sh: with any Harmony patch on SaveSelectionScreen.ConsumeUIPopupInputs
/// (called every frame while a save-screen popup is up) the save completes on every PC, on the same frame;
/// without it, it never does. Why is not known (the game's native code can't be read from here). This empty
/// patch keeps it patched; tests/save-sync.sh checks the save completes on both PCs.
/// </summary>
[HarmonyPatch(typeof(SaveSelectionScreen), nameof(SaveSelectionScreen.ConsumeUIPopupInputs))]
internal static class SaveCompletesPatch
{
    private static void Postfix() { }
}

/// <summary>
/// The game's UI input module skips input while its window isn't focused, and the game's pop-up questions (the save
/// question among them) take their answers through it. A player alt-tabbed to chat never saw the other player's
/// answer: that PC stayed on the question while the other went on, every time someone saved (2026-11-15). During
/// lockstep the input comes from the session, not the window.
/// </summary>
[HarmonyPatch(typeof(UnityEngine.EventSystems.CustomUIInputModule), nameof(UnityEngine.EventSystems.CustomUIInputModule.ShouldIgnoreEventsOnNoFocus))]
internal static class UiFocusPatch
{
    private static bool Prefix(UnityEngine.EventSystems.CustomUIInputModule __instance, ref bool __result)
    {
        if (!InputOverride.Active || !NativeCode.Is<UnityEngine.EventSystems.CustomUIInputModule>(__instance)) return true;
        __result = false;
        return false;
    }
}

/// <summary>
/// Focus loss / Steam overlay must not pause the game on one machine only. (SteamPlatform's own IsInFocus and
/// OnGameOverlayActivated are folded with unrelated methods in the native build, so the wrapper is hooked instead.)
/// </summary>
[HarmonyPatch(typeof(PlatformManager), nameof(PlatformManager.IsInFocus))]
internal static class FocusPatch
{
    private static bool Prefix(PlatformManager __instance, ref bool __result)
    {
        if (!InputOverride.Active || !NativeCode.Is<PlatformManager>(__instance)) return true;
        __result = true;
        return false;
    }
}
