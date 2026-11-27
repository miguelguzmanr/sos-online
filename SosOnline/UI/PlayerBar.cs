using System.Collections.Generic;
using System.Linq;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SosOnline.UI;

/// <summary>
/// The players of the session in a small column at the top left, in the frame of the game's co-op notices: each
/// player's P1/P2/P3 icon (the game's <c>icn-player-square-N</c>) and, only when it matters, a status icon (paused,
/// joining, waited for, a high ping, re-syncing). In play it shows icons only; on the title screen, the pause screens, Assign Players and the save screen it stays and
/// adds names and status words. Over the game's other menus (main menu, shops, map), which have a panel there, it hides.
/// Rows never move: the status icon has its own column.
///
/// Whose battle turn it is, who is paused and who has a menu open show as notices (<see cref="PlayerNotice"/>).
///
/// Display only: it reads session and game state and changes nothing, so it may differ between PCs.
/// </summary>
internal static class PlayerBar
{
    private const float FadeSeconds = 0.4f, TurnSeconds = 2.5f;
    // Same insets as the game's notice: the icon 3 pixels in, text 5 pixels after it and 2 lower.
    private const float Left = 8, TopY = 8, Pad = 3, Icon = 15, RowGap = 3, StatusIcon = 11, TextGap = 5, TextH = 11;
    /// <summary>A ping above this shows on the player (the game itself feels it from about here).</summary>
    private const float HighPingMs = 150;
    /// <summary>The game's menus the bar stays over; the others fill the screen and have a panel where the bar sits.</summary>
    private static readonly string[] BarMenus = { "Pause menu", "Assign Players", "Saves", "Question" };

    private static Canvas _canvas;
    private static CanvasGroup _group;
    private static RectTransform _panel;
    private static readonly List<Chip> _chips = new();

    private static string _shownKey, _stateKey;
    private static float _alpha;
    private static int _turnFrame = -1;

    private sealed class Chip
    {
        public RectTransform Root;
        public Image Icon, StatusIcon;
        public TextMeshProUGUI Number, Name, Status;
    }

    private sealed class Row
    {
        public int Slot;
        public string Name;
        public string StatusIcon, Status;
        public bool Problem;
    }

    private static float Now => Time.realtimeSinceStartup;

    /// <summary>The game's colours for its players 1-3 (sampled from its P1/P2/P3 badges).</summary>
    public static Color PlayerColor(int number) => number switch
    {
        1 => new Color(85 / 255f, 241 / 255f, 1f),
        2 => new Color(1f, 190 / 255f, 19 / 255f),
        _ => new Color(1f, 108 / 255f, 174 / 255f),
    };

    public static void Update()
    {
        var s = Session.Instance;
        UpdateNotices(s);
        PlayerNotice.Update();

        var rows = Rows(s);
        bool wanted = rows.Count > 1 && !s.CatchingUp && !s.ResyncWaiting && GameUi.Ready;
        if (!wanted)
        {
            LogVisibility(rows.Count < 2 ? $"hidden: {rows.Count} player(s)" : s.CatchingUp ? "hidden: catching up"
                : s.ResyncWaiting ? "hidden: re-sync screen" : "hidden: game UI art not loaded yet");
            if (_canvas != null && _canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
            _stateKey = null;
            return;
        }
        if (_canvas == null) Build();
        if (!_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(true);

        bool names = GameBridge.AtTitleScreen || s.Phase != Phase.Running || LocalPause.IsOpen || MenuStatus.Paused || MenuStatus.Menu.Length > 0;
        string state = string.Join("|", rows.Select(r => $"{r.Slot}{GameBridge.PlayerNumber(r.Slot)}{r.Name}{r.Status}"));
        _stateKey = state;

        bool covered = MenuStatus.Open.Any(m => !BarMenus.Contains(m));
        // Always on outside the game's full-screen menus. It used to fade 4 s after a change in play, which players
        // took for the bar having vanished (2026-11-26).
        bool stay = !covered;
        LogVisibility(covered ? $"hidden: under {string.Join(" > ", MenuStatus.Open)}"
            : $"shown, {rows.Count} rows{(GameBridge.AtTitleScreen ? ", title screen" : "")}");
        _alpha = Mathf.MoveTowards(_alpha, stay ? 1 : 0, Time.unscaledDeltaTime / FadeSeconds);
        _group.alpha = _alpha;
        // The player icons load with the game's co-op HUD, so on the title screen they may come later than the rows:
        // lay out again once they do, or the bar keeps its "P1" text stand-ins.
        string icons = string.Concat(rows.Where(r => r.Slot >= 0)
            .Select(r => GameUi.Find($"icn-player-square-{GameBridge.PlayerNumber(r.Slot)}") != null ? '1' : '0'));
        string key = state + names + (_alpha > 0) + icons;
        if (_alpha > 0 && key != _shownKey) { _shownKey = key; Layout(rows, names); }
    }

    private static string _loggedVisibility = "";

    /// <summary>Logs when the bar shows or hides and why, so a player's log tells why it wasn't there.</summary>
    private static void LogVisibility(string what)
    {
        if (what == _loggedVisibility) return;
        _loggedVisibility = what;
        Plugin.Log.LogInfo($"[bar] {what}");
    }

    /// <summary>The bar's state, for test logs.</summary>
    public static string Describe()
    {
        var s = Session.Instance;
        var rows = Rows(s);
        string panel = _panel == null ? "none" : $"{_panel.anchoredPosition} {_panel.sizeDelta}";
        return $"phase {s.Phase}, rows {rows.Count}, gameui {GameUi.Ready}, canvas {(_canvas == null ? "none" : _canvas.gameObject.activeSelf.ToString())}, "
            + $"alpha {_alpha:F2}, menus [{string.Join(", ", MenuStatus.Open)}], panel {panel}, title {GameBridge.AtTitleScreen}";
    }

    // ---------------------------------------------------------------- what to show

    private static List<Row> Rows(Session s)
    {
        var rows = new List<Row>();
        if (!s.InSession || s.Phase is Phase.Idle or Phase.CreatingLobby or Phase.JoiningLobby or Phase.Connecting) return rows;
        var peers = s.Peers.ToList();
        bool running = s.Phase == Phase.Running && s.Engine.Running;
        for (int slot = 0; slot < InputOverride.SlotCount; slot++)
        {
            string name = s.SlotName[slot];
            if (string.IsNullOrEmpty(name)) continue;
            var row = new Row { Slot = slot, Name = name };
            bool inRun = running && s.Engine.InSessionAt(slot, s.Engine.AppliedFrame);
            float ping = s.IsHost
                ? peers.Where(p => p.slot == slot).Select(p => p.rtt).DefaultIfEmpty(0).First()
                : slot == 0 ? peers.Select(p => p.rtt).DefaultIfEmpty(0).First() : 0;
            if (running && !inRun && slot != s.LocalSlot) Set(row, "menu-icn-clock", "Joining", false);
            else if (s.SlotPaused[slot]) Set(row, "menu-icn-clock-pause", "Paused", false);
            else if (running && slot == s.LocalSlot && s.Sync.Desynced) Set(row, "icn-option-warning", "Re-syncing", true);
            else if (running && slot != s.LocalSlot && Now - s.Engine.WaitedAt[slot] < 1.5f) Set(row, "menu-icn-clock", "Waiting", true);
            else if (ping > HighPingMs) Set(row, "icn-option-warning", $"{ping:F0} ms", true);
            rows.Add(row);
        }
        if (s.IsHost)
            foreach (string joining in s.PendingNames)
            {
                var row = new Row { Slot = -1, Name = joining };
                Set(row, "menu-icn-clock", "Joining", false);
                rows.Add(row);
            }
        return rows;
    }

    private static void Set(Row row, string icon, string status, bool problem)
    {
        row.StatusIcon = icon;
        row.Status = status;
        row.Problem = problem;
    }

    /// <summary>
    /// The notices about players: whose battle turn begins, who is paused on their own pause screen, who has the
    /// game's menu open for everyone.
    /// </summary>
    private static void UpdateNotices(Session s)
    {
        if (s.Phase != Phase.Running || !s.Engine.Running || s.CatchingUp) { _turnFrame = -1; return; }

        int turn = BattleTurns.CurrentSlot;
        bool battle = GameBridge.InBattle && turn >= 0 && s.Engine.Occupied.Count(o => o) > 1;
        if (battle && BattleTurns.TurnFrame != _turnFrame)
        {
            _turnFrame = BattleTurns.TurnFrame;
            PlayerNotice.Flash("turn", GameBridge.PlayerNumber(turn), turn == s.LocalSlot ? "Your turn" : "'s turn", TurnSeconds);
        }
        if (!battle) _turnFrame = -1;

        for (int slot = 0; slot < s.SlotPaused.Length; slot++)
            if (s.SlotPaused[slot] && slot != s.LocalSlot)
                PlayerNotice.Hold("paused" + slot, GameBridge.PlayerNumber(slot), "Paused");

        if (MenuStatus.Menu.Length > 0 && MenuStatus.Slot >= 0)
            PlayerNotice.Hold("menu", GameBridge.PlayerNumber(MenuStatus.Slot), MenuStatus.Menu);
    }

    // ---------------------------------------------------------------- drawing

    private static void Build()
    {
        // Under the game's menus' banner and the toasts; above the game's HUD.
        _canvas = GameUi.CreateCanvas("SosOnline_PlayerBar", 29000);
        _group = _canvas.gameObject.AddComponent<CanvasGroup>();
        _group.interactable = false;
        _group.blocksRaycasts = false;
        _group.alpha = _alpha = 0;
        _panel = GameUi.NoticePanel(GameUi.Area(_canvas), "Panel");
        GameUi.Place(_panel, Left, TopY, 21, 21);
        _chips.Clear();
        _shownKey = null;
    }

    private static Chip NewChip(int i)
    {
        var c = new Chip { Root = GameUi.Rect(_panel, "Player" + i) };
        c.Icon = GameUi.Image(c.Root, "Icon", null, Image.Type.Simple, Color.white);
        c.Number = GameUi.Text(c.Icon.transform, "Number", false, TextAlignmentOptions.Center);
        GameUi.Stretch(c.Number.rectTransform);
        c.Name = GameUi.NoticeText(c.Root, "Name");
        c.Name.fontStyle = FontStyles.Normal;
        c.StatusIcon = GameUi.Image(c.Root, "StatusIcon", null, Image.Type.Simple, Color.white);
        c.Status = GameUi.NoticeText(c.Root, "Status");
        c.Status.fontStyle = FontStyles.Normal;
        _chips.Add(c);
        return c;
    }

    /// <summary>The game's P icon for a player number, or a coloured "P2" where it isn't loaded yet.</summary>
    public static void PlayerIcon(Image icon, TextMeshProUGUI fallback, int number)
    {
        var sprite = number > 0 ? GameUi.Find($"icn-player-square-{number}") : null;
        icon.sprite = sprite;
        icon.color = sprite != null ? Color.white : Color.clear;
        fallback.text = sprite != null || number <= 0 ? "" : $"P{number}";
        fallback.color = PlayerColor(number);
    }

    /// <summary>
    /// One row per player: icon, then (with names) the name, then the status. Every column is as wide as its widest
    /// entry, so nothing moves when a status comes or goes; the status column exists only while a row has a status.
    /// </summary>
    private static void Layout(List<Row> rows, bool names)
    {
        // Pending joiners have no player icon yet; in play (icons only) they show as a notice instead.
        if (!names) rows = rows.Where(r => r.Slot >= 0).ToList();
        while (_chips.Count < rows.Count) NewChip(_chips.Count);

        float nameW = 0, statusW = 0;
        bool anyStatus = rows.Any(r => !string.IsNullOrEmpty(r.Status));
        for (int i = 0; i < rows.Count; i++)
        {
            var c = _chips[i];
            var row = rows[i];
            c.Name.text = names ? GameUi.Drawable(PlayerNames.Shorten(row.Name, 16), true) : "";
            c.Status.text = names && !string.IsNullOrEmpty(row.Status) ? GameUi.Drawable(row.Status, true) : "";
            if (names) nameW = Mathf.Max(nameW, Mathf.Ceil(c.Name.GetPreferredValues(c.Name.text).x));
            if (c.Status.text.Length > 0) statusW = Mathf.Max(statusW, Mathf.Ceil(c.Status.GetPreferredValues(c.Status.text).x));
        }

        float nameX = Icon + TextGap;
        float statusX = names ? nameX + nameW + TextGap + 1 : Icon + RowGap;
        float width = names ? nameX + nameW : Icon;
        if (anyStatus) width = statusX + StatusIcon + (statusW > 0 ? 3 + statusW : 0);

        for (int i = 0; i < _chips.Count; i++)
        {
            var c = _chips[i];
            bool on = i < rows.Count;
            c.Root.gameObject.SetActive(on);
            if (!on) continue;
            var row = rows[i];
            int number = row.Slot >= 0 ? GameBridge.PlayerNumber(row.Slot) : 0;
            GameUi.Place(c.Root, Pad, Pad + i * (Icon + RowGap), width, Icon);

            PlayerIcon(c.Icon, c.Number, number);
            if (number <= 0) { c.Icon.sprite = GameUi.Find("menu-icn-clock"); c.Icon.color = c.Icon.sprite != null ? GameUi.TextSecondary : Color.clear; }
            GameUi.Place(c.Icon.rectTransform, 0, 0, Icon, Icon);

            c.Name.gameObject.SetActive(names);
            if (names)
            {
                c.Name.color = row.Slot == Session.Instance.LocalSlot ? GameUi.TextMain : GameUi.TextSecondary;
                GameUi.Place(c.Name.rectTransform, nameX, (Icon - TextH) / 2 + 1, nameW + 2, TextH);
            }

            // A pending joiner's clock is already its icon.
            bool status = !string.IsNullOrEmpty(row.Status) && number > 0;
            var sprite = status ? GameUi.Find(row.StatusIcon) : null;
            var tint = row.Problem ? new Color(1f, 0.8f, 0.35f) : Color.white;
            c.StatusIcon.gameObject.SetActive(sprite != null);
            if (sprite != null)
            {
                c.StatusIcon.sprite = sprite;
                c.StatusIcon.color = tint;
                GameUi.Place(c.StatusIcon.rectTransform, statusX, (Icon - StatusIcon) / 2, StatusIcon, StatusIcon);
            }
            c.Status.gameObject.SetActive(names && c.Status.text.Length > 0);
            if (c.Status.gameObject.activeSelf)
            {
                c.Status.color = row.Problem ? tint : GameUi.TextSecondary;
                GameUi.Place(c.Status.rectTransform, statusX + StatusIcon + 3, (Icon - TextH) / 2 + 1, statusW + 2, TextH);
            }
        }
        GameUi.Place(_panel, Left, TopY, Pad + width + Pad + (names ? 1 : 0), Pad + rows.Count * (Icon + RowGap) - RowGap + Pad);
    }
}
