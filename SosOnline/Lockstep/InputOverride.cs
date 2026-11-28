using System.Collections.Generic;
using HarmonyLib;
using Rewired;

namespace SosOnline.Lockstep;

/// <summary>
/// Replaces what Rewired.Player reports with the lockstep-synchronized input of the current frame.
/// Every peer runs identical game code fed by identical input, so every peer simulates the same game.
///
/// Channels: 0..2 are session slots (host = 0), <see cref="SystemChannel"/> is Rewired's System player,
/// which the game uses for menus outside of gameplay; it always follows the host.
/// </summary>
internal static class InputOverride
{
    public const int SlotCount = 3;
    public const int SystemChannel = SlotCount;
    public const int ChannelCount = SlotCount + 1;

    /// <summary>True while the lockstep simulation drives input.</summary>
    public static bool Active;

    /// <summary>Set while we read real hardware input through the original Rewired methods.</summary>
    [System.ThreadStatic] public static bool Bypass;

    public static readonly InputSnapshot[] Current = new InputSnapshot[ChannelCount];

    /// <summary>Rewired player id → channel.</summary>
    private static readonly Dictionary<int, int> ChannelByPlayerId = new();

    public static void MapPlayer(int rewiredPlayerId, int channel) => ChannelByPlayerId[rewiredPlayerId] = channel;

    /// <summary>Lockstep channel driving a Rewired player, or -1.</summary>
    public static int ChannelOf(Player p) => p != null && ChannelByPlayerId.TryGetValue(p.id, out int c) ? c : -1;
    public static void ClearMapping() => ChannelByPlayerId.Clear();

    /// <param name="advance">False when priming the first frame at start: hold durations don't count it.</param>
    public static void SetFrame(InputSnapshot[] channels, bool advance = true)
    {
        for (int c = 0; c < ChannelCount; c++) Current[c] = channels[c] ?? InputSnapshot.Empty;
        if (advance) AdvanceHold();
    }

    /// <summary>
    /// Called every frame. Unity's EventSystem stops processing UI input while the game window isn't focused, and
    /// the game's pop-up questions are answered through it. A player alt-tabbed to chat would then never see the
    /// answer on their PC while everyone else's game went on. During lockstep the input comes from the session,
    /// not this window, so the EventSystem always counts as focused.
    /// </summary>
    public static void KeepUiFocus()
    {
        if (!Active) return;
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && !es.m_HasFocus) es.m_HasFocus = true;
        }
        catch { }
    }

    // ---------------- Hold durations ----------------
    // Rewired's GetButtonTimePressed & co. measure how long each PC's own controllers have held a button, so a hold
    // made by a remote player never advanced on this PC: the game's "overwrite this save?" question, confirmed by a
    // hold on the guest's PC, stayed open on the host's while the guest's game went on (2026-11-15). They answer
    // from the synced input instead: consecutive applied frames in each state, 1/60 s each.

    private const double FrameTime = 1.0 / 60.0;
    private enum Hold { Pressed, Unpressed, NegPressed, NegUnpressed, AxisActive, AxisInactive, RawActive, RawInactive, Count }
    private static int[,,] _hold = new int[ChannelCount, 0, (int)Hold.Count];

    /// <summary>Starts every hold count from zero (a new lockstep run).</summary>
    public static void ResetHold() => _hold = new int[ChannelCount, ActionTable.Ids.Length, (int)Hold.Count];

    private static void AdvanceHold()
    {
        int n = ActionTable.Ids.Length;
        if (_hold.GetLength(1) != n) ResetHold();
        for (int c = 0; c < ChannelCount; c++)
        {
            var s = Current[c];
            for (int a = 0; a < n; a++)
            {
                // A snapshot made before the action table was built has no entries: nothing held.
                bool known = a < s.Bits.Length;
                var bits = known ? s.Bits[a] : ActionBits.None;
                Step(c, a, Hold.Pressed, Hold.Unpressed, (bits & ActionBits.Button) != 0);
                Step(c, a, Hold.NegPressed, Hold.NegUnpressed, (bits & ActionBits.NegButton) != 0);
                Step(c, a, Hold.AxisActive, Hold.AxisInactive, known && s.Axis[a] != 0);
                Step(c, a, Hold.RawActive, Hold.RawInactive, known && s.AxisRaw[a] != 0);
            }
        }
    }

    private static void Step(int c, int a, Hold on, Hold off, bool isOn)
    {
        if (isOn) { _hold[c, a, (int)on]++; _hold[c, a, (int)off] = 0; }
        else { _hold[c, a, (int)off]++; _hold[c, a, (int)on] = 0; }
    }

    private static bool HoldTime(Player p, int index, Hold which, ref double result)
    {
        var s = Resolve(p);
        if (s == null) return true;
        int ch = ChannelByPlayerId.TryGetValue(p.id, out int c) ? c : -1;
        result = ch >= 0 && index >= 0 && index < _hold.GetLength(1) ? _hold[ch, index, (int)which] * FrameTime : 0.0;
        if (ReadProbeTo >= 0) ProbeRead(p, index, which.ToString(), result > 0);
        return false;
    }

    /// <summary>Returns the snapshot to answer from, or null to run Rewired's original method.</summary>
    private static InputSnapshot Resolve(Player p)
    {
        if (!Active || Bypass || p == null || !NativeCode.Is<Player>(p)) return null;
        // Players that aren't in the session (e.g. a 4th Rewired player) are kept silent.
        return ChannelByPlayerId.TryGetValue(p.id, out int ch) ? (Current[ch] ?? InputSnapshot.Empty) : InputSnapshot.Empty;
    }

    private static bool Has(InputSnapshot s, int index, ActionBits bit) => index >= 0 && (s.Bits[index] & bit) != 0;
    private static float Axis(InputSnapshot s, int index) => index >= 0 ? InputSnapshot.Dequantize(s.Axis[index]) : 0f;
    private static float AxisRaw(InputSnapshot s, int index) => index >= 0 ? InputSnapshot.Dequantize(s.AxisRaw[index]) : 0f;

    private static bool AnyBit(InputSnapshot s, ActionBits mask)
    {
        for (int i = 0; i < s.Bits.Length; i++) if ((s.Bits[i] & mask) != 0) return true;
        return false;
    }

    // Shared prefix bodies. Returning false skips the original Rewired implementation.
    /// <summary>Test probe (--sosonline-readprobe=F1,F2): log which synced reads the game makes in that frame range.</summary>
    public static int ReadProbeFrom = int.MaxValue, ReadProbeTo = int.MinValue;
    private static readonly HashSet<string> _reads = new();
    private static int _readsFrame = -1;

    private static void ProbeRead(Player p, int index, string kind, bool result)
    {
        int f = Net.Session.Instance.Engine.AppliedFrame;
        if (f < ReadProbeFrom || f > ReadProbeTo) return;
        if (f != _readsFrame) { _readsFrame = f; _reads.Clear(); }
        string name = index >= 0 && index < ActionTable.Names.Length ? ActionTable.Names[index] : index.ToString();
        string key = $"player {p.id} {name} {kind}={result}";
        if (_reads.Add(key)) Plugin.Log.LogInfo($"[read] f{f} {key}");
    }

    private static bool BitById(Player p, int id, ActionBits bit, ref bool result)
    {
        var s = Resolve(p);
        if (s == null) return true;
        int index = ActionTable.IndexOf(id);
        // Team Timing: a press that wouldn't count reads as none, so the game shows nothing for it.
        result = Has(s, index, bit) && !TeamTiming.Hides(p, index);
        if (ReadProbeTo >= 0) ProbeRead(p, index, bit.ToString(), result);
        return false;
    }

    private static bool BitByName(Player p, string name, ActionBits bit, ref bool result)
    {
        var s = Resolve(p);
        if (s == null) return true;
        int index = ActionTable.IndexOf(name);
        result = Has(s, index, bit) && !TeamTiming.Hides(p, index);
        if (ReadProbeTo >= 0) ProbeRead(p, index, bit.ToString(), result);
        return false;
    }

    // ---------------- Patches ----------------
    // Harmony's __0 refers to the first parameter regardless of its name.

    [HarmonyPatch(typeof(Player), nameof(Player.GetButton), typeof(int))]
    private static class P_GetButton_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.Button, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButton), typeof(string))]
    private static class P_GetButton_S { private static bool Prefix(Player __instance, string __0, ref bool __result) => BitByName(__instance, __0, ActionBits.Button, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDown), typeof(int))]
    private static class P_GetButtonDown_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.ButtonDown, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDown), typeof(string))]
    private static class P_GetButtonDown_S { private static bool Prefix(Player __instance, string __0, ref bool __result) => BitByName(__instance, __0, ActionBits.ButtonDown, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonUp), typeof(int))]
    private static class P_GetButtonUp_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.ButtonUp, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonUp), typeof(string))]
    private static class P_GetButtonUp_S { private static bool Prefix(Player __instance, string __0, ref bool __result) => BitByName(__instance, __0, ActionBits.ButtonUp, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDoublePressHold), typeof(int), typeof(float))]
    private static class P_GetButtonDPH_IF { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.DoublePressHold, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDoublePressHold), typeof(int))]
    private static class P_GetButtonDPH_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.DoublePressHold, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonShortPress), typeof(int))]
    private static class P_GetButtonShort_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.ShortPress, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonLongPress), typeof(int))]
    private static class P_GetButtonLong_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.LongPress, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonRepeating), typeof(int))]
    private static class P_GetButtonRepeating_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.Repeating, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButton), typeof(int))]
    private static class P_GetNegButton_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.NegButton, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonDown), typeof(int))]
    private static class P_GetNegButtonDown_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.NegButtonDown, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonDown), typeof(string))]
    private static class P_GetNegButtonDown_S { private static bool Prefix(Player __instance, string __0, ref bool __result) => BitByName(__instance, __0, ActionBits.NegButtonDown, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonDoublePressHold), typeof(int), typeof(float))]
    private static class P_GetNegButtonDPH_IF { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.NegDoublePressHold, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonDoublePressHold), typeof(int))]
    private static class P_GetNegButtonDPH_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.NegDoublePressHold, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonShortPress), typeof(int))]
    private static class P_GetNegButtonShort_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.NegShortPress, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonLongPress), typeof(int))]
    private static class P_GetNegButtonLong_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.NegLongPress, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonRepeating), typeof(int))]
    private static class P_GetNegButtonRepeating_I { private static bool Prefix(Player __instance, int __0, ref bool __result) => BitById(__instance, __0, ActionBits.NegRepeating, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAnyButton))]
    private static class P_GetAnyButton
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            var s = Resolve(__instance);
            if (s == null) return true;
            __result = AnyBit(s, ActionBits.Button | ActionBits.NegButton);
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAnyButtonDown))]
    private static class P_GetAnyButtonDown
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            var s = Resolve(__instance);
            if (s == null) return true;
            __result = AnyBit(s, ActionBits.ButtonDown | ActionBits.NegButtonDown);
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAnyNegativeButtonDown))]
    private static class P_GetAnyNegButtonDown
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            var s = Resolve(__instance);
            if (s == null) return true;
            __result = AnyBit(s, ActionBits.NegButtonDown);
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAxis), typeof(int))]
    private static class P_GetAxis_I
    {
        private static bool Prefix(Player __instance, int __0, ref float __result)
        {
            var s = Resolve(__instance);
            if (s == null) return true;
            __result = Axis(s, ActionTable.IndexOf(__0));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAxis), typeof(string))]
    private static class P_GetAxis_S
    {
        private static bool Prefix(Player __instance, string __0, ref float __result)
        {
            var s = Resolve(__instance);
            if (s == null) return true;
            __result = Axis(s, ActionTable.IndexOf(__0));
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonTimePressed), typeof(int))]
    private static class P_TimePressed_I { private static bool Prefix(Player __instance, int __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.Pressed, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonTimePressed), typeof(string))]
    private static class P_TimePressed_S { private static bool Prefix(Player __instance, string __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.Pressed, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetButtonTimeUnpressed), typeof(int))]
    private static class P_TimeUnpressed_I { private static bool Prefix(Player __instance, int __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.Unpressed, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonTimePressed), typeof(int))]
    private static class P_NegTimePressed_I { private static bool Prefix(Player __instance, int __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.NegPressed, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetNegativeButtonTimeUnpressed), typeof(int))]
    private static class P_NegTimeUnpressed_I { private static bool Prefix(Player __instance, int __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.NegUnpressed, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAxisTimeActive), typeof(int))]
    private static class P_AxisActive_I { private static bool Prefix(Player __instance, int __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.AxisActive, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAxisTimeInactive), typeof(int))]
    private static class P_AxisInactive_I { private static bool Prefix(Player __instance, int __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.AxisInactive, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAxisRawTimeActive), typeof(int))]
    private static class P_RawActive_I { private static bool Prefix(Player __instance, int __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.RawActive, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAxisRawTimeInactive), typeof(int))]
    private static class P_RawInactive_I { private static bool Prefix(Player __instance, int __0, ref double __result) => HoldTime(__instance, ActionTable.IndexOf(__0), Hold.RawInactive, ref __result); }

    [HarmonyPatch(typeof(Player), nameof(Player.GetAxisRaw), typeof(int))]
    private static class P_GetAxisRaw_I
    {
        private static bool Prefix(Player __instance, int __0, ref float __result)
        {
            var s = Resolve(__instance);
            if (s == null) return true;
            __result = AxisRaw(s, ActionTable.IndexOf(__0));
            return false;
        }
    }
}
