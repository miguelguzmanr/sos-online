using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline.Lockstep;

/// <summary>
/// Every enemy that enters or leaves a battle, with the lockstep frame: summons (<c>SummonMoveHelper.SpawnEnemy</c>),
/// enemies added to the encounter (<c>Encounter.AddEnemy</c>, which also places the opening roster) and actors
/// removed from it. A few lines per battle.
///
/// A running count and hash of these events, since the last load release (the same frame on every PC), goes into the
/// state snapshot as the <c>spawn</c> component, and the snapshot carries the events of its own 30-frame window. A
/// guest that summoned a different add than the host then reports the spawn and its frame at the next snapshot,
/// instead of finding a different roster or HP later (2026-11-16: the Bosslug's Acid Bugs).
/// </summary>
internal static class SpawnTrace
{
    public static int Count { get; private set; }
    public static uint Hash { get; private set; } = Fnv0;
    private const uint Fnv0 = 2166136261;

    /// <summary>(frame, event) since the last two snapshot windows; older ones are dropped.</summary>
    private static readonly List<(int frame, string what)> _events = new();

    private static bool Active => Session.Instance.Engine.Running && Session.Instance.Engine.AppliedFrame >= 0;
    private static int Frame => Session.Instance.Engine.AppliedFrame;

    public static void Install(Harmony harmony)
    {
        int n = 0;
        n += Patch(harmony, typeof(SummonMoveHelper), nameof(SummonMoveHelper.SpawnEnemy), nameof(Summoned));
        n += Patch(harmony, typeof(Encounter), nameof(Encounter.AddEnemy), nameof(Added));
        n += Patch(harmony, typeof(Encounter), nameof(Encounter.RemoveActor), nameof(Removed));
        Plugin.Log.LogInfo($"[spawn] {n} spawn path(s) traced");
        if (TestHarness.TryArg("summonprobe", out var extra))
        {
            int.TryParse(extra, out _perturb);
            PatchPrefix(harmony, typeof(SummonMoveHelper), nameof(SummonMoveHelper.GetEnemyPrefabsToSummon), nameof(ProbeSummon));
            PatchPrefix(harmony, typeof(SummonMoveHelper), nameof(SummonMoveHelper.ReserveSummonSlot), nameof(ProbeReserve));
            PatchPrefix(harmony, typeof(EncounterEnemySlot), nameof(EncounterEnemySlot.AssignActor), nameof(ProbeAssign));
            PatchPrefix(harmony, typeof(EncounterEnemySlot), nameof(EncounterEnemySlot.SetReserved), nameof(ProbeSetReserved));
        }
    }

    // --sosonline-summonprobe[=N] (tests): which summon slot is filled in which order, and why. Logs the summoner's
    // summonSlots list and UnityEngine.Random's state when a summon starts and on every slot assignment; with N > 0,
    // draws N random values when a summon starts and before each reservation (if the slot order then changes, the pick reads the shared generator).
    private static int _perturb;

    private static void ProbeSummon(SummonMoveHelper __instance)
    {
        if (!Active || !NativeCode.Is<SummonMoveHelper>(__instance)) return;
        try
        {
            for (int i = 0; i < _perturb; i++) _ = UnityEngine.Random.value;
            Plugin.Log.LogInfo($"[summon] f{Frame} start: slots [{Slots(__instance)}] random {RandomState()}{(_perturb > 0 ? $" (after {_perturb} extra draws)" : "")}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[summon] probe: {e.Message}"); }
    }

    private static void ProbeReserve(SummonMoveHelper __instance)
    {
        if (!Active || !NativeCode.Is<SummonMoveHelper>(__instance)) return;
        try { for (int i = 0; i < _perturb; i++) _ = UnityEngine.Random.value; Plugin.Log.LogInfo($"[summon] f{Frame} reserve: slots [{Slots(__instance)}] random {RandomState()} wrapper {DeterministicRandom.Current ?? "none"}"); }
        catch (Exception e) { Plugin.Log.LogWarning($"[summon] probe: {e.Message}"); }
    }

    private static void ProbeAssign(EncounterEnemySlot __instance, CombatActor __0)
    {
        if (!Active || !NativeCode.Is<EncounterEnemySlot>(__instance)) return;
        try { Plugin.Log.LogInfo($"[summon] f{Frame} assign {Name(__0)} to slot {SlotName(__instance)} (was {Name(__instance.Enemy)}, reserved {__instance.Reserved}) random {RandomState()}"); }
        catch (Exception e) { Plugin.Log.LogWarning($"[summon] probe: {e.Message}"); }
    }

    private static void ProbeSetReserved(EncounterEnemySlot __instance, bool __0, CombatActor __1)
    {
        if (!Active || !NativeCode.Is<EncounterEnemySlot>(__instance)) return;
        try { Plugin.Log.LogInfo($"[summon] f{Frame} slot {SlotName(__instance)} reserved {__0} by {Name(__1)}"); }
        catch (Exception e) { Plugin.Log.LogWarning($"[summon] probe: {e.Message}"); }
    }

    private static string Slots(SummonMoveHelper h)
    {
        var list = h.summonSlots;
        if (list == null) return "null";
        var parts = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var sl = list[i];
            parts.Add(sl == null ? "null" : $"{SlotName(sl)}:{Name(sl.Enemy)}{(sl.Enemy != null && sl.Enemy.CombatTarget != null && sl.Enemy.CombatTarget.Dead ? "/dead" : "")}{(sl.Reserved ? "/res" : "")}");
        }
        return string.Join(" ", parts);
    }

    private static string RandomState()
    {
        var st = UnityEngine.Random.state;
        return st.GetHashCode().ToString("x8");
    }

    private static void PatchPrefix(Harmony harmony, Type type, string method, string prefix)
    {
        foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (m.Name != method) continue;
            if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[summon] {type.Name}.{method} is folded; not probed"); continue; }
            try { harmony.Patch(m, prefix: new HarmonyMethod(typeof(SpawnTrace).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First }); }
            catch (Exception e) { Plugin.Log.LogWarning($"[summon] {type.Name}.{method} not probed: {e.Message}"); }
        }
    }

    /// <summary>A load was released on this frame (every PC): the count starts over.</summary>
    public static void Reset()
    {
        Count = 0;
        Hash = Fnv0;
        _events.Clear();
    }

    /// <summary>Events of the window (<paramref name="frame"/> - interval, <paramref name="frame"/>].</summary>
    public static List<(int frame, string what)> Window(int frame, int interval)
    {
        var list = new List<(int, string)>();
        foreach (var e in _events) if (e.frame > frame - interval && e.frame <= frame) list.Add(e);
        _events.RemoveAll(e => e.frame <= frame - 2 * interval);
        return list;
    }

    private static void Record(string kind, string name, string slot, string by, Vector3 pos)
    {
        if (!Active) return;
        int f = Frame;
        string what = $"{kind} {name} slot {slot}{(by == null ? "" : " by " + by)}";
        Count++;
        foreach (char c in what) Hash = (Hash ^ c) * 16777619;
        Hash = (Hash ^ (uint)f) * 16777619;
        _events.Add((f, what));
        Plugin.Log.LogInfo($"[spawn] f{f} #{Count} {what} at ({P(pos.x)},{P(pos.y)},{P(pos.z)})");
    }

    private static void Summoned(SummonMoveHelper __instance, EnemyCombatActor __result, GameObject __0, EncounterEnemySlot __1, CombatActor __2)
    {
        if (!NativeCode.Is<SummonMoveHelper>(__instance)) return;
        try { Record("summon", Name(__result != null ? __result.gameObject : __0), SlotName(__1), Name(__2), __result != null ? __result.transform.position : Vector3.zero); }
        catch (Exception e) { Plugin.Log.LogWarning($"[spawn] summon not logged: {e.Message}"); }
    }

    private static void Added(Encounter __instance, EnemyCombatActor __0, EncounterEnemySlot __1)
    {
        if (!NativeCode.Is<Encounter>(__instance)) return;
        try { Record("add", Name(__0), SlotName(__1), null, __0 != null ? __0.transform.position : Vector3.zero); }
        catch (Exception e) { Plugin.Log.LogWarning($"[spawn] add not logged: {e.Message}"); }
    }

    private static void Removed(Encounter __instance, CombatActor __0)
    {
        if (!NativeCode.Is<Encounter>(__instance)) return;
        try { Record("remove", Name(__0), "-", null, __0 != null ? __0.transform.position : Vector3.zero); }
        catch (Exception e) { Plugin.Log.LogWarning($"[spawn] remove not logged: {e.Message}"); }
    }

    private static int Patch(Harmony harmony, Type type, string method, string postfix)
    {
        int n = 0;
        foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (m.Name != method) continue;
            if (NativeCode.IsFolded(m)) { Plugin.Log.LogWarning($"[spawn] {type.Name}.{method} is folded with other methods; not traced"); continue; }
            try
            {
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(SpawnTrace).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
                n++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[spawn] {type.Name}.{method} not traced: {e.Message}"); }
        }
        return n;
    }

    private static string Name(Component c) => c == null ? "none" : Name(c.gameObject);

    private static string Name(GameObject g)
    {
        try { return g == null ? "none" : g.name.Replace("(Clone)", "").Trim(); }
        catch { return "?"; }
    }

    private static string SlotName(EncounterSlot s)
    {
        if (s == null) return "none";
        try
        {
            // The slot's index among its siblings: names repeat ("EnemySlot"), indices don't.
            var t = s.transform;
            return $"{t.GetSiblingIndex()}";
        }
        catch { return "?"; }
    }

    private static string P(float f) => f.ToString("F1", CultureInfo.InvariantCulture);
}
