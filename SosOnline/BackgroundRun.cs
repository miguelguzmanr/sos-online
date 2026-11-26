using System.Linq;
using Rewired;
using UnityEngine;

namespace SosOnline;

/// <summary>
/// --sosonline-background: a test instance that runs behind the user's work. The test scripts pass it by default
/// (tests/launch.sh; VISIBLE=1 turns it off). The instance plays silently and ignores this PC's keyboard, mouse and
/// pads, so nothing the user does meanwhile reaches it: Rewired reads pads (and, through raw input, possibly the
/// keyboard) whether or not the window has focus. Scripts and bots still drive it, through the lockstep input filter.
/// It keeps running without focus. The window itself is on a hidden desktop (tests/start-game.ps1), out of sight and
/// out of reach of the user's keyboard and mouse; pads are read on any desktop.
/// </summary>
internal static class BackgroundRun
{
    public static bool On { get; private set; }
    private static float _next;
    private static bool _logged;

    public static void Init()
    {
        if (!TestHarness.TryArg("background", out _)) return;
        On = true;
        Plugin.Log.LogInfo("[test] background run: muted, this PC's keyboard, mouse and pads ignored");
    }

    /// <summary>Every frame. The game (and the catch-up replay) can turn sound and devices back on: keep them off.
    /// The game's sound is all Wwise, so muting Wwise's output mutes the game.</summary>
    public static void Update()
    {
        if (!On) return;
        float now = Time.realtimeSinceStartup;
        if (now < _next) return;
        _next = now + 0.25f;
        Application.runInBackground = true;
        try { AkSoundEngine.SetOutputVolume(0, 0f); } catch { }
        if (!ReInput.isReady) return;
        int off = 0;
        var c = ReInput.controllers;
        if (c.Keyboard != null && c.Keyboard.enabled) { c.Keyboard.enabled = false; off++; }
        if (c.Mouse != null && c.Mouse.enabled) { c.Mouse.enabled = false; off++; }
        foreach (var joy in c.Joysticks.ToList())
            if (joy != null && joy.enabled) { joy.enabled = false; off++; }
        if (off > 0 && !_logged) { _logged = true; Plugin.Log.LogInfo($"[test] background run: {off} local devices disabled"); }
    }
}
