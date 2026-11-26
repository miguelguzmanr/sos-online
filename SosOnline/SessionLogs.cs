using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using SosOnline.Lockstep;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline;

/// <summary>
/// Logs to share when something goes wrong, kept per game start in <c>BepInEx\SosOnline\logs</c> (BepInEx's own
/// LogOutput.log is overwritten on every start):
/// <list type="bullet">
/// <item><c>sosonline-&lt;time&gt;.log</c>: everything the mod logs, the game's errors, the build and game version,
/// and a session summary every minute while co-op runs (frame, players, ping, delay, stalls, sync checks).</item>
/// <item><c>state-&lt;time&gt;.log</c>: every state snapshot (every 30 frames). The host's and a guest's files can be
/// compared frame by frame to find where they went apart.</item>
/// </list>
/// The last <see cref="Keep"/> starts are kept. F8 and the title screen's Online menu open the folder.
/// Test runs (any <c>--sosonline-*</c> flag) write theirs to <c>logs\tests</c> instead, with their own rotation
/// (<see cref="KeepTests"/>), so a test run never pushes a player's real logs out.
/// </summary>
internal static class SessionLogs
{
    public const int Keep = 10;
    /// <summary>A test run starts several instances, so its folder keeps more starts.</summary>
    public const int KeepTests = 40;

    /// <summary>Started with any --sosonline-* flag. Read here because <see cref="Init"/> runs before TestHarness.Init.</summary>
    public static bool IsTestRun { get; } =
        Environment.GetCommandLineArgs().Any(a => a.StartsWith("--sosonline-", StringComparison.OrdinalIgnoreCase));

    /// <summary>This start's logs: the real logs folder, or its <c>tests</c> subfolder in a test run.</summary>
    public static string Folder => IsTestRun ? Path.Combine(RealFolder, "tests") : RealFolder;

    private static string RealFolder => Path.Combine(Paths.BepInExRootPath, "SosOnline", "logs");

    /// <summary>The mod's version and the commit it was built from ("0.1.0+e3d3ca0").</summary>
    public static string Build { get; } =
        typeof(SessionLogs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Plugin.Version;

    private static Listener _listener;
    private static StreamWriter _state;
    private static string _stamp;
    private static float _nextSummary;

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Prune();
            _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            // Two games started in the same second on one PC (tests): keep them apart.
            if (File.Exists(Path.Combine(Folder, $"sosonline-{_stamp}.log"))) _stamp += $"-{Process.GetCurrentProcess().Id}";
            _listener = new Listener(Path.Combine(Folder, $"sosonline-{_stamp}.log"));
            BepInEx.Logging.Logger.Listeners.Add(_listener);
            _listener.Write($"Sea of Stars Online Co-op {Build}, started {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            _listener.Write($"OS {Environment.OSVersion}, {Environment.ProcessorCount} CPUs, .NET {Environment.Version}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[logs] could not open the log file: {e.Message}");
        }
    }

    /// <summary>Old starts beyond the last <see cref="Keep"/> (<see cref="KeepTests"/> in a test run) are deleted (log
    /// and state file of each), in this start's folder only.</summary>
    private static void Prune()
    {
        int keep = IsTestRun ? KeepTests : Keep;
        var starts = new DirectoryInfo(Folder).GetFiles("sosonline-*.log").OrderByDescending(f => f.Name).Skip(keep - 1).ToList();
        foreach (var log in starts)
        {
            string stamp = log.Name.Substring("sosonline-".Length, log.Name.Length - "sosonline-".Length - ".log".Length);
            try { log.Delete(); } catch { }
            try { File.Delete(Path.Combine(Folder, $"state-{stamp}.log")); } catch { }
        }
    }

    /// <summary>Called once the game is up (Unity's version and screen are known only then).</summary>
    private static bool _gameInfoLogged;

    public static void Update()
    {
        if (_listener == null) return;
        if (!_gameInfoLogged && Time.frameCount > 1)
        {
            _gameInfoLogged = true;
            _listener.Write($"Game {Application.version}, Unity {Application.unityVersion}, screen {Screen.width}x{Screen.height}, {SystemInfo.graphicsDeviceName}");
        }
        var s = Session.Instance;
        if (s.Phase != Phase.Running || Time.realtimeSinceStartup < _nextSummary) return;
        _nextSummary = Time.realtimeSinceStartup + 60f;
        try { Plugin.Log.LogInfo("[summary] " + Summary(s)); } catch { }
    }

    private static string Summary(Session s)
    {
        var e = s.Engine;
        string players = string.Join(", ", Enumerable.Range(0, InputOverride.SlotCount)
            .Where(i => !string.IsNullOrEmpty(s.SlotName[i]))
            .Select(i => $"P{i + 1} {s.SlotName[i]}{(Game.GameBridge.IsPlaying(i) ? "" : " (not playing)")}"));
        string pings = string.Join(", ", s.Peers.Select(p => $"P{p.slot + 1} {p.rtt:F0} ms"));
        return $"f{e.AppliedFrame} {(s.IsHost ? "host" : $"guest P{s.LocalSlot + 1}")}, {players}; ping {pings}; delay {e.Delay}; " +
               $"stall max {e.MaxStallMs:F0} ms; sync {s.Sync.Checks} checks, {s.Sync.Corrections} corrections, " +
               $"{(s.Sync.Desynced ? "OUT OF SYNC: " + s.Sync.LastDesyncReason : "in sync")}; level {Game.GameBridge.CurrentLevelId}";
    }

    /// <summary>Every state snapshot of a running session (host: its own; guests: their own at the same frames).</summary>
    public static void Snapshot(StateSnapshot snap)
    {
        if (_stamp == null) return;
        try
        {
            _state ??= new StreamWriter(Path.Combine(Folder, $"state-{_stamp}.log"), true) { AutoFlush = true };
            _state.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {snap.Hash():X8} {snap}");
        }
        catch { }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true });
            Plugin.Log.LogInfo("[logs] opened the logs folder");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[logs] could not open {Folder}: {e.Message}"); }
    }

    private sealed class Listener : ILogListener
    {
        private readonly StreamWriter _w;
        public Listener(string path) { _w = new StreamWriter(path, false) { AutoFlush = true }; }

        public LogLevel LogLevelFilter => LogLevel.All;

        public void Write(string line)
        {
            lock (_w) _w.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }

        public void LogEvent(object sender, LogEventArgs e)
        {
            bool ours = e.Source?.SourceName == "Sea of Stars Online Co-op";
            // The game's own messages only when something went wrong (exceptions and errors; its warnings are noise).
            if (!ours && (e.Level & (LogLevel.Fatal | LogLevel.Error)) == 0) return;
            try { Write($"[{e.Level}{(ours ? "" : ":" + e.Source?.SourceName)}] {e.Data}"); } catch { }
        }

        public void Dispose() => _w.Dispose();
    }
}
