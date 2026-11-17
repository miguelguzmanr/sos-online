using System.Linq;
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

    /// <summary>"Pause for everyone" chosen: the next captured input of this PC's slot carries a pause press.</summary>
    private static bool _forwardStart;

    private enum Page { Main, Options, ConfirmLeave, ConfirmReturn, NoSave }

    private static Page _page;
    private static int _returnEntry;
    private static Page _returnFrom;
    private static int _cursor;
    private static int _start = -2, _confirm, _back, _vertical, _horizontal;

    /// <summary>Actions held when the screen closed: kept out of the input until released, so they don't arrive as new presses.</summary>
    private static readonly HashSet<int> _held = new();

    private static void Init()
    {
        if (_start != -2) return;
        _start = ActionTable.IndexOf("UI_Start");
        _confirm = ActionTable.IndexOf("UI_Confirm");
        _back = ActionTable.IndexOf("UI_Back");
        _vertical = ActionTable.IndexOf("UIMoveVertical");
        _horizontal = ActionTable.IndexOf("UIMoveHorizontal");
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
        if (IsOpen && (_page == Page.NoSave || _page == Page.ConfirmReturn) && !HostNoSave(s)) Close("return to area start no longer needed");

        if (!IsOpen && own && _forwardStart)
        {
            _forwardStart = false;
            snap = StripHeld(snap, true);
            if (_start >= 0) snap.Bits[_start] |= ActionBits.Button | ActionBits.ButtonDown;
            Plugin.Log.LogInfo($"[localpause] f{s.Engine.AppliedFrame} pause press sent for everyone (input frame {frame})");
            return snap;
        }

        if (!IsOpen)
        {
            if (!own || !Down(snap, _start) || !CanOpen()) return StripHeld(snap, own);
            Open(frame);
            return new InputSnapshot(); // the opening press is not also a navigation press
        }

        if (own) Navigate(snap);
        return new InputSnapshot();
    }

    /// <summary>
    /// Free gameplay: no battle, cutscene, loading, or shared pause or menu. Or, for the host, where the game doesn't
    /// allow saving while a friend waits to join (they'd wait until it does): a page with Return to area start
    /// (<see cref="Page.NoSave"/>). Otherwise Start there goes to the game as usual.
    /// </summary>
    private static bool CanOpen()
    {
        var s = Session.Instance;
        if (s.Phase != Phase.Running || s.CatchingUp || !s.Engine.InSessionAt(s.LocalSlot, s.Engine.AppliedFrame)) return false;
        if (MenuStatus.Paused || MenuStatus.Path.Length > 0) return false;
        try
        {
            if (Manager<PauseManager>.instance?.isPaused ?? false) return false;
            if (HostNoSave(s)) return true;
            if ((Manager<PauseManager>.instance?.pauseDisablers ?? 0) > 0) return false;
        }
        catch { return false; }
        return GameBridge.CanSaveNow;
    }

    private static bool HostNoSave(Session s) => s.IsHost && !GameBridge.CanSaveNow && s.CanReturnToAreaStart;

    private static void Open(int frame)
    {
        IsOpen = true;
        _page = HostNoSave(Session.Instance) ? Page.NoSave : Page.Main;
        _cursor = _page == Page.NoSave ? Entries().Count - 1 : 0; // on Back: a stray press mustn't undo the area
        _held.Clear();
        Plugin.Log.LogInfo($"[localpause] f{Session.Instance.Engine.AppliedFrame} opened (input frame {frame})");
        Session.Instance.SetLocalPaused(true);
    }

    private static void Close(string why, InputSnapshot raw = null)
    {
        if (!IsOpen) return;
        raw ??= _raw;
        IsOpen = false;
        _held.Clear();
        if (raw != null)
            for (int i = 0; i < raw.Bits.Length; i++)
                if ((raw.Bits[i] & (ActionBits.Button | ActionBits.NegButton)) != 0 || raw.Axis[i] != 0) _held.Add(i);
        LocalOptions.SaveIfChanged();
        Plugin.Log.LogInfo($"[localpause] f{Session.Instance.Engine.AppliedFrame} closed: {why}");
        Session.Instance.SetLocalPaused(false);
    }

    /// <summary>Leaving the session or a stop: close without sending anything.</summary>
    public static void Reset()
    {
        IsOpen = false;
        _forwardStart = false;
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

    private sealed class Entry
    {
        public string Label;
        public System.Func<string> Value;
        public System.Action Action;
        public System.Action<int> Change;
    }

    /// <summary>
    /// Test harness (--sosonline-pausechoose): choose the entry with this label on the open page, as a press would.
    /// Chosen by label so a test doesn't break when entries are added or reordered.
    /// </summary>
    internal static bool ChooseForTest(string label)
    {
        if (!IsOpen) return false;
        var entry = Entries().Find(e => e.Label == label);
        if (entry?.Action == null) return false;
        entry.Action();
        return true;
    }

    private static List<Entry> Entries()
    {
        var s = Session.Instance;
        var list = new List<Entry>();
        switch (_page)
        {
            case Page.ConfirmLeave:
                list.Add(new Entry
                {
                    Label = s.IsHost ? "End" : "Leave",
                    Action = () =>
                    {
                        if (s.IsHost) s.EndSessionScheduled("the host ended it");
                        else s.RequestLeave();
                        Close(s.IsHost ? "end session" : "leave session");
                    },
                });
                list.Add(new Entry { Label = "Back", Action = () => GoTo(Page.Main, 2) });
                break;
            case Page.ConfirmReturn:
                list.Add(new Entry { Label = "Return", Action = () => { s.ReturnToAreaStart(); Close("return to area start"); } });
                list.Add(new Entry { Label = "Back", Action = () => GoTo(_returnFrom, _returnEntry) });
                break;
            case Page.NoSave:
                // The host, where the game doesn't allow saving, while a friend waits to join.
                list.Add(new Entry { Label = "Return to area start", Action = () => { _returnFrom = Page.NoSave; _returnEntry = 0; GoTo(Page.ConfirmReturn, 1); } });
                // The game's own pause, shared as on the main page. Not in battles: the game has no pause there and
                // ignores Start (host and guests alike; findings §8), so the entry would do nothing.
                if (!GameBridge.InBattle)
                    list.Add(new Entry { Label = "Pause for everyone", Action = () => { Close("pause for everyone"); _forwardStart = true; } });
                list.Add(new Entry { Label = "Back", Action = () => Close("back") });
                break;
            case Page.Options:
                foreach (var (field, label) in LocalOptions.Rows)
                {
                    var f = field;
                    list.Add(new Entry { Label = label, Value = () => LocalOptions.Value(f), Change = dir => LocalOptions.Change(f, dir) });
                }
                list.Add(new Entry { Label = "Back", Action = () => GoTo(Page.Main, 1) });
                break;
            default:
                list.Add(new Entry { Label = "Resume", Action = () => Close("resume") });
                if (LocalOptions.Available) list.Add(new Entry { Label = "Options", Action = () => GoTo(Page.Options, 0) });
                // The game's own pause menu (Assign Players, the game's Options, return to title), shared as before:
                // the next synced input carries a pause press that this screen lets through.
                list.Add(new Entry { Label = "Pause for everyone", Action = () => { Close("pause for everyone"); _forwardStart = true; } });
                list.Add(new Entry { Label = s.IsHost ? "End co-op" : "Leave co-op", Action = () => GoTo(Page.ConfirmLeave, 1) });
                // Last, so the cursor positions above stay as they were.
                if (!s.IsHost) list.Add(new Entry { Label = "Send logs to host", Action = () => { s.SendLogsToHost(); Close("send logs"); } });
                else if (s.Peers.Any()) list.Add(new Entry { Label = "Ask for logs", Action = () => { s.AskForLogs(); Close("ask for logs"); } });
                break;
        }
        return list;
    }

    private static void GoTo(Page page, int cursor)
    {
        if (_page == Page.Options) LocalOptions.SaveIfChanged();
        _page = page;
        _cursor = cursor;
    }

    private static InputSnapshot _raw;

    private static void Navigate(InputSnapshot raw)
    {
        var entries = Entries();
        _raw = raw;
        try
        {
            if (Down(raw, _start)) { Close("resume (pause)"); return; }
            if (Down(raw, _back))
            {
                if (_page == Page.Main || _page == Page.NoSave) Close("resume (back)");
                else if (_page == Page.ConfirmReturn) GoTo(_returnFrom, _returnEntry);
                else GoTo(Page.Main, _page == Page.Options && LocalOptions.Available ? 1 : 0);
                return;
            }
            if (Down(raw, _vertical)) _cursor = (_cursor + entries.Count - 1) % entries.Count;
            if (NegDown(raw, _vertical)) _cursor = (_cursor + 1) % entries.Count;
            var e = entries[Mathf.Clamp(_cursor, 0, entries.Count - 1)];
            if (e.Change != null)
            {
                if (Down(raw, _horizontal) || Repeat(raw, _horizontal, false)) e.Change(1);
                else if (NegDown(raw, _horizontal) || Repeat(raw, _horizontal, true)) e.Change(-1);
            }
            if (Down(raw, _confirm) && e.Action != null)
            {
                Plugin.Log.LogInfo($"[localpause] f{Session.Instance.Engine.AppliedFrame} chose {e.Label}");
                e.Action();
            }
        }
        finally { _raw = null; }
    }

    /// <summary>A held direction repeats (Rewired's repeat state), so a slider can be swept by holding.</summary>
    private static bool Repeat(InputSnapshot s, int action, bool negative) =>
        action >= 0 && (s.Bits[action] & (negative ? ActionBits.NegRepeating : ActionBits.Repeating)) != 0
        && (s.Bits[action] & (negative ? ActionBits.NegButtonDown : ActionBits.ButtonDown)) == 0;

    // ------------------------------------------------------------------ screen (canvas)

    private static Canvas _canvas;
    private static RectTransform _panel;
    private static TMPro.TextMeshProUGUI _title, _hint;
    private static readonly List<(RectTransform row, UnityEngine.UI.Image cursor, TMPro.TextMeshProUGUI text, TMPro.TextMeshProUGUI value)> _rows = new();
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
        string key = $"{_page}|{_cursor}|{string.Join("|", entries.ConvertAll(e => e.Label + "=" + e.Value?.Invoke()))}";
        if (key == _shown) return;
        _shown = key;

        var s = Session.Instance;
        _title.text = Title(s);
        _hint.text = GameUi.Drawable(Hint(s), true);

        while (_rows.Count < entries.Count) _rows.Add(Row(_rows.Count));
        for (int i = 0; i < _rows.Count; i++)
        {
            bool on = i < entries.Count;
            _rows[i].row.gameObject.SetActive(on);
            if (!on) continue;
            _rows[i].text.text = GameUi.Drawable(entries[i].Label);
            string value = entries[i].Value?.Invoke();
            _rows[i].value.text = string.IsNullOrEmpty(value) ? "" : GameUi.Drawable($"< {value} >");
            _rows[i].cursor.gameObject.SetActive(i == _cursor);
            _rows[i].text.color = _rows[i].value.color = i == _cursor ? GameUi.TextMain : GameUi.TextSecondary;
        }
        float y = Top + entries.Count * RowH + 2;
        GameUi.Place(_separator2, 6, y, W - 12, 6);
        GameUi.Place(_hint.rectTransform, Pad, y + 11, W - 2 * Pad, 20);
        _panel.sizeDelta = new Vector2(W, y + 11 + 22);
    }

    private static string Title(Session s) => _page switch
    {
        Page.Options => "Options",
        Page.ConfirmLeave => s.IsHost ? "End co-op for everyone?" : "Leave co-op?",
        Page.ConfirmReturn => "Return to the area start?",
        _ => "Paused",
    };

    private static string Hint(Session s) => _page switch
    {
        Page.Options => "These settings only change on this PC.",
        Page.ConfirmLeave => s.IsHost ? "Everyone may continue playing solo." : "The AI takes over your character.",
        Page.ConfirmReturn => "Progress since entering this area will be lost.",
        Page.NoSave => "Only you are paused. The game keeps running.",
        _ => "Only you are paused. The game keeps running.",
    };

    private static (RectTransform, UnityEngine.UI.Image, TMPro.TextMeshProUGUI, TMPro.TextMeshProUGUI) Row(int i)
    {
        var row = GameUi.Rect(_panel, "Row" + i);
        GameUi.Place(row, 6, Top + i * RowH, W - 12, RowH - 2);
        var cursor = GameUi.Image(row, "Cursor", "panel-rounded-shape-1px", UnityEngine.UI.Image.Type.Sliced, new Color(GameUi.Highlight.r, GameUi.Highlight.g, GameUi.Highlight.b, 0.35f));
        GameUi.Stretch(cursor.rectTransform);
        var text = GameUi.Text(row, "Label", false, TMPro.TextAlignmentOptions.Left);
        text.enableWordWrapping = false;
        GameUi.Stretch(text.rectTransform);
        text.rectTransform.offsetMin = new Vector2(Pad - 6, 0);
        var value = GameUi.Text(row, "Value", false, TMPro.TextAlignmentOptions.Right);
        value.enableWordWrapping = false;
        GameUi.Stretch(value.rectTransform);
        value.rectTransform.offsetMax = new Vector2(-(Pad - 6), 0);
        return (row, cursor, text, value);
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
        var s = Session.Instance;
        var lines = new List<string> { Title(s), Hint(s) };
        for (int i = 0; i < entries.Count; i++)
        {
            string value = entries[i].Value?.Invoke();
            lines.Add((i == _cursor ? "> " : "   ") + entries[i].Label + (string.IsNullOrEmpty(value) ? "" : $": {value}"));
        }
        string text = string.Join("\n", lines);
        _style ??= new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(20, 20, 12, 12) };
        float w = 420, h = _style.CalcHeight(new GUIContent(text), w) + 4;
        GUI.Box(new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h), text, _style);
    }
}
