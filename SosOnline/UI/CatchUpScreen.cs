using SosOnline.Net;
using UnityEngine;

namespace SosOnline.UI;

/// <summary>
/// Joiner, Stage 3: covers the screen while this PC loads the area entry save and replays what the party did since,
/// with the cameras off (the last rendered frame would otherwise stay frozen on screen). Nobody else waits.
/// </summary>
internal static class CatchUpScreen
{
    private static GUIStyle _gTitle, _gText, _gSmall;
    private static Texture2D _pixel;

    private static readonly Color Background = new(0.05f, 0.06f, 0.11f, 1f);
    private static readonly Color BarBack = new(1f, 1f, 1f, 0.12f);
    private static readonly Color BarFill = new(1f, 0.82f, 0.42f, 1f);

    // ------------------------------------------------------------------ game-styled screen (canvas)

    private static Canvas _canvas;
    private static TMPro.TextMeshProUGUI _title, _step, _detail, _footer;
    private static RectTransform _fill;
    private static float _nextText;
    private static bool _shown, _wasResync;
    private static float _nextRender;

    /// <summary>
    /// Draws the catch-up screen while every game camera is off (fast-forward). The game's overlay canvases only
    /// render when some camera does, so without it the screen stays frozen on the frame the cameras went off. It sees
    /// nothing of the scene and renders one frame in six real-time tenths of a second, so the replay keeps its speed.
    /// </summary>
    public static Camera Camera { get; private set; }
    private static string _lastStep;

    /// <summary>Every frame: show, update or hide the game-styled screen. Falls back to <see cref="OnGUI"/> without the game's art.</summary>
    public static void Update()
    {
        var s = Session.Instance;
        bool resync = s.ResyncWaiting && !s.CatchingUp;
        bool show = (s.CatchingUp || resync) && GameUi.Ready;
        if (show && _canvas == null) Build();
        if (_canvas != null && _canvas.gameObject.activeSelf != show) _canvas.gameObject.SetActive(show);
        if ((resync && show) != _wasResync) Plugin.Log.LogInfo($"[resync] waiting screen {(resync && show ? "shown" : "hidden")}");
        _wasResync = resync && show;
        _shown = show;
        PulseCamera(show && s.CatchUpFastForwarding);
        if (!show) return;
        if (resync) { UpdateResync(s); return; }
        _footer.text = s.Rejoining ? "The others keep playing while you rejoin." : "The others keep playing while you join.";

        var (step, progress, done, left, rate, eta) = s.CatchUpProgress;
        float now = Time.realtimeSinceStartup;
        if (step == "Loading the area")
        {
            // No measurable progress while the save loads: a block sweeping along the bar.
            float t = Mathf.Repeat(now * 0.6f, 1.25f) - 0.25f;
            _fill.anchorMin = new Vector2(Mathf.Max(0, t), 0);
            _fill.anchorMax = new Vector2(Mathf.Min(1, t + 0.25f), 1);
        }
        else
        {
            _fill.anchorMin = Vector2.zero;
            _fill.anchorMax = new Vector2(Mathf.Clamp01(progress), 1);
        }
        // The text is rebuilt a few times a second (and on every step), not on every frame of an uncapped replay.
        if (now < _nextText && step == _lastStep) return;
        if (step != _lastStep) Plugin.Log.LogInfo($"[catchup] screen: {step} at f{s.Engine.AppliedFrame}");
        _lastStep = step;
        _nextText = now + 0.2f;
        string host = GameUi.Drawable(s.SlotName[0]);
        _title.text = s.Rejoining ? $"Rejoining {(string.IsNullOrEmpty(host) ? "the host" : host)}'s game"
            : string.IsNullOrEmpty(host) ? "Joining the game" : $"Joining {host}'s game";
        _step.text = step + new string('.', 1 + (int)(now * 2) % 3);
        _detail.text = Detail(step, done, left, rate, eta);
    }

    /// <summary>Waiting to drop in again after a re-sync: what's happening, a moving bar, and Esc to leave instead.</summary>
    private static void UpdateResync(Session s)
    {
        float now = Time.realtimeSinceStartup;
        float t = Mathf.Repeat(now * 0.6f, 1.25f) - 0.25f;
        _fill.anchorMin = new Vector2(Mathf.Max(0, t), 0);
        _fill.anchorMax = new Vector2(Mathf.Min(1, t + 0.25f), 1);
        if (EscPressed()) { Plugin.Log.LogInfo("[resync] left the session from the waiting screen"); s.Leave(); return; }
        if (now < _nextText && _lastStep == "resync") return;
        _lastStep = "resync";
        _nextText = now + 0.2f;
        string host = GameUi.Drawable(s.SlotName[0]);
        if (string.IsNullOrEmpty(host)) host = "the host";
        _title.text = $"Rejoining {host}'s game";
        _step.text = "Your game got out of sync" + new string('.', 1 + (int)(now * 2) % 3);
        _detail.text = $"You'll rejoin as soon as {host}'s game allows saving.";
        _footer.text = GameUi.Drawable("Esc: Leave co-op");
    }

    /// <summary>Esc / Back / Start on any player, read from the devices (no lockstep while waiting).</summary>
    private static bool EscPressed()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.Escape)) return true;
            if (!Rewired.ReInput.isReady) return false;
            foreach (var p in Rewired.ReInput.players.AllPlayers.ToList())
                if (p != null && (p.GetButtonDown("UI_Back") || p.GetButtonDown("UI_Start"))) return true;
        }
        catch { }
        return false;
    }

    private static void PulseCamera(bool needed)
    {
        if (!needed)
        {
            if (Camera != null && Camera.enabled) Camera.enabled = false;
            return;
        }
        if (Camera == null)
        {
            var go = new GameObject("SosOnline_CatchUpCamera");
            Object.DontDestroyOnLoad(go);
            Camera = go.AddComponent<Camera>();
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = Color.black;
            Camera.cullingMask = 0;
            Camera.depth = 100;
            Camera.enabled = false;
        }
        float now = Time.realtimeSinceStartup;
        // On for one frame every 0.1 s, off in between.
        if (Camera.enabled) Camera.enabled = false;
        else if (now >= _nextRender) { Camera.enabled = true; _nextRender = now + 0.1f; }
    }

    private static string Detail(string step, int done, int left, double rate, double eta) => step switch
    {
        "Loading the area" => "Loading the party's save for this area.",
        "Joining the party" => "Caught up. You'll take control in a moment.",
        _ => $"Replaying the party's moves: {done:N0} of {done + left:N0}"
             + (rate > 0 ? $", {rate / 60:F0}x speed" : "")
             + (eta >= 0 ? $", about {Mathf.CeilToInt((float)eta)} s left." : "."),
    };

    private static void Build()
    {
        _canvas = GameUi.CreateCanvas("SosOnline_CatchUp", 30000);
        var root = _canvas.transform;
        // The game's loading screens are black; the cameras are off during the replay anyway.
        GameUi.Stretch(GameUi.Image(root, "Black", "ui-white-square", UnityEngine.UI.Image.Type.Simple, Color.black).rectTransform);

        const float W = 300, H = 104, Pad = 12;
        var panel = GameUi.Panel(root, "Panel");
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(W, H);
        panel.anchoredPosition = new Vector2(0, 10);

        _title = GameUi.Text(panel, "Title", false, TMPro.TextAlignmentOptions.Left);
        GameUi.Place(_title.rectTransform, Pad, 9, W - 2 * Pad, 12);
        GameUi.Place(GameUi.Separator(panel), 6, 25, W - 12, 6);
        _step = GameUi.Text(panel, "Step", false, TMPro.TextAlignmentOptions.Left);
        GameUi.Place(_step.rectTransform, Pad, 37, W - 2 * Pad, 12);

        // Progress bar: the menus' rounded 1px shape as the track, the cursor highlight colour as the fill.
        var track = GameUi.Image(panel, "Track", "panel-rounded-shape-1px", UnityEngine.UI.Image.Type.Sliced, GameUi.SeparatorMain);
        GameUi.Place(track.rectTransform, Pad, 53, W - 2 * Pad, 6);
        var fill = GameUi.Image(track.transform, "Fill", "panel-rounded-shape-1px", UnityEngine.UI.Image.Type.Sliced, GameUi.Highlight);
        _fill = fill.rectTransform;
        _fill.offsetMin = _fill.offsetMax = Vector2.zero;
        var outline = GameUi.Image(track.transform, "Outline", "panel-rounded-outline-1px", UnityEngine.UI.Image.Type.Sliced, GameUi.PanelOutline);
        GameUi.Stretch(outline.rectTransform);

        _detail = GameUi.Text(panel, "Detail", true, TMPro.TextAlignmentOptions.TopLeft);
        GameUi.Place(_detail.rectTransform, Pad, 65, W - 2 * Pad, 20);
        _footer = GameUi.Text(panel, "Footer", true, TMPro.TextAlignmentOptions.TopLeft);
        GameUi.Place(_footer.rectTransform, Pad, 87, W - 2 * Pad, 10);
        _footer.text = "The others keep playing; nobody waits for you.";
        _nextText = 0;
        _lastStep = null;
    }

    // ------------------------------------------------------------------ fallback (IMGUI)

    public static void OnGUI()
    {
        var s = Session.Instance;
        if (!s.CatchingUp || _shown) return;
        var (step, progress, done, left, rate, eta) = s.CatchUpProgress;
        Styles();

        Fill(new Rect(0, 0, Screen.width, Screen.height), Background);

        float w = Mathf.Min(560, Screen.width - 48);
        float x = (Screen.width - w) / 2, y = Screen.height * 0.36f;
        string host = s.SlotName[0];
        GUI.Label(new Rect(x, y, w, 34), string.IsNullOrEmpty(host) ? "Joining the game" : $"Joining {host}'s game", _gTitle);
        y += 42;
        GUI.Label(new Rect(x, y, w, 26), step + Dots(), _gText);
        y += 34;

        Fill(new Rect(x, y, w, 10), BarBack);
        if (step == "Loading the area")
        {
            // No measurable progress while the save loads: a block sweeping across the bar.
            float t = Mathf.Repeat(Time.realtimeSinceStartup * 0.6f, 1.25f) - 0.25f;
            Fill(new Rect(x + Mathf.Max(0, t) * w, y, Mathf.Min(0.25f * w, (1 - Mathf.Max(0, t)) * w), 10), BarFill);
        }
        else Fill(new Rect(x, y, Mathf.Clamp01(progress) * w, 10), BarFill);
        y += 22;

        string detail = step switch
        {
            "Loading the area" => "Loading the save from where the party entered this area.",
            "Joining the party" => "Caught up. You take over a party member in a moment.",
            _ => $"Replaying the party's moves · {done:N0} of {done + left:N0}"
                 + (rate > 0 ? $" · {rate / 60:F0}× speed" : "")
                 + (eta >= 0 ? $" · about {Mathf.CeilToInt((float)eta)} s left" : ""),
        };
        GUI.Label(new Rect(x, y, w, 40), detail, _gSmall);
        y += 44;
        GUI.Label(new Rect(x, y, w, 24), "The others keep playing while you join.", _gSmall);
    }

    private static string Dots() => new('.', 1 + (int)(Time.realtimeSinceStartup * 2) % 3);

    private static void Fill(Rect r, Color c)
    {
        _pixel ??= Texture2D.whiteTexture;
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, _pixel);
        GUI.color = old;
    }

    private static void Styles()
    {
        if (_gTitle != null) return;
        _gTitle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _gTitle.normal.textColor = new Color(1f, 0.88f, 0.6f);
        _gText = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
        _gText.normal.textColor = Color.white;
        _gSmall = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, alignment = TextAnchor.UpperLeft };
        _gSmall.normal.textColor = new Color(0.78f, 0.8f, 0.86f);
    }
}
