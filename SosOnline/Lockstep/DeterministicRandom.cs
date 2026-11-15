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
        foreach (var (type, name) in Targets)
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name == name))
            {
                // Never detour native code shared with unrelated methods.
                if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[random] {type.Name}.{name} is folded with other methods; left alone"); continue; }
                try { harmony.Patch(m, prefix: enter, postfix: exit); patched++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[random] {type.Name}.{name} not patched: {e.Message}"); }
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
