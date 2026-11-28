using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline.UI;

/// <summary>
/// Shows who has the game paused or a menu open, and which menu.
///
/// Online there is one game: when any player pauses or opens a menu, it opens on every PC on the same frame, the
/// game stops for everyone, and the player who opened it (the game's "UI player") is the only one who can move in
/// it. Everyone sees the same screens, but not whose cursor that is; a notice says so ("[P2] ASSIGN PLAYERS",
/// <see cref="PlayerBar"/>). It reads only the game's own state, which is identical on every PC, so it needs no network
/// messages and changes nothing.
/// </summary>
internal static class MenuStatus
{
    /// <summary>Menus a player navigates, from the bottom of the stack up (the last open one is the one in use).</summary>
    private static readonly (Type type, string label)[] Menus =
    {
        (typeof(PauseMenu), "Pause menu"),
        (typeof(GameMenu), "Menu"),
        (typeof(MapScreen), "Map"),
        (typeof(ShopMenu), "Shop"),
        (typeof(CookingScreen), "Cooking"),
        (typeof(SaveSelectionScreen), "Saves"),
        (typeof(RelicSelectionScreen), "Relics"),
        (typeof(CharacterSelectionScreen), "Party"),
        (typeof(OptionsScreen), "Options"),
        (typeof(RemappingMenu), "Controls"),
        (typeof(AssignControllersScreen), "Assign Players"),
        (typeof(LevelUpScreen), "Level Up"),
        (typeof(HowToPlayScreen), "How to play"),
        (typeof(AchievementScreen), "Achievements"),
        (typeof(ShopPopup), "Shop"),
        (typeof(GenericPopupScreen), "Question"),
    };

    private static Il2CppSystem.Type[] _il2cppTypes;

    /// <summary>Slot of the player who has the menu (-1: nobody, or unknown).</summary>
    public static int Slot { get; private set; } = -1;
    /// <summary>The open menus, bottom to top ("Pause menu › Options"); empty when none is open.</summary>
    public static string Path { get; private set; } = "";
    /// <summary>The open menus, bottom to top.</summary>
    public static IReadOnlyList<string> Open { get; private set; } = Array.Empty<string>();
    /// <summary>The menu in use (the top one of <see cref="Path"/>); empty when none is open.</summary>
    public static string Menu { get; private set; } = "";
    public static bool Paused { get; private set; }

    private static float _nextPoll;
    private static string _logged = "";
    private static GUIStyle _style;

    public static void Update()
    {
        var s = Session.Instance;
        if (s.Phase != Phase.Running || s.CatchingUp) { Clear(); return; }
        if (Time.realtimeSinceStartup < _nextPoll) return;
        _nextPoll = Time.realtimeSinceStartup + 0.15f;
        try { Poll(); }
        catch (Exception e) { Clear(); Plugin.Log.LogWarning($"[menu] {e.Message}"); _nextPoll = Time.realtimeSinceStartup + 5f; }

        string now = Paused || Path.Length > 0 ? $"{Slot}|{Paused}|{Path}" : "";
        if (now != _logged)
        {
            _logged = now;
            Plugin.Log.LogInfo(now.Length == 0
                ? $"[menu] f{s.Engine.AppliedFrame} closed"
                : $"[menu] f{s.Engine.AppliedFrame} {(Slot >= 0 ? $"P{Slot + 1}" : "?")} {(Paused ? "paused" : "not paused")}: {(Path.Length > 0 ? Path : "no menu")}");
        }
    }

    private static void Clear()
    {
        Slot = -1;
        Path = "";
        Menu = "";
        Open = Array.Empty<string>();
        Paused = false;
    }

    private static void Poll()
    {
        Paused = Manager<PauseManager>.instance?.isPaused ?? false;
        var ui = Manager<UIManager>.instance;
        var open = new List<string>();
        if (ui != null)
        {
            _il2cppTypes ??= Menus.Select(m => Il2CppType.From(m.type)).ToArray();
            var byType = ui.screensByType;
            for (int i = 0; i < Menus.Length; i++)
            {
                if (byType == null || !byType.ContainsKey(_il2cppTypes[i])) continue;
                var views = byType[_il2cppTypes[i]];
                bool shown = false;
                for (int v = 0; v < views.Count && !shown; v++)
                {
                    var view = views[v];
                    shown = view != null && view.gameObject.activeInHierarchy && !view.closing;
                }
                if (!shown) continue;
                string label = Menus[i].label;
                if (Menus[i].type == typeof(GameMenu)) label = GameMenuLabel(byType[_il2cppTypes[i]]);
                if (!open.Contains(label)) open.Add(label);
            }
        }
        Path = string.Join(GameUi.Arrow, open);
        Menu = open.Count > 0 ? open[^1] : "";
        Open = open;
        Slot = -1;
        if (Paused || open.Count > 0)
        {
            var player = GameBridge.Coop?.uiInputsPlayer;
            Slot = GameBridge.SlotOf(player);
            // The System player (menus outside gameplay) follows the host's input.
            if (Slot < 0 && player != null && player.id == Rewired.ReInput.players.GetSystemPlayer().id) Slot = 0;
        }
    }

    private static string GameMenuLabel(Il2CppSystem.Collections.Generic.List<View> views)
    {
        for (int v = 0; v < views.Count; v++)
        {
            var menu = views[v]?.TryCast<GameMenu>();
            if (menu == null || !menu.gameObject.activeInHierarchy) continue;
            return menu.currentSectionId switch
            {
                GameMenu.GameMenuSectionId.Characters => "Menu: Characters",
                GameMenu.GameMenuSectionId.Inventory => "Menu: Inventory",
                GameMenu.GameMenuSectionId.Relics => "Menu: Relics",
                _ => "Menu",
            };
        }
        return "Menu";
    }

    /// <summary>Immediately closes/pools any open modal menu screens (used before an in-place save reload).</summary>
    public static int CloseOpenMenus()
    {
        int closed = 0;
        try
        {
            var ui = Manager<UIManager>.instance;
            var byType = ui?.screensByType;
            if (ui == null || byType == null) return 0;
            _il2cppTypes ??= Menus.Select(m => Il2CppType.From(m.type)).ToArray();
            for (int i = Menus.Length - 1; i >= 0; i--)
            {
                if (!byType.ContainsKey(_il2cppTypes[i])) continue;
                var views = byType[_il2cppTypes[i]];
                if (views == null) continue;
                for (int v = views.Count - 1; v >= 0; v--)
                {
                    var view = views[v];
                    if (view == null || !view.gameObject.activeInHierarchy) continue;
                    try
                    {
                        ui.PoolScreen(view, false);
                        closed++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[reload] PoolScreen({Menus[i].label}): {e.Message}"); }
                }
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[reload] CloseOpenMenus: {e.Message}"); }
        return closed;
    }

    /// <summary>(main line, second line) of the banner, or null when nobody has the game paused or a menu open.</summary>
    private static (string main, string sub)? Banner()
    {
        var s = Session.Instance;
        if (s.Phase != Phase.Running || s.CatchingUp) return null;
        if (!Paused && Path.Length == 0) return LocalPauseTip(s);
        if (Slot == s.LocalSlot)
            return ($"{(Paused ? "Paused for everyone" : "Everyone sees this menu")}{(Path.Length > 0 ? $": {Path}" : "")}",
                "The others wait until you close it.");
        string who = Slot < 0 ? "Someone" : $"P{Slot + 1} {s.SlotName[Slot] ?? ""}".TrimEnd();
        return ($"{who} {(Paused ? "paused the game" : "opened a menu")}{(Path.Length > 0 ? $": {Path}" : "")}",
            "They're in control until they close it.");
    }

    /// <summary>
    /// Others' local pause screens (no shared menu open): "P2 is paused". Not on the paused player's own PC, where the
    /// pause screen itself says so.
    /// </summary>
    private static (string main, string sub)? LocalPauseTip(Session s)
    {
        var slots = new List<int>();
        for (int i = 0; i < s.SlotPaused.Length; i++)
            if (s.SlotPaused[i] && i != s.LocalSlot) slots.Add(i);
        if (slots.Count == 0) return null;
        string who = string.Join(" and ", slots.Select(i => $"P{i + 1}"));
        string names = string.Join(" and ", slots.Select(i => s.SlotName[i]).Where(n => !string.IsNullOrEmpty(n)));
        return ($"{who} {(slots.Count == 1 ? "is" : "are")} paused",
            $"The game keeps running.");
    }

    /// <summary>Fallback without the game's UI art: a plain banner at the bottom of the screen.</summary>
    public static void OnGUI()
    {
        if (GameUi.Ready) return;
        var banner = Banner();
        if (banner == null) return;
        string text = banner.Value.main + ". " + banner.Value.sub;
        _style ??= new GUIStyle(GUI.skin.box) { fontSize = 17, wordWrap = true, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(16, 16, 8, 8) };
        float w = Mathf.Min(760, Screen.width - 40);
        float h = _style.CalcHeight(new GUIContent(text), w) + 2;
        GUI.Box(new Rect((Screen.width - w) / 2, Screen.height - h - 20, w, h), text, _style);
    }
}
