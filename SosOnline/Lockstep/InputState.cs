using System;
using System.Collections.Generic;
using System.IO;
using Rewired;

namespace SosOnline.Lockstep;

/// <summary>Per-action flags captured from a Rewired player every frame.</summary>
[Flags]
internal enum ActionBits : ushort
{
    None = 0,
    Button = 1 << 0,
    ButtonDown = 1 << 1,
    ButtonUp = 1 << 2,
    DoublePressHold = 1 << 3,
    ShortPress = 1 << 4,
    LongPress = 1 << 5,
    Repeating = 1 << 6,
    NegButton = 1 << 7,
    NegButtonDown = 1 << 8,
    NegDoublePressHold = 1 << 9,
    NegShortPress = 1 << 10,
    NegLongPress = 1 << 11,
    NegRepeating = 1 << 12,
}

/// <summary>The ordered list of Rewired actions. Index into this table is what goes on the wire.</summary>
internal static class ActionTable
{
    public static int[] Ids = Array.Empty<int>();
    public static string[] Names = Array.Empty<string>();
    private static readonly Dictionary<int, int> IndexById = new();
    private static readonly Dictionary<string, int> IndexByName = new(StringComparer.Ordinal);
    public static uint Hash { get; private set; }
    public static bool Ready => Ids.Length > 0;

    public static bool TryBuild()
    {
        if (Ready) return true;
        if (!ReInput.isReady) return false;
        var list = new List<(int id, string name)>();
        var actions = ReInput.mapping.Actions.ToList();
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            list.Add((a.id, a.name));
        }
        list.Sort((x, y) => x.id.CompareTo(y.id));
        Ids = new int[list.Count];
        Names = new string[list.Count];
        uint h = 2166136261;
        for (int i = 0; i < list.Count; i++)
        {
            Ids[i] = list[i].id;
            Names[i] = list[i].name;
            IndexById[list[i].id] = i;
            IndexByName[list[i].name] = i;
            foreach (char c in list[i].name) h = (h ^ c) * 16777619;
            h = (h ^ (uint)list[i].id) * 16777619;
        }
        Hash = h;
        Plugin.Log.LogInfo($"[input] {Ids.Length} Rewired actions, hash {Hash:X8}: {string.Join(", ", Names)}");
        return true;
    }

    public static int IndexOf(int actionId) => IndexById.TryGetValue(actionId, out var i) ? i : -1;
    public static int IndexOf(string name) => name != null && IndexByName.TryGetValue(name, out var i) ? i : -1;
}

/// <summary>Input of one channel (one player) for one frame. Sparse: only active actions are stored.</summary>
internal sealed class InputSnapshot
{
    public static readonly InputSnapshot Empty = new();

    public ActionBits[] Bits;
    public short[] Axis;
    public short[] AxisRaw;

    public InputSnapshot()
    {
        int n = ActionTable.Ids.Length;
        Bits = new ActionBits[n];
        Axis = new short[n];
        AxisRaw = new short[n];
    }

    public static short Quantize(float v) => (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f);
    public static float Dequantize(short v) => v / 32767f;

    public bool IsIdle()
    {
        for (int i = 0; i < Bits.Length; i++)
            if (Bits[i] != 0 || Axis[i] != 0 || AxisRaw[i] != 0) return false;
        return true;
    }

    public void Write(BinaryWriter w)
    {
        int count = 0;
        for (int i = 0; i < Bits.Length; i++)
            if (Bits[i] != 0 || Axis[i] != 0 || AxisRaw[i] != 0) count++;
        w.Write((ushort)count);
        for (int i = 0; i < Bits.Length; i++)
        {
            if (Bits[i] == 0 && Axis[i] == 0 && AxisRaw[i] == 0) continue;
            w.Write((ushort)i);
            w.Write((ushort)Bits[i]);
            w.Write(Axis[i]);
            w.Write(AxisRaw[i]);
        }
    }

    public static InputSnapshot Read(BinaryReader r)
    {
        var s = new InputSnapshot();
        int count = r.ReadUInt16();
        for (int k = 0; k < count; k++)
        {
            int i = r.ReadUInt16();
            var bits = (ActionBits)r.ReadUInt16();
            short axis = r.ReadInt16();
            short raw = r.ReadInt16();
            if (i >= s.Bits.Length) continue;
            s.Bits[i] = bits;
            s.Axis[i] = axis;
            s.AxisRaw[i] = raw;
        }
        return s;
    }

    public uint Checksum()
    {
        uint h = 2166136261;
        for (int i = 0; i < Bits.Length; i++)
        {
            if (Bits[i] == 0 && Axis[i] == 0 && AxisRaw[i] == 0) continue;
            h = (h ^ (uint)i) * 16777619;
            h = (h ^ (uint)Bits[i]) * 16777619;
            h = (h ^ (ushort)Axis[i]) * 16777619;
        }
        return h;
    }
}
