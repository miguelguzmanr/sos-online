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
    /// <summary>--sosonline-botall: press for every party member's hits and blocks, as a player helping a friend would.</summary>
    public static bool AnyActor;
    private static readonly System.Collections.Generic.HashSet<int> PressedImpacts = new();

    /// <summary>Frames after the visible impact at which the bot presses.</summary>
    public static int ImpactOffset = 1;

    /// <summary>--sosonline-botblock=N: frames after the block window opens at which the bot blocks (-1: never).</summary>
    public static int BlockOffset = -1;
    private static int _blockSeen = -1;

    /// <summary>--sosonline-botphase: offset of this bot's menu presses in the 45-frame schedule, so two players
    /// press at different moments (in a real battle they race for the turn) instead of on the same frames.</summary>
    public static int Phase;

    /// <summary>--sosonline-botswitch=N: every Nth menu press is a move to the other character first, the way a player
    /// takes a character that isn't theirs (0: never).</summary>
    public static int SwitchEvery;
    /// <summary>--sosonline-botvary: periodically browse Skills / Combos and Boost during battle menus.</summary>
    public static bool VaryMoves;
    private static int _menuPresses;

    /// <summary>--sosonline-botlevelup=N: on a level-up bonus choice, this bot moves the cursor right N times before it
    /// confirms (-1: it only confirms, as on any menu). Two PCs with different N choose different stars, so the star
    /// chosen shows whose input the screen took.</summary>
    public static int LevelUpMoves = -1;
    private static int _levelUpScreen = -1, _levelUpMoved;

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
            if (a == null || a.Player == null || (a.Player.id != me.id && !AnyActor)) continue;
            var atk = a.timedAttackHandler;
            if (atk != null && atk.IsInputPhaseStarted())
            {
                timedPhase = true;
                // Press ImpactOffset frames after the impact is on screen, like a player reacting to the hit.
                int origin = Session.Instance.Engine.AppliedFrame + 1; // the frame this capture belongs to
                if (PerfectPresses && TimingCompensation.LastImpactFrame.TryGetValue(a.Player.id, out int impact)
                    && origin == impact + ImpactOffset && PressedImpacts.Add(impact))
                {
                    Press(snap, "Interact");
                    Plugin.Log.LogInfo($"[bot] impact f{impact}: pressed at origin f{origin} (applies f{frame})");
                }
            }
            if (a.timedBlockHandler != null && a.timedBlockHandler.IsInputPhaseStarted())
            {
                timedPhase = true;
                // --sosonline-botblock=N: block N frames after this PC first sees the block window open.
                if (BlockOffset >= 0)
                {
                    int origin = Session.Instance.Engine.AppliedFrame + 1;
                    if (_blockSeen < 0 || origin - _blockSeen > 120) { _blockSeen = origin; }
                    if (origin == _blockSeen + BlockOffset)
                    {
                        Press(snap, "Interact");
                        Plugin.Log.LogInfo($"[bot] block window seen f{_blockSeen}: pressed at origin f{origin} (applies f{frame})");
                    }
                }
            }
        }

        // Outside timing phases, confirm menus (attack → target) on a fixed schedule of *target* frames,
        // so the battle timeline doesn't depend on the input delay.
        if (!timedPhase && frame > 640 && (frame - Phase) % 45 == 0)
        {
            if (LevelUpMoves >= 0 && LevelUps.CanChoose)
            {
                if (_levelUpScreen != LevelUps.ScreenCount) { _levelUpScreen = LevelUps.ScreenCount; _levelUpMoved = 0; }
                if (_levelUpMoved < LevelUpMoves)
                {
                    _levelUpMoved++;
                    int h = ActionTable.IndexOf("UIMoveHorizontal");
                    if (h >= 0)
                    {
                        snap.Axis[h] = InputSnapshot.Quantize(1f);
                        snap.AxisRaw[h] = InputSnapshot.Quantize(1f);
                        snap.Bits[h] |= ActionBits.Button | ActionBits.ButtonDown;
                    }
                    Plugin.Log.LogInfo($"[bot] f{frame} level up: cursor right ({_levelUpMoved}/{LevelUpMoves})");
                }
                else
                {
                    Press(snap, "UI_Confirm");
                    Plugin.Log.LogInfo($"[bot] f{frame} level up: confirm");
                }
                return snap;
            }
            _menuPresses++;
            if (SwitchEvery > 0 && _menuPresses % SwitchEvery == 0)
            {
                // Move the selection to the next character; the confirm comes at the next scheduled press.
                int h = ActionTable.IndexOf("UIMoveHorizontal");
                if (h >= 0)
                {
                    snap.Axis[h] = InputSnapshot.Quantize(1f);
                    snap.AxisRaw[h] = InputSnapshot.Quantize(1f);
                    snap.Bits[h] |= ActionBits.Button | ActionBits.ButtonDown;
                }
                int m = ActionTable.IndexOf("MoveHorizontal");
                if (m >= 0)
                {
                    snap.Axis[m] = InputSnapshot.Quantize(1f);
                    snap.AxisRaw[m] = InputSnapshot.Quantize(1f);
                    snap.Bits[m] |= ActionBits.Button | ActionBits.ButtonDown;
                }
                Plugin.Log.LogInfo($"[bot] f{frame} switch character");
            }
            else if (VaryMoves && _menuPresses % 5 == 0)
            {
                Press(snap, "Combat_Boost");
                Plugin.Log.LogInfo($"[bot] f{frame} boost");
            }
            else if (VaryMoves && _menuPresses % 4 == 1)
            {
                int v = ActionTable.IndexOf("UIMoveVertical");
                if (v >= 0)
                {
                    snap.Axis[v] = InputSnapshot.Quantize(-1f);
                    snap.AxisRaw[v] = InputSnapshot.Quantize(-1f);
                    snap.Bits[v] |= ActionBits.Button | ActionBits.ButtonDown | ActionBits.NegButton;
                }
                Plugin.Log.LogInfo($"[bot] f{frame} cycle move");
            }
            else Press(snap, "UI_Confirm");
        }
        return snap;
    }

    private static void Press(InputSnapshot snap, string action)
    {
        int i = ActionTable.IndexOf(action);
        if (i < 0) return;
        snap.Bits[i] |= ActionBits.Button | ActionBits.ButtonDown | ActionBits.ShortPress;
    }
}
