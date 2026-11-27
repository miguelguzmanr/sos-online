using SosOnline.Net;

namespace SosOnline.UI;

/// <summary>
/// The settings' names, values and help, shared by the title screen's Online pages and the pause screen, so both
/// say the same thing.
/// </summary>
internal static class SettingLabels
{
    public const string OpenOnStart = "Open on Start", Rejoin = "Rejoin", BattleTurns = "Battle Turns",
        InputDelay = "Input Delay", StatusBar = "Status Bar";

    public static string OnOff(bool on) => on ? "On" : "Off";

    public static string RejoinValue(Settings.RejoinModes mode) => mode switch
    {
        Settings.RejoinModes.Auto => "Auto (Experimental)",
        Settings.RejoinModes.Off => "Off",
        Settings.RejoinModes.CatchUp => "Catch Up (Test)",
        _ => "Require Approval (Safest)",
    };

    /// <summary>The host's choices, safest first. Catch Up (tests only) moves on to the first.</summary>
    public static Settings.RejoinModes NextRejoin(Settings.RejoinModes mode, int dir = 1)
    {
        var order = new[] { Settings.RejoinModes.Ask, Settings.RejoinModes.Auto, Settings.RejoinModes.Off };
        int i = System.Array.IndexOf(order, mode);
        return i < 0 ? order[0] : order[(i + dir + order.Length) % order.Length];
    }

    public static string BattleTurnsValue(bool ownTurns) => ownTurns ? "Custom" : "Original";

    /// <summary>Input delay in frames: 0 is Auto (from the ping).</summary>
    private static readonly int[] Delays = { 0, 3, 5, 8 };

    public static string DelayValue(int frames) => frames switch
    {
        0 => "Auto",
        3 => "Low",
        5 => "Medium",
        8 => "High",
        _ => $"{frames * 17} ms",
    };

    public static int NextDelay(int frames, int dir = 1)
    {
        int i = System.Array.IndexOf(Delays, frames);
        return i < 0 ? Delays[0] : Delays[(i + dir + Delays.Length) % Delays.Length];
    }

    public static Settings.StatusBars NextStatusBar(Settings.StatusBars bar, int dir = 1) =>
        (Settings.StatusBars)(((int)bar + dir + 3) % 3);

    /// <summary>What a setting does, for the one under the cursor (two short lines). Null: no help.</summary>
    public static string Help(string key) => key switch
    {
        OpenOnStart => "Open your game to friends\nevery time it starts",
        Rejoin => Settings.Rejoin switch
        {
            Settings.RejoinModes.Auto => "Friends join without asking, in the\nnext area you enter, after a short reload",
            Settings.RejoinModes.Off => "Friends join only at the title screen.\nAfter a disconnect, everyone regroups there",
            _ => "You let each friend in. They join in the\nnext area you enter, after a short reload",
        },
        BattleTurns => "Original: a random player takes each turn.\nCustom: you take your own character's turns",
        InputDelay => "Each press waits a moment so every PC stays in step.\nAuto follows the ping. Low 50, Medium 85, High 135 ms",
        StatusBar => "The players at the top left during play.\nAdvanced adds ping and sync details",
        _ => null,
    };

    /// <summary>The pause screen's help for a setting that is fixed while co-op runs.</summary>
    public const string Locked = "Set when co-op starts";
}
