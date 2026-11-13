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
        (typeof(PauseMenu), "ReturnToTitle"),
        (typeof(PauseMenu), "OnReturnToTitlePopupAnswered"),
        (typeof(PauseMenu), "OnTitleScreenLoadingStarted"),
        (typeof(GameOverScreen), "OnBackToTitle"),
        (typeof(ReturnToTitleScreenNode), "OnUpdate"),
        (typeof(ReturnToTitleScreenNode), "OnEnter"),
        (typeof(TitleSequenceManager), "GoToScreen"),
        (typeof(SaveManager), "SaveLoadedSaveSlot"),
        (typeof(SinglePlayerPlusManager), "AssignCharactersToPlayers"),
        (typeof(SinglePlayerPlusManager), "RemoveAllActivePlayers"),
    };

    public static void Run()
    {
        foreach (var (type, name) in Targets)
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(x => x.Name == name))
            {
                Plugin.Log.LogInfo($"[xref] scanning {type.Name}.{name}");
                try
                {
                    var code = NativeCode.CodeOf(m);
                    var calls = XrefScannerLowLevel.CallAndIndirectTargets(code)
                        .Select(t => NativeCode.NamesAt(t))
                        .Where(n => n.Count > 0)
                        .Select(n => n.Count == 1 ? n[0] : $"{n[0]}(+{n.Count - 1} folded)")
                        .Distinct();
                    Plugin.Log.LogInfo($"[xref] {type.Name}.{name}({m.GetParameters().Length}) -> {string.Join(", ", calls)}");
                }
                catch (Exception e) { Plugin.Log.LogInfo($"[xref] {type.Name}.{name}: {e.Message}"); }
            }
    }
}
