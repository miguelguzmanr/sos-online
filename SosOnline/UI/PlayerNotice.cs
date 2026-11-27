using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SosOnline.UI;

/// <summary>
/// Small notices about one player, in the look and place of the game's own co-op notices ("[P3] LEFT", on the right):
/// whose battle turn it is, who is paused, who has a shared menu open. A timed notice (<see cref="Flash"/>) shows for a
/// few seconds; a held one (<see cref="Hold"/>) shows while it is asked for every frame. They stack upwards, above the
/// game's own notices.
///
/// The game's own join and leave notices stay; during a session they also say the player's name
/// (<see cref="GameNoticeName"/>).
///
/// Display only: it changes nothing in the game, so it may differ between PCs.
/// </summary>
internal static class PlayerNotice
{
    // The game's PlayerLeftNotification, measured with --sosonline-uiprobe (2026-11-26).
    private const float Height = 21, IconXY = 3, Icon = 15, TextX = 23, TextY = 5, TextH = 11, PadRight = 4;
    /// <summary>Where the game's notice sits: its right edge this far from the screen's, its bottom this far up.</summary>
    private const float Right = 7, Bottom = 129;
    /// <summary>Between two notices: the outlines (5 pixels each) just apart.</summary>
    private const float Gap = 11;
    private const float FadeSeconds = 0.2f;

    private sealed class Entry
    {
        public string Key;
        public int Number;
        public string Text;
        public float Until, HeldAt = -1;
        public float Alpha;
        public RectTransform Root;
        public CanvasGroup Group;
        public Image Icon;
        public TextMeshProUGUI Label, Fallback;
    }

    private static Canvas _canvas;
    private static readonly List<Entry> _entries = new();
    private static float Now => Time.realtimeSinceStartup;

    /// <summary>Shows a notice for <paramref name="seconds"/>; one with the same key is replaced.</summary>
    public static void Flash(string key, int number, string text, float seconds = 3f)
    {
        var e = Get(key, number, text);
        e.Until = Now + seconds;
        e.HeldAt = -1;
    }

    /// <summary>Shows a notice for as long as this is called every frame.</summary>
    public static void Hold(string key, int number, string text)
    {
        var e = Get(key, number, text);
        e.HeldAt = Now;
        e.Until = Now + 0.25f;
    }

    private static Entry Get(string key, int number, string text)
    {
        var e = _entries.FirstOrDefault(x => x.Key == key);
        if (e == null) { e = new Entry { Key = key }; _entries.Add(e); }
        if (e.Number != number || e.Text != text) { e.Number = number; e.Text = text; if (e.Root != null) Fill(e); }
        return e;
    }

    public static void Update()
    {
        GameNoticeName.Update();
        if (_entries.Count == 0) return;
        if (!GameUi.Ready) return;
        if (_canvas == null) _canvas = GameUi.CreateCanvas("SosOnline_PlayerNotice", 29000);

        float y = Bottom + GameNoticeName.GameNoticesShown() * (Height + Gap);
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            bool on = Now < e.Until;
            e.Alpha = Mathf.MoveTowards(e.Alpha, on ? 1 : 0, Time.unscaledDeltaTime / FadeSeconds);
            if (!on && e.Alpha <= 0)
            {
                if (e.Root != null) Object.Destroy(e.Root.gameObject);
                _entries.RemoveAt(i--);
                continue;
            }
            if (e.Root == null) Build(e);
            e.Group.alpha = e.Alpha;
            e.Root.anchoredPosition = new Vector2(-Right, y);
            y += Height + Gap;
        }
    }

    private static void Build(Entry e)
    {
        e.Root = GameUi.NoticePanel(GameUi.Area(_canvas), "Notice");
        e.Root.anchorMin = e.Root.anchorMax = e.Root.pivot = new Vector2(1, 0);
        e.Group = e.Root.gameObject.AddComponent<CanvasGroup>();
        e.Group.interactable = false;
        e.Group.blocksRaycasts = false;
        e.Group.alpha = 0;
        e.Icon = GameUi.Image(e.Root, "PlayerIcon", null, Image.Type.Simple, Color.white);
        GameUi.Place(e.Icon.rectTransform, IconXY, IconXY, Icon, Icon);
        e.Fallback = GameUi.Text(e.Icon.transform, "Number", false, TextAlignmentOptions.Center);
        GameUi.Stretch(e.Fallback.rectTransform);
        e.Label = GameUi.NoticeText(e.Root, "Text");
        Fill(e);
    }

    private static void Fill(Entry e)
    {
        PlayerBar.PlayerIcon(e.Icon, e.Fallback, e.Number);
        e.Label.text = GameUi.Drawable(e.Text, true);
        float w = Mathf.Ceil(e.Label.GetPreferredValues(e.Label.text).x);
        // "[P2]'s turn": the apostrophe goes right after the icon, as after a word.
        float x = e.Text.StartsWith("'") ? TextX - 4 : TextX;
        GameUi.Place(e.Label.rectTransform, x, TextY, w, TextH);
        e.Root.sizeDelta = new Vector2(x + w + PadRight, Height);
    }

    /// <summary>Removes every notice (a session ended).</summary>
    public static void Clear()
    {
        foreach (var e in _entries) if (e.Root != null) Object.Destroy(e.Root.gameObject);
        _entries.Clear();
    }
}

/// <summary>
/// The game's own co-op join and leave notices ("[P3] LEFT") say who during a session: "[P3] ROBIN LEFT". The game
/// sets the text when the notice is made; the name goes in front of it.
/// </summary>
[HarmonyPatch(typeof(PlayerJoinLeaveNotification), nameof(PlayerJoinLeaveNotification.Init))]
internal static class GameNoticeName
{
    private static readonly List<(PlayerJoinLeaveNotification notice, TextMeshProUGUI text, string name)> _named = new();
    /// <summary>Each slot's last name: a leaver's name is gone from the session by the time the game makes its notice.</summary>
    private static readonly string[] _lastName = new string[InputOverride.SlotCount];
    private static RectTransform _container;
    private static float _nextFind;

    private static void Postfix(PlayerJoinLeaveNotification __instance, SinglePlayerPlusPlayer __0)
    {
        if (!NativeCode.Is<PlayerJoinLeaveNotification>(__instance) || __0 == null) return;
        try
        {
            var s = Session.Instance;
            if (!s.InSession || s.Phase != Phase.Running) return;
            int slot = -1;
            for (int i = 0; i < InputOverride.SlotCount && slot < 0; i++)
                if (GameBridge.CoopPlayer(i)?.Pointer == __0.Pointer) slot = i;
            string name = slot < 0 ? null : !string.IsNullOrEmpty(s.SlotName[slot]) ? s.SlotName[slot] : _lastName[slot];
            if (string.IsNullOrEmpty(name)) { Plugin.Log.LogInfo($"[notice] game notice for slot {slot}: no name"); return; }
            var text = __instance.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text == null) return;
            name = PlayerNames.Shorten(name, 12);
            _named.Add((__instance, text, name));
            Apply(text, name);
        }
        catch (System.Exception e) { Plugin.Log.LogWarning($"[notice] {e.Message}"); }
    }

    private static void Apply(TextMeshProUGUI text, string name)
    {
        string current = text.text ?? "";
        if (current.StartsWith(name + " ")) return;
        text.text = GameUi.Drawable(name, true) + " " + current;
    }

    /// <summary>Keeps the name in front if the game sets the text again; forgets notices that closed.</summary>
    public static void Update()
    {
        var s = Session.Instance;
        for (int slot = 0; slot < _lastName.Length; slot++)
            if (!string.IsNullOrEmpty(s.SlotName[slot])) _lastName[slot] = s.SlotName[slot];
        for (int i = _named.Count - 1; i >= 0; i--)
        {
            var (notice, text, name) = _named[i];
            bool alive;
            try { alive = notice != null && text != null && notice.gameObject.activeInHierarchy; } catch { alive = false; }
            if (!alive) { _named.RemoveAt(i); continue; }
            try { Apply(text, name); } catch { _named.RemoveAt(i); }
        }
    }

    /// <summary>How many of the game's own join or leave notices are showing now.</summary>
    public static int GameNoticesShown()
    {
        try
        {
            if (_container == null && Now >= _nextFind)
            {
                _nextFind = Now + 2f;
                var screen = Object.FindObjectOfType<SinglePlayerPlusScreen>();
                _container = screen?.playerJoinedNotificationParent;
            }
            if (_container == null || !_container.gameObject.activeInHierarchy) return 0;
            int n = 0;
            for (int i = 0; i < _container.childCount; i++)
                if (_container.GetChild(i).gameObject.activeSelf) n++;
            return n;
        }
        catch { _container = null; return 0; }
    }

    private static float Now => Time.realtimeSinceStartup;
}
