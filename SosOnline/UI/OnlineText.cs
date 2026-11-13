using System;
using System.Collections.Generic;
using HarmonyLib;
using Rewired;
using Sabotage.Localization;

namespace SosOnline.UI;

/// <summary>
/// Lets the mod's strings flow through the game's own localization pipeline, so native widgets (title buttons,
/// pause menu items, popup choices) can show them. Strings live in a private category the game never loads.
/// </summary>
internal static class OnlineText
{
    public const string Category = "SosOnline";

    private static readonly Dictionary<string, Func<string>> Texts = new(StringComparer.Ordinal);
    private static int _dynamicCounter;

    public static LocalizationId Id(string key) => new LocalizationId(Category, key);

    /// <summary>A key with fixed text.</summary>
    public static LocalizationId Static(string key, string text)
    {
        Texts[key] = () => text;
        return Id(key);
    }

    /// <summary>A throwaway key for one-off text (popup choices, friend names, …).</summary>
    public static LocalizationId Dynamic(string text)
    {
        string key = "dyn" + (++_dynamicCounter);
        Texts[key] = () => text;
        if (_dynamicCounter % 512 == 0) Prune();
        return Id(key);
    }

    private static void Prune()
    {
        var stale = new List<string>();
        foreach (var k in Texts.Keys)
            if (k.StartsWith("dyn", StringComparison.Ordinal) && int.TryParse(k.AsSpan(3), out int n) && n < _dynamicCounter - 256)
                stale.Add(k);
        foreach (var k in stale) Texts.Remove(k);
    }

    private static bool TryGet(string category, string key, out string text)
    {
        text = null;
        if (category != Category || key == null) return false;
        text = Texts.TryGetValue(key, out var f) ? f() : key;
        return true;
    }

    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.GetText), typeof(LocalizationId), typeof(Controller), typeof(Player))]
    private static class P_GetTextId
    {
        private static bool Prefix(LocalizationManager __instance, LocalizationId __0, ref string __result)
        {
            if (!SosOnline.NativeCode.Is<LocalizationManager>(__instance) || __0 == null || !TryGet(__0.categoryName, __0.locId, out var t)) return true;
            __result = t;
            return false;
        }
    }

    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.GetText), typeof(string), typeof(string), typeof(Controller), typeof(Player))]
    private static class P_GetTextStrings
    {
        private static bool Prefix(LocalizationManager __instance, string __0, string __1, ref string __result)
        {
            if (!SosOnline.NativeCode.Is<LocalizationManager>(__instance) || !TryGet(__0, __1, out var t)) return true;
            __result = t;
            return false;
        }
    }

    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.IsCategoryLoaded))]
    private static class P_IsLoaded
    {
        private static bool Prefix(LocalizationManager __instance, string __0, ref bool __result)
        {
            if (__0 != Category || !SosOnline.NativeCode.Is<LocalizationManager>(__instance)) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.LoadCategory))]
    private static class P_Load { private static bool Prefix(LocalizationManager __instance, string __0) => __0 != Category || !SosOnline.NativeCode.Is<LocalizationManager>(__instance); }

    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.UnloadCategory))]
    private static class P_Unload { private static bool Prefix(LocalizationManager __instance, string __0) => __0 != Category || !SosOnline.NativeCode.Is<LocalizationManager>(__instance); }
}
