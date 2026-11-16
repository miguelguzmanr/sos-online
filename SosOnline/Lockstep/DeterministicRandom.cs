using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SosOnline.Net;

namespace SosOnline.Lockstep;

/// <summary>
/// Gameplay random rolls that give the same result on every PC.
///
/// <see cref="LockstepEngine"/> reseeds <c>UnityEngine.Random</c> at the start of each frame, but visual effects
/// (screen shakes, randomized sprites and particles, animator offsets) draw from the same generator during the frame,
/// and how often they do can differ between PCs (resolution, what is on screen, pooled objects). A roll made after
/// them then differs: a basic attack hit for 11 on the host and 10 on the guest (2026-11-15, Mooncradle training,
/// <c>PlayerAttackDamage.GetRandomDamageModifier</c>).
///
/// Each method below runs with the generator seeded from the session seed, the frame and how many rolls this frame
/// came before it (all the same on every PC), and the generator's previous state is restored afterwards so effects
/// are untouched. Nested calls (a roll that calls another roll) use the outer call's sequence.
/// </summary>
internal static class DeterministicRandom
{
    private static readonly (Type type, string method)[] Targets =
    {
        (typeof(PlayerAttackDamage), nameof(PlayerAttackDamage.GetRandomDamageModifier)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.GetRandomAvailableMove)),
        (typeof(EnemyCombatMovesConfig), nameof(EnemyCombatMovesConfig.GetRandomCombatMove)),
        (typeof(EnemyMovePattern), nameof(EnemyMovePattern.AssignRandomAvailableSequence)),
        (typeof(HitPatternParams), nameof(HitPatternParams.GetRandomHitPattern)),
        (typeof(CombatActorListExtensions), nameof(CombatActorListExtensions.GetRandomOpponent)),
        (typeof(CombatActorListExtensions), nameof(CombatActorListExtensions.GetRandomActor)),
        (typeof(CombatActorListExtensions), nameof(CombatActorListExtensions.GetRandomAlly)),
        (typeof(CombatTargetListExtensions), nameof(CombatTargetListExtensions.GetRandomOpponent)),
        (typeof(CombatTargetListExtensions), nameof(CombatTargetListExtensions.GetRandomTarget)),
        (typeof(CombatTargetListExtensions), nameof(CombatTargetListExtensions.GetRandomAlly)),
        (typeof(GunGoddessFanTheHammer), nameof(GunGoddessFanTheHammer.GetRandomTarget)),
        (typeof(MedusoCombatActor), nameof(MedusoCombatActor.GetRandomAvailableClaw)),
        (typeof(NarcisKingEncounterMirrorMechanic), nameof(NarcisKingEncounterMirrorMechanic.GetRandomUsableMirror)),
        (typeof(AephorulAppendageCombatActor), nameof(AephorulAppendageCombatActor.SetRandomWeakness)),
        (typeof(ReshanCorrosiveBlend), nameof(ReshanCorrosiveBlend.BreakRandomLocks)),
        (typeof(RadiusShowerSpell), nameof(RadiusShowerSpell.GetRandomPositionAroundTarget)),
        (typeof(RandomLootTable), nameof(RandomLootTable.GetLootToGive)),
        // Co-op battles: when more than one player could take the next round, the game picks one at random. Without
        // these the PCs picked different players on the same frame (tests/battle-turns.sh, f2535, 2026-11-15).
        (typeof(EncounterController), nameof(EncounterController.AdvancePlayers)),
        (typeof(EncounterController), nameof(EncounterController.OnPlayersTurn)),
        (typeof(EncounterController), nameof(EncounterController.ActivatePlayerTurn)),
        (typeof(EncounterController), nameof(EncounterController.DoNextActorTurn)),
        (typeof(EncounterController), nameof(EncounterController.EnterPlayerSelectBattleCommandState)),
        (typeof(EncounterController), nameof(EncounterController.SetPlayersInDoNothingState)),
        // Who acts next (a party member or an enemy) is decided here and in the coroutine below.
        (typeof(CombatTurnsQueue), nameof(CombatTurnsQueue.GetNextActor)),
        (typeof(CombatTurnsQueue), nameof(CombatTurnsQueue.BuildTurnBasedQueue)),
        (typeof(CombatTurnsQueue), nameof(CombatTurnsQueue.IsSomeoneReady)),
        // Enemy spells: how many turns a cast takes, and the turn bookkeeping around it. The PCs disagreed on when
        // a cast went off with every logged event identical (tests/battle-turns.sh, f2756).
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.GetMoveCastingTime)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.UseOverrideCastingTime)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.EnterChargeState)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.UpdateChargeState)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.OnDoCastingCombatMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.OnSelfTurnDone)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.EndTurn)),
        (typeof(CombatActor), nameof(CombatActor.OnPlayerPartyTurnDone)),
        (typeof(CombatActor), nameof(CombatActor.OnAnyCharacterTurnDone)),
        (typeof(CombatActor), nameof(CombatActor.OnPlayerCharacterTurnDone)),
        (typeof(CombatActor), nameof(CombatActor.OnEnemyCharacterTurnDone)),
        (typeof(EncounterController), nameof(EncounterController.EndCurrentTurn)),
        (typeof(EncounterController), nameof(EncounterController.OnPlayerCharacterTurnDone)),
        (typeof(EncounterController), nameof(EncounterController.OnEnemyCharacterTurnDone)),
        (typeof(EncounterController), nameof(EncounterController.OnEnemiesTurn)),
    };

    /// <summary>
    /// Spell lock types are picked when an enemy starts casting. The Bosslug's locks were Blunt + Sword on the host
    /// and Moon + Moon on the guest; a Sword hit then broke a lock on one PC only, the boss re-planned into its summon
    /// there, and the PCs split on the Acid Bug adds (tests/battle-turns.sh, f1379, 2026-11-16).
    /// --sosonline-nolockfix leaves these alone (to reproduce that split).
    /// </summary>
    private static readonly (Type type, string method)[] LockTargets =
    {
        (typeof(EnemyCastingData), nameof(EnemyCastingData.CastMove)),
        (typeof(EnemyCastingData), nameof(EnemyCastingData.SwapNextMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.GetSpecialMoveLocks)),
        (typeof(AbstractLockCastingCombatMoveParams), nameof(AbstractLockCastingCombatMoveParams.GetSpellLockSettings)),
        (typeof(MultiLockSettingsCastingCombatMoveParams), nameof(MultiLockSettingsCastingCombatMoveParams.GetSpellLockSettings)),
        (typeof(Bosslug), nameof(Bosslug.UpdateNextMove)),
    };

    /// <summary>Compiler-generated coroutine steps (found by name prefix: the suffix can change between builds).</summary>
    private static readonly (Type outer, string prefix)[] CoroutineTargets =
    {
        (typeof(EncounterController), "_DoNextTurnWhenNextActorIsReadyCoroutine_"),
    };

    private static int _depth, _frame = int.MinValue, _count;
    private static bool _seeded;
    private static int _lastEnterFrame = int.MinValue;
    private static UnityEngine.Random.State _saved;

    public static int Rolls { get; private set; }

    private static bool Active
    {
        get
        {
            var e = Session.Instance.Engine;
            return InputOverride.Active && e.Running && e.AppliedFrame >= 0;
        }
    }

    public static void Install(Harmony harmony)
    {
        var enter = new HarmonyMethod(typeof(DeterministicRandom).GetMethod(nameof(Enter), BindingFlags.Static | BindingFlags.NonPublic));
        var exit = new HarmonyMethod(typeof(DeterministicRandom).GetMethod(nameof(Exit), BindingFlags.Static | BindingFlags.NonPublic));
        int patched = 0;
        var targets = TestHarness.TryArg("nolockfix", out _) ? Targets : Targets.Concat(LockTargets).ToArray();
        foreach (var (type, name) in targets)
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name == name))
            {
                // Never detour native code shared with unrelated methods.
                if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[random] {type.Name}.{name} is folded with other methods; left alone"); continue; }
                try { harmony.Patch(m, prefix: enter, postfix: exit); patched++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[random] {type.Name}.{name} not patched: {e.Message}"); }
            }
        foreach (var (outer, prefix) in CoroutineTargets)
            foreach (var nested in outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var m = nested.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null || NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[random] {nested.Name}.MoveNext not patched"); continue; }
                try { harmony.Patch(m, prefix: enter, postfix: exit); patched++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[random] {nested.Name}.MoveNext not patched: {e.Message}"); }
            }
        Plugin.Log.LogInfo($"[random] {patched} gameplay random roll(s) made deterministic");
    }

    private static void Enter()
    {
        // A roll that threw never reached Exit: start each frame from the outermost level again.
        int frame = Active ? Session.Instance.Engine.AppliedFrame : int.MinValue;
        if (frame != _lastEnterFrame) { _lastEnterFrame = frame; _depth = 0; }
        if (_depth++ > 0 || !Active) return;
        try
        {
            _seeded = false;
            var e = Session.Instance.Engine;
            if (e.AppliedFrame != _frame) { _frame = e.AppliedFrame; _count = 0; }
            _saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(unchecked(e.Seed ^ (_frame * 486187739) ^ ((++_count) * 1640531527) ^ 0x5EED));
            _seeded = true;
            Rolls++;
        }
        catch { }
    }

    private static void Exit()
    {
        if (--_depth > 0) return;
        _depth = 0;
        if (_seeded)
        {
            _seeded = false;
            try { UnityEngine.Random.state = _saved; } catch { }
        }
    }
}
