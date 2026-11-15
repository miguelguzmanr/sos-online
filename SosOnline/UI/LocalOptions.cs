using System;
using SosOnline.Game;
using UnityEngine;

namespace SosOnline.UI;

/// <summary>
/// The per-PC settings the local pause screen can change: volumes, brightness and display mode. They change only
/// how this PC sounds and looks, never the simulation, so they stay out of the synced game. They are applied with the
/// game's own setters on its live options (<c>SaveManager.SaveData.optionsSaveData</c>) and written to this PC's
/// own options file (never the session's mirrored copy, see <see cref="SaveMirror"/>).
///
/// Left out: language and "Timed Hit HUD feedback" (they may change what the shared game shows or does), and the
/// control mappings (the game's controls pages need its own Options screen).
/// </summary>
internal static class LocalOptions
{
    public enum Field { Master, Music, Sfx, Ambience, Dialog, Brightness, Display }

    public static readonly (Field field, string label)[] Rows =
    {
        (Field.Master, "Master volume"),
        (Field.Music, "Music"),
        (Field.Sfx, "Sound effects"),
        (Field.Ambience, "Ambience"),
        (Field.Dialog, "Voices"),
        (Field.Brightness, "Brightness"),
        (Field.Display, "Display"),
    };

    /// <summary>The game's sliders have this many steps above 0 (its UIIntSlider levels).</summary>
    public const int Max = 10;

    private static bool _dirty, _logged;

    private static OptionsSaveData Data
    {
        get { try { return GameBridge.Saves?.SaveData?.optionsSaveData; } catch { return null; } }
    }

    public static bool Available => Data != null;

    public static string Value(Field f)
    {
        var d = Data;
        if (d == null) return "";
        try
        {
            if (!_logged)
            {
                _logged = true;
                var a0 = d.audio;
                Plugin.Log.LogInfo($"[localpause] options: master {a0.masterVolume}, music {a0.musicVolume}, sfx {a0.sfxVolume}, ambience {a0.ambienceVolume}, dialog {a0.dialogVolume}, brightness {d.video.brightness}, display {d.video.DisplayMode}");
            }
            var a = d.audio;
            return f switch
            {
                Field.Master => a.masterVolume.ToString(),
                Field.Music => a.musicVolume.ToString(),
                Field.Sfx => a.sfxVolume.ToString(),
                Field.Ambience => a.ambienceVolume.ToString(),
                Field.Dialog => a.dialogVolume.ToString(),
                Field.Brightness => d.video.brightness.ToString(),
                Field.Display => d.video.DisplayMode switch
                {
                    OptionsSaveData.DisplayMode.Windowed => "Windowed",
                    OptionsSaveData.DisplayMode.FullScreen => "Fullscreen",
                    _ => "Exclusive",
                },
                _ => "",
            };
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[localpause] options: {e.Message}"); return "?"; }
    }

    /// <summary>One step left (-1) or right (+1).</summary>
    public static void Change(Field f, int dir)
    {
        var d = Data;
        if (d == null || dir == 0) return;
        try
        {
            if (f == Field.Brightness)
            {
                var v = d.video;
                v.brightness = Mathf.Clamp(v.brightness + dir, 0, Max);
                d.video = v;
                d.ApplyVideo();
            }
            else if (f == Field.Display)
            {
                var v = d.video;
                int mode = ((int)v.DisplayMode + dir + 3) % 3;
                var res = mode == (int)OptionsSaveData.DisplayMode.Windowed ? v.LastSavedResolution : v.DesktopMonitorResolution;
                int w = res.width > 0 ? res.width : Screen.width, h = res.height > 0 ? res.height : Screen.height;
                v.SetScreen(w, h, (OptionsSaveData.DisplayMode)mode, v.PixelPerfect, v.Vsync);
                d.video = v;
                d.ApplyVideo();
            }
            else
            {
                var a = d.audio;
                switch (f)
                {
                    case Field.Master: a.masterVolume = Mathf.Clamp(a.masterVolume + dir, 0, Max); break;
                    case Field.Music: a.musicVolume = Mathf.Clamp(a.musicVolume + dir, 0, Max); break;
                    case Field.Sfx: a.sfxVolume = Mathf.Clamp(a.sfxVolume + dir, 0, Max); break;
                    case Field.Ambience:
                        a.ambienceVolume = Mathf.Clamp(a.ambienceVolume + dir, 0, Max);
                        a.environmentalVolume = Mathf.Clamp(a.environmentalVolume + dir, 0, Max);
                        break;
                    case Field.Dialog: a.dialogVolume = Mathf.Clamp(a.dialogVolume + dir, 0, Max); break;
                }
                d.audio = a;
                d.ApplyAudio();
            }
            _dirty = true;
            Plugin.Log.LogInfo($"[localpause] option {f} = {Value(f)}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[localpause] option {f}: {e.Message}"); }
    }

    /// <summary>Writes this PC's options file if anything changed (leaving the page or closing the screen).</summary>
    public static void SaveIfChanged()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            GameBridge.Saves?.SaveOptions();
            Plugin.Log.LogInfo("[localpause] options saved");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[localpause] saving options: {e.Message}"); }
    }
}
