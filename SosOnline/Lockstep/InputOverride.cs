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

    public static void SetFrame(InputSnapshot[] channels)
    {
        for (int c = 0; c < ChannelCount; c++) Current[c] = channels[c] ?? InputSnapshot.Empty;
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
    private static bool BitById(Player p, int id, ActionBits bit, ref bool result)
    {
        var s = Resolve(p);
        if (s == null) return true;
        result = Has(s, ActionTable.IndexOf(id), bit);
        return false;
    }

    private static bool BitByName(Player p, string name, ActionBits bit, ref bool result)
    {
        var s = Resolve(p);
        if (s == null) return true;
        result = Has(s, ActionTable.IndexOf(name), bit);
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
