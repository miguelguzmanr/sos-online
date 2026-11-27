using System.Collections.Generic;
using UnityEngine;

namespace SosOnline.UI;

/// <summary>
/// Short session notices ("Alex joined", "Your game got out of sync…") at the top of the screen, one at a time.
///
/// Drawn like the game's menus (its panel, font and colours, <see cref="GameUi"/>), sized to the text and wrapped to at
/// most <see cref="MaxWidth"/>. Each stays long enough to read: <see cref="BaseSeconds"/> plus
/// <see cref="SecondsPerChar"/> per character (about reading speed), between <see cref="MinSeconds"/> and
/// <see cref="MaxSeconds"/>, with a short fade in and out. Before the game's art is available (very early at start)
/// a plain box is drawn instead.
///
/// It never covers text on screen (<see cref="Place"/>): a screen's title sits where a notice goes ("Player joined."
/// over "Assign Players", 2026-11-16), so a notice that would overlap visible text moves below it, or to the bottom.
/// </summary>
internal static class Toast
{
    private const float BaseSeconds = 2.5f, SecondsPerChar = 0.06f, MinSeconds = 3.5f, MaxSeconds = 12f, Fade = 0.25f;
    private const float MaxWidth = 300, MinWidth = 120, PadX = 12, PadY = 7, Top = 30;

    private static readonly Queue<string> Pending = new();
    private static string _current;
    private static float _since, _until;
    private static GUIStyle _style;

    /// <summary>
    /// This PC's own clock. The game's clocks follow the session (they jump forward when one starts or a level is
    /// released), which dismissed a fresh notice at once; notices change nothing in the game, so local time is fine.
    /// </summary>
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static float Now => (float)Clock.Elapsed.TotalSeconds;

    private static Canvas _canvas;
    private static CanvasGroup _group;
    private static RectTransform _panel;
    private static TMPro.TextMeshProUGUI _text;
    private static string _laidOut;

    /// <summary>
    /// Longest notice, in characters. A notice is read at a glance during play: one short sentence about what
    /// happened, and at most what to do. Details belong in the log or the Online menu. Longer text is cut at a word
    /// and logged as a warning so it gets rewritten.
    /// </summary>
    public const int MaxChars = 80;

    public static void Show(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (message.Length > MaxChars)
        {
            Plugin.Log.LogWarning($"[toast] over {MaxChars} characters ({message.Length}), shortened: {message}");
            int cut = message.LastIndexOf(' ', MaxChars - 3);
            message = message[..(cut > MaxChars / 2 ? cut : MaxChars - 3)].TrimEnd(',', ';', ':', '.') + "...";
        }
        Plugin.Log.LogInfo("[toast] " + message);
        Pending.Enqueue(message);
    }

    /// <summary>How long a message stays: enough to read it.</summary>
    public static float Duration(string message) =>
        Mathf.Clamp(BaseSeconds + SecondsPerChar * (message?.Length ?? 0), MinSeconds, MaxSeconds);

    private static bool Hidden => Net.Session.Instance.CatchingUp || Net.Session.Instance.ResyncWaiting; // a screen covers everything

    /// <summary>Every frame: advance the queue and draw with the game's art when it's available.</summary>
    public static void Update()
    {
        float now = Now;
        if (!Hidden && (_current == null || now > _until))
        {
            _current = Pending.Count > 0 ? Pending.Dequeue() : null;
            _since = now;
            _until = _current == null ? 0 : now + Duration(_current);
        }
        bool show = _current != null && !Hidden && GameUi.Ready;
        if (show && _canvas == null) Build();
        if (_canvas == null) return;
        if (_canvas.gameObject.activeSelf != show) _canvas.gameObject.SetActive(show);
        if (!show) return;

        if (_laidOut != _current) { Layout(_current); _nextPlace = 0; }
        if (now >= _nextPlace) { _nextPlace = now + PlaceEvery; Place(); }
        float alpha = Mathf.Min(1, (now - _since) / Fade, (_until - now) / Fade);
        _group.alpha = Mathf.Clamp01(alpha);
    }

    private static void Build()
    {
        // Above the game's menus and the menu banner, below the catch-up screen.
        _canvas = GameUi.CreateCanvas("SosOnline_Toast", 29500);
        _group = _canvas.gameObject.AddComponent<CanvasGroup>();
        _group.interactable = false;
        _group.blocksRaycasts = false;
        _panel = GameUi.Panel(GameUi.Area(_canvas), "Panel");
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0.5f, 1f);
        _panel.anchoredPosition = new Vector2(0, -Top);
        _text = GameUi.Text(_panel, "Text", false, TMPro.TextAlignmentOptions.Center);
        _text.enableWordWrapping = true;
        _text.rectTransform.anchorMin = _text.rectTransform.anchorMax = _text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _laidOut = null;
    }

    /// <summary>Sizes the panel to the text: one line when it fits, wrapped to <see cref="MaxWidth"/> otherwise.</summary>
    private static void Layout(string message)
    {
        _laidOut = message;
        _text.text = GameUi.Drawable(message);
        var one = _text.GetPreferredValues(_text.text, MaxWidth, 0);
        float width = Mathf.Clamp(Mathf.Min(one.x, MaxWidth), MinWidth - 2 * PadX, MaxWidth);
        float height = _text.GetPreferredValues(_text.text, width, 0).y;
        _text.rectTransform.sizeDelta = new Vector2(width, height);
        _panel.sizeDelta = new Vector2(Mathf.Ceil(width + 2 * PadX), Mathf.Ceil(height + 2 * PadY));
    }

    private const float PlaceEvery = 0.25f, Gap = 4, Margin = 8;
    private static float _nextPlace, _y = Top;
    private static string _placedFor;
    private static readonly List<(Rect rect, string text)> Obstacles = new();

    /// <summary>
    /// Puts the notice where it covers no visible text: at the top as usual; else just below the texts it would
    /// cover, while that stays in the upper half; else at the bottom, above the texts there. Where no spot is free,
    /// the one covering the least. In canvas units, y down from the top of the screen.
    /// </summary>
    private static void Place()
    {
        try
        {
            float scale = _canvas.scaleFactor > 0 ? _canvas.scaleFactor : 1;
            float screenH = Screen.height / scale, screenW = Screen.width / scale;
            var size = _panel.sizeDelta;
            CollectObstacles(scale, screenH);

            float best = Top, bestOverlap = float.MaxValue;
            string avoided = null;
            bool Try(float y, out float overlap, out string covered)
            {
                var r = new Rect((screenW - size.x) / 2, y, size.x, size.y);
                overlap = 0; covered = null;
                foreach (var (o, text) in Obstacles)
                    if (o.Overlaps(r)) { var i = Rect.MinMaxRect(Mathf.Max(o.xMin, r.xMin), Mathf.Max(o.yMin, r.yMin), Mathf.Min(o.xMax, r.xMax), Mathf.Min(o.yMax, r.yMax)); overlap += i.width * i.height; covered ??= text; }
                if (overlap < bestOverlap) { bestOverlap = overlap; best = y; }
                return overlap <= 0;
            }
            float Below(float y)
            {
                var r = new Rect((screenW - size.x) / 2, y, size.x, size.y);
                float lowest = y;
                foreach (var (o, _) in Obstacles) if (o.Overlaps(r)) lowest = Mathf.Max(lowest, o.yMax + Gap);
                return lowest;
            }
            float Above(float y)
            {
                var r = new Rect((screenW - size.x) / 2, y, size.x, size.y);
                float highest = y;
                foreach (var (o, _) in Obstacles) if (o.Overlaps(r)) highest = Mathf.Min(highest, o.yMin - Gap - size.y);
                return highest;
            }

            float chosen = -1;
            float y = Top;
            for (int i = 0; i < 6 && y + size.y <= screenH / 2; i++)
            {
                if (Try(y, out _, out var c)) { chosen = y; break; }
                avoided ??= c;
                float next = Below(y);
                if (next <= y) break;
                y = next;
            }
            if (chosen < 0)
            {
                y = screenH - size.y - Margin;
                for (int i = 0; i < 6 && y >= Margin; i++)
                {
                    if (Try(y, out _, out var c)) { chosen = y; break; }
                    avoided ??= c;
                    float next = Above(y);
                    if (next >= y) break;
                    y = next;
                }
            }
            if (chosen < 0) chosen = best;
            if (!Mathf.Approximately(chosen, _y) || _placedFor != _current)
            {
                if (!Mathf.Approximately(chosen, Top))
                    Plugin.Log.LogInfo($"[toast] placed at y {chosen:F0} (not {Top:F0}) to keep '{avoided ?? "?"}' readable{(bestOverlap > 0 && chosen == best && avoided != null ? ", no free spot" : "")}");
                _y = chosen;
                _placedFor = _current;
                _panel.anchoredPosition = new Vector2(0, -_y);
            }
        }
        catch (System.Exception e) { Plugin.Log.LogWarning($"[toast] placement: {e.Message}"); _nextPlace = Now + 5; }
    }

    /// <summary>Visible texts on screen outside this notice (game screens, the mod's banner), in canvas units from the top.</summary>
    private static void CollectObstacles(float scale, float screenH)
    {
        Obstacles.Clear();
        foreach (var t in Object.FindObjectsOfType<TMPro.TMP_Text>())
        {
            if (t == null || !t.isActiveAndEnabled || string.IsNullOrWhiteSpace(t.text) || t.color.a < 0.05f) continue;
            if (t.transform.IsChildOf(_canvas.transform)) continue;
            var cr = t.canvasRenderer;
            if (cr != null && (cr.cull || cr.GetInheritedAlpha() * t.color.a < 0.05f)) continue;
            var canvas = t.canvas;
            if (canvas == null || !canvas.isActiveAndEnabled) continue;
            var b = t.textBounds;
            if (b.size.x <= 0 || b.size.y <= 0) continue;
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, t.transform.TransformPoint(b.min));
            Vector2 c = RectTransformUtility.WorldToScreenPoint(cam, t.transform.TransformPoint(b.max));
            float xMin = Mathf.Min(a.x, c.x) / scale, xMax = Mathf.Max(a.x, c.x) / scale;
            float top = screenH - Mathf.Max(a.y, c.y) / scale, bottom = screenH - Mathf.Min(a.y, c.y) / scale;
            if (bottom < 0 || top > screenH) continue;
            string text = t.text.Length > 30 ? t.text[..30] : t.text;
            Obstacles.Add((Rect.MinMaxRect(xMin, top, xMax, bottom), text));
        }
    }

    /// <summary>Fallback without the game's UI art: a plain box at the top.</summary>
    public static void OnGUI()
    {
        if (GameUi.Ready || _current == null || Hidden) return;
        _style ??= new GUIStyle(GUI.skin.box) { fontSize = 18, wordWrap = true, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(16, 16, 10, 10) };
        float w = Mathf.Min(720, Screen.width - 40);
        float h = _style.CalcHeight(new GUIContent(_current), w) + 4;
        GUI.Box(new Rect((Screen.width - w) / 2, 40, w, h), _current, _style);
    }
}
