using System.Collections.Generic;
using System.Diagnostics;
using SosOnline.Game;
using UnityEngine;

namespace SosOnline;

/// <summary>
/// Spike C: --sosonline-ff runs the simulation as fast as the PC can go (one fixed 1/60 s step per rendered frame,
/// no frame cap) with audio muted; --sosonline-ff=nocam also disables every camera. Logs simulated frames per
/// wall-clock second, split into free roaming, battle and loading.
/// </summary>
internal static class FastForward
{
    private static bool _on, _noCam, _started;
    private static readonly List<Camera> _disabled = new();
    private static readonly Dictionary<string, (int frames, double ms)> _buckets = new();
    private static readonly Stopwatch _wall = new();
    private static double _lastMs, _lastReportMs;

    public static void Init()
    {
        if (!TestHarness.TryArg("ff", out var mode)) return;
        _on = true;
        _noCam = mode == "nocam";
        Plugin.Log.LogInfo($"[ff] fast-forward {(_noCam ? "without cameras" : "with cameras")}");
    }

    public static void OnFrame(int frame)
    {
        if (!_on) return;
        if (!_started)
        {
            _started = true;
            try { AkSoundEngine.SetOutputVolume(0, 0f); } catch (System.Exception e) { Plugin.Log.LogWarning($"[ff] mute failed: {e.Message}"); }
            _wall.Start();
        }
        // LockTime caps at 60 fps; lift the cap but keep the fixed step (Time.captureDeltaTime).
        if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
        if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
        if (_noCam && frame % 30 == 0)
            foreach (var cam in Camera.allCameras)
                if (cam != null && cam.enabled) { cam.enabled = false; _disabled.Add(cam); }

        double now = _wall.Elapsed.TotalMilliseconds;
        string tag = GameBridge.LoadingLevel ? "loading" : (GameBridge.Combat?.IsAnEncounterRunning() ?? false) ? "battle" : "roaming";
        _buckets.TryGetValue(tag, out var b);
        _buckets[tag] = (b.frames + 1, b.ms + (now - _lastMs));
        _lastMs = now;
        if (now - _lastReportMs >= 5000) { _lastReportMs = now; Report($"f{frame}"); }
    }

    private static void Report(string label)
    {
        var parts = new List<string>();
        foreach (var kv in _buckets) parts.Add($"{kv.Key} {kv.Value.frames / (kv.Value.ms / 1000.0):F0} fps ({kv.Value.frames} frames)");
        Plugin.Log.LogInfo($"[ff] {label}: {string.Join(", ", parts)}");
    }

    public static void Close()
    {
        if (!_on) return;
        Report("total");
        foreach (var cam in _disabled) if (cam != null) cam.enabled = true;
        _disabled.Clear();
    }
}
