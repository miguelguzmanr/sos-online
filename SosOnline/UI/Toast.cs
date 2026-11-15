using System.Collections.Generic;
using UnityEngine;

namespace SosOnline.UI;

/// <summary>Short session notices ("Alex joined", "Connection lost"), shown at the top of the screen for a few seconds.</summary>
internal static class Toast
{
    private static readonly Queue<string> Pending = new();
    private static string _current;
    private static float _until;
    private static GUIStyle _style;

    public static void Show(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        Plugin.Log.LogInfo("[toast] " + message);
        Pending.Enqueue(message);
    }

    public static void OnGUI()
    {
        // The catch-up screen covers everything; notices wait until it's gone.
        if (Net.Session.Instance.CatchingUp) return;
        if (_current == null || Time.unscaledTime > _until)
        {
            _current = Pending.Count > 0 ? Pending.Dequeue() : null;
            _until = Time.unscaledTime + 4f;
        }
        if (_current == null) return;
        _style ??= new GUIStyle(GUI.skin.box) { fontSize = 18, wordWrap = true, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(16, 16, 10, 10) };
        float w = Mathf.Min(720, Screen.width - 40);
        float h = _style.CalcHeight(new GUIContent(_current), w) + 4;
        var r = new Rect((Screen.width - w) / 2, 40, w, h);
        GUI.Box(r, _current, _style);
    }
}
