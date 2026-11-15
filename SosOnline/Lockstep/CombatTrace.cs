using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SosOnline.Net;

namespace SosOnline.Lockstep;

/// <summary>
/// Logs the battle's turn decisions with the lockstep frame: whose turn it is, which player is "current" (picks the
/// next action in co-op), which character is selected, and which player a character answers to. A few lines per turn.
///
/// Battles went out of sync now and then (2026-11-15): one PC had P1 act on a character while the other waited for
/// P2 on the same turn. Every input matched, so the turn bookkeeping itself must differ; with these lines, both PCs'
/// logs show the first frame where it does.
/// </summary>
internal static class CombatTrace
{
    private static int Frame => Session.Instance.Engine.Running ? Session.Instance.Engine.AppliedFrame : -1;
    private static bool Active => Session.Instance.Engine.Running;

    public static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.SetCurrentPlayer), nameof(CurrentPlayer));
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.SetSelectedActor), nameof(SelectedActor));
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.ActivatePlayerTurn), nameof(PlayerTurn));
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.AdvancePlayers), nameof(Advance));
        Patch(harmony, typeof(PlayerCombatActor), nameof(PlayerCombatActor.SetPlayer), nameof(ActorPlayer));
    }

    private static void Patch(Harmony harmony, Type type, string method, string postfix)
    {
        foreach (var target in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name == method))
        {
            if (NativeCode.IsFolded(target)) { Plugin.Log.LogWarning($"[combat] {type.Name}.{method} is folded with other methods; not traced"); continue; }
            try { harmony.Patch(target, postfix: new HarmonyMethod(typeof(CombatTrace).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic))); }
            catch (Exception e) { Plugin.Log.LogWarning($"[combat] {type.Name}.{method} not traced: {e.Message}"); }
        }
    }

    private static string Name(CombatActor a)
    {
        try { return a == null ? "none" : a.gameObject.name.Replace("PlayableCharacter_", "").Replace("(Clone)", ""); }
        catch { return "?"; }
    }

    private static string Who(Rewired.Player p)
    {
        try { return p == null ? "none" : $"player {p.id} (slot {Game.GameBridge.SlotOf(p)})"; }
        catch { return "?"; }
    }

    private static void CurrentPlayer(SinglePlayerPlusPlayer __0)
    {
        if (!Active) return;
        try { Plugin.Log.LogInfo($"[combat] f{Frame} current player: {(__0 == null ? "none" : $"P{__0.Index + 1}, {Who(__0.Player)}")}"); } catch { }
    }

    private static void SelectedActor(CombatActor __0)
    {
        if (!Active) return;
        Plugin.Log.LogInfo($"[combat] f{Frame} selected: {Name(__0)}");
    }

    private static void PlayerTurn(CombatActor __0)
    {
        if (!Active) return;
        Plugin.Log.LogInfo($"[combat] f{Frame} turn: {Name(__0)}");
    }

    private static void Advance()
    {
        if (!Active) return;
        Plugin.Log.LogInfo($"[combat] f{Frame} players advance");
    }

    private static void ActorPlayer(PlayerCombatActor __instance, Rewired.Player __0)
    {
        if (!Active) return;
        Plugin.Log.LogInfo($"[combat] f{Frame} {Name(__instance)} answers to {Who(__0)}");
    }
}
