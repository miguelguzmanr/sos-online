using System.Collections.Generic;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline.UI;

/// <summary>
/// The pause screen online (tier 1 of independent menus): a player who pauses gets a screen on their own PC only.
/// The world keeps running for everyone, them included; nobody else's game stops or shows a menu.
///
/// How: the local pause press is taken out of the input before it is sent, so the shared game never sees it, and
/// while the screen is open this PC sends neutral input for its slot (its character idles like an idle co-op
/// player). The keys still drive this screen, read from the captured input before it is neutralised. Every PC gets
/// the same (neutral) input, so this changes nothing about determinism, and a joiner catching up replays nothing of it.
///
/// Only in free gameplay: in a battle, cutscene, loading or an already open shared menu the press goes to the game
/// as before (the shared pause there can skip cutscenes and the like). Others see "P2 is paused" through a
/// display-only message (<see cref="Session.SetLocalPaused"/>).
/// </summary>
internal static class LocalPause
{
    public static bool IsOpen { get; private set; }

    private enum Page { Main, ConfirmLeave }

    private static Page _page;
    private static int _cursor;
    private static int _start = -2, _confirm, _back, _vertical;

    /// <summary>Actions held when the screen closed: kept out of the input until released, so they don't arrive as new presses.</summary>
    private static readonly HashSet<int> _held = new();

    private static void Init()
    {
        if (_start != -2) return;
        _start = ActionTable.IndexOf("UI_Start");
        _confirm = ActionTable.IndexOf("UI_Confirm");
        _back = ActionTable.IndexOf("UI_Back");
        _vertical = ActionTable.IndexOf("UIMoveVertical");
    }

    /// <summary>
    /// The last stage of the local input filter (after test scripts, so a scripted pause opens this screen too).
    /// Runs on the capture of every frame, for this PC's slot and (host) the System channel.
    /// </summary>
    public static InputSnapshot Filter(int frame, int channel, InputSnapshot snap)
    {
        var s = Session.Instance;
        bool own = channel == s.LocalSlot;
        if (!own && channel != InputOverride.SystemChannel) return snap;
        if (!ActionTable.Ready) return snap;
        Init();

        if (IsOpen && (s.Phase != Phase.Running || s.CatchingUp)) Close("session not running");

        if (!IsOpen)
        {
            if (!own || !Down(snap, _start) || !CanOpen()) return StripHeld(snap, own);
            Open(frame);
            return new InputSnapshot(); // the opening press is not also a navigation press
        }

        if (own) Navigate(snap);
        return new InputSnapshot();
    }

    /// <summary>Free gameplay: no battle, cutscene, loading, or shared pause or menu.</summary>
    private static bool CanOpen()
    {
        var s = Session.Instance;
        if (s.Phase != Phase.Running || s.CatchingUp || !s.Engine.InSessionAt(s.LocalSlot, s.Engine.AppliedFrame)) return false;
        if (MenuStatus.Paused || MenuStatus.Path.Length > 0) return false;
        try
        {
            if (Manager<PauseManager>.instance?.isPaused ?? false) return false;
            if ((Manager<PauseManager>.instance?.pauseDisablers ?? 0) > 0) return false;
        }
        catch { return false; }
        return GameBridge.CanSaveNow;
    }

    private static void Open(int frame)
    {
        IsOpen = true;
        _page = Page.Main;
        _cursor = 0;
        _held.Clear();
        Plugin.Log.LogInfo($"[localpause] f{Session.Instance.Engine.AppliedFrame} opened (input frame {frame})");
        Session.Instance.SetLocalPaused(true);
    }

    private static void Close(string why, InputSnapshot raw = null)
    {
        if (!IsOpen) return;
        IsOpen = false;
        _held.Clear();
        if (raw != null)
            for (int i = 0; i < raw.Bits.Length; i++)
                if ((raw.Bits[i] & (ActionBits.Button | ActionBits.NegButton)) != 0 || raw.Axis[i] != 0) _held.Add(i);
        Plugin.Log.LogInfo($"[localpause] f{Session.Instance.Engine.AppliedFrame} closed: {why}");
        Session.Instance.SetLocalPaused(false);
    }

    /// <summary>Leaving the session or a stop: close without sending anything.</summary>
    public static void Reset()
    {
        IsOpen = false;
        _held.Clear();
    }

    private static InputSnapshot StripHeld(InputSnapshot snap, bool own)
    {
        if (_held.Count == 0) return snap;
        var released = own ? new List<int>() : null;
        foreach (int i in _held)
        {
            if (own && (snap.Bits[i] & (ActionBits.Button | ActionBits.NegButton)) == 0 && snap.Axis[i] == 0) { released.Add(i); continue; }
            snap.Bits[i] = 0;
            snap.Axis[i] = 0;
            snap.AxisRaw[i] = 0;
        }
        if (released != null) foreach (int i in released) _held.Remove(i);
        return snap;
    }

    private static bool Down(InputSnapshot s, int action) => action >= 0 && (s.Bits[action] & ActionBits.ButtonDown) != 0;
    private static bool NegDown(InputSnapshot s, int action) => action >= 0 && (s.Bits[action] & ActionBits.NegButtonDown) != 0;

    private static List<(string label, System.Action action)> Entries()
    {
        var s = Session.Instance;
        var list = new List<(string, System.Action)> { ("Resume", null) };
        if (_page == Page.ConfirmLeave)
        {
            list.Clear();
            list.Add((s.IsHost ? "End session for everyone" : "Leave session", () =>
            {
                if (s.IsHost) s.EndSessionScheduled("The host ended the co-op session");
                else s.RequestLeave();
            }));
            list.Add(("Back", () => { _page = Page.Main; _cursor = 0; }));
            return list;
        }
        list.Add((s.IsHost ? "End session" : "Leave session", () => { _page = Page.ConfirmLeave; _cursor = 1; }));
        return list;
    }

    private static void Navigate(InputSnapshot raw)
    {
        var entries = Entries();
        if (Down(raw, _start)) { Close("resume (pause)", raw); return; }
        if (Down(raw, _back))
        {
            if (_page == Page.Main) Close("resume (back)", raw);
            else { _page = Page.Main; _cursor = 0; }
            return;
        }
        if (Down(raw, _vertical)) _cursor = (_cursor + entries.Count - 1) % entries.Count;
        if (NegDown(raw, _vertical)) _cursor = (_cursor + 1) % entries.Count;
        if (Down(raw, _confirm))
        {
            var (label, action) = entries[Mathf.Clamp(_cursor, 0, entries.Count - 1)];
            Plugin.Log.LogInfo($"[localpause] f{Session.Instance.Engine.AppliedFrame} chose {label}");
            if (action == null) { Close("resume", raw); return; }
            bool confirming = _page == Page.ConfirmLeave;
            action();
            if (confirming && label != "Back") Close(label, raw);
        }
    }

    // ------------------------------------------------------------------ screen (canvas)

    private static Canvas _canvas;
    private static RectTransform _panel;
    private static TMPro.TextMeshProUGUI _title, _hint;
    private static readonly List<(RectTransform row, UnityEngine.UI.Image cursor, TMPro.TextMeshProUGUI text)> _rows = new();
    private static string _shown;
    private static RectTransform _separator2;

    private const float W = 260, Pad = 12, RowH = 16, Top = 37;

    public static void Update()
    {
        bool show = IsOpen && GameUi.Ready;
        if (show && _canvas == null) Build();
        if (_canvas != null && _canvas.gameObject.activeSelf != show) _canvas.gameObject.SetActive(show);
        if (!show) { _shown = null; return; }

        var entries = Entries();
        string key = $"{_page}|{_cursor}|{string.Join("|", entries.ConvertAll(e => e.label))}";
        if (key == _shown) return;
        _shown = key;

        var s = Session.Instance;
        _title.text = _page == Page.Main ? "Paused" : s.IsHost ? "End the co-op session?" : "Leave the co-op session?";
        _hint.text = GameUi.Drawable(_page == Page.Main
            ? "Only you are paused. The game keeps running for everyone."
            : s.IsHost ? "Everyone keeps playing on their own from here." : "Your character goes back to the party AI; the others keep playing.", true);

        while (_rows.Count < entries.Count) _rows.Add(Row(_rows.Count));
        for (int i = 0; i < _rows.Count; i++)
        {
            bool on = i < entries.Count;
            _rows[i].row.gameObject.SetActive(on);
            if (!on) continue;
            _rows[i].text.text = GameUi.Drawable(entries[i].label);
            _rows[i].cursor.gameObject.SetActive(i == _cursor);
            _rows[i].text.color = i == _cursor ? GameUi.TextMain : GameUi.TextSecondary;
        }
        float y = Top + entries.Count * RowH + 2;
        GameUi.Place(_separator2, 6, y, W - 12, 6);
        GameUi.Place(_hint.rectTransform, Pad, y + 11, W - 2 * Pad, 20);
        _panel.sizeDelta = new Vector2(W, y + 11 + 22);
    }

    private static (RectTransform, UnityEngine.UI.Image, TMPro.TextMeshProUGUI) Row(int i)
    {
        var row = GameUi.Rect(_panel, "Row" + i);
        GameUi.Place(row, 6, Top + i * RowH, W - 12, RowH - 2);
        var cursor = GameUi.Image(row, "Cursor", "panel-rounded-shape-1px", UnityEngine.UI.Image.Type.Sliced, new Color(GameUi.Highlight.r, GameUi.Highlight.g, GameUi.Highlight.b, 0.35f));
        GameUi.Stretch(cursor.rectTransform);
        var text = GameUi.Text(row, "Label", false, TMPro.TextAlignmentOptions.Left);
        text.enableWordWrapping = false;
        GameUi.Stretch(text.rectTransform);
        text.rectTransform.offsetMin = new Vector2(Pad - 6, 0);
        return (row, cursor, text);
    }

    private static void Build()
    {
        // Above the game's menus, HUD and the menu banner; below the catch-up screen.
        _canvas = GameUi.CreateCanvas("SosOnline_LocalPause", 29500);
        _panel = GameUi.Panel(_canvas.transform, "Panel");
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0.5f, 0.5f);
        _panel.anchoredPosition = new Vector2(0, 10);
        _panel.sizeDelta = new Vector2(W, 100);
        _title = GameUi.Text(_panel, "Title", false, TMPro.TextAlignmentOptions.Left);
        _title.enableWordWrapping = false;
        GameUi.Place(_title.rectTransform, Pad, 9, W - 2 * Pad, 12);
        GameUi.Place(GameUi.Separator(_panel), 6, 25, W - 12, 6);
        _separator2 = GameUi.Separator(_panel);
        _hint = GameUi.Text(_panel, "Hint", true, TMPro.TextAlignmentOptions.TopLeft);
        _rows.Clear();
        _shown = null;
    }

    // ------------------------------------------------------------------ fallback (IMGUI)

    private static GUIStyle _style;

    public static void OnGUI()
    {
        if (!IsOpen || GameUi.Ready) return;
        var entries = Entries();
        var lines = new List<string> { _page == Page.Main ? "Paused (only you; the game keeps running)" : "Are you sure?" };
        for (int i = 0; i < entries.Count; i++) lines.Add((i == _cursor ? "> " : "   ") + entries[i].label);
        string text = string.Join("\n", lines);
        _style ??= new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(20, 20, 12, 12) };
        float w = 420, h = _style.CalcHeight(new GUIContent(text), w) + 4;
        GUI.Box(new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h), text, _style);
    }
}
