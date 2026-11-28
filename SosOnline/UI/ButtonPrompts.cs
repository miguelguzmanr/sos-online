using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Rewired;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;

namespace SosOnline.UI;

/// <summary>
/// Button prompts (the glyphs in dialogue, menus, coachmarks, battle hints and the game menu's controls page) show
/// this PC's own device during a session, as they show the player's own device in local play.
///
/// How the game picks a glyph: every prompt asks a Rewired player for <c>controllers.GetLastActiveController()</c>
/// and draws that controller's button, falling back to the keyboard when the player has none. The player it asks is
/// <c>InputManager.FirstPlayer</c> (P1) or <c>DefaultPlayer</c> for most prompts and dialogue text, the UI player
/// (<c>SinglePlayerPlusManager</c>) for prompts that follow whoever drives the menu, or the acting character's
/// player for battle hints.
///
/// Why a session broke it: <see cref="ControllerRouting"/> puts every device of this PC on its own slot's player (and
/// the System player). Every other player has no device here, so a prompt asking about one fell back to the
/// keyboard: a guest with a gamepad saw keyboard keys in all of P1's prompts, and a host saw them whenever a friend
/// drove the menu or took a battle turn.
///
/// The fix: while one of the game's prompt methods runs (<see cref="Scope"/>), asking a player that isn't this PC's
/// for its last active controller answers with this PC's player's. Glyphs are local to each PC and nothing in the
/// simulation reads them; Rewired's last active controller is already this PC's hardware, so nothing synced changes.
/// Calls from anywhere else (input consumption, rumble) get the game's own answer. Outside a session nothing changes.
/// </summary>
internal static class ButtonPrompts
{
    /// <summary>Prompt methods running now (nested calls count), valid only in <see cref="_scopeFrame"/>.</summary>
    private static int _depth;
    private static int _scopeFrame = -1;
    private static string _scopeName;
    private static bool _inner;

    private static bool InScope => _depth > 0 && _scopeFrame == UnityEngine.Time.frameCount;

    private static readonly Dictionary<MethodBase, string> Names = new();

    /// <summary>The game's methods that pick a controller for a glyph (from their native calls).</summary>
    [HarmonyPatch]
    private static class Scope
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            var direct = new (Type type, string[] methods)[]
            {
                (typeof(RemappableSpriteButton), new[] { nameof(RemappableSpriteButton.GetController) }),
                (typeof(InputPrompt), new[] { nameof(InputPrompt.GetController), nameof(InputPrompt.SetControllerType), nameof(InputPrompt.TryGetFromRewiredAction) }),
                (typeof(CoachmarkAction), new[] { nameof(CoachmarkAction.GetController) }),
                (typeof(CoachmarkHoldAction), new[] { nameof(CoachmarkHoldAction.GetController) }),
                (typeof(CombatInfoBox), new[] { nameof(CombatInfoBox.ShowSpecialMoveInfo) }),
                (typeof(UIControlsTabPage), new[] { nameof(UIControlsTabPage.OnTabShown) }),
                (typeof(SunboyShootQTESunballState), new[] { nameof(SunboyShootQTESunballState.BeginDisplayInstructions) }),
                (typeof(InputManager), new[] { nameof(InputManager.GetControllerForButtonImageTag), nameof(InputManager.GetTextButtonImageTag), nameof(InputManager.GetLastUsedController) }),
            };
            // Battle hints typed by coroutines ("Press … to …").
            var coroutines = new (Type type, string nested)[]
            {
                (typeof(PlayerCombatMove), "_ShowInstructions_d__"),
                (typeof(MoonrangSpecialMove), "_ShowInstructionsAndThrowMoonrangCoroutine_d__"),
                (typeof(PrototypeZeroCombatMove), "_DisplayNextMoveHintCoroutine_d__"),
                (typeof(PotionKick), "_WaitForSeraiReadyAndThrowPotionsCoroutine_d__"),
            };
            var found = new List<MethodBase>();
            foreach (var (type, methods) in direct)
                foreach (var name in methods)
                {
                    var ms = type.GetMethods(all).Where(m => m.Name == name).ToList();
                    if (ms.Count == 0) Plugin.Log.LogWarning($"[prompts] {type.Name}.{name} not found");
                    foreach (var m in ms) Add(found, m, $"{type.Name}.{name}");
                }
            foreach (var (type, nested) in coroutines)
            {
                var t = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(n => n.Name.StartsWith(nested, StringComparison.Ordinal));
                var m = t?.GetMethod("MoveNext", all);
                if (m == null) { Plugin.Log.LogWarning($"[prompts] {type.Name}.{nested}… not found"); continue; }
                Add(found, m, $"{type.Name}.{t.Name}");
            }
            return found;
        }

        private static void Add(List<MethodBase> found, MethodBase m, string name)
        {
            // A detour on folded code would also run for unrelated methods (NativeCode.AuditPatches would remove it).
            if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[prompts] {name} is folded; its prompts keep the game's own device choice"); return; }
            Names[m] = name;
            found.Add(m);
        }

        private static void Prefix(MethodBase __originalMethod)
        {
            int frame = UnityEngine.Time.frameCount;
            if (_scopeFrame != frame) { _scopeFrame = frame; _depth = 0; }
            if (_depth++ == 0) _scopeName = Names.TryGetValue(__originalMethod, out var n) ? n : __originalMethod.Name;
        }

        private static void Postfix()
        {
            if (_depth > 0) _depth--;
        }
    }

    [HarmonyPatch(typeof(Player.ControllerHelper), nameof(Player.ControllerHelper.GetLastActiveController), new Type[0])]
    private static class LastActive
    {
        private static bool Prefix(Player.ControllerHelper __instance, ref Controller __result)
        {
            if (_inner || !InputOverride.Active || !InScope) return true;
            if (!NativeCode.Is<Player.ControllerHelper>(__instance)) return true;
            try
            {
                var mine = GameBridge.PlayerForSlot(Session.Instance.LocalSlot);
                var helper = mine?.controllers;
                if (helper == null || helper.Pointer == __instance.Pointer) return true;
                Controller result;
                _inner = true;
                try { result = helper.GetLastActiveController(); }
                finally { _inner = false; }
                // Nothing used yet: the pad if this PC has one, else the keyboard (the game's own fallback order).
                if (result == null)
                {
                    var joysticks = ReInput.controllers.Joysticks.ToList();
                    result = helper.joystickCount > 0 && joysticks.Count > 0 ? joysticks[0].Cast<Controller>() : ReInput.controllers.Keyboard?.Cast<Controller>();
                }
                if (result == null) return true;
                __result = result;
                LogChange(__instance, mine, result);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[prompts] {_scopeName}: {e.Message}");
                return true;
            }
        }
    }

    /// <summary>Last redirect per prompt method; logged when it changes (the battle asks every frame).</summary>
    private static readonly Dictionary<string, (IntPtr asked, IntPtr shown)> Last = new();

    private static void LogChange(Player.ControllerHelper asked, Player mine, Controller shown)
    {
        string key = _scopeName ?? "?";
        var now = (asked.Pointer, shown.Pointer);
        if (Last.TryGetValue(key, out var was) && was == now) return;
        Last[key] = now;
        string askedId = "?";
        try
        {
            foreach (var p in ReInput.players.AllPlayers.ToList())
                if (p?.controllers != null && p.controllers.Pointer == asked.Pointer) { askedId = p.id.ToString(); break; }
        }
        catch { }
        Plugin.Log.LogInfo($"[prompts] {key}: player {askedId} has no device here; shows player {mine.id}'s {shown.type} ({shown.name})");
    }
}
