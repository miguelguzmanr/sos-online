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
    ResyncRequest = 17, // guest → host: reason (this guest diverged; it leaves and drops in again by a reload)
    SlotLeft = 18,    // host → guests: slot, first frame without that slot
    DropInSave = 19,  // host → guests in the session: autosave on this frame (drop-in)
    Loaded = 20,      // guest → host: load sequence, frame this PC reached the end of level loading
    LoadRelease = 21, // host → guests: load sequence, frame on which every PC ends the level loading
    DropInReload = 22, // host → guests in the session: join frame, autosave slot, joining slots, party positions
    DropIn = 23,      // host → joiner: seed, delay, join frame, roster, slot→player map, autosave slot, party positions
    DropInAbort = 24, // host → joiner: the party was loading a level or in a battle on the join frame; wait again
    CatchUp = 25,     // host → joiner (Stage 3): seed, delay, area entry frame L, load seq, clock, roster, player map, playing, save slot, party, roster changes since L
    InputLog = 26,    // host → joiner (Stage 3): a block of logged frames since L
    CaughtUp = 27,    // joiner → host (Stage 3): replayed up to the live session
    Merge = 28,       // host → all (Stage 3): merge frame, slot that joins on it
    CatchUpFailed = 29, // joiner → host (Stage 3): the replay diverged; use a reload (Stage 2)
    PauseStatus = 30, // any → host, host → all: slot, paused on its own PC (local pause screen; display only, never input)
    LeaveRequest = 31, // guest → host: leave the session (chosen in the local pause screen)
    EndAt = 32,       // host → all: frame on which lockstep ends, reason (host chose End session in the local pause screen)
    AssignCharacter = 33, // host → all: frame, slot, character id (a rejoining guest gets its character back)
    LogChunk = 34,    // guest → host: transfer id, piece index, piece count, bytes (a zip of the guest's logs)
    LogReceived = 35, // host → guest: the logs arrived
    LogRequest = 36,  // host → guests: the host asks for logs (a toast; the guest decides)
    AreaFiles = 37,   // host → guests playing: transfer id, the area entry save files (the host returns to the area start)
    TurnCommand = 39, // host → all: frame, command (the host takes over or passes the current battle turn)
    Removed = 40,     // host → guest: the host removed you from co-op (a SlotLeft for your slot follows)
    DlcOwned = 38,    // guest → host: the DLC this PC owns, again (the game reads Steam's answer a while after its start)
    PlayersRelease = 41, // host → guests playing: frame (a drop-in stopped before its reload; players move again after it)
    Booted = 42,      // guest → host: this PC's game has finished starting (it can load a drop-in save at once)
    LevelUpReady = 43, // guest → host: level-up sequence, frame this PC has the level-up scene (its start is held)
    LevelUpRelease = 44, // host → guests: level-up sequence, frame on which every PC starts the level-up
    AdditiveReached = 45, // guest → host: step key, frame this PC got to a held step of a level loaded during play
    AdditiveRelease = 46, // host → guests: step key, frame on which every PC runs it
    DeviceKind = 47,  // any → host, host → all: slot, device kind (the Assign Players icon; display only, never input)
}

internal static class Protocol
{
    public const int Version = 21; // 21: each player's device kind (DeviceKind); 20: barrier for levels loaded during play (AdditiveReached, AdditiveRelease; their releases in the catch-up); 19: the level-up barrier (LevelUpReady, LevelUpRelease, in the area log); 18: DropInSave sends the joiners' names one per line; 17: drop-ins at the next area entry (Booted, DropInSave at the entry); 16: players held still during a drop-in (DropInSave names, PlayersRelease), Welcome says whether the host takes players back; 15: Welcome says whether the host still has to let the joiner in; 14: the session's battle-turn rule; 9: snapshots carry each character's facing; 10: and the game's pause flag; 11: log sharing; 12: character changes in the catch-up, area files; 13: DLC owned and the session's DLC

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
