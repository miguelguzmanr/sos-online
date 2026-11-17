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
        // (The controller probes on InputManager/Rewired are gone: the patch on the 32-byte
        // InputManager.OnActiveControllerChanged crashed the game whenever the active controller changed, at start
        // and in Options with a gamepad, 2026-11-16; breakpoint at GameAssembly.dll+0xc7fcbe, the end of that method.)
        // Damage inputs: a one-point difference between PCs shows which one differs.
        Patch(harmony, typeof(PlayerAttackDamage), nameof(PlayerAttackDamage.GetRandomDamageModifier), nameof(RandomModifier));
        Patch(harmony, typeof(PlayerAttackDamage), nameof(PlayerAttackDamage.GetTimedHitMultiplier), nameof(TimedMultiplier));
        Patch(harmony, typeof(PlayerAttackDamage), nameof(PlayerAttackDamage.GetTimedHitBonusDamage), nameof(TimedBonus));
        Patch(harmony, typeof(CombatDamage), nameof(CombatDamage.GetRawDamage), nameof(RawDamage));
        Patch(harmony, typeof(PlayerBasicAttackDamage), nameof(PlayerBasicAttackDamage.CalculatePreModifiersDamage), nameof(PreDamage));
        Patch(harmony, typeof(PlayerBasicAttackDamage), nameof(PlayerBasicAttackDamage.GetDamage), nameof(BasicDamage));
        Patch(harmony, typeof(CombatDamage), nameof(CombatDamage.GetHitDamage), nameof(HitDamage));
        Patch(harmony, typeof(CombatDamage), nameof(CombatDamage.ApplyModifiers), nameof(AfterModifiers));
        // Views open asynchronously; when one is ready can differ between PCs.
        Patch(harmony, typeof(UIManager), nameof(UIManager.LoadView), nameof(ViewRequested));
        Patch(harmony, typeof(UIManager), nameof(UIManager.EndScreenPreloading), nameof(ViewReady));
        // Spell locks: which hit broke how many (the Bosslug split, 2026-11-16: one PC broke a lock the other didn't).
        PatchLocks(harmony, nameof(EnemyCastingData.BreakLocks));
        PatchLocks(harmony, nameof(EnemyCastingData.BreakAllLocksOfType));
        foreach (var name in new[] { nameof(EnemyCombatActor.EnterChargeState), nameof(EnemyCombatActor.EndChargeState), nameof(EnemyCombatActor.OnDoCastingCombatMove), nameof(EnemyCombatActor.BreakSpellLocks), nameof(EnemyCombatActor.OverrideNextCastingTime) })
            Patch(harmony, typeof(EnemyCombatActor), name, nameof(EnemyEvent));
        // Each modifier pass: log the ones that change the damage.
        foreach (var type in new[] { typeof(CombatDamage), typeof(PlayerBasicAttackDamage) })
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name.StartsWith("Apply", StringComparison.Ordinal) && m.Name != nameof(CombatDamage.ApplyModifiers)))
            {
                if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[combat] {type.Name}.{m.Name} is folded; not traced"); continue; }
                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(CombatTrace).GetMethod(nameof(ModBefore), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(CombatTrace).GetMethod(nameof(ModAfter), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[combat] {type.Name}.{m.Name} not traced: {e.Message}"); }
            }
    }

    private static void RandomModifier(int __result) { if (Active) Plugin.Log.LogInfo($"[combat] f{Frame} damage random modifier {__result}"); }
    private static void TimedMultiplier(float __0, float __result) { if (Active) Plugin.Log.LogInfo($"[combat] f{Frame} timed-hit multiplier({__0:R}) = {__result:R}"); }
    private static void TimedBonus(float __0, float __result) { if (Active) Plugin.Log.LogInfo($"[combat] f{Frame} timed-hit bonus({__0:R}) = {__result:R}"); }
    /// <summary>
    /// --sosonline-castprobe also traces every call (first per frame and method) on the enemy and its damage target,
    /// so diffing two PCs' logs shows the first call that happened on one and not the other.
    /// </summary>
    public static void InstallCallProbe(Harmony harmony)
    {
        int n = 0;
        var post = new HarmonyMethod(typeof(CombatTrace).GetMethod(nameof(CallProbe), BindingFlags.Static | BindingFlags.NonPublic));
        var types = new[] { typeof(EnemyCombatActor), typeof(EnemyCombatTarget), typeof(CombatTarget),
                            typeof(GenericPopupScreen), typeof(UnityEngine.EventSystems.CustomUIInputModule), typeof(UITextButton), typeof(UIButton),
                            typeof(SaveManager), typeof(SaveSelectionScreen), typeof(SaveSlotUI),
                            typeof(SaveLoad), typeof(SaveLoadStandalone), typeof(PlatformManager) };
        // --sosonline-callprobe=TypeA,TypeB: only those (to bisect).
        if (TestHarness.TryArg("callprobe", out var only) && only.Length > 0)
        {
            var names = only.Split(',');
            types = typeof(SaveManager).Assembly.GetTypes().Where(t => names.Contains(t.Name)).ToArray();
            Plugin.Log.LogInfo($"[combat] call probe limited to {string.Join(",", types.Select(t => t.Name))}");
        }
        foreach (var type in types)
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName || m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.IsGenericMethod || m.Name is "Update" or "LateUpdate" or "FixedUpdate") continue;
                if (TestHarness.TryArg("callprobemethods", out var onlyMethods) && !onlyMethods.Split(',').Contains(m.Name)) continue;
                if (NativeCode.IsFolded(m)) continue;
                try { harmony.Patch(m, postfix: post); n++; } catch { }
            }
        Plugin.Log.LogInfo($"[combat] call probe on {n} methods");
    }

    private static readonly System.Collections.Generic.HashSet<string> _called = new();
    private static int _calledFrame = -1;

    private static void CallProbe(MethodBase __originalMethod)
    {
        if (!CastProbe || !Active) return;
        int f = Frame;
        if (__originalMethod.Name is "Process" or "UpdateModule" or "ShouldActivateModule" or "IsModuleSupported") return; // every frame
        if (f != _calledFrame) { _calledFrame = f; _called.Clear(); }
        string key = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
        if (_called.Add(key)) Plugin.Log.LogInfo($"[call] f{f} {key}");
    }

    /// <summary>--sosonline-castprobe: every frame in a battle, log enemies whose casting state changed.</summary>
    public static bool CastProbe;
    private static readonly System.Collections.Generic.Dictionary<string, bool> _casting = new();

    public static void OnFrame(int frame)
    {
        if (Probed.Count > 0 && !Game.GameBridge.InBattle) Probed.Clear(); // each battle logs its first answer
        if (!CastProbe || !Game.GameBridge.InBattle) { _casting.Clear(); return; }
        try
        {
            foreach (var e in UnityEngine.Object.FindObjectsOfType<EnemyCombatActor>())
            {
                if (e == null) continue;
                string n = Name(e);
                bool c = e.IsCasting();
                if (!_casting.TryGetValue(n, out bool was) || was != c)
                {
                    _casting[n] = c;
                    Plugin.Log.LogInfo($"[combat] f{frame} {n} casting {c}");
                }
            }
        }
        catch { }
    }

    private static void EnemyEvent(EnemyCombatActor __instance, MethodBase __originalMethod)
    {
        if (!Active) return;
        bool casting = false;
        try { casting = __instance.IsCasting(); } catch { }
        Plugin.Log.LogInfo($"[combat] f{Frame} {Name(__instance)} {__originalMethod.Name} (casting {casting})");
    }

    private static void ViewRequested(Il2CppSystem.Type __0)
    {
        if (!Active) return;
        try { Plugin.Log.LogInfo($"[view] f{Frame} requested {__0?.Name}"); } catch { }
    }

    private static void ViewReady(View __0)
    {
        if (!Active) return;
        try { Plugin.Log.LogInfo($"[view] f{Frame} ready {__0?.GetIl2CppType()?.Name}"); } catch { }
    }

    private static void ModBefore(CombatDamage __instance, out int __state)
    {
        __state = int.MinValue;
        try { if (Active) __state = __instance.finalDamage; } catch { }
    }

    private static void ModAfter(CombatDamage __instance, int __state, MethodBase __originalMethod)
    {
        if (!Active || __state == int.MinValue) return;
        try
        {
            int now = __instance.finalDamage;
            if (now != __state) Plugin.Log.LogInfo($"[combat] f{Frame} {__originalMethod.Name}: damage {__state} -> {now}");
        }
        catch { }
    }

    private static void PreDamage(int __result) { if (Active) Plugin.Log.LogInfo($"[combat] f{Frame} pre-modifier damage {__result}"); }
    private static void BasicDamage(float __result) { if (Active) Plugin.Log.LogInfo($"[combat] f{Frame} basic damage {__result:R}"); }
    private static void HitDamage(int __result) { if (Active) Plugin.Log.LogInfo($"[combat] f{Frame} hit damage {__result}"); }
    private static void AfterModifiers(CombatDamage __instance) { if (Active) { try { Plugin.Log.LogInfo($"[combat] f{Frame} final damage {__instance.finalDamage}"); } catch { } } }
    private static void RawDamage(float __result) { if (Active) Plugin.Log.LogInfo($"[combat] f{Frame} raw damage {__result:R}"); }

    /// <summary>Last answer per probe. The battle asks every frame, so only changes are logged (it was ~4,000 lines a battle).</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Rewired.Controller> Probed = new();

    private static void Probe(string what, Rewired.Controller result)
    {
        if (!Active || !Game.GameBridge.InBattle) return;
        if (Probed.TryGetValue(what, out var last) && (last == null ? result == null : result != null && last.Pointer == result.Pointer)) return;
        Probed[what] = result;
        string owner = "none";
        try
        {
            if (result != null)
                foreach (var p in Rewired.ReInput.players.AllPlayers.ToList())
                    if (p != null && p.controllers.ContainsController(result)) { owner = $"player {p.id}"; break; }
        }
        catch { }
        Plugin.Log.LogInfo($"[combat] f{Frame} probe {what}: {(result == null ? "null" : $"{result.type} {result.name}, {owner}")}");
    }

    private static void LastUsed(Rewired.Controller __result) => Probe("InputManager.GetLastUsedController", __result);
    private static void LastActive(Rewired.Controller __result) => Probe("Rewired.GetLastActiveController", __result);
    private static void ActiveChanged(Rewired.Player __0, Rewired.Controller __1) => Probe($"OnActiveControllerChanged(player {__0?.id})", __1);

    private static void PatchLocks(Harmony harmony, string method)
    {
        foreach (var m in typeof(EnemyCastingData).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name == method))
        {
            if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[combat] EnemyCastingData.{method} is folded; not traced"); continue; }
            try
            {
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(CombatTrace).GetMethod(nameof(LocksBefore), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(CombatTrace).GetMethod(nameof(LocksAfter), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[combat] EnemyCastingData.{method} not traced: {e.Message}"); }
        }
    }

    private static int LockCount(EnemyCastingData d)
    {
        try { return d?.spellLocks?.Count ?? -1; } catch { return -1; }
    }

    /// <summary>Each lock: damage type, revealed (!) or not (?), and its visibility window. Diagnostics.</summary>
    internal static string LockDesc(EnemyCastingData d, bool window)
    {
        try
        {
            var locks = d?.spellLocks;
            if (locks == null) return "";
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < locks.Count; i++)
            {
                var l = locks[i];
                if (l == null) { parts.Add("null"); continue; }
                string type = "?";
                try { type = l.damageType != null ? l.damageType.name : "none"; } catch { }
                parts.Add(type + (l.revealed ? "!" : "?") + (window ? l.visibilityWindow.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : ""));
            }
            return string.Join(" ", parts);
        }
        catch { return "!"; }
    }

    private static string _locksBefore;

    private static void LocksBefore(EnemyCastingData __instance, out int __state)
    {
        __state = Active ? LockCount(__instance) : -1;
        _locksBefore = __state >= 0 ? LockDesc(__instance, true) : null;
    }

    /// <summary>Logged only when a lock actually broke.</summary>
    private static void LocksAfter(EnemyCastingData __instance, int __state, MethodBase __originalMethod, Il2CppSystem.Collections.Generic.List<EDamageType> __0)
    {
        if (!Active || __state <= 0) return; // no locks to break: most hits
        int now = LockCount(__instance);
        string types = "?";
        try
        {
            var parts = new System.Collections.Generic.List<string>();
            if (__0 != null) for (int i = 0; i < __0.Count; i++) parts.Add(__0[i].ToString());
            types = string.Join("+", parts);
        }
        catch { }
        string who = "?";
        try { who = Name(__instance.owner); } catch { }
        // Every hit on an enemy with locks (a few per battle), broken or not: a hit that breaks nothing on one PC and a
        // lock on the other is the point.
        Plugin.Log.LogInfo($"[combat] f{Frame} {who} {__originalMethod.Name}({types}): locks {__state} -> {now} [{_locksBefore}]");
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
        Plugin.Log.LogInfo($"[combat] f{Frame} selected: {Name(__0)}; {Clocks()}");
    }

    private static void PlayerTurn(CombatActor __0)
    {
        if (!Active) return;
        Plugin.Log.LogInfo($"[combat] f{Frame} turn: {Name(__0)}; {Clocks()}");
    }

    private static void Advance()
    {
        if (!Active) return;
        Plugin.Log.LogInfo($"[combat] f{Frame} players advance; {Clocks()}");
    }

    /// <summary>Every Unity clock: the synced ones read the same on every PC; any that differs is suspect.</summary>
    private static string Clocks()
    {
        try
        {
            return $"clocks time {UnityEngine.Time.timeAsDouble:R} level {UnityEngine.Time.timeSinceLevelLoadAsDouble:R} fixed {UnityEngine.Time.fixedTimeAsDouble:R} " +
                   $"fixedUnscaled {UnityEngine.Time.fixedUnscaledTimeAsDouble:R} real {UnityEngine.Time.realtimeSinceStartupAsDouble:R} unscaled {UnityEngine.Time.unscaledTimeAsDouble:R} " +
                   $"rendered {UnityEngine.Time.renderedFrameCount} frame {UnityEngine.Time.frameCount} scale {UnityEngine.Time.timeScale:R}";
        }
        catch (Exception e) { return "clocks ?" + e.GetType().Name; }
    }

    private static void ActorPlayer(PlayerCombatActor __instance, Rewired.Player __0)
    {
        if (!Active) return;
        Plugin.Log.LogInfo($"[combat] f{Frame} {Name(__instance)} answers to {Who(__0)}");
    }
}
