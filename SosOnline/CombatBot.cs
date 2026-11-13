using System.Linq;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline;

/// <summary>
/// --sosonline-bot: a test player with perfect timing *as seen on its own screen*.
/// It decides from the local simulation at capture time (what the player is looking at), exactly like a human.
/// Without client-side timing compensation its presses reach the game Delay frames late.
/// </summary>
internal static class CombatBot
{
    private static PlayerCombatActor[] _actors = new PlayerCombatActor[0];
    private static int _actorsFrame = -1000;
    /// <summary>Press timed attacks at the locally perfect moment (off: menus only, presses come from a script).</summary>
    public static bool PerfectPresses;
    private static readonly System.Collections.Generic.HashSet<int> PressedImpacts = new();

    /// <summary>Frames after the visible impact at which the bot presses.</summary>
    public static int ImpactOffset = 1;

    public static InputSnapshot Apply(int frame, InputSnapshot snap)
    {
        if (frame - _actorsFrame > 30)
        {
            _actorsFrame = frame;
            _actors = Object.FindObjectsOfType<PlayerCombatActor>();
        }
        var me = GameBridge.PlayerForSlot(Session.Instance.LocalSlot);
        if (me == null) return snap;

        bool timedPhase = false;
        foreach (var a in _actors)
        {
            if (a == null || a.Player == null || a.Player.id != me.id) continue;
            var atk = a.timedAttackHandler;
            if (atk != null && atk.IsInputPhaseStarted())
            {
                timedPhase = true;
                // Press ImpactOffset frames after the impact is on screen, like a player reacting to the hit.
                int origin = Session.Instance.Engine.AppliedFrame + 1; // the frame this capture belongs to
                if (PerfectPresses && TimingCompensation.LastImpactFrame.TryGetValue(me.id, out int impact)
                    && origin == impact + ImpactOffset && PressedImpacts.Add(impact))
                {
                    Press(snap, "Interact");
                    Plugin.Log.LogInfo($"[bot] impact f{impact}: pressed at origin f{origin} (applies f{frame})");
                }
            }
            if (a.timedBlockHandler != null && a.timedBlockHandler.IsInputPhaseStarted()) timedPhase = true;
        }

        // Outside timing phases, confirm menus (attack → target) on a fixed schedule of *target* frames,
        // so the battle timeline doesn't depend on the input delay.
        if (!timedPhase && frame > 640 && frame % 45 == 0)
            Press(snap, "UI_Confirm");
        return snap;
    }

    private static void Press(InputSnapshot snap, string action)
    {
        int i = ActionTable.IndexOf(action);
        if (i < 0) return;
        snap.Bits[i] |= ActionBits.Button | ActionBits.ButtonDown | ActionBits.ShortPress;
    }
}
