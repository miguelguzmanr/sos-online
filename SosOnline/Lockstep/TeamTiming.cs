using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SosOnline.Game;
using SosOnline.Net;
using SosOnline.UI;

namespace SosOnline.Lockstep;

/// <summary>
/// Team Timing (a host setting, changeable during co-op from the next battle on): whose presses count for a timed hit
/// or block.
///
/// The game's own way (Original): every timed hit and block collects one result per player (<c>TeamQTEResult</c>),
/// and the best one counts, so anyone's press can land anyone's hit ("team Timed Hit or Block" in the game's Feats).
/// Hits Only keeps that for hits; a block counts only from the player who controls the blocking character. None also
/// limits hits: the player who has the turn times its attacks; a combo is timed by the players of its characters, and
/// by a player who got a turn passed to them that round. Characters nobody plays stay team-timed in every mode.
///
/// Two layers, both on synced state only, so every PC decides the same on the same frame:
///  - The verdict: other players' entries in the game's result become "no press" (<see cref="TimingCompensation"/>).
///  - What the players see: while a limited timed input runs, Interact reads as not pressed for the other players
///    (<see cref="Hides"/>), so their presses show no block pose, star or sound. During an attack that covers the
///    whole move (every QTE of a skill or combo); during an enemy's turn it applies inside each character's block
///    handler, so one press blocks only for the presser's own character.
/// </summary>
internal static class TeamTiming
{
    public enum Modes : byte { Original = 0, HitsOnly = 1, None = 2 }

    /// <summary>The session's rule (the host's setting; Original outside a session).</summary>
    public static Modes Mode { get; private set; } = Modes.Original;
    /// <summary>The rule of the battle running now: <see cref="Mode"/> as it was when the battle started.</summary>
    public static Modes Battle { get; private set; } = Modes.Original;

    private static Session S => Session.Instance;
    private static bool Running => S.Engine.Running;
    private static int Frame => Running ? S.Engine.AppliedFrame : -1;

    /// <summary>Slots a turn was passed to (<see cref="BattleTurns.CmdPass"/>), with the round it happened in.</summary>
    private static readonly Dictionary<int, int> PassedIn = new();
    /// <summary>Slots that may time the attack running now (null: everyone).</summary>
    private static HashSet<int> _attack;
    /// <summary>Slots that may press inside the timed input running now (null: no limit). Set around one handler.</summary>
    [ThreadStatic] private static HashSet<int> _context;
    private static int _interact = -2;

    public static void SetRule(Modes mode)
    {
        if (Mode != mode) Plugin.Log.LogInfo($"[teamtiming] rule {mode}");
        Mode = mode;
        if (!GameBridge.InBattle) Battle = mode;
    }

    public static void Reset()
    {
        PassedIn.Clear();
        _attack = null;
        _context = null;
        Battle = Mode;
    }

    /// <summary>On the scheduled frame on every PC (<c>Msg.TeamTiming</c>): the host changed the setting during co-op.</summary>
    public static void Apply(Modes mode, int frame)
    {
        bool changed = mode != Mode;
        SetRule(mode);
        Plugin.Log.LogInfo($"[teamtiming] f{frame} the host set {mode}{(GameBridge.InBattle ? " (from the next battle)" : "")}");
        if (changed && !S.CatchingUp) Toast.Show($"{SettingLabels.TeamTiming}: {SettingLabels.TeamTimingValue(mode)} from the next battle");
    }

    public static void Install(Harmony harmony)
    {
        // Every player move starts here (AwardMPOnPlayerCombatMoveConfirmation missed 2 of 5 in a test battle).
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.DoCombatMove), nameof(MoveStartedPrefix), true);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.OnPlayerCharacterTurnDone), nameof(AttackDonePrefix), true);
        Patch(harmony, typeof(EncounterController), nameof(EncounterController.OnEnemiesTurn), nameof(AttackDonePrefix), true);
        Patch(harmony, typeof(TimedBlockHandler), nameof(TimedBlockHandler.IUpdatableUpdate), nameof(BlockUpdatePrefix), true);
        Patch(harmony, typeof(TimedBlockHandler), nameof(TimedBlockHandler.IUpdatableUpdate), nameof(BlockUpdatePostfix), false);
        Patch(harmony, typeof(TimedHitPlayerUI), nameof(TimedHitPlayerUI.AddResult), nameof(PanelRowPostfix), false);
        Patch(harmony, typeof(TimedHitUI), nameof(TimedHitUI.ClearUIs), nameof(PanelClearPostfix), false);
    }

    private static void Patch(Harmony harmony, Type type, string method, string patch, bool prefix)
    {
        var target = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == method);
        if (target == null) { Plugin.Log.LogWarning($"[teamtiming] {type.Name}.{method} not found"); return; }
        if (NativeCode.IsFolded(target)) { Plugin.Log.LogWarning($"[teamtiming] {type.Name}.{method} is folded with other methods; not patched"); return; }
        var hm = new HarmonyMethod(typeof(TeamTiming).GetMethod(patch, BindingFlags.Static | BindingFlags.NonPublic));
        if (prefix) harmony.Patch(target, prefix: hm); else harmony.Patch(target, postfix: hm);
    }

    /// <summary>Every applied frame: a battle takes the rule it starts with.</summary>
    public static void OnFrame(int frame)
    {
        if (GameBridge.InBattle) return;
        if (Battle != Mode) Plugin.Log.LogInfo($"[teamtiming] f{frame} battles now use {Mode}");
        Battle = Mode;
        PassedIn.Clear();
        _attack = null;
    }

    private static bool Limited => Running && S.Engine.Occupied.Count(o => o) > 1;

    // ---------------------------------------------------------------- who may press

    /// <summary>Slots whose presses count for this block (null: everyone).</summary>
    public static HashSet<int> ForBlock(PlayerCombatActor actor)
    {
        if (!Limited || Battle == Modes.Original) return null;
        int owner = BattleTurns.OwnerSlot(IdOf(actor));
        return owner < 0 ? null : new HashSet<int> { owner };
    }

    /// <summary>Slots whose presses count for the attack running now (null: everyone).</summary>
    public static HashSet<int> ForAttack() => Limited && Battle == Modes.None ? _attack : null;

    /// <summary>A player move starts (the player who has the turn chose it, BattleTurns).</summary>
    private static void MoveStartedPrefix(EncounterController __instance)
    {
        if (!Running || !NativeCode.Is<EncounterController>(__instance)) return;
        _attack = null;
        if (!Limited || Battle != Modes.None) return;
        try
        {
            var move = __instance.SelectedCombatMove;
            var actor = move?.mainActor?.TryCast<PlayerCombatActor>();
            if (actor == null) return; // not a party member's move
            string actorId = IdOf(actor);
            // Party AI stays team-timed: a character nobody plays, unless the host gave its turn to someone.
            int slot = BattleTurns.CurrentSlot;
            if (slot < 0 || (BattleTurns.OwnerSlot(actorId) < 0 && !BattleTurns.Given)) return;
            var allowed = new HashSet<int> { slot };
            bool combo = move != null && (move.comboPointCost > 0 || move.isUlt);
            if (combo)
            {
                foreach (string id in ComboCharacters(move, actorId))
                {
                    int owner = BattleTurns.OwnerSlot(id);
                    if (owner >= 0) allowed.Add(owner);
                }
                int round = Round(__instance);
                foreach (var (s, r) in PassedIn) if (r == round) allowed.Add(s);
            }
            _attack = allowed;
            Plugin.Log.LogInfo($"[teamtiming] f{Frame} {(combo ? "combo" : "move")} by {actorId}: timed by {string.Join(", ", allowed.OrderBy(s => s).Select(s => $"P{s + 1}"))}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[teamtiming] move confirmed: {e.Message}"); _attack = null; }
    }

    private static void AttackDonePrefix(EncounterController __instance)
    {
        if (Running && NativeCode.Is<EncounterController>(__instance)) _attack = null;
    }

    /// <summary>BattleTurns gave the current turn to <paramref name="slot"/> by a pass (synced: on the command's frame).</summary>
    public static void OnPassed(int slot)
    {
        try { PassedIn[slot] = Round(GameBridge.Combat?.currentEncounter?.controller); } catch { PassedIn[slot] = -1; }
    }

    private static int Round(EncounterController ctrl)
    {
        try { return ctrl?.roundElapsed ?? -1; } catch { return -1; }
    }

    /// <summary>
    /// The characters a combo needs, from the game's move definition (the one whose prefab the selected move runs);
    /// every party member when the definition isn't found. Read from game data, the same on every PC.
    /// </summary>
    private static IEnumerable<string> ComboCharacters(SelectedCombatMove move, string actorId)
    {
        var ids = new List<string> { actorId };
        bool found = false;
        try
        {
            var prefab = move.combatMovePrefab;
            if (prefab != null)
                foreach (var def in UnityEngine.Resources.FindObjectsOfTypeAll<PlayerCombatMoveDefinition>())
                {
                    if (def == null || def.combatMovePrefab == null || def.combatMovePrefab.Pointer != prefab.Pointer) continue;
                    var req = def.requiredCharacters;
                    if (req != null) for (int i = 0; i < req.Count; i++) if (req[i] != null) ids.Add(req[i].characterId);
                    found = true;
                    break;
                }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[teamtiming] combo characters: {e.Message}"); }
        if (!found)
        {
            try
            {
                var actors = GameBridge.Combat?.currentEncounter?.playerActors;
                if (actors != null) for (int i = 0; i < actors.Count; i++) ids.Add(IdOf(actors[i]));
            }
            catch { }
        }
        return ids.Where(id => !string.IsNullOrEmpty(id)).Distinct();
    }

    public static string IdOf(CombatActor actor)
    {
        try { return actor?.TryCast<PlayerCombatActor>()?.characterDefinitionId?.characterId ?? ""; }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- what the players see

    private static void BlockUpdatePrefix(TimedBlockHandler __instance)
    {
        if (!Limited || Battle == Modes.Original || !NativeCode.Is<TimedBlockHandler>(__instance)) return;
        try { _context = ForBlock(__instance.combatActor); } catch { _context = null; }
    }

    private static void BlockUpdatePostfix() => _context = null;

    // ---------------------------------------------------------------- the timed-hit panel

    /// <summary>The players whose presses counted for the hit or block graded last, and its frame.</summary>
    private static (HashSet<int> allowed, int frame) _lastNoted;

    /// <summary>TimingCompensation limited a result to <paramref name="allowed"/> (the panel hides the others).</summary>
    public static void NoteLimited(TeamQTEResult result, HashSet<int> allowed)
    {
        if (result == null || allowed == null) return;
        _lastNoted = (allowed, Frame);
    }

    /// <summary>Panel rows hidden because their player's press didn't count; shown again when it counts or the panel clears.</summary>
    private static readonly HashSet<IntPtr> HiddenRows = new();

    /// <summary>
    /// The game's timed-hit panel (right side, a row per player) showed a cross for a player whose press didn't count,
    /// which reads as a miss. Their row is hidden instead, and comes back with the first result that is theirs to
    /// time. Only drawn: nothing synced reads the panel.
    /// </summary>
    private static void PanelRowPostfix(TimedHitPlayerUI __instance, QTEResult __0)
    {
        if (__0 == null || __0.owner == null || !NativeCode.Is<TimedHitPlayerUI>(__instance)) return;
        try
        {
            var go = __instance.gameObject;
            bool hide = false;
            if (Limited && Battle != Modes.Original && __0.result == EQTEResult.FailDidNoPress)
            {
                var allowed = _lastNoted.allowed != null && Frame - _lastNoted.frame <= 30 ? _lastNoted.allowed : _context ?? ForAttack();
                int slot = GameBridge.SlotOf(__0.owner);
                hide = allowed != null && slot >= 0 && !allowed.Contains(slot);
                if (hide && go.activeSelf) Plugin.Log.LogInfo($"[teamtiming] f{Frame} panel: P{slot + 1}'s row hidden (their press didn't count)");
            }
            if (hide) { if (go.activeSelf) { go.SetActive(false); HiddenRows.Add(go.Pointer); } }
            else if (HiddenRows.Remove(go.Pointer)) go.SetActive(true);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[teamtiming] panel: {e.Message}"); }
    }

    /// <summary>The panel clears (a new battle or input): every row the mod hid comes back.</summary>
    private static void PanelClearPostfix(TimedHitUI __instance)
    {
        if (HiddenRows.Count == 0 || !NativeCode.Is<TimedHitUI>(__instance)) return;
        try
        {
            var rows = __instance.playersUIs;
            for (int i = 0; rows != null && i < rows.Count; i++)
                if (rows[i] != null && HiddenRows.Remove(rows[i].gameObject.Pointer)) rows[i].gameObject.SetActive(true);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[teamtiming] panel: {e.Message}"); }
    }

    /// <summary>
    /// Synced input getters ask this first: Interact reads as not pressed for a player whose press wouldn't count, so
    /// the game shows nothing for it.
    /// </summary>
    public static bool Hides(Rewired.Player p, int actionIndex)
    {
        var allowed = _context ?? ForAttack();
        if (allowed == null) return false;
        if (_interact == -2) _interact = ActionTable.IndexOf("Interact");
        if (actionIndex != _interact || actionIndex < 0) return false;
        int slot = GameBridge.SlotOf(p);
        return slot >= 0 && !allowed.Contains(slot);
    }
}
