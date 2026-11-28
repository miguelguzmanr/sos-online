using SosOnline.Game;
using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SosOnline.Lockstep;
using SosOnline.Net;
using SosOnline.Steam;
using SosOnline.UI;
using UnityEngine;

namespace SosOnline;

[BepInPlugin(Guid, "Sea of Stars Online Co-op", Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "sosonline.coop";
    public const string Version = "0.1.0";

    // Static copy of BasePlugin.Log (an instance member) so every class can log; hides it on purpose.
    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        SessionLogs.Init();
        try
        {
            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            ClassInjector.RegisterTypeInIl2Cpp<OnlineBehaviour>();
            AddComponent<OnlineBehaviour>();
            Settings.Bind(Config);
            SaveBackup.Init();
            TestHarness.Init();
            SosOnline.Update.AutoUpdate.Start();
            GameClock.Install();
            LoadBarrier.Install();
            if (TestHarness.TraceEnabled) Diagnostics.InstallTrace(harmony);
            if (TestHarness.TryArg("tracecalls", out var traceClasses)) Diagnostics.InstallCallTrace(harmony, traceClasses);
            if (TestHarness.TryArg("dashprobe", out _)) Diagnostics.InstallDashProbe(harmony);
            TimingCompensation.Install(harmony);
            DeterministicRandom.Install(harmony);
            if (TestHarness.TryArg("rngprobe", out _)) RngProbe.Install(harmony);
            CombatTrace.Install(harmony);
            BattleTurns.Install(harmony);
            LevelUps.Install(harmony);
            SpawnTrace.Install(harmony);
            if (TestHarness.TryArg("whichrva", out var rvas))
                foreach (var hex in rvas.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    Log.LogInfo($"[fold] rva 0x{hex}: {NativeCode.Containing(Convert.ToInt64(hex, 16))}");
            if (TestHarness.TryArg("castprobe", out _) || TestHarness.TryArg("callprobe", out _)) CombatTrace.InstallCallProbe(harmony);
            NativeCode.AuditPatches(harmony, Guid);
            SteamInvites.ParseCommandLine();
            Log.LogInfo($"Sea of Stars Online Co-op {SessionLogs.Build} loaded. Press F8 in game. Logs: {SessionLogs.Folder}");
        }
        catch (Exception e)
        {
            Log.LogError($"Failed to initialize: {e}");
        }
    }
}

/// <summary>The player's settings (BepInEx/config/sosonline.coop.cfg).</summary>
internal static class Settings
{
    private static BepInEx.Configuration.ConfigEntry<bool> _openOnStart, _ownTurns, _advancedStatus;
    private static BepInEx.Configuration.ConfigEntry<string> _rejoin, _statusBar;

    public static void Bind(BepInEx.Configuration.ConfigFile config)
    {
        _openOnStart = config.Bind("Online", "OpenOnStart", false,
            "Open a Steam lobby every time the game starts, so friends can join you without a hosting step.");
        _ownTurns = config.Bind("Online", "OwnCharacterTurns", true,
            "Host: in battles, each player gets the turns of their own character (off: the game picks a player at random, as in local co-op).");
        _rejoin = config.Bind("Online", "Rejoin", nameof(RejoinModes.Ask),
            "Host: how friends join or rejoin a game in progress. Ask: you let them in from your pause screen, then everyone " +
            "holds still and reloads for a moment. Auto: the same without asking (experimental). Off: nobody joins a game " +
            "in progress; everyone regroups at the title screen.");
        _statusBar = config.Bind("Display", "StatusBar", nameof(StatusBars.On),
            "The player bar at the top left during play: Off, On or Advanced (adds a detailed line with ping, sync and " +
            "waits). It always shows on the title screen and the co-op pause screen.");
        // Before 2026-11-27 the detailed line was a separate on/off setting.
        _advancedStatus = config.Bind("Display", "AdvancedStatusBar", false, "Replaced by StatusBar.");
        if (_advancedStatus.Value) { _statusBar.Value = nameof(StatusBars.Advanced); _advancedStatus.Value = false; }
    }

    /// <summary>Host setting: "Your character, your turn" in battles (fixed for a session when it starts).</summary>
    public static bool OwnTurns
    {
        get => TestHarness.TryArg("ownturns", out var v) ? v != "0" : _ownTurns?.Value ?? true;
        set { if (_ownTurns != null) _ownTurns.Value = value; }
    }

    /// <summary>
    /// Ask, Auto and Off are the host's choices. CatchUp is for tests only: a joiner replays the area first, a
    /// re-syncing guest falls back to a reload without asking (what the mod did before the setting, and what every
    /// test script expects).
    /// </summary>
    public enum RejoinModes { Ask, Auto, Off, CatchUp }

    /// <summary>
    /// Host setting: how friends join or rejoin a session in progress, new joiners, reconnects and re-syncs alike.
    /// Ask and Auto reload everyone in place (Stage 2), Ask only once the host lets them in; a re-syncing guest
    /// first tries to replay the area on its own PC, which disturbs nobody. Off: nobody joins a game in progress.
    /// Test runs keep CatchUp unless --sosonline-joinmode says otherwise.
    /// </summary>
    public static RejoinModes Rejoin
    {
        get
        {
            if (TestHarness.TryArg("joinmode", out var t) && Enum.TryParse<RejoinModes>(t, true, out var tm)) return tm;
            if (SessionLogs.IsTestRun) return RejoinModes.CatchUp;
            return Enum.TryParse<RejoinModes>(_rejoin?.Value, true, out var m) && m != RejoinModes.CatchUp ? m : RejoinModes.Ask;
        }
        set { if (_rejoin != null) _rejoin.Value = value.ToString(); }
    }

    public enum StatusBars { Off, On, Advanced }

    /// <summary>This PC: the player bar in play (Off hides it there), Advanced adds the detailed line at the top.</summary>
    public static StatusBars StatusBar
    {
        get => Enum.TryParse<StatusBars>(_statusBar?.Value, true, out var v) ? v : StatusBars.On;
        set { if (_statusBar != null) _statusBar.Value = value.ToString(); }
    }

    /// <summary>A Steam lobby opens once each time the game starts (the host still opens or closes it by hand).</summary>
    public static bool OpenOnStart
    {
        get => _openOnStart?.Value ?? false;
        set { if (_openOnStart != null) _openOnStart.Value = value; }
    }
}

/// <summary>Per-frame driver living on a hidden GameObject.</summary>
public sealed class OnlineBehaviour : MonoBehaviour
{
    public OnlineBehaviour(IntPtr ptr) : base(ptr) { }

    /// <summary>Physics steps Unity has run (diagnostics: --sosonline-fixedlog).</summary>
    internal static int FixedSteps;

    private void FixedUpdate() => FixedSteps++;

    /// <summary>A closing line, so a log that ends without it means the game crashed or was killed.</summary>
    private void OnApplicationQuit()
    {
        try
        {
            var s = Session.Instance;
            Plugin.Log.LogInfo($"[quit] the game is closing after {Time.realtimeSinceStartup / 60:F1} min" +
                (s.Engine.Running ? $", in a session at frame {s.Engine.AppliedFrame}" : ", not in a session"));
        }
        catch { }
    }

    private void Update()
    {
        try
        {
            if (!ActionTable.Ready) ActionTable.TryBuild();
            Session.Instance.Update();
            InputOverride.KeepUiFocus();
            SosOnline.Update.AutoUpdate.Update();
            Toast.Update();
            SessionLogs.Update();
            Overlay.Update();
            SosOnline.UI.GameUi.UpdateAreas();
            PlayerBar.Update();
            NativeMenus.Update();
            VersionLabel.Update();
            SteamInvites.Update();
            CoopProbe.Update();
            TestHarness.Update();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    private void OnGUI()
    {
        try { Overlay.OnGUI(); Toast.OnGUI(); }
        catch (Exception e) { Plugin.Log.LogError(e); }
    }
}
