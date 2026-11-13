using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Rewired;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;

namespace SosOnline.UI;

/// <summary>
/// The game's "Assign Players" screen, made to work over the network.
///
/// Offline it is device-driven: a gamepad joins the screen by pressing a button (the screen polls every local device),
/// the device is assigned to a Rewired player, that player moves a cursor between characters (through Rewired
/// player input) and readies up; when everyone is ready the screen re-joins all players with their characters.
///
/// Online, local devices differ per PC, so in a session:
///  - Devices never join the screen. A session player joins it by pressing "UIAssignConfirm" on their synced
///    input; the keyboard object (present on every PC) stands in as their "device" for the screen's bookkeeping.
///  - When the screen opens, it is rebuilt identically on every peer: every session player currently playing,
///    plus whoever opened it.
///  - The game's own Rewired controller (re)assignment is ignored while a session runs (the mod routes devices),
///    so the screen's device shuffling can't move anyone's pad or keyboard.
/// The cursor, ready and the final character assignment already run on synced player input, so they stay in sync.
/// </summary>
internal static class AssignPlayersSession
{
    private static bool InSession => InputOverride.Active && Session.Instance.Engine.Running;

    // ---------------------------------------------------------------- device assignment is ours during a session

    private static bool BlockDeviceChange(Player.ControllerHelper helper) =>
        InSession && !ControllerRouting.Routing && NativeCode.Is<Player.ControllerHelper>(helper);

    [HarmonyPatch(typeof(Player.ControllerHelper), nameof(Player.ControllerHelper.AddController), typeof(Controller), typeof(bool))]
    private static class P_Add1 { private static bool Prefix(Player.ControllerHelper __instance) => !BlockDeviceChange(__instance); }

    [HarmonyPatch(typeof(Player.ControllerHelper), nameof(Player.ControllerHelper.AddController), typeof(ControllerType), typeof(int), typeof(bool))]
    private static class P_Add2 { private static bool Prefix(Player.ControllerHelper __instance) => !BlockDeviceChange(__instance); }

    [HarmonyPatch(typeof(Player.ControllerHelper), nameof(Player.ControllerHelper.RemoveController), typeof(ControllerType), typeof(int))]
    private static class P_Remove1 { private static bool Prefix(Player.ControllerHelper __instance) => !BlockDeviceChange(__instance); }

    [HarmonyPatch(typeof(Player.ControllerHelper), nameof(Player.ControllerHelper.RemoveController), typeof(Controller))]
    private static class P_Remove2 { private static bool Prefix(Player.ControllerHelper __instance) => !BlockDeviceChange(__instance); }

    [HarmonyPatch(typeof(Player.ControllerHelper), nameof(Player.ControllerHelper.ClearAllControllers))]
    private static class P_Clear { private static bool Prefix(Player.ControllerHelper __instance) => !BlockDeviceChange(__instance); }

    // ---------------------------------------------------------------- the screen

    /// <summary>Device-driven joins (a local pad pressing a button) don't exist online.</summary>
    [HarmonyPatch(typeof(AssignControllersScreen), nameof(AssignControllersScreen.AddController), typeof(Controller))]
    private static class P_DeviceJoin
    {
        private static bool Prefix(AssignControllersScreen __instance, ref bool __result)
        {
            if (!InSession || !NativeCode.Is<AssignControllersScreen>(__instance)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>Rebuild the screen's players identically on every peer.</summary>
    [HarmonyPatch(typeof(AssignControllersScreen), nameof(AssignControllersScreen.Init))]
    private static class P_Init
    {
        private static void Postfix(AssignControllersScreen __instance)
        {
            if (!InSession || !NativeCode.Is<AssignControllersScreen>(__instance)) return;
            try
            {
                var items = new List<AssignControllersScreenController>();
                for (int i = 0; i < __instance.controllerUIItems.Count; i++) items.Add(__instance.controllerUIItems[i]);
                foreach (var item in items) __instance.RemoveController(item);

                int opener = GameBridge.SlotOf(GameBridge.Coop?.GetUIInputsPlayer());
                var engine = Session.Instance.Engine;
                for (int slot = 0; slot < InputOverride.SlotCount; slot++)
                    if (engine.InSessionAt(slot, engine.AppliedFrame) && (GameBridge.IsPlaying(slot) || slot == opener))
                        AddSlot(__instance, slot);
                Plugin.Log.LogInfo($"[assign] screen opened online by P{opener + 1}; {__instance.controllerUIItems.Count} player(s) on it");
            }
            catch (Exception e) { Plugin.Log.LogError($"[assign] init failed: {e}"); }
        }
    }

    private static bool _submitting, _closeRequested;
    private static int _allReadySince = -1;

    /// <summary>
    /// Offline, the screen's Update polls raw local devices: a press joins a new device, and the close input (once
    /// everyone is ready) makes it assign everyone and close. Online, before the game's Update runs:
    ///  - a session player who isn't on the screen joins it with Confirm;
    ///  - once every player on it has been ready since an earlier frame, Confirm from any of them arms the close,
    ///    which the game's own Update then carries out (assign + close).
    /// Back from any player on the screen also asks the game to close.
    /// </summary>
    [HarmonyPatch(typeof(AssignControllersScreen), nameof(AssignControllersScreen.Update))]
    private static class P_Update
    {
        private static void Prefix(AssignControllersScreen __instance)
        {
            _closeRequested = _submitting = false;
            if (!InSession || !NativeCode.Is<AssignControllersScreen>(__instance)) return;
            try
            {
                var engine = Session.Instance.Engine;
                bool confirm = false, back = false;
                for (int slot = 0; slot < InputOverride.SlotCount; slot++)
                {
                    if (!engine.InSessionAt(slot, engine.AppliedFrame)) continue;
                    var p = GameBridge.PlayerForSlot(slot);
                    if (p == null) continue;
                    bool onScreen = IsOnScreen(__instance, slot);
                    if (p.GetButtonDown("UIAssignConfirm"))
                    {
                        if (!onScreen) AddSlot(__instance, slot);
                        else confirm = true;
                    }
                    if (onScreen && p.GetButtonDown("UIAssignBack")) back = true;
                }

                bool ready = __instance.controllerUIItems.Count > 0 && __instance.AreAllPanelsReady();
                if (!ready) _allReadySince = -1;
                else if (_allReadySince < 0) _allReadySince = engine.AppliedFrame;
                else if (confirm && engine.AppliedFrame > _allReadySince)
                {
                    _allReadySince = -1;
                    ApplyChosenPlayers(__instance);
                    _submitting = _closeRequested = true;
                }
                if (back) _closeRequested = true;
            }
            catch (Exception e) { Plugin.Log.LogError($"[assign] update failed: {e}"); }
        }

        private static void Postfix() => _closeRequested = _submitting = false;
    }

    /// <summary>The game's close check, answered from synced input only (never a local device).</summary>
    [HarmonyPatch(typeof(AssignControllersScreen), nameof(AssignControllersScreen.GetCloseScreenInput))]
    private static class P_Close
    {
        private static bool Prefix(AssignControllersScreen __instance, ref bool __result)
        {
            if (!InSession || !NativeCode.Is<AssignControllersScreen>(__instance)) return true;
            __result = _closeRequested;
            return false;
        }
    }

    /// <summary>The screen re-joined everyone; make sure local devices still drive the local slot.</summary>
    [HarmonyPatch(typeof(AssignControllersScreen), nameof(AssignControllersScreen.AssignControllers))]
    private static class P_Assign
    {
        /// <summary>During a session the screen only assigns through our synced submit (not a local device press).</summary>
        private static bool Prefix(AssignControllersScreen __instance) =>
            !InSession || _submitting || !NativeCode.Is<AssignControllersScreen>(__instance);

        private static void Postfix(AssignControllersScreen __instance)
        {
            if (!InSession || !_submitting || !NativeCode.Is<AssignControllersScreen>(__instance)) return;
            ControllerRouting.RouteLocalDevicesTo(GameBridge.PlayerForSlot(Session.Instance.LocalSlot));
            var assigned = new List<string>();
            for (int slot = 0; slot < InputOverride.SlotCount; slot++)
            {
                var c = GameBridge.CoopPlayer(slot);
                if (c != null && c.Playing) assigned.Add($"slot {slot} (P{GameBridge.SlotPlayerIndex[slot] + 1})={(c.AssignedCharacter != null ? c.AssignedCharacter.Cast<UnityEngine.Object>()?.name : "?")}");
            }
            Plugin.Log.LogInfo($"[assign] players assigned: {string.Join(", ", assigned)}");
        }
    }

    /// <summary>
    /// Offline the screen moves each device to the player whose column its cursor ends on. Devices stay put online,
    /// so the equivalent is to point each session slot at the game player of its column (deterministic: computed
    /// from the screen state, which is identical on every peer).
    /// </summary>
    private static void ApplyChosenPlayers(AssignControllersScreen screen)
    {
        var chosen = new int[InputOverride.SlotCount];
        for (int i = 0; i < chosen.Length; i++) chosen[i] = -1;
        for (int i = 0; i < screen.controllerUIItems.Count; i++)
        {
            var item = screen.controllerUIItems[i];
            int slot = GameBridge.SlotOf(item.player?.Player);
            int column = item.currentPanelIndex;
            if (slot >= 0 && column >= 0 && column < chosen.Length && Array.IndexOf(chosen, column) < 0) chosen[slot] = column;
        }
        // Slots not on the screen keep a free player number, lowest first.
        for (int slot = 0; slot < chosen.Length; slot++)
        {
            if (chosen[slot] >= 0) continue;
            int keep = GameBridge.SlotPlayerIndex[slot];
            if (Array.IndexOf(chosen, keep) < 0) { chosen[slot] = keep; continue; }
            for (int c = 0; c < chosen.Length; c++) if (Array.IndexOf(chosen, c) < 0) { chosen[slot] = c; break; }
        }
        bool changed = false;
        for (int slot = 0; slot < chosen.Length; slot++)
            if (GameBridge.SlotPlayerIndex[slot] != chosen[slot]) { GameBridge.SlotPlayerIndex[slot] = chosen[slot]; changed = true; }
        if (!changed) return;
        LockstepEngine.RemapPlayers();
        ControllerRouting.RouteLocalDevicesTo(GameBridge.PlayerForSlot(Session.Instance.LocalSlot));
        Plugin.Log.LogInfo($"[assign] slots now drive players: {string.Join(", ", Enumerable.Range(0, chosen.Length).Select(sl => $"slot {sl}→P{chosen[sl] + 1}"))}");
    }

    private static bool IsOnScreen(AssignControllersScreen screen, int slot)
    {
        var p = GameBridge.PlayerForSlot(slot);
        for (int i = 0; i < screen.controllerUIItems.Count; i++)
            if (screen.controllerUIItems[i].player?.Player?.id == p?.id) return true;
        return false;
    }

    private static void AddSlot(AssignControllersScreen screen, int slot)
    {
        var coop = GameBridge.CoopPlayer(slot);
        var standIn = ReInput.controllers.Keyboard;
        if (coop == null || standIn == null) return;
        screen.AddController(standIn.Cast<Controller>(), coop);
        Plugin.Log.LogInfo($"[assign] P{slot + 1} joined the screen");
    }
}
