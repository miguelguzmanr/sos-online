using System;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.XrefScans;

namespace SosOnline;

/// <summary>--sosonline-xref: lists what selected game methods call (decoded from native code), to learn their behavior.</summary>
internal static class XrefProbe
{
    private static readonly (Type type, string method)[] Targets =
    {
        // Loading path: title screen Continue → LevelManager (spike A, loading barrier).
        (typeof(TitleScreen), "OnContinue"),
        (typeof(TitleScreen), "WaitAndContinueGame"),
        (typeof(LevelManager), "GetInitialLevelInfo"),
        (typeof(LevelManager), "LoadLevel"),
        (typeof(LevelManager), "DoLevelLoading"),
        (typeof(LevelManager), "OnLevelLoaded"),
        (typeof(LevelManager), "EndCurrentLevelLoading"),
        (typeof(LevelManager), "UnloadUnusedAssetsBeforeEndingLoading"),
        (typeof(LevelManager), "OnTransitionInDone"),
        (typeof(LevelManager), "WaitBeforeLoading"),
        (typeof(SaveManager), "LoadSaveSlot"),
        (typeof(SaveManager), "LoadSaveSlotAndSetLastIndex"),
        (typeof(SaveManager), "UnloadSaveSlotData"),
        // Level ups: which player's input the bonus choice reads (Lockstep/LevelUps.cs).
        (typeof(LevelUpSceneController), "LevelUpCharacter"),
        (typeof(LevelUpSceneController), "ShowUpgradeSelection"),
        (typeof(LevelUpSceneController), "EndLevelUp"),
        (typeof(LevelUpScreen), "Init"),
        (typeof(LevelUpScreen), "Update"),
        (typeof(WorldUINavigation<LevelUpStar>), "EnableNavigation"),
    };

    public static void Run()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly;
        foreach (var (type, name) in Targets)
        {
            // Coroutines and lambdas compile to nested classes (e.g. _WaitAndContinueGame_d__12.MoveNext): scan those too.
            var methods = type.GetMethods(all).Where(x => x.Name == name).Select(m => (label: $"{type.Name}.{name}", m)).ToList();
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(n => n.Name.Contains(name)))
                methods.AddRange(nested.GetMethods(all).Where(x => x.Name == "MoveNext" || x.Name.StartsWith("_" + name) || x.Name.Contains("b__"))
                    .Select(m => (label: $"{type.Name}.{nested.Name}.{m.Name}", m)));
            foreach (var (label, m) in methods)
            {
                Plugin.Log.LogInfo($"[xref] scanning {label}");
                try
                {
                    var code = NativeCode.CodeOf(m);
                    var calls = XrefScannerLowLevel.CallAndIndirectTargets(code)
                        .Select(t => NativeCode.NamesAt(t))
                        .Where(n => n.Count > 0)
                        .Select(n => n.Count == 1 ? n[0] : $"{n[0]}(+{n.Count - 1} folded)")
                        .Distinct();
                    Plugin.Log.LogInfo($"[xref] {label}({m.GetParameters().Length}) -> {string.Join(", ", calls)}");
                }
                catch (Exception e) { Plugin.Log.LogInfo($"[xref] {label}: {e.Message}"); }
            }
        }
    }
}
