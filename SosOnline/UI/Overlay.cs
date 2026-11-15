using System;
using System.Collections.Generic;
using System.Linq;
using Rewired;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using SosOnline.Steam;
using UnityEngine;

namespace SosOnline.UI;

/// <summary>Immediate-mode co-op menu (toggle with F8) and a small status HUD while a session runs.</summary>
internal static class Overlay
{
    public static bool Open;

    private static List<(ulong id, string name, ulong lobby)> _friends = new();
    private static float _nextFriendRefresh;
    private static List<(int index, bool auto, string label)> _slots = new();
    private static float _nextSlotRefresh;
    private static bool _savedCursorVisible;
    private static CursorLockMode _savedCursorLock;

    private static GUIStyle _box, _label, _title, _small;

    // Manual layout cursor (GUILayout's params arrays are awkward through IL2CPP interop).
    private static Rect _area;
    private static float _y;
    private const float Line = 26f, Pad = 10f;

    public static void Update()
    {
        bool toggle = false;
        try { toggle = ReInput.isReady && ReInput.controllers.Keyboard.GetKeyDown(KeyCode.F8); } catch { }
        if (toggle) SetOpen(!Open);
        MenuStatus.Update();
        CatchUpScreen.Update();
        LocalPause.Update();

        if (Open && Time.unscaledTime >= _nextFriendRefresh && Session.Instance.Role == Role.None && !Session.Instance.UsingUdp)
        {
            _nextFriendRefresh = Time.unscaledTime + 2f;
            if (SteamApi.TryInit()) _friends = SteamApi.FriendsInGame();
        }
        if (Open && Time.unscaledTime >= _nextSlotRefresh)
        {
            _nextSlotRefresh = Time.unscaledTime + 2f;
            _slots = GameBridge.ListSaveSlots();
        }
    }

    private static void SetOpen(bool open)
    {
        if (open == Open) return;
        Open = open;
        if (open)
        {
            _savedCursorVisible = Cursor.visible;
            _savedCursorLock = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedCursorLock;
        }
    }

    private static void Styles()
    {
        if (_box != null) return;
        _box = new GUIStyle(GUI.skin.box);
        _label = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        _label.normal.textColor = Color.white;
        _small = new GUIStyle(_label) { fontSize = 13 };
        _small.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
        _title = new GUIStyle(_label) { fontSize = 19, fontStyle = FontStyle.Bold };
        _title.normal.textColor = new Color(1f, 0.85f, 0.45f);
    }

    public static void OnGUI()
    {
        Styles();
        var s = Session.Instance;
        CatchUpScreen.OnGUI();
        if (s.Phase == Phase.Running && !s.CatchingUp) DrawHud(s);
        MenuStatus.OnGUI();
        LocalPause.OnGUI();
        if (!Open)
        {
            return;
        }

        float w = 460, h = Math.Min(Screen.height - 40, 640);
        _area = new Rect(Screen.width - w - 20, 20, w, h);
        GUI.Box(_area, "");
        GUI.Box(_area, "");
        _y = _area.y + Pad;

        Label("Sea of Stars: Online Co-op", _title);
        Label(s.UsingUdp ? "Transport: local UDP (test mode)" : SteamApi.Available ? $"Signed in as {SteamApi.LocalName}" : "Steam not detected", _small);
        if (!string.IsNullOrEmpty(s.Status)) Label(s.Status, _label);
        _y += 4;

        switch (s.Phase)
        {
            case Phase.Idle: DrawIdle(s); break;
            case Phase.CreatingLobby:
            case Phase.JoiningLobby:
            case Phase.Connecting:
                if (Button("Cancel")) s.Leave();
                break;
            case Phase.Lobby:
            case Phase.AwaitingReady:
                DrawRoster(s);
                if (s.IsHost) DrawHostLobby(s);
                else if (Button("Leave session")) s.Leave();
                break;
            case Phase.Running:
                DrawRoster(s);
                DrawRunning(s);
                break;
        }
        _y += 4;
        Label($"Mod {SessionLogs.Build}", _small);
        if (Button("Open logs folder (to share if something went wrong)")) SessionLogs.OpenFolder();

        _y = _area.yMax - Line - 4;
        Label("F8: close   ·   Use a gamepad or keyboard to play; the mouse is not synced.", _small);
    }

    private static void DrawIdle(Session s)
    {
        if (Button("Host a session")) s.Host();
        _y += 6;
        Label("Friends in Sea of Stars:", _label);
        if (_friends.Count == 0) Label("  (none online)", _small);
        foreach (var f in _friends.Take(8))
        {
            var row = new Rect(_area.x + Pad, _y, _area.width - 2 * Pad, Line);
            GUI.Label(new Rect(row.x, row.y + 3, row.width - 110, Line), f.name, _label);
            if (f.lobby != 0)
            {
                if (GUI.Button(new Rect(row.xMax - 100, row.y, 100, Line - 2), "Join")) s.JoinFriend(f.id);
            }
            else GUI.Label(new Rect(row.xMax - 100, row.y + 3, 100, Line), "not hosting", _small);
            _y += Line;
        }
    }

    private static void DrawRoster(Session s)
    {
        Label("Players:", _label);
        for (int slot = 0; slot < InputOverride.SlotCount; slot++)
        {
            string name = s.SlotName[slot];
            if (string.IsNullOrEmpty(name)) { Label($"  P{slot + 1}: (open)", _small); continue; }
            var peer = s.Peers.FirstOrDefault(p => p.slot == slot);
            string extra = slot == s.LocalSlot ? " (you)" : peer.name != null ? $"  {peer.rtt:F0} ms{(s.Phase == Phase.AwaitingReady ? peer.ready ? "  ready" : "  loading…" : "")}" : "";
            Label($"  P{slot + 1}: {name}{extra}", _small);
        }
        _y += 4;
    }

    private static void DrawHostLobby(Session s)
    {
        if (!s.UsingUdp && Button("Invite friends (Steam overlay)")) s.InviteFriends();

        var row = new Rect(_area.x + Pad, _y, _area.width - 2 * Pad, Line);
        GUI.Label(new Rect(row.x, row.y + 3, 240, Line), $"Input delay: {(s.ConfiguredDelay == 0 ? "auto" : s.ConfiguredDelay + " frames")}", _small);
        if (GUI.Button(new Rect(row.xMax - 90, row.y, 40, Line - 2), "-")) s.ConfiguredDelay = Math.Max(0, s.ConfiguredDelay - 1);
        if (GUI.Button(new Rect(row.xMax - 45, row.y, 40, Line - 2), "+")) s.ConfiguredDelay = Math.Min(20, s.ConfiguredDelay + 1);
        _y += Line + 4;

        if (s.Phase == Phase.AwaitingReady)
        {
            Label("Waiting for everyone to load the save data…", _label);
        }
        else if (!GameBridge.AtTitleScreen)
        {
            Label("Return to the title screen to start the co-op game.", _label);
        }
        else
        {
            Label("Start co-op from save:", _label);
            foreach (var slot in _slots.Take(10))
                if (Button(slot.label)) s.StartFromSave(slot.index, slot.auto);
            if (_slots.Count == 0) Label("  (no saves found: start a game solo and save first)", _small);
        }
        _y += 6;
        if (Button("Close session")) s.Leave();
    }

    private static void DrawRunning(Session s)
    {
        var e = s.Engine;
        Label($"Frame {e.Frame} · input delay {e.Delay} · worst stall {e.MaxStallMs:F0} ms", _small);
        Label($"Sync checks {s.Sync.Checks} · corrections {s.Sync.Corrections}{(s.Sync.Desynced ? " · DESYNC: " + s.Sync.LastDesyncReason : "")}", _small);
        _y += 6;
        if (s.IsHost) { if (Button("End co-op (everyone keeps playing solo)")) s.EndSession(); }
        else if (Button("Leave session")) s.Leave();
    }

    private static void DrawHud(Session s)
    {
        var e = s.Engine;
        var names = Enumerable.Range(0, InputOverride.SlotCount).Where(i => !string.IsNullOrEmpty(s.SlotName[i])).Select(i => $"P{i + 1} {s.SlotName[i]}");
        string sync = s.Sync.Desynced ? "<color=#ff6060>DESYNC</color>" : "<color=#80ff80>synced</color>";
        string ping = s.Peers.Any() ? $" · {s.Peers.Max(p => p.rtt):F0} ms" : "";
        string stall = e.LastStallMs > 30 ? $" · <color=#ffcc40>waiting {e.LastStallMs:F0} ms</color>" : "";
        GUI.Label(new Rect(12, 8, 900, 24), $"Online co-op · {string.Join(", ", names)}{ping} · {sync}{stall}", _small);
    }

    private static void Label(string text, GUIStyle style)
    {
        float h = style.CalcHeight(new GUIContent(text), _area.width - 2 * Pad);
        GUI.Label(new Rect(_area.x + Pad, _y, _area.width - 2 * Pad, h), text, style);
        _y += h + 2;
    }

    private static bool Button(string text)
    {
        bool clicked = GUI.Button(new Rect(_area.x + Pad, _y, _area.width - 2 * Pad, Line), text);
        _y += Line + 4;
        return clicked;
    }
}
