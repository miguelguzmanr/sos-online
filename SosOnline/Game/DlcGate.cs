using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using SosOnline.Lockstep;

namespace SosOnline.Game;

/// <summary>
/// During a session a DLC counts only if every player owns it (findings §17). The game asks
/// <c>PlatformManager.IsDLCUsable</c> per PC, from that PC's Steam entitlements: story nodes, the new-game setup and the
/// Throes of the Watchmaker manager branch on it, so an owner and a non-owner played different scenes from the same
/// save (a DLC owner's new game starts with an extra relic and the Solstice diploma, and the intro differs).
///
/// The host fixes the session's set (Steam DLC app ids): the owners' intersection at the session start, narrowed by
/// each joiner on the frame it drops in. Leaves never widen it. While <see cref="Session"/> is set, every PC answers
/// <c>IsDLCUsable</c> from it; outside a session the game's own answer stands.
/// </summary>
internal static class DlcGate
{
    /// <summary>The session's DLC app ids, or null outside a session.</summary>
    public static HashSet<int> Session { get; private set; }

    private static bool _own; // answer from Steam: reading this PC's ownership while the gate is on
    private static readonly Lazy<bool> _noDlc = new(() => TestHarness.TryArg("nodlc", out _));
    private static bool NoDlc => _noDlc.Value;

    private static PlatformManager Platform
    {
        get { try { return Manager<PlatformManager>.instance; } catch { return null; } }
    }

    /// <summary>The DLC this PC owns (app ids). <c>--sosonline-nodlc</c>: none.</summary>
    public static List<int> Owned()
    {
        var owned = new List<int>();
        if (NoDlc) return owned;
        var platform = Platform;
        if (platform == null) return owned;
        _own = true;
        try
        {
            var defs = platform.dlcDefinitions;
            if (defs != null)
                for (int i = 0; i < defs.Count; i++)
                {
                    var d = defs[i];
                    if (d != null && d.SteamDLCAppId != 0 && platform.IsDLCUsable(d)) owned.Add(d.SteamDLCAppId);
                }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[dlc] could not read this PC's DLC: {e.Message}"); }
        finally { _own = false; }
        return owned;
    }

    public static string Describe(IEnumerable<int> ids)
    {
        var list = ids?.ToList() ?? new List<int>();
        if (list.Count == 0) return "none";
        var names = new List<string>();
        try
        {
            var defs = Platform?.dlcDefinitions;
            foreach (int id in list)
            {
                string name = null;
                if (defs != null)
                    for (int i = 0; i < defs.Count; i++)
                        if (defs[i] != null && defs[i].SteamDLCAppId == id) name = defs[i].Name;
                names.Add(name != null ? $"{name} ({id})" : id.ToString());
            }
        }
        catch { names = list.Select(i => i.ToString()).ToList(); }
        return string.Join(", ", names);
    }

    /// <summary>
    /// Every PC, on an agreed lockstep frame (the session start, a drop-in reload): from now on the game sees exactly
    /// these DLC. Where that changes this PC's answer, the game's own "entitlements changed" event fires so anything
    /// that cached the old answer refreshes, as when Steam reports a change.
    /// </summary>
    public static void Apply(IEnumerable<int> ids, int frame)
    {
        var next = new HashSet<int>(ids ?? Enumerable.Empty<int>());
        var before = Effective();
        Session = next;
        var after = Effective();
        Plugin.Log.LogInfo($"[dlc] f{frame} session DLC: {Describe(next)}");
        if (before.SetEquals(after)) return;
        var off = before.Except(after).ToList();
        if (off.Count > 0)
        {
            Plugin.Log.LogInfo($"[dlc] f{frame} off for this session on this PC: {Describe(off)}");
            UI.Toast.Show("DLC is off: a friend doesn't have it");
        }
        try { Platform?.OnDLCEntitlementChanged?.Invoke(); }
        catch (Exception e) { Plugin.Log.LogWarning($"[dlc] entitlement event failed: {e.Message}"); }
    }

    /// <summary>Lockstep stopped: the game's own answer again.</summary>
    public static void Clear()
    {
        if (Session == null) return;
        Session = null;
        Plugin.Log.LogInfo("[dlc] session DLC cleared: this PC's own DLC again");
        try { Platform?.OnDLCEntitlementChanged?.Invoke(); } catch { }
    }

    /// <summary>The DLC the game sees right now (the session's set, or this PC's own).</summary>
    private static HashSet<int> Effective() => Session != null ? new HashSet<int>(Session) : new HashSet<int>(Owned());

    /// <summary>The Throes of the Watchmaker app id, or 0 when unknown.</summary>
    public static int WatchmakerAppId()
    {
        try
        {
            var m = Manager<ThroesOfTheWatchmakerManager>.instance;
            return m?.totwDLCDefinition?.SteamDLCAppId ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>The game being played needs the Throes of the Watchmaker DLC (it is in it, or the save was flagged).</summary>
    public static bool RunningGameNeedsWatchmaker()
    {
        try
        {
            var m = Manager<ThroesOfTheWatchmakerManager>.instance;
            if (m == null) return false;
            if (m.IsInDLC) return true;
            var data = m.GetSaveData();
            return data != null && (data.saveRequiresTOTW || data.isInDLC);
        }
        catch { return false; }
    }

    /// <summary>The save file needs the Throes of the Watchmaker DLC (its JSON says so).</summary>
    public static bool SaveNeedsWatchmaker(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            string json = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            return System.Text.RegularExpressions.Regex.IsMatch(json, "\"(saveRequiresTOTW|isInDLC)\"\\s*:\\s*true");
        }
        catch { return false; }
    }

    [HarmonyPatch(typeof(PlatformManager), nameof(PlatformManager.IsDLCUsable))]
    private static class UsablePatch
    {
        private static bool Prefix(PlatformManager __instance, DLCDefinition __0, ref bool __result)
        {
            if (_own || !NativeCode.Is<PlatformManager>(__instance)) return true;
            // --sosonline-nodlc: this test instance acts as a PC without any DLC, in and out of sessions.
            if (NoDlc) { __result = false; return false; }
            if (Session == null) return true;
            __result = __0 != null && Session.Contains(__0.SteamDLCAppId);
            return false;
        }
    }
}
