using System;
using System.Linq;
using System.Text;
using Rewired;
using SosOnline.Game;
using SosOnline.Lockstep;
using UnityEngine;

namespace SosOnline;

/// <summary>
/// Logs who controls what, only when it changes: each co-op player (playing, its character, the devices it owns)
/// and each party member's state (player-controlled or the party AI). Finds characters nobody controls and devices
/// left on a player that no longer plays, e.g. after a session ends or a player leaves.
/// </summary>
internal static class CoopProbe
{
    private static string _last;
    private static int _nextCheck;

    public static void Update()
    {
        if (Time.frameCount < _nextCheck) return;
        _nextCheck = Time.frameCount + 15;
        string now;
        try { now = Describe(); }
        catch (Exception e) { now = "unavailable: " + e.Message; }
        if (now == null || now == _last) return;
        _last = now;
        var s = Net.Session.Instance;
        Plugin.Log.LogInfo($"[coop] {(s.Engine.Running ? $"f{s.Engine.AppliedFrame} " : "")}{now}");
    }

    private static string Describe()
    {
        var coop = GameBridge.Coop;
        var party = GameBridge.Party?.LeaderAndFollowers;
        if (coop == null || party == null || party.Count == 0 || !ReInput.isReady) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < InputOverride.SlotCount; i++)
        {
            var player = GameBridge.GamePlayer(i);
            var cp = player == null ? null : coop.GetSinglePlayerPlusPlayer(player);
            if (cp == null) continue;
            string character = "";
            try { character = cp.AssignedCharacter?.CharacterDefinitionId?.characterId ?? ""; } catch { }
            sb.Append($"P{i + 1}{(cp.Playing ? "" : " (not playing)")}{(character.Length > 0 ? " " + character : "")} [{Devices(player)}]  ");
        }
        sb.Append("| ");
        for (int i = 0; i < party.Count; i++)
        {
            if (party[i] == null) continue;
            var c = new ISinglePlayerPlusCharacter(party[i].Pointer);
            string id = "?", state = "?";
            try { id = c.CharacterDefinitionId?.characterId ?? "?"; } catch { }
            try { state = c.StateMachine?.CurrentState?.GetIl2CppType()?.Name ?? "none"; } catch { }
            bool controlled = false;
            try { controlled = coop.IsPlayerControlledCharacter(c); } catch { }
            sb.Append($"{id}={state}{(controlled ? "*" : "")} ");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Devices(Player p)
    {
        var parts = new System.Collections.Generic.List<string>();
        try
        {
            if (p.controllers.hasKeyboard) parts.Add("kb");
            int pads = p.controllers.joystickCount;
            if (pads > 0) parts.Add(pads == 1 ? "pad" : $"{pads} pads");
        }
        catch { }
        return parts.Count == 0 ? "no devices" : string.Join("+", parts);
    }
}
