using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Rewired;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline.Lockstep;

/// <summary>
/// Client-side precision for timed inputs (timed hits, timed blocks, timed QTEs).
///
/// Under lockstep every press reaches the game exactly <c>Delay</c> frames after the player made it. The player
/// pressed while looking at frame <c>F - Delay</c> (the press's "origin"). Timing windows in Sea of Stars are about
/// 8 frames, so judging presses when they arrive would make precise timing depend on ping.
///
/// Instead, every timing decision is judged against the moment the player actually pressed:
///  - Timed attacks: the game asks for the result at impact (TimedAttackHandler.GetResult) and collects it
///    asynchronously. That call is deferred by Delay frames, so the game's own logic sees every press
///    exactly as far from the impact as the player made it, before or after it.
///  - Timed blocks: the game asks synchronously when the enemy hit lands (PlayerCombatActor.GetBlockResult).
///    Inputs up to Delay frames ahead are already captured by every peer (they are what players pressed while
///    watching frames up to now), so the verdict is recomputed from press origins with the game's block duration.
///  - Time-to-event QTEs (TimeQTEHandler): windows are evaluated as of Delay frames ago.
/// All of it runs identically on every peer (same inputs, same frame, time deltas only), so the game stays in sync.
/// </summary>
internal static class TimingCompensation
{
    public static bool Enabled = true;

    private static LockstepEngine Engine => Session.Instance.Engine;
    private static bool Active => Enabled && InputOverride.Active && Engine.Running && Engine.Delay > 0 && Engine.AppliedFrame >= 0;

    [ThreadStatic] private static bool _reentrant;

    public static int DeferredAttacks, BlockChecks, BlockUpgrades, BlockDowngrades;
    private static int _interact = -2;

    /// <summary>Frame the game last hit an impact (for the test bot): Rewired player id → applied frame.</summary>
    public static readonly Dictionary<int, int> LastImpactFrame = new();

    public static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(TimedAttackHandler), nameof(TimedAttackHandler.GetResult), nameof(AttackGetResultPrefix), prefix: true);
        Patch(harmony, typeof(PlayerCombatActor), nameof(PlayerCombatActor.GetBlockResult), nameof(BlockResultPostfix), prefix: false);
        foreach (var name in new[] { nameof(TimeQTEHandler.GetQTEResult), nameof(TimeQTEHandler.IsTooEarly), nameof(TimeQTEHandler.IsOnTime), nameof(TimeQTEHandler.IsTooLate) })
            Patch(harmony, typeof(TimeQTEHandler), name, nameof(QteTimePrefix), prefix: true);
    }

    private static void Patch(Harmony harmony, Type type, string method, string patch, bool prefix)
    {
        var target = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == method);
        if (target == null) { Plugin.Log.LogWarning($"[timing] {type.Name}.{method} not found"); return; }
        // Never detour native code shared with unrelated methods.
        if (NativeCode.IsFolded(target)) { Plugin.Log.LogWarning($"[timing] {type.Name}.{method} is folded with other methods; not compensated"); return; }
        var hm = new HarmonyMethod(typeof(TimingCompensation).GetMethod(patch, BindingFlags.Static | BindingFlags.NonPublic));
        if (prefix) harmony.Patch(target, prefix: hm); else harmony.Patch(target, postfix: hm);
    }

    // ---------------------------------------------------------------- timed attacks

    private static bool AttackGetResultPrefix(TimedAttackHandler __instance, PlayerCombatMoveDefinition __0, Il2CppSystem.Action<TeamQTEResult> __1)
    {
        if (_reentrant || !NativeCode.Is<TimedAttackHandler>(__instance)) return true;
        try
        {
            var player = __instance.combatActor?.Player;
            if (player != null) LastImpactFrame[player.id] = Engine.AppliedFrame;
        }
        catch { }
        if (!Active) return true;

        var handler = __instance;
        var move = __0;
        var callback = __1;
        var impact = new Impact
        {
            Frame = Engine.AppliedFrame,
            Time = Time.time,
            BeforeWindow = handler.GetBeforeHitWindowDuration(),
            AfterWindow = handler.GetAfterHitWindowDuration(),
            Owner = handler.combatActor?.Player,
        };
        bool hadOverride = handler.overrideAfterHitWindowDuration;
        float previousOverride = handler.overridenAfterHitWindowDuration;

        // Judge the impact Delay frames later, when every press made up to the visible impact has arrived. The game's
        // after-hit window is stretched by the delay so it keeps waiting for presses still in flight; the verdict
        // itself comes from the press origin times (Grade), so the stretch never makes timing more lenient.
        Engine.Schedule(impact.Frame + Engine.Delay, () =>
        {
            _reentrant = true;
            try
            {
                handler.OverrideAfterHitWindowDuration(impact.AfterWindow + Engine.Delay / 60f + 0.02f);
                Action<TeamQTEResult> graded = result =>
                {
                    try
                    {
                        if (hadOverride) handler.OverrideAfterHitWindowDuration(previousOverride);
                        else handler.StopOverrideAfterHitWindowDuration();
                        result = Regrade(result, impact);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[timing] attack grading failed: {e}"); }
                    callback?.Invoke(result);
                };
                handler.GetResult(move, graded);
            }
            catch (Exception e) { Plugin.Log.LogError($"[timing] deferred attack result failed: {e}"); }
            finally { _reentrant = false; }
        });
        DeferredAttacks++;
        return false;
    }

    private sealed class Impact
    {
        public int Frame;
        public float Time, BeforeWindow, AfterWindow;
        /// <summary>Game time when the deferred judgment ran (the game's post-impact slow-down starts here).</summary>
        public Player Owner;
    }

    /// <summary>Replaces each session player's result with the grade of the presses they actually made.</summary>
    private static TeamQTEResult Regrade(TeamQTEResult result, Impact impact)
    {
        if (result == null) return result;
        var players = new List<Player>();
        if (impact.Owner != null) players.Add(impact.Owner);
        for (int slot = 0; slot < InputOverride.SlotCount; slot++)
        {
            var p = Game.GameBridge.PlayerForSlot(slot);
            if (p == null || players.Any(x => x.id == p.id)) continue;
            var r = result.GetResultForPlayer(p);
            if (r != null && r.owner != null) players.Add(p);
        }

        TeamQTEResult rebuilt = null;
        bool changed = false;
        var grades = new List<(Player p, EQTEResult v)>();
        foreach (var p in players)
        {
            var existing = result.GetResultForPlayer(p);
            EQTEResult game = existing != null && existing.owner != null ? existing.result : EQTEResult.FailDidNoPress;
            int channel = InputOverride.ChannelOf(p);
            EQTEResult mine = channel >= 0 && channel < InputOverride.SlotCount ? Grade(channel, impact) : game;
            grades.Add((p, mine));
            if (mine != game) changed = true;
            if (TestHarness.TraceEnabled)
                Plugin.Log.LogInfo($"[timing] attack impact f{impact.Frame} player {p.id}: game={game} graded={mine}");
        }
        if (!changed) return result;
        foreach (var (p, v) in grades)
        {
            if (rebuilt == null) rebuilt = TeamQTEResult.GetPooledInstance(p, v);
            else rebuilt.AddResult(new QTEResult(p, v));
        }
        AttackRegrades++;
        return rebuilt ?? result;
    }

    public static int AttackRegrades;

    /// <summary>The game's timed-hit rules applied to when the player pressed (measured, see README).</summary>
    private static EQTEResult Grade(int channel, Impact impact)
    {
        if (_interact == -2) _interact = ActionTable.IndexOf("Interact");
        if (_interact < 0) return EQTEResult.FailDidNoPress;
        int delay = Engine.Delay;
        int latestBefore = -1, firstAfter = -1;
        float tBefore = 0, afterDelta = 0;
        for (int applied = impact.Frame - 60; applied <= Engine.AppliedFrame + delay; applied++)
        {
            var snap = Engine.Peek(applied, channel);
            if (snap == null) continue;
            if ((snap.Bits[_interact] & ActionBits.ButtonDown) == 0) continue;
            int origin = applied - delay;
            if (origin <= impact.Frame)
            {
                // Before the impact time runs normally: compare when the player pressed with the visible impact.
                float t = Engine.TimeAt(origin);
                if (!float.IsNaN(t)) { latestBefore = origin; tBefore = t; }
            }
            else if (firstAfter < 0)
            {
                // The game ends the after-hit window in the first frame whose time is past it, but still takes a
                // press read in that same frame: compare the window with the time of the frame before the press.
                float tPrev = Engine.TimeAt(origin - 1);
                if (float.IsNaN(tPrev)) continue;
                firstAfter = origin;
                afterDelta = tPrev - impact.Time;
            }
        }
        // A press stays valid for BeforeWindow; the impact checks it (measured: 7 frames before the impact succeed, 8 fail).
        if (latestBefore >= 0 && impact.Time - tBefore <= impact.BeforeWindow) return EQTEResult.SuccessBeforeEvent;
        if (firstAfter >= 0 && afterDelta <= impact.AfterWindow) return EQTEResult.SuccessAfterEvent;
        return EQTEResult.FailDidNoPress;
    }

    // ---------------------------------------------------------------- timed blocks


    private static void BlockResultPostfix(PlayerCombatActor __instance, ref TeamQTEResult __result)
    {
        if (!Active || __result == null || !NativeCode.Is<PlayerCombatActor>(__instance)) return;
        try
        {
            var owner = __instance.Player;
            int channel = InputOverride.ChannelOf(owner);
            if (channel < 0 || channel >= InputOverride.SlotCount) return;
            if (_interact == -2) _interact = ActionTable.IndexOf("Interact");
            if (_interact < 0) return;

            var handler = __instance.timedBlockHandler;
            float duration = handler != null ? handler.GetBlockDuration() : 0.133f;
            int hit = Engine.AppliedFrame;
            int delay = Engine.Delay;
            // The hit is processed before the block handler ends an expiring block in the same frame, so a block
            // counts if it was still up at the end of the previous frame (measured: presses 0..8 frames before succeed).
            float tHit = Engine.TimeAt(hit);
            float tBeforeHit = Engine.TimeAt(hit - 1);
            if (float.IsNaN(tHit) || float.IsNaN(tBeforeHit)) return;

            // Most recent press the player made at or before the hit (as they saw it), looking up to Delay frames ahead.
            bool success = false;
            int usedFrame = -1;
            for (int applied = hit + delay; applied > hit - 90; applied--)
            {
                var snap = Engine.Peek(applied, channel);
                if (snap == null) return; // session broke; leave the game's own verdict
                if ((snap.Bits[_interact] & ActionBits.ButtonDown) == 0) continue;
                float tOrigin = Engine.TimeAt(applied - delay);
                if (float.IsNaN(tOrigin) || tOrigin > tHit) continue; // pressed after the hit
                success = tBeforeHit - tOrigin <= duration;
                usedFrame = applied;
                break;
            }

            var current = __result.GetResultForPlayer(owner);
            bool gameSaid = current != null && current.owner != null && current.IsSuccess();
            BlockChecks++;
            if (success && usedFrame > hit) Engine.Consume(usedFrame, channel, _interact); // don't replay the block late
            if (success == gameSaid) return;

            var verdict = success ? EQTEResult.SuccessBeforeEvent : EQTEResult.FailDidNoPress;
            var replaced = TeamQTEResult.GetPooledInstance(owner, verdict);
            __result = replaced;
            if (success) BlockUpgrades++; else BlockDowngrades++;
            if (TestHarness.TraceEnabled)
                Plugin.Log.LogInfo($"[timing] block at f{hit} by player {owner.id}: game={(gameSaid ? "success" : "fail")} → {verdict} (press at f{usedFrame}, origin f{usedFrame - delay})");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[timing] block compensation failed: {e}");
        }
    }

    // ---------------------------------------------------------------- time-to-event QTEs

    private static void QteTimePrefix(TimeQTEHandler __instance, ref float __0)
    {
        if (!Active || !NativeCode.Is<TimeQTEHandler>(__instance)) return;
        float then = Engine.TimeAt(Engine.AppliedFrame - Engine.Delay);
        if (float.IsNaN(then)) return;
        // Time to the event as it was when the player pressed (Delay frames ago).
        __0 += Time.time - then;
    }
}
