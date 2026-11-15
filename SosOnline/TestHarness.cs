using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline;

/// <summary>
/// Development harness driven by command-line flags, so two game instances on one PC can test a session end to end:
///   --sosonline-udp=PORT          use loopback UDP instead of Steam (peer id = port)
///   --sosonline-host              host automatically
///   --sosonline-join=PORT         join a UDP host automatically
///   --sosonline-start=SLOT[a]     host: start from save SLOT (suffix 'a' = autosave) once guests are in and title is up
///   --sosonline-guests=N          guests to wait for before starting (default 1, 0 = solo lockstep)
///   --sosonline-delay=N           fixed input delay in frames
///   --sosonline-script=FILE       scripted local input: "frame [sys] action holdFrames [axis]" per line
///   --sosonline-shots=F1,F2,...   screenshot at those lockstep frames
///   --sosonline-quit=F            quit at lockstep frame F
///   --sosonline-reload=F          spike A: autosave at F, reload it in place at F+120 (every PC)
///   --sosonline-stateevery=N      state snapshot interval in frames (default 30)
///   --sosonline-hpdiverge=F       at lockstep frame F, lower the first party member's HP by 1 on this PC only
/// Every state snapshot is also appended to BepInEx/SosOnline/state-&lt;id&gt;.log for offline comparison.
/// </summary>
/// <summary>Per-instance copy of this plugin's log lines (several test instances share BepInEx's log file).</summary>
internal sealed class InstanceLog : BepInEx.Logging.ILogListener
{
    private readonly StreamWriter _w;
    public InstanceLog(string path) { _w = new StreamWriter(path, false) { AutoFlush = true }; }
    public BepInEx.Logging.LogLevel LogLevelFilter => BepInEx.Logging.LogLevel.All;
    public void LogEvent(object sender, BepInEx.Logging.LogEventArgs e)
    {
        if (e.Source?.SourceName == "Sea of Stars Online Co-op") lock (_w) _w.WriteLine($"[{e.Level}] {e.Data}");
    }
    public void Dispose() => _w.Dispose();
}

internal static class TestHarness
{
    private static readonly Dictionary<string, string> Args = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(int frame, bool sys, int action, int hold, float axis)> Script = new();
    private static readonly HashSet<int> Shots = new();
    private static int _quitFrame = -1;
    private static int _guests = 1;
    private static string _startSlot;
    private static double _titleSince = -1;
    private static bool _autoDone;
    private static StreamWriter _stateLog;
    private static string _scriptPath;

    public static string OutDir => Path.Combine(Paths.BepInExRootPath, "SosOnline");
    public static string InstanceId => Args.TryGetValue("udp", out var p) ? p : "steam";

    public static void Init()
    {
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (!a.StartsWith("--sosonline-", StringComparison.OrdinalIgnoreCase)) continue;
            var body = a.Substring("--sosonline-".Length);
            int eq = body.IndexOf('=');
            Args[eq < 0 ? body : body[..eq]] = eq < 0 ? "" : body[(eq + 1)..];
        }
        if (Args.Count == 0) return;
        Plugin.Log.LogInfo("[test] args: " + string.Join(" ", Args.Select(kv => $"{kv.Key}={kv.Value}")));

        if (Args.TryGetValue("udp", out var port))
        {
            Session.Instance.UseUdp(ushort.Parse(port));
            // Test instances never touch the real saves, nor each other's.
            SaveMirror.UseSandbox($"SosOnlineTest-{port}");
            SaveMirror.MirrorFolder = $"SosOnlineSession-{port}";
        }
        if (Args.TryGetValue("delay", out var d)) Session.Instance.ConfiguredDelay = int.Parse(d);
        if (Args.TryGetValue("guests", out var g)) _guests = int.Parse(g);
        if (Args.TryGetValue("start", out var s)) _startSlot = s;
        if (Args.TryGetValue("quit", out var q)) _quitFrame = int.Parse(q);
        if (Args.TryGetValue("shots", out var shots))
            foreach (var f in shots.Split(',', StringSplitOptions.RemoveEmptyEntries)) Shots.Add(int.Parse(f));
        Args.TryGetValue("script", out _scriptPath);
        if (Args.ContainsKey("nocomp")) TimingCompensation.Enabled = false;
        if (Args.TryGetValue("botoffset", out var bo)) CombatBot.ImpactOffset = int.Parse(bo);
        if (Args.TryGetValue("stateevery", out var se)) StateSync.Interval = Math.Max(1, int.Parse(se));

        Directory.CreateDirectory(OutDir);
        BepInEx.Logging.Logger.Listeners.Add(new InstanceLog(Path.Combine(OutDir, $"log-{InstanceId}.txt")));
        _stateLog = new StreamWriter(Path.Combine(OutDir, $"state-{InstanceId}.log"), false) { AutoFlush = true };
    }

    private static int _uiStep;
    private static double _uiAt;

    /// <summary>--sosonline-uitest: screenshots of the title screen, the Online popup and the friends popup.</summary>
    private static void UiTest()
    {
        if (!GameBridge.AtTitleScreen) return;
        double now = Time.realtimeSinceStartupAsDouble;
        if (_uiStep == 0) { _uiStep = 1; _uiAt = now + 5; return; }
        if (now < _uiAt) return;
        var title = GameBridge.FindTitleScreen();
        switch (_uiStep)
        {
            case 1: title.ShowChoices(); _uiAt = now + 2; break;
            case 2: Shot("ui-title"); _uiAt = now + 1; break;
            case 3: UI.NativeMenus.OpenTitleMenu(); _uiAt = now + 2; break;
            case 4: Shot("ui-online"); _uiAt = now + 1; break;
            case 5: UI.NativeMenus.ClickEntry(0); _uiAt = now + 5; break; // Host
            case 6: Shot("ui-hosting"); _uiAt = now + 1; break;
            case 7: Plugin.Log.LogInfo($"[test] lobby={Session.Instance.LobbyId} phase={Session.Instance.Phase}"); Session.Instance.Leave(); _uiAt = now + 2; break;
            case 8: Shot("ui-left"); _uiAt = now + 1; break;
            case 9: Plugin.Log.LogInfo("[test] ui test done"); Application.Quit(); break;
        }
        _uiStep++;
    }

    private static void Shot(string name) => ScreenCapture.CaptureScreenshot(Path.Combine(OutDir, $"{name}-{InstanceId}.png"));

    /// <summary>Called from the plugin's Update.</summary>
    private static bool _xrefDone, _connectedOnce;

    public static void Update()
    {
        if (!_xrefDone && Args.ContainsKey("xref") && Time.frameCount > 60) { _xrefDone = true; XrefProbe.Run(); Application.Quit(); }
        if (Args.ContainsKey("uitest")) UiTest();
        if (Args.Count == 0 || _autoDone) return;
        var session = Session.Instance;

        if (Args.ContainsKey("host") && !session.InSession && !_connectedOnce) { _connectedOnce = true; session.Host(); }
        if (Args.TryGetValue("join", out var hostPort) && !session.InSession && !_connectedOnce) { _connectedOnce = true; session.JoinUdp(ushort.Parse(hostPort)); }

        if (_startSlot != null && session.IsHost && session.Phase == Phase.Lobby)
        {
            if (GameBridge.AtTitleScreen)
            {
                if (_titleSince < 0) _titleSince = Time.realtimeSinceStartupAsDouble;
                bool guestsIn = session.Peers.Count() >= _guests;
                if (guestsIn && Time.realtimeSinceStartupAsDouble - _titleSince > 4)
                {
                    bool auto = _startSlot.EndsWith("a");
                    int slot = int.Parse(_startSlot.TrimEnd('a'));
                    Plugin.Log.LogInfo($"[test] auto-start from slot {slot} auto={auto}");
                    session.StartFromSave(slot, auto);
                    _autoDone = true;
                }
            }
            else _titleSince = -1;
        }
    }

    public static bool TraceEnabled => Args.ContainsKey("trace");

    /// <summary>Test input filter (scripts, bots), chained after the session's own filter.</summary>
    public static Func<int, int, InputSnapshot, InputSnapshot> Filter;

    public static void Attach(LockstepEngine engine)
    {
        bool bot = Args.TryGetValue("bot", out var botMode);
        CombatBot.PerfectPresses = bot && botMode != "menu";
        if (_scriptPath == null && !bot) return;
        if (_scriptPath != null) LoadScript();
        Filter = (frame, channel, snap) =>
        {
            if (_scriptPath != null) snap = ApplyScript(frame, channel, snap);
            if (bot && channel != InputOverride.SystemChannel) snap = CombatBot.Apply(frame, snap);
            return snap;
        };
    }

    private static void LoadScript()
    {
        Script.Clear();
        if (!File.Exists(_scriptPath)) { Plugin.Log.LogError($"[test] script not found: {_scriptPath}"); return; }
        foreach (var raw in File.ReadAllLines(_scriptPath))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            int frame = int.Parse(t[0]);
            bool sys = t.Count > 1 && t[1] == "sys";
            if (sys) t.RemoveAt(1);
            int action = ActionTable.IndexOf(t[1]);
            if (action < 0) { Plugin.Log.LogError($"[test] unknown action '{t[1]}'. Known: {string.Join(", ", ActionTable.Names)}"); continue; }
            int hold = t.Count > 2 ? int.Parse(t[2]) : 1;
            float axis = t.Count > 3 ? float.Parse(t[3], CultureInfo.InvariantCulture) : 0f;
            Script.Add((frame, sys, action, hold, axis));
        }
        Plugin.Log.LogInfo($"[test] loaded {Script.Count} scripted inputs");
    }

    private static InputSnapshot ApplyScript(int frame, int channel, InputSnapshot snap)
    {
        bool sysChannel = channel == InputOverride.SystemChannel;
        foreach (var e in Script)
        {
            if (e.sys != sysChannel) continue;
            int rel = frame - e.frame;
            if (rel < 0 || rel > e.hold) continue;
            if (rel < e.hold)
            {
                if (e.axis < 0)
                {
                    snap.Bits[e.action] |= ActionBits.NegButton | (rel == 0 ? ActionBits.NegButtonDown : 0);
                }
                else
                {
                    snap.Bits[e.action] |= ActionBits.Button | (rel == 0 ? ActionBits.ButtonDown : 0) | ActionBits.Repeating;
                }
                float v = e.axis == 0 ? 1f : e.axis;
                snap.Axis[e.action] = InputSnapshot.Quantize(v);
                snap.AxisRaw[e.action] = InputSnapshot.Quantize(v);
            }
            else if (e.axis >= 0)
            {
                snap.Bits[e.action] |= ActionBits.ButtonUp | (e.hold < 15 ? ActionBits.ShortPress : 0);
            }
        }
        return snap;
    }

    /// <summary>Starts the closest triggerable encounter. Runs on the same frame on every peer.</summary>
    private static void StartNearestEncounter()
    {
        var leader = GameBridge.Party?.Leader;
        if (leader == null) { Plugin.Log.LogWarning("[test] no party leader"); return; }
        var lp = leader.transform.position;
        var encounters = UnityEngine.Object.FindObjectsOfType<Encounter>()
            .Where(e => e != null)
            .OrderBy(e => (e.transform.position - lp).sqrMagnitude).ThenBy(e => e.gameObject.name)
            .ToList();
        Plugin.Log.LogInfo($"[test] encounters: {string.Join(", ", encounters.Select(e => $"{e.gameObject.name}@{(e.transform.position - lp).magnitude:F1}m trig={e.CanTrigger()}"))}");
        var target = encounters.FirstOrDefault(e => e.CanTrigger()) ?? encounters.FirstOrDefault();
        if (target == null) return;
        Plugin.Log.LogInfo($"[test] initiating encounter {target.gameObject.name}");
        target.InitiateEncounter(leader);
    }

    /// <summary>Deliberate divergence for testing the state check: this PC only.</summary>
    private static void DivergeHp(int frame)
    {
        var ids = GameBridge.Party?.CurrentParty;
        if (ids == null || ids.Count == 0) { Plugin.Log.LogWarning("[test] hpdiverge: no party"); return; }
        var data = GameBridge.Stats?.GetCharacterData(ids[0]);
        if (data == null) { Plugin.Log.LogWarning("[test] hpdiverge: no character data"); return; }
        int hp = data.currentHP;
        data.SetCurrentHP(hp - 1);
        Plugin.Log.LogInfo($"[test] hpdiverge at {frame}: {ids[0].characterId} HP {hp} -> {data.currentHP}");
    }

    public static void OnSnapshot(StateSnapshot s)
    {
        _stateLog?.WriteLine($"{s.Hash():X8} {s}");
    }

    private static bool At(string flag, int frame) => Args.TryGetValue(flag, out var v) && int.TryParse(v, out int f) && f == frame;

    private static bool _wasLoading = true;
    private static string _lastLevel = "";
    private static readonly bool[] _wasPlaying = new bool[InputOverride.SlotCount];
    private static int _reloadSlot = -1;
    private static Vector3? _reloadPos;
    private static int _reloadAngle = -1;

    /// <summary>Logs level-loading and co-op playing transitions with their lockstep frames.</summary>
    private static void TrackTransitions(int frame)
    {
        bool loading = GameBridge.LoadingLevel;
        if (loading != _wasLoading)
        {
            string lvl = GameBridge.CurrentLevelId;
            Plugin.Log.LogInfo($"[load] f{frame} loading={loading} level={lvl}{(lvl != _lastLevel ? " (changed)" : "")}");
            if (!loading) _lastLevel = lvl;
            _wasLoading = loading;
        }
        for (int s = 0; s < InputOverride.SlotCount; s++)
        {
            bool p = GameBridge.IsPlaying(s);
            if (p != _wasPlaying[s]) { Plugin.Log.LogInfo($"[load] f{frame} P{s + 1} playing={p}"); _wasPlaying[s] = p; }
        }
    }

    private static readonly List<(string name, Vector3 pos)> _reloadParty = new();

    /// <summary>
    /// --sosonline-reload=F: spike A. Autosave at F; at F+120 reload it in place. With --sosonline-reloadpos the
    /// party respawns where it stands at F+120, and at F+240 every member is put back on its own position.
    /// </summary>
    private static void ReloadSpike(int frame)
    {
        if (!Args.TryGetValue("reload", out var v) || !int.TryParse(v, out int f)) return;
        bool restore = Args.ContainsKey("reloadpos");
        if (frame == f)
        {
            _reloadSlot = GameBridge.AutoSave();
            Plugin.Log.LogInfo($"[reload] f{frame} autosave -> slot {_reloadSlot}, leader at {GameBridge.Party?.Leader?.transform.position}");
        }
        if (frame == f + 120)
        {
            if (GameBridge.Saving) Plugin.Log.LogError($"[reload] f{frame} still saving");
            Vector3? spawn = null;
            int angle = -1;
            _reloadParty.Clear();
            var leader = GameBridge.Party?.Leader;
            if (restore && leader != null)
            {
                spawn = leader.transform.position;
                try { angle = leader.lookDirectionController.CurrentLookAngle; } catch { }
                var list = GameBridge.Party.LeaderAndFollowers;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) _reloadParty.Add((list[i].gameObject.name, list[i].transform.position));
            }
            Plugin.Log.LogInfo($"[reload] f{frame} reloading slot {_reloadSlot} in place, spawn {(spawn.HasValue ? spawn.Value.ToString() : "checkpoint")}");
            if (_reloadSlot >= 0 && GameBridge.ReloadInPlace(_reloadSlot, true, spawn, angle)) Session.Instance.RearmAutoJoin();
        }
        if (restore && frame == f + 240 && _reloadParty.Count > 0)
        {
            if (GameBridge.LoadingLevel) { Plugin.Log.LogError($"[reload] f{frame} still loading, positions not restored"); return; }
            var list = GameBridge.Party?.LeaderAndFollowers;
            if (list == null) return;
            var moved = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                foreach (var (name, pos) in _reloadParty)
                    if (name == c.gameObject.name)
                    {
                        moved.Add($"{name} {(c.transform.position - pos).magnitude:F2}m");
                        c.transform.position = pos;
                    }
            }
            Plugin.Log.LogInfo($"[reload] f{frame} restored positions: {string.Join(", ", moved)}");
        }
    }

    public static void OnFrame(int frame)
    {
        var session = Session.Instance;
        TrackTransitions(frame);
        ReloadSpike(frame);
        if (At("leave", frame)) { Plugin.Log.LogInfo($"[test] leaving at {frame}"); session.Leave(); }
        if (At("kill", frame)) { Plugin.Log.LogInfo($"[test] killing process at {frame}"); System.Diagnostics.Process.GetCurrentProcess().Kill(); }
        if (At("pauseleave", frame)) { Plugin.Log.LogInfo($"[test] synced pause-menu leave of P2 at {frame}"); session.LeaveSlotDeterministic(1); }
        if (At("hpdiverge", frame)) DivergeHp(frame);
        if (At("admit", frame)) { Plugin.Log.LogInfo($"[test] admit waiting players at {frame}"); session.RequestResync(); }
        if (At("assigntest", frame))
        {
            var pm = UnityEngine.Object.FindObjectsOfType<PauseMenu>().FirstOrDefault();
            Plugin.Log.LogInfo($"[test] opening Assign Players at {frame} (pause menu {(pm != null ? "open" : "missing")})");
            pm?.AssignControllers();
        }
        if (frame % 300 == 0 && session.Engine.Running)
        {
            var playing = Enumerable.Range(0, InputOverride.SlotCount).Where(GameBridge.IsPlaying).Select(i => $"P{i + 1}");
            Plugin.Log.LogInfo($"[test] f{frame} playing: {string.Join(",", playing)}");
        }
        if (Args.ContainsKey("assignprobe")) Diagnostics.AssignProbe(frame);
        if (Args.TryGetValue("encounter", out var ef) && int.TryParse(ef, out int encounterFrame) && frame == encounterFrame)
            StartNearestEncounter();
        if (Shots.Contains(frame))
            ScreenCapture.CaptureScreenshot(Path.Combine(OutDir, $"shot-{InstanceId}-{frame:D6}.png"));
        if (frame == _quitFrame)
        {
            Plugin.Log.LogInfo($"[timing] stats: deferred attacks {TimingCompensation.DeferredAttacks}, attack regrades {TimingCompensation.AttackRegrades}, block checks {TimingCompensation.BlockChecks}, upgrades {TimingCompensation.BlockUpgrades}, downgrades {TimingCompensation.BlockDowngrades}, look-ahead wait {Session.Instance.Engine.LookaheadWaitMs:F0} ms");
            Plugin.Log.LogInfo($"[sync] snapshot capture: {StateSnapshot.CaptureCount} taken, avg {StateSnapshot.CaptureMsTotal / Math.Max(1, StateSnapshot.CaptureCount):F2} ms, max {StateSnapshot.CaptureMsMax:F2} ms");
            Plugin.Log.LogInfo($"[test] quitting at frame {frame}");
            _stateLog?.Flush();
            Application.Quit();
        }
    }
}
