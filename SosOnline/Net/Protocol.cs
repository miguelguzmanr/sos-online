using System;
using System.IO;

namespace SosOnline.Net;

internal enum Msg : byte
{
    Hello = 1,        // guest → host: protocol, mod version, game build, action table hash, name
    Welcome = 2,      // host → guest: assigned slot
    Reject = 3,       // host → guest: reason
    Roster = 4,       // host → all: slot → (id, name)
    Ping = 5,
    Pong = 6,
    File = 7,         // host → guest: one save/options file (name, bytes)
    FilesDone = 8,    // host → guest: transfer id, file count
    Ready = 9,        // guest → host: transfer id applied
    Start = 10,       // host → all: lockstep parameters
    Input = 11,       // any → host, host → guests (relayed): slot, frame, channel snapshots
    Snapshot = 12,    // host → guests: authoritative state at a frame
    Hash = 13,        // guest → host: state hash at a frame (diagnostics)
    Stop = 14,        // host → all: end lockstep (back to independent play)
    Bye = 15,         // leaving the session
    Chat = 16,        // short status text
    ResyncRequest = 17, // guest → host
    SlotLeft = 18,    // host → guests: slot, first frame without that slot
    Resync = 19,      // host → guests: stop and return to the title screen; the session restarts from a fresh save
}

internal static class Protocol
{
    public const int Version = 2;

    public static (BinaryWriter w, MemoryStream ms) Begin(Msg type)
    {
        var ms = new MemoryStream(256);
        var w = new BinaryWriter(ms);
        w.Write((byte)type);
        return (w, ms);
    }

    public static void WriteBytes(this BinaryWriter w, byte[] data)
    {
        w.Write(data.Length);
        w.Write(data);
    }

    public static byte[] ReadBytes(this BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 64 * 1024 * 1024) throw new InvalidDataException("bad length");
        return r.ReadBytes(n);
    }
}
