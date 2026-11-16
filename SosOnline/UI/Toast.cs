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

    public static void Show(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
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

        if (_laidOut != _current) Layout(_current);
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
        _panel = GameUi.Panel(_canvas.transform, "Panel");
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
