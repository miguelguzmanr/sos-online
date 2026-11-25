using System;
using System.Collections.Generic;
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
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.OnBeginEncounter)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.CommitInitialCombatMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.CommitMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.CommitNextMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.SelectNextMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.GetInitialCombatMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.GetNextCombatMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.GetReplacementMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.SwapNextMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.UpdatePattern)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.ValidateNextMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.GetMoveCastingTime)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.UseOverrideCastingTime)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.EnterChargeState)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.UpdateChargeState)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.OnDoCastingCombatMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.OnDoCombatMove)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.OnCombatMoveDone)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.OnSelfTurnDone)),
        (typeof(EnemyCombatActor), nameof(EnemyCombatActor.EndTurn)),
        (typeof(EnemyCastingData), nameof(EnemyCastingData.GetCombatMoveCastingTime)),
        (typeof(EnemyCastingData), nameof(EnemyCastingData.ResetCastingTimeByMove)),
        (typeof(EnemyCastingData), nameof(EnemyCastingData.SetTurnsToAction)),
        (typeof(EnemyCastingData), nameof(EnemyCastingData.StoreCurrentMoveCastingTime)),
        (typeof(EnemyCastingData), nameof(EnemyCastingData.AdvanceCastingTime)),
        (typeof(EnemyMovePattern), nameof(EnemyMovePattern.BeginPattern)),
        (typeof(EnemyMovePattern), nameof(EnemyMovePattern.ConsumeNextMove)),
        (typeof(EnemyMovePattern), nameof(EnemyMovePattern.AssignNewSequence)),
        (typeof(EnemyMovePattern), nameof(EnemyMovePattern.AssignNextSequence)),
        (typeof(EnemyMovePattern), nameof(EnemyMovePattern.OnCurrentSequenceDone)),
        (typeof(EnemyMovePattern), nameof(EnemyMovePattern.ResetCurrentSequence)),
        (typeof(HasteModifier), nameof(HasteModifier.Apply)),
        (typeof(HasteModifier), nameof(HasteModifier.GetCastingTime)),
        (typeof(HasteModifier), nameof(HasteModifier.GetCombatMove)),
        (typeof(CombatActor), nameof(CombatActor.OnPlayerPartyTurnDone)),
        (typeof(CombatActor), nameof(CombatActor.OnAnyCharacterTurnDone)),
        (typeof(CombatActor), nameof(CombatActor.OnPlayerCharacterTurnDone)),
        (typeof(CombatActor), nameof(CombatActor.OnEnemyCharacterTurnDone)),
        (typeof(EncounterController), nameof(EncounterController.EndCurrentTurn)),
        (typeof(EncounterController), nameof(EncounterController.OnPlayerCharacterTurnDone)),
        (typeof(EncounterController), nameof(EncounterController.OnEnemyCharacterTurnDone)),
        (typeof(EncounterController), nameof(EncounterController.OnEnemiesTurn)),
        // Co-op encounter setup + leaf move/cast rolls.
        (typeof(EncounterPlayersManager), nameof(EncounterPlayersManager.Activate)),
        (typeof(EnemyCombatMove), nameof(EnemyCombatMove.GetCastingTime)),
        (typeof(EnemyCombatMoveSequence), nameof(EnemyCombatMoveSequence.PickNextMove)),
        // Fishing minigame.
        (typeof(Fish), nameof(Fish.GetJumpHeight)),
        (typeof(Fish), nameof(Fish.Jump)),
        (typeof(FishIdleState), nameof(FishIdleState.StateEnter)),
        (typeof(FishIdleState), nameof(FishIdleState.StateExecute)),
        (typeof(FishHookedState), nameof(FishHookedState.UpdateSidePullDirection)),
        (typeof(FishHookedFightingState), nameof(FishHookedFightingState.StateEnter)),
        (typeof(FishHookedFightingState), nameof(FishHookedFightingState.UpdateSidePullDirection)),
        (typeof(FishHookedFightingState), nameof(FishHookedFightingState.UpdateStateChange)),
        (typeof(FishHookedFinalFightState), nameof(FishHookedFinalFightState.UpdateSidePullDirection)),
        (typeof(FishReelingZoneGoToState), nameof(FishReelingZoneGoToState.SelectTargetX)),
        (typeof(FishingLine), nameof(FishingLine.Snap)),
        // Wheels & minigames.
        (typeof(Sabotage.Wheels.WheelsRevealEnemySlotsState), nameof(Sabotage.Wheels.WheelsRevealEnemySlotsState.ComputeFromResultSheet)),
        (typeof(MemoryMinigame), nameof(MemoryMinigame.GenerateTiles)),
        (typeof(SoSMinigameSecondPhaseController), nameof(SoSMinigameSecondPhaseController.Move)),
        (typeof(SoSMinigameSecondPhaseController), nameof(SoSMinigameSecondPhaseController.SpawnOrb)),
        (typeof(SpaceShooterConfigRandomFloat), nameof(SpaceShooterConfigRandomFloat.GetValue)),
        (typeof(BilletBullGatlingFun), nameof(BilletBullGatlingFun.GetNextLookDirChangeTime)),
        (typeof(BilletBullGatlingFun), nameof(BilletBullGatlingFun.GetNextPlayerHitTime)),
        (typeof(BilletBullGatlingFun), nameof(BilletBullGatlingFun.GetNextShotTime)),
        (typeof(BilletBullGatlingFun), nameof(BilletBullGatlingFun.ShootEmptySpot)),
        (typeof(ArtificerBigBimState), nameof(ArtificerBigBimState.SpawnCrystal)),
        (typeof(ArtificerClock), nameof(ArtificerClock.SetStage)),
        (typeof(ArtificerClock), nameof(ArtificerClock.UpdateSettings)),
        (typeof(LimboBarSpawner), nameof(LimboBarSpawner.SetStage)),
        (typeof(TrampolineTeam), nameof(TrampolineTeam.Update)),
        (typeof(SunbalancingCollectibleRing), nameof(SunbalancingCollectibleRing.OnExitPool)),
        // Player skills, combos & co-op.
        (typeof(CombatMoveCannonballTrampoline), nameof(CombatMoveCannonballTrampoline.GetGroundBouncePosition)),
        (typeof(ConflagrateFlamePillar), nameof(ConflagrateFlamePillar.Init)),
        (typeof(ConflagrateFlamePillar), nameof(ConflagrateFlamePillar.UpdateSelfDirection)),
        (typeof(HurlSmash), nameof(HurlSmash.GetSecondTarget)),
        (typeof(ToadcanoTremor), nameof(ToadcanoTremor.GatherEnemyTargets)),
        (typeof(ShowerSpell), nameof(ShowerSpell.GatherNextDropSettings)),
        (typeof(ShowerSpellRainDrop), nameof(ShowerSpellRainDrop.GetDropSpeed)),
        (typeof(SeraiCombatDodgeState), nameof(SeraiCombatDodgeState.GetAvoidAngle)),
        (typeof(SeraiCombatDodgeState), nameof(SeraiCombatDodgeState.TeleportToCounterPosition)),
        (typeof(SeraiFanOfKnives), nameof(SeraiFanOfKnives.ExitNextPortal)),
        (typeof(SeraiFanOfKnives), nameof(SeraiFanOfKnives.JumpOutOfPortal)),
        (typeof(SeraiFanOfKnives), nameof(SeraiFanOfKnives.GetNextTarget)),
        (typeof(HexShieldTargetSelection), nameof(HexShieldTargetSelection.DoTargetSelection)),
        (typeof(SinglePlayerPlusBubbleState), nameof(SinglePlayerPlusBubbleState.SetNextRandomOffset)),
        // Bosses & specific enemies.
        (typeof(DwellerOfDreadChompCastingCombatMoveParams), nameof(DwellerOfDreadChompCastingCombatMoveParams.GetSpellLockSettings)),
        (typeof(TrainBossCurtainCallCastingCombatMoveParams), nameof(TrainBossCurtainCallCastingCombatMoveParams.GetSpellLockSettings)),
        (typeof(ChromaticApparitionCombatActor), nameof(ChromaticApparitionCombatActor.MustShapeShift)),
        (typeof(ChromaticApparitionCombatActor), nameof(ChromaticApparitionCombatActor.OnCombatMoveResumeFight)),
        (typeof(WyrdCombatActor), nameof(WyrdCombatActor.OnCombatMoveResumeFight)),
        (typeof(NarcisKingEncounterMirrorMechanic), nameof(NarcisKingEncounterMirrorMechanic.GetNewMirrorSetup)),
        (typeof(PrototypeZeroCombatActor), nameof(PrototypeZeroCombatActor.GetNextHitDamage)),
        (typeof(GiantPuppeteer), nameof(GiantPuppeteer.GetTiltResult)),
        (typeof(GiantPuppeteer), nameof(GiantPuppeteer.GetWheelsToLockCheating)),
        (typeof(GiantPuppeteer), nameof(GiantPuppeteer.GoIdle)),
        (typeof(GiantPuppeteer), nameof(GiantPuppeteer.OnPlayerChangedWheelConfig)),
        (typeof(GiantPuppeteerEncounter), nameof(GiantPuppeteerEncounter.ComputeFromResultSheet)),
        (typeof(GiantPuppeteerEncounter), nameof(GiantPuppeteerEncounter.GetReplacementFigurines)),
        (typeof(PuppeteerEncounterWheelCombatActor), nameof(PuppeteerEncounterWheelCombatActor.HitSymbol)),
        (typeof(PuppeteerEncounterWheelCombatActor), nameof(PuppeteerEncounterWheelCombatActor.OnAfterTargetApplyHitImpact)),
        (typeof(PuppeteerEncounterWheelEnemyCombatTarget), nameof(PuppeteerEncounterWheelEnemyCombatTarget.ApplyHitEffects)),
        (typeof(AngryTrainCoalslay), nameof(AngryTrainCoalslay.DoMove)),
        (typeof(GaletteDesRois), nameof(GaletteDesRois.DoMove)),
        (typeof(ElysandArelleTelekinesisCrashDeux), nameof(ElysandArelleTelekinesisCrashDeux.ApplyFloat)),
        (typeof(PifPoufTourpille), nameof(PifPoufTourpille.OnLiftPif)),
        (typeof(PifPoufTourpille), nameof(PifPoufTourpille.OnLiftPouf)),
        // Overworld AI, Crypts, Live Mana & Sabotage utils.
        (typeof(PatrolNode), nameof(PatrolNode.BeforeExecute)),
        (typeof(PatrolNode), nameof(PatrolNode.Pause)),
        (typeof(PatrolNode), nameof(PatrolNode.UpdatePath)),
        (typeof(GroundChaseLostTargetArchetypeState), nameof(GroundChaseLostTargetArchetypeState.OnStartBehavior)),
        (typeof(GroundChaseLostTargetArchetypeState), nameof(GroundChaseLostTargetArchetypeState.OnUpdateBehavior)),
        (typeof(RandomPathFinderZone), nameof(RandomPathFinderZone.SelectRandomDestination)),
        (typeof(Sabotage.Crypt.CryptManager), nameof(Sabotage.Crypt.CryptManager.LoadRoomAtPosition)),
        (typeof(Sabotage.Crypt.CryptManager), nameof(Sabotage.Crypt.CryptManager.SpawnMonuments)),
        (typeof(FillCryptFavoriteRoomNode), nameof(FillCryptFavoriteRoomNode.BeforeExecute)),
        (typeof(LiveManaEmitter), nameof(LiveManaEmitter.GetSpawnVelocity)),
        (typeof(LiveManaEmitter), nameof(LiveManaEmitter.Emit)),
        (typeof(BigLiveManaMergeState), nameof(BigLiveManaMergeState.StateEnter)),
        (typeof(EncounterLootBag), nameof(EncounterLootBag.OnExitPool)),
        (typeof(EncounterLootBag), nameof(EncounterLootBag.FindDestination)),
        (typeof(Sabotage.Core.RandomUtils.RandomFloat), nameof(Sabotage.Core.RandomUtils.RandomFloat.GetRandomValue)),
        (typeof(Sabotage.Core.RandomUtils.RandomInt), nameof(Sabotage.Core.RandomUtils.RandomInt.GetRandomValue)),
        (typeof(Sabotage.Core.RandomList.ShuffleExtensions), nameof(Sabotage.Core.RandomList.ShuffleExtensions.ShuffleList)),
        (typeof(Sabotage.Graph.BehaviorTree.Nodes.Leaf.Utils.GetRandomFloatNode), nameof(Sabotage.Graph.BehaviorTree.Nodes.Leaf.Utils.GetRandomFloatNode.Execute)),
        (typeof(Sabotage.Graph.BehaviorTree.Nodes.Leaf.Utils.GetRandomIntNode), nameof(Sabotage.Graph.BehaviorTree.Nodes.Leaf.Utils.GetRandomIntNode.Execute)),
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

    /// <summary>
    /// Which free slots a summon takes, in which order. The Bosslug's second summon reserved its slots as [1 2] on one
    /// PC and [2 1] on the other, and the Acid Bugs swapped places (tests/resync.sh, f5047, 2026-11-16): the order
    /// followed the shared generator's state (two guest states, each always giving the same order). The first summon
    /// happened to agree. --sosonline-nosummonfix leaves these alone (to reproduce that split).
    /// </summary>
    private static readonly (Type type, string method)[] SummonTargets =
    {
        (typeof(SummonMoveHelper), nameof(SummonMoveHelper.ReserveSummonSlot)),
        (typeof(SummonMoveHelper), nameof(SummonMoveHelper.DoPermanentSlotReservation)),
        (typeof(SummonMoveHelper), nameof(SummonMoveHelper.GetEnemyPrefabsToSummon)),
        (typeof(SummonMoveHelper), nameof(SummonMoveHelper.GetSpawnPosition)),
    };

    /// <summary>Compiler-generated coroutine steps (found by name prefix: the suffix can change between builds).</summary>
    private static readonly (Type outer, string prefix)[] CoroutineTargets =
    {
        (typeof(EncounterController), "_DoNextTurnWhenNextActorIsReadyCoroutine_"),
        (typeof(Sabotage.Wheels.WheelsBoard), "__DoAssassinAction_"),
        (typeof(LiveManaEmitter), "_SpawnManaCoroutine_"),
        (typeof(EncounterLootBag), "_LootMovementCoroutine_"),
        (typeof(DefaultEncounterLoot), "_AwardLootsInternal_"),
        (typeof(FeralQueenBuissRouletteMove), "__DoMove_"),
        (typeof(PantoufPoufInTheBox), "_CastCoroutine_"),
        (typeof(PhaseReaperPhaseStrike), "_AttackCoroutine_"),
        (typeof(MirrorAttack), "_DoMoveCoroutine_"),
        (typeof(MirrorAttack), "_SpawnMirrorImageCoroutine_"),
        (typeof(PrototypeZeroCombatMove), "_DisplayNextMoveHintCoroutine_"),
        (typeof(PuppeteerDaggerSpell), "_SpawnDaggersCoroutine_"),
        (typeof(PuppeteerDaggerSpell), "_SpawnOutDagger_"),
        (typeof(GiantPuppeteerLockWheels), "_WaitAndBeginSpin_"),
        (typeof(GiantPuppeteerLockWheels), "_LockCoroutine_"),
        (typeof(GiantPuppeteerTilt), "_WaitAndBeginTilt_"),
        (typeof(NarcisKingZaleGannonball), "_ProjectileReachedPlayerCoroutine_"),
        (typeof(NarcisKingZaleGannonball), "_BlockCoroutine_"),
        (typeof(NarcisKingZaleGannonball), "_ProjectileReachedMainActorCoroutine_"),
        (typeof(NarcisKingZaleGannonball), "_StunnedCoroutine_"),
        (typeof(WerwofmizquardAwoo), "_DoMoveCoroutine_"),
        (typeof(WerwofmizquardAwoo), "_TransformPtecassesCoroutine_"),
        (typeof(DynamouseBignition), "_AttackCoroutine_"),
        (typeof(DynamouseBignition), "_ExplodeProjectilesCoroutine_"),
        (typeof(MonsieurTenduMirageTossClone), "__DoSpawn_"),
        (typeof(MonsieurTenduMirageTossClone), "__FlashCoroutine_"),
        (typeof(WakeUpAfterDeathGameAction), "_WakeUpCoroutine_"),
        (typeof(RomayaSummon), "_CastCoroutine_"),
        (typeof(RomayaSummon), "_MoveBallsCoroutine_"),
    };

    private static int _depth, _frame = int.MinValue;
    private static readonly Dictionary<uint, int> _perMethodCounts = new();
    private static bool _seeded;
    private static int _lastEnterFrame = int.MinValue;
    private static UnityEngine.Random.State _saved;

    public static int Rolls { get; private set; }
    public static bool InScope => _depth > 0 && _seeded;
    internal static bool InternalSeeding;

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
        var targets = Targets.AsEnumerable();
        if (!TestHarness.TryArg("nolockfix", out _)) targets = targets.Concat(LockTargets);
        if (!TestHarness.TryArg("nosummonfix", out _)) targets = targets.Concat(SummonTargets);
        foreach (var (type, name) in targets)
        {
            int hits = 0;
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name == name))
            {
                hits++;
                // Never detour native code shared with unrelated methods.
                if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[random] {type.Name}.{name} is folded with other methods; left alone"); continue; }
                try { harmony.Patch(m, prefix: enter, finalizer: exit); patched++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[random] {type.Name}.{name} not patched: {e.Message}"); }
            }
            if (hits == 0) Plugin.Log.LogWarning($"[random] {type.Name}.{name} not found");
        }
        foreach (var (outer, prefix) in CoroutineTargets)
        {
            int hits = 0;
            foreach (var nested in outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(t => MatchesCoroutine(t.Name, prefix)))
            {
                hits++;
                var m = nested.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null || NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[random] {nested.Name}.MoveNext not patched"); continue; }
                try { harmony.Patch(m, prefix: enter, finalizer: exit); patched++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[random] {nested.Name}.MoveNext not patched: {e.Message}"); }
            }
            if (hits == 0) Plugin.Log.LogWarning($"[random] {outer.Name}+{prefix} coroutine not found");
        }
        Plugin.Log.LogInfo($"[random] {patched} gameplay random roll(s) made deterministic");
    }

    private static bool MatchesCoroutine(string typeName, string prefix)
    {
        if (prefix.EndsWith("_", StringComparison.Ordinal))
            return typeName.StartsWith(prefix + "d__", StringComparison.Ordinal);
        return typeName.StartsWith("_" + prefix + "_d__", StringComparison.Ordinal)
            || typeName.StartsWith("__" + prefix + "_d__", StringComparison.Ordinal)
            || typeName.StartsWith(prefix + "_d__", StringComparison.Ordinal);
    }

    /// <summary>The outermost wrapped method running now, or null (the summon probe logs it).</summary>
    public static string Current => _depth > 0 && _seeded ? _outer : null;
    private static string _outer;

    private static uint Fnv1a(string declaringType, string name)
    {
        uint h = 2166136261u;
        if (declaringType != null)
            for (int i = 0; i < declaringType.Length; i++) { h ^= declaringType[i]; h *= 16777619u; }
        h ^= '.'; h *= 16777619u;
        if (name != null)
            for (int i = 0; i < name.Length; i++) { h ^= name[i]; h *= 16777619u; }
        return h;
    }

    private static void Enter(MethodBase __originalMethod)
    {
        // A roll that threw never reached Exit: start each frame from the outermost level again.
        int frame = Active ? Session.Instance.Engine.AppliedFrame : int.MinValue;
        if (frame != _lastEnterFrame) { _lastEnterFrame = frame; _depth = 0; }
        if (_depth++ > 0 || !Active) return;
        try
        {
            _seeded = false;
            var e = Session.Instance.Engine;
            // During additive scene loading (e.g. World Map sub-scenes), NPCs finish spawning on different local frames
            // before EndCurrentLevelLoading is released; key loading rolls on CurrentLoadSeq instead of AppliedFrame.
            int bucket = Game.GameBridge.LoadingLevel ? unchecked(-Session.Instance.CurrentLoadSeq - 1) : e.AppliedFrame;
            if (bucket != _frame) { _frame = bucket; _perMethodCounts.Clear(); }
            string typeName = __originalMethod?.DeclaringType?.FullName ?? __originalMethod?.DeclaringType?.Name ?? "";
            string methodName = __originalMethod?.Name ?? "";
            uint methodId = Fnv1a(typeName, methodName);
            _perMethodCounts.TryGetValue(methodId, out int n);
            _perMethodCounts[methodId] = ++n;
            _saved = UnityEngine.Random.state;
            InternalSeeding = true;
            try { UnityEngine.Random.InitState(unchecked(e.Seed ^ (_frame * 486187739) ^ (int)(methodId * 2654435761u) ^ (n * 1640531527) ^ 0x5EED)); }
            finally { InternalSeeding = false; }
            _seeded = true;
            _outer = __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name;
            Rolls++;
        }
        catch { }
    }

    private static Exception Exit(Exception __exception)
    {
        if (--_depth > 0) return __exception;
        _depth = 0;
        if (_seeded)
        {
            _seeded = false;
            try { UnityEngine.Random.state = _saved; } catch { }
        }
        return __exception;
    }
}
