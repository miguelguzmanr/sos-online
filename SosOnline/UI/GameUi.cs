using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SosOnline.UI;

/// <summary>
/// Builds the mod's own screens from the game's UI art, so they look like the game's menus: the same atlas sprites
/// (panel background, ornament frame, outline, shadow, separators), the same bitmap fonts with their drop shadow, and
/// the same 640×360 reference layout. Recipe measured from the pause menu (<c>--sosonline-uiprobe</c>).
///
/// Everything lives on the mod's own overlay canvas: nothing is added to the game's UI hierarchy or its view stack,
/// so the game's state (and the lockstep simulation) never sees it.
/// </summary>
internal static class GameUi
{
    // Colours of the pause menu panel and texts.
    public static readonly Color PanelBackground = new(0.098f, 0.114f, 0.169f, 1f);
    public static readonly Color PanelOrnament = new(0.220f, 0.282f, 0.353f, 1f);
    public static readonly Color PanelOutline = new(0.388f, 0.443f, 0.506f, 1f);
    public static readonly Color PanelShadow = new(0f, 0f, 0f, 0.863f);
    public static readonly Color SeparatorVolume = new(0.188f, 0.212f, 0.282f, 1f);
    public static readonly Color SeparatorMain = new(0.051f, 0.063f, 0.106f, 1f);
    public static readonly Color Highlight = new(0.125f, 0.749f, 1f, 1f);
    public static readonly Color TextMain = Color.white;
    public static readonly Color TextSecondary = new(0.647f, 0.678f, 0.718f, 1f);
    /// <summary>The game's bitmap fonts are drawn at this size (reference pixels); multiples stay crisp.</summary>
    public const float FontSize = 8f;

    private static readonly Dictionary<string, Sprite> Sprites = new();
    private static TMP_FontAsset _copyFont, _smallFont;
    private static Material _copyMaterial, _smallMaterial, _smallPlainMaterial;
    private static bool _searched;
    private static float _nextSearch;

    private static readonly string[] Needed =
    {
        "ui-white-square", "panel-background-t1", "panel-ornament-2-full-t1", "Shadow-rounded", "panel-border-rounded",
        "panel-separator-base-volume", "panel-separator-base", "panel-rounded-shape-1px", "panel-rounded-outline-1px",
    };

    /// <summary>The game's UI art is loaded (true from the title screen on). Retries every few seconds until then.</summary>
    public static bool Ready
    {
        get
        {
            if (_searched) return true;
            if (Time.realtimeSinceStartup < _nextSearch) return false;
            _nextSearch = Time.realtimeSinceStartup + 3f;
            try { Search(); }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[gameui] {e.Message}"); }
            return _searched;
        }
    }

    private static void Search()
    {
        foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            if (sprite != null && System.Array.IndexOf(Needed, sprite.name) >= 0 && !Sprites.ContainsKey(sprite.name))
                Sprites[sprite.name] = sprite;
        foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font == null) continue;
            if (font.name == "copy1056-webfontBitmap") _copyFont = font;
            else if (font.name == "kroegerBitmap") _smallFont = font;
        }
        foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (mat == null) continue;
            if (mat.name == "copy1056-webfontBitmap_DropShadow") _copyMaterial = mat;
            else if (mat.name == "kroegerBitmapDropShadow") _smallMaterial = mat;
            else if (mat.name == "kroegerSDF Material") _smallPlainMaterial = mat;
        }
        _searched = Sprites.Count == Needed.Length && _copyFont != null;
        if (_searched) Plugin.Log.LogInfo($"[gameui] game UI art found ({Sprites.Count} sprites, fonts {_copyFont?.name}, {_smallFont?.name})");
    }

    private static readonly List<RectTransform> _areas = new();
    private static Rect _area = new(0, 0, 1, 1);
    private static string _areaLogged;
    private static float _nextAreaCheck;

    /// <summary>
    /// The part of <paramref name="canvas"/> the game draws its picture in, for content placed against the screen's
    /// edges. On a screen wider than 16:9 the game keeps 16:9 with black bars at the sides, and the mod's UI placed
    /// against the window's edges sat on the bars (2026-11-26, 3440x1440).
    /// </summary>
    public static RectTransform Area(Canvas canvas)
    {
        var t = canvas.transform.Find("Area")?.GetComponent<RectTransform>();
        if (t != null) return t;
        var go = new GameObject("Area");
        go.layer = 5;
        t = go.AddComponent<RectTransform>();
        t.SetParent(canvas.transform, false);
        t.pivot = new Vector2(0.5f, 0.5f);
        t.offsetMin = t.offsetMax = Vector2.zero;
        t.anchorMin = _area.min;
        t.anchorMax = _area.max;
        _areas.Add(t);
        return t;
    }

    /// <summary>Every frame: follows the game's picture area (window resizes, resolution changes).</summary>
    public static void UpdateAreas()
    {
        if (Time.realtimeSinceStartup < _nextAreaCheck) return;
        _nextAreaCheck = Time.realtimeSinceStartup + 0.5f;
        var area = GamePictureArea();
        if (area == _area) return;
        _area = area;
        string log = $"{Screen.width}x{Screen.height}: {area.xMin:F3}-{area.xMax:F3} x {area.yMin:F3}-{area.yMax:F3}";
        if (log != _areaLogged) { _areaLogged = log; Plugin.Log.LogInfo($"[gameui] picture area on {log}"); }
        _areas.RemoveAll(a => a == null);
        foreach (var a in _areas) { a.anchorMin = area.min; a.anchorMax = area.max; }
    }

    /// <summary>
    /// The game's picture as a fraction of the window: on a window wider than 16:9, the centred 16:9 part (the game's
    /// cameras render there, measured at 1680x720: 0.119-0.881). Computed rather than read from <c>Camera.main</c>,
    /// which flips between the game's cameras (some full-width) during loads.
    /// </summary>
    private static Rect GamePictureArea()
    {
        float w = Screen.width, h = Screen.height;
        if (w <= 0 || h <= 0 || w / h <= 16f / 9f + 0.01f) return new Rect(0, 0, 1, 1);
        float part = h * 16f / 9f / w;
        return new Rect((1 - part) / 2, 0, part, 1);
    }

    /// <summary>A screen-space overlay canvas scaled like the game's (640×360 reference), above the game's UI.</summary>
    public static Canvas CreateCanvas(string name, int sortingOrder)
    {
        var go = new GameObject(name);
        Object.DontDestroyOnLoad(go);
        go.layer = 5; // UI
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        canvas.pixelPerfect = true;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(640, 360);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;
        scaler.referencePixelsPerUnit = 24; // the atlas's pixels per unit: slice borders keep their pixel size
        try
        {
            var game = Manager<UIManager>.instance?.canvasScaler;
            if (game != null)
            {
                scaler.uiScaleMode = game.uiScaleMode;
                scaler.referenceResolution = game.referenceResolution;
                scaler.screenMatchMode = game.screenMatchMode;
                scaler.matchWidthOrHeight = game.matchWidthOrHeight;
                scaler.referencePixelsPerUnit = game.referencePixelsPerUnit;
            }
        }
        catch { }
        return canvas;
    }

    public static RectTransform Rect(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.layer = 5;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>Stretches <paramref name="rt"/> over its parent, grown by <paramref name="outset"/> pixels on each side.</summary>
    public static RectTransform Stretch(RectTransform rt, float outset = 0)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(-outset, -outset);
        rt.offsetMax = new Vector2(outset, outset);
        return rt;
    }

    private static readonly Dictionary<string, Sprite> Optional = new();
    private static float _nextOptionalSearch;

    /// <summary>
    /// A sprite of the game's that may not be loaded yet (the player icons load with the co-op HUD): null until it is.
    /// Looks again every few seconds while any asked-for one is missing.
    /// </summary>
    public static Sprite Find(string name)
    {
        if (Sprites.TryGetValue(name, out var s) || (Optional.TryGetValue(name, out s) && s != null)) return s;
        if (!Optional.ContainsKey(name)) { Optional[name] = null; _nextOptionalSearch = 0; }
        if (Time.realtimeSinceStartup < _nextOptionalSearch) return null;
        _nextOptionalSearch = Time.realtimeSinceStartup + 3f;
        try
        {
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sprite != null && Optional.ContainsKey(sprite.name) && Optional[sprite.name] == null) Optional[sprite.name] = sprite;
        }
        catch { }
        return Optional.TryGetValue(name, out s) ? s : null;
    }

    public static Image Image(Transform parent, string name, string sprite, Image.Type type, Color color)
    {
        var rt = Rect(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite == null ? null : Sprites.TryGetValue(sprite, out var s) ? s : Find(sprite);
        img.type = type;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>
    /// The pause menu's panel: tiled background, ornament frame just inside it, shadow and rounded outline around it.
    /// Returns the background; put content in it.
    /// </summary>
    public static RectTransform Panel(Transform parent, string name)
    {
        var bg = Image(parent, name, "panel-background-t1", UnityEngine.UI.Image.Type.Tiled, PanelBackground);
        Stretch(Image(bg.transform, "Ornaments", "panel-ornament-2-full-t1", UnityEngine.UI.Image.Type.Sliced, PanelOrnament).rectTransform, -1);
        Stretch(Image(bg.transform, "Shadow", "Shadow-rounded", UnityEngine.UI.Image.Type.Sliced, PanelShadow).rectTransform, 1);
        Stretch(Image(bg.transform, "Outline", "panel-border-rounded", UnityEngine.UI.Image.Type.Sliced, PanelOutline).rectTransform, 5);
        return bg.rectTransform;
    }

    /// <summary>
    /// The frame of the game's co-op notices ("P3 LEFT"): the panel background without the ornament, its shadow nudged
    /// 3 pixels right and 2 down, the rounded outline 5 pixels out. Measured from <c>PlayerLeftNotification</c>.
    /// </summary>
    public static RectTransform NoticePanel(Transform parent, string name)
    {
        var bg = Image(parent, name, "panel-background-t1", UnityEngine.UI.Image.Type.Tiled, PanelBackground);
        var shadow = Image(bg.transform, "Shadow", "Shadow-rounded", UnityEngine.UI.Image.Type.Sliced, PanelShadow).rectTransform;
        Stretch(shadow);
        shadow.offsetMin = new Vector2(2, -4);
        shadow.offsetMax = new Vector2(4, -2);
        Stretch(Image(bg.transform, "Outline", "panel-border-rounded", UnityEngine.UI.Image.Type.Sliced, PanelOutline).rectTransform, 5);
        return bg.rectTransform;
    }

    /// <summary>The co-op notices' text: the small font, white, in capitals, as in "P3 LEFT".</summary>
    public static TextMeshProUGUI NoticeText(Transform parent, string name)
    {
        var t = Text(parent, name, true, TextAlignmentOptions.Left);
        if (_smallPlainMaterial != null && _smallFont != null) t.fontSharedMaterial = _smallPlainMaterial;
        t.color = TextMain;
        t.fontStyle = FontStyles.UpperCase;
        t.enableWordWrapping = false;
        return t;
    }

    /// <summary>The menus' two-tone separator line, 6 pixels high.</summary>
    public static RectTransform Separator(Transform parent)
    {
        var volume = Image(parent, "Separator", "panel-separator-base-volume", UnityEngine.UI.Image.Type.Sliced, SeparatorVolume);
        var main = Image(volume.transform, "SeparatorMainColor", "panel-separator-base", UnityEngine.UI.Image.Type.Sliced, SeparatorMain);
        Stretch(main.rectTransform);
        main.rectTransform.offsetMax = new Vector2(0, -1);
        return volume.rectTransform;
    }

    /// <summary>Text in the game's menu font (or its small grey secondary font).</summary>
    public static TextMeshProUGUI Text(Transform parent, string name, bool secondary, TextAlignmentOptions align, float size = FontSize)
    {
        var rt = Rect(parent, name);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = secondary && _smallFont != null ? _smallFont : _copyFont;
        var mat = secondary && _smallFont != null ? _smallMaterial : _copyMaterial;
        if (font != null) t.font = font;
        if (mat != null) t.fontSharedMaterial = mat;
        t.fontSize = size;
        t.color = secondary ? TextSecondary : TextMain;
        t.alignment = align;
        t.enableWordWrapping = true;
        t.overflowMode = TextOverflowModes.Overflow;
        t.raycastTarget = false;
        return t;
    }

    /// <summary>Separator between nested menus ("Pause menu > Options"), in a character the game's font has.</summary>
    public static string Arrow => _copyFont == null || _copyFont.HasCharacter('›', true) ? " › " : _copyFont.HasCharacter('>', true) ? " > " : " / ";

    /// <summary>
    /// <paramref name="text"/> without the characters the game's font can't draw (player names: '@', emoji, scripts the
    /// bitmap font lacks), which would otherwise show as boxes.
    /// </summary>
    public static string Drawable(string text, bool secondary = false)
    {
        var font = secondary && _smallFont != null ? _smallFont : _copyFont;
        if (font == null || string.IsNullOrEmpty(text)) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            if (c == ' ' || c == '\n' || font.HasCharacter(c, true)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Places <paramref name="rt"/> by its top-left corner inside its parent's top-left, in reference pixels.</summary>
    public static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }
}
