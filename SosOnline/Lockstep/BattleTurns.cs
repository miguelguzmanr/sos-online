using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SosOnline.Game;
using SosOnline.Net;

namespace SosOnline.Lockstep;

/// <summary>
/// "Your character, your turn" (a host setting, fixed for the session): in a battle each player gets the turns of
/// the character they control outside battles, instead of a player picked at random for each turn.
///
/// How the game does it (traced 2026-11-26): when a battle starts it unassigns every co-op character. For each
/// player turn it takes the next character from the turn queue (<c>GetNextActor</c>), picks a player at random
/// (seeded, one turn per player per round), makes every party actor answer to that player and calls
/// <c>SetCurrentPlayer</c>. That player can then move the cursor to any character that still has to act.
///
/// With the rule on, <c>SetCurrentPlayer</c> gets the owner of the character the turn starts on instead (or the game's
/// pick when nobody playing owns it), the cursor starts on a character of the current player (or one nobody owns),
/// and moving it skips characters other players own. Everything reads synced state (who controls which character
/// outside battles, the battle's turn queue), so every PC makes the same choice on the same frame.
///
/// The host can also give a turn away (<see cref="Apply"/>): take it over, or pass it to the next player. Those come
/// from the host's pause screen as scheduled commands (<c>Msg.TurnCommand</c>); the receiver may control any
/// character for the rest of that turn.
/// </summary>
internal static class BattleTurns
{
    /// <summary>The session's rule (the host's setting when lockstep started; off outside a session).</summary>
    public static bool OwnTurns { get; private set; }

    public const byte CmdTakeOver = 1, CmdPass = 2;

    private static Session S => Session.Instance;
    private static bool Running => S.Engine.Running;
    private static int Frame => Running ? S.Engine.AppliedFrame : -1;

    /// <summary>Character id each slot controls (or last controlled) outside battles.</summary>
    private static readonly string[] Owner = new string[InputOverride.SlotCount];

    /// <summary>Slot whose turn it is now (-1: none, an enemy's turn or no battle).</summary>
    public static int CurrentSlot { get; private set; } = -1;
    /// <summary>Frame the current slot's turn started on (for the turn banner).</summary>
    public static int TurnFrame { get; private set; } = -1;
    /// <summary>The current player is choosing a command (moderation commands only act then).</summary>
    public static bool Choosing { get; private set; }

    /// <summary>Slot the host gave this turn to (-1: none). Ownership doesn't restrict it until the turn ends.</summary>
    private static int _forced = -1;
    /// <summary>Inside a host command's own <c>SetCurrentPlayer</c>: every other call starts a new turn and ends the command.</summary>
    private static bool _applying;
    private static CombatActor _nextActor;
    private static bool _reentrant;

    public static void SetRule(bool on)
    {
        if (OwnTurns != on) Plugin.Log.LogInfo($"[turns] your character, your turn: {(on ? "on" : "off")}");
        OwnTurns = on;
    }

    public static void Reset()
    {
        Array.Clear(Owner, 0, Owner.Length);
        CurrentSlot = -1;
        TurnFrame = -1;
        Choosing = false;
        _forced = -1;
        _nextActor = null;
    }

    public static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.SetCurrentPlayer), nameof(CurrentPlayerPrefix), true);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.SetSelectedActor), nameof(SelectedActorPrefix), true);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.GetNavigationActor), nameof(NavigationPostfix), false);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.GetNextActor), nameof(NextActorPostfix), false);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.EnterPlayerSelectBattleCommandState), nameof(ChoosingPostfix), false);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.AwardMPOnPlayerCombatMoveConfirmation), nameof(ChosenPrefix), true);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.OnPlayerCharacterTurnDone), nameof(TurnDonePrefix), true);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.OnEnemiesTurn), nameof(TurnDonePrefix), true);
    }

    private static void Patch(Harmony harmony, Type type, string method, string patch, bool prefix)
    {
        var target = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == method);
        if (target == null) { Plugin.Log.LogWarning($"[turns] {type.Name}.{method} not found"); return; }
        if (NativeCode.IsFolded(target)) { Plugin.Log.LogWarning($"[turns] {type.Name}.{method} is folded with other methods; not patched"); return; }
        var hm = new HarmonyMethod(typeof(BattleTurns).GetMethod(patch, BindingFlags.Static | BindingFlags.NonPublic));
        if (prefix) harmony.Patch(target, prefix: hm); else harmony.Patch(target, postfix: hm);
    }

    // ---------------------------------------------------------------- ownership

    /// <summary>Every applied frame: who controls which character, read outside battles only (the game unassigns them in one).</summary>
    public static void OnFrame(int frame)
    {
        if (GameBridge.InBattle) return;
        if (CurrentSlot >= 0 || _forced >= 0) { CurrentSlot = -1; _forced = -1; Choosing = false; }
        for (int slot = 0; slot < Owner.Length; slot++)
        {
            if (!S.Engine.InSessionAt(slot, frame)) { Owner[slot] = null; continue; }
            string id = GameBridge.CharacterOf(slot);
            if (string.IsNullOrEmpty(id) || id == Owner[slot]) continue; // no character right now: keep the last one
            for (int other = 0; other < Owner.Length; other++) if (other != slot && Owner[other] == id) Owner[other] = null;
            Owner[slot] = id;
            Plugin.Log.LogInfo($"[turns] f{frame} P{slot + 1} owns {id}");
        }
    }

    /// <summary>The slot that owns this actor's character and is playing, or -1.</summary>
    private static int OwnerOf(CombatActor actor)
    {
        string id = IdOf(actor);
        if (string.IsNullOrEmpty(id)) return -1;
        for (int slot = 0; slot < Owner.Length; slot++)
            if (Owner[slot] == id && S.Engine.InSessionAt(slot, Frame) && GameBridge.IsPlaying(slot)) return slot;
        return -1;
    }

    private static string IdOf(CombatActor actor)
    {
        try
        {
            var p = actor?.TryCast<PlayerCombatActor>();
            return p?.characterDefinitionId?.characterId ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Party actors that still act this round, in the battle's own order.</summary>
    private static List<PlayerCombatActor> Ready(EncounterController ctrl)
    {
        var list = new List<PlayerCombatActor>();
        try
        {
            var actors = GameBridge.Combat?.currentEncounter?.sortedPlayerActors;
            var queue = ctrl.combatTurnsQueue;
            if (actors == null || queue == null) return list;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a != null && !a.Dead && queue.Contains(a)) list.Add(a);
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[turns] ready actors: {e.Message}"); }
        return list;
    }

    private static bool Active(EncounterController ctrl) =>
        OwnTurns && Running && !_reentrant && NativeCode.Is<EncounterController>(ctrl) && S.Engine.Occupied.Count(o => o) > 1;

    /// <summary>The current player may act with this character: theirs, nobody's, or any after the host gave them the turn.</summary>
    private static bool MayControl(int slot, CombatActor actor)
    {
        if (_forced >= 0) return true;
        int owner = OwnerOf(actor);
        return owner < 0 || owner == slot;
    }

    // ---------------------------------------------------------------- patches

    private static void NextActorPostfix(EncounterController __instance, CombatActor __result)
    {
        if (Running && NativeCode.Is<EncounterController>(__instance)) _nextActor = __result;
    }

    private static void CurrentPlayerPrefix(EncounterController __instance, ref SinglePlayerPlusPlayer __0)
    {
        if (!Running || !NativeCode.Is<EncounterController>(__instance)) return;
        // The game's own call starts the next turn: a host's take over or pass lasted only the turn it was given in.
        if (!_applying) _forced = -1;
        int chosen = GameBridge.SlotOf(__0?.Player);
        int target = chosen;
        string why = "the game's pick";
        try
        {
            if (_forced >= 0) { target = _forced; why = "given by the host"; }
            else if (Active(__instance))
            {
                var ready = Ready(__instance);
                int nextOwner = _nextActor != null ? OwnerOf(_nextActor) : -1;
                if (nextOwner >= 0) { target = nextOwner; why = $"owns {IdOf(_nextActor)}"; }
                else if (chosen >= 0 && !ready.Any(a => MayControl(chosen, a)))
                {
                    // Nothing left this round that the game's pick may control: the owner of the first one that acts.
                    int first = ready.Select(OwnerOf).FirstOrDefault(o => o >= 0, -1);
                    if (first >= 0) { target = first; why = "has a character left"; }
                }
            }
            if (target != chosen && target >= 0)
            {
                var player = GameBridge.CoopPlayer(target);
                if (player != null && player.Playing)
                {
                    __0 = player;
                    // The actors answer to the current player (the game set them to its pick just before).
                    var actors = GameBridge.Combat?.currentEncounter?.playerActors;
                    if (actors != null) for (int i = 0; i < actors.Count; i++) actors[i]?.SetPlayer(player.Player);
                }
                else target = chosen;
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[turns] current player: {e.Message}"); target = chosen; }
        if (target != CurrentSlot || TurnFrame < 0) TurnFrame = Frame;
        CurrentSlot = target;
        Plugin.Log.LogInfo($"[turns] f{Frame} turn of P{target + 1}{(target != chosen ? $" ({why}; the game picked P{chosen + 1})" : "")}");
    }

    private static void SelectedActorPrefix(EncounterController __instance, ref CombatActor __0)
    {
        if (!Active(__instance) || CurrentSlot < 0 || __0 == null || MayControl(CurrentSlot, __0)) return;
        try
        {
            var ready = Ready(__instance);
            var mine = ready.FirstOrDefault(a => OwnerOf(a) == CurrentSlot) ?? ready.FirstOrDefault(a => OwnerOf(a) < 0);
            if (mine == null) return;
            Plugin.Log.LogInfo($"[turns] f{Frame} cursor on {IdOf(mine)} (P{CurrentSlot + 1}'s) instead of {IdOf(__0)}");
            __0 = mine;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[turns] selected actor: {e.Message}"); }
    }

    /// <summary>Moving the cursor skips characters another player owns (back where it was when none is left).</summary>
    private static void NavigationPostfix(EncounterController __instance, PlayerCombatActor __0, int __1, bool __2, ref PlayerCombatActor __result)
    {
        if (!Active(__instance) || CurrentSlot < 0 || __result == null || MayControl(CurrentSlot, __result)) return;
        _reentrant = true;
        try
        {
            var next = __result;
            for (int i = 0; i < 4 && next != null && !MayControl(CurrentSlot, next); i++)
            {
                var after = __instance.GetNavigationActor(next, __1, __2);
                if (after == null || after.Pointer == next.Pointer || (__0 != null && after.Pointer == __0.Pointer)) { next = __0; break; }
                next = after;
            }
            __result = next != null && MayControl(CurrentSlot, next) ? next : __0;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[turns] navigation: {e.Message}"); __result = __0; }
        finally { _reentrant = false; }
    }

    private static void ChoosingPostfix(EncounterController __instance)
    {
        if (Running && NativeCode.Is<EncounterController>(__instance)) Choosing = true;
    }

    private static void ChosenPrefix(EncounterController __instance)
    {
        if (Running && NativeCode.Is<EncounterController>(__instance)) Choosing = false;
    }

    private static void TurnDonePrefix(EncounterController __instance)
    {
        if (!Running || !NativeCode.Is<EncounterController>(__instance)) return;
        Choosing = false;
        _forced = -1;
        CurrentSlot = -1;
    }

    // ---------------------------------------------------------------- host commands

    /// <summary>The slot <see cref="CmdPass"/> would give the turn to: the next player in session after the current one.</summary>
    public static int NextSlot()
    {
        if (CurrentSlot < 0) return -1;
        for (int i = 1; i < Owner.Length; i++)
        {
            int slot = (CurrentSlot + i) % Owner.Length;
            if (S.Engine.InSessionAt(slot, Frame) && GameBridge.IsPlaying(slot)) return slot;
        }
        return -1;
    }

    /// <summary>A host command can act now: a player is choosing a command in a battle.</summary>
    public static bool CanCommand => Running && GameBridge.InBattle && Choosing && CurrentSlot >= 0;

    /// <summary>
    /// Runs on the scheduled frame on every PC (<c>Msg.TurnCommand</c>): gives the current turn to the host
    /// (<see cref="CmdTakeOver"/>) or to the next player (<see cref="CmdPass"/>). Nothing happens when no player is
    /// choosing a command any more; every PC sees that the same way.
    /// </summary>
    public static void Apply(byte cmd, int frame)
    {
        int target = cmd == CmdTakeOver ? 0 : NextSlot();
        string what = cmd == CmdTakeOver ? "take over" : "pass";
        if (!CanCommand || target < 0 || target == CurrentSlot)
        {
            Plugin.Log.LogInfo($"[turns] f{frame} host's {what}: nothing to do (choosing {Choosing}, P{CurrentSlot + 1} -> P{target + 1})");
            return;
        }
        try
        {
            var ctrl = GameBridge.Combat?.currentEncounter?.controller;
            var player = GameBridge.CoopPlayer(target);
            if (ctrl == null || player == null || !player.Playing) { Plugin.Log.LogInfo($"[turns] f{frame} host's {what}: no player {target + 1}"); return; }
            int from = CurrentSlot;
            _forced = target;
            var selected = ctrl.SelectedActor;
            _applying = true;
            try { ctrl.SetCurrentPlayer(player); }
            finally { _applying = false; }
            if (selected != null) ctrl.SetSelectedActor(selected);
            Plugin.Log.LogInfo($"[turns] f{frame} host's {what}: P{from + 1}'s turn goes to P{target + 1}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[turns] f{frame} host's {what} failed: {e.Message}"); }
    }
}
