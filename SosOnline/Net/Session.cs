using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Steam;
using SosOnline.UI;
using UnityEngine;

namespace SosOnline.Net;

internal enum Role { None, Host, Guest }

internal enum Phase
{
    Idle,
    CreatingLobby,
    JoiningLobby,
    Connecting,
    Lobby,
    AwaitingReady,
    Running,
}

/// <summary>Everything about one online co-op session: who is in it, the handshake, and the lockstep run.</summary>
internal sealed class Session
{
    public static readonly Session Instance = new();

    private sealed class Peer
    {
        public ulong Id;
        public int Slot = -1;
        public string Name = "";
        /// <summary>Median of the last <see cref="RttKeep"/> round trips: one slow reply (e.g. right after connecting) doesn't move it.</summary>
        public float RttMs;
        public readonly List<float> RttSamples = new();
        public double LastHeard;
        public bool Ready;
        /// <summary>Joined while a session was running: drops in at the next moment the game allows saving.</summary>
        public bool Pending;
        /// <summary>Last lockstep frame we received this guest's input for.</summary>
        public int LastInputFrame = -1;
        /// <summary>Leaving at this frame (deterministic leave); removed once the session is past it.</summary>
        public int LeavingAt = -1;
        /// <summary>How far this guest's game clock was ahead of ours when it reported ready (seconds).</summary>
        public double ClockLead;
        /// <summary>Id of the last save transfer sent to this guest (each joiner can have its own in flight).</summary>
        public int TransferId = -1;
        /// <summary>Stage 3 progress for a pending joiner.</summary>
        public CatchUpStep CatchUp;
        /// <summary>Area entry frame the joiner's catch-up files are from.</summary>
        public int CatchUpL = -1;
        public double CatchUpSince;
        /// <summary>Catch-up isn't possible for this joiner (it failed, or was cancelled): reload in place instead.</summary>
        public bool ForceStage2;
        /// <summary>Its game diverged: it leaves on <see cref="LeavingAt"/> and drops in again with a reload in place.</summary>
        public bool Resyncing;
        /// <summary>When this guest's recent re-syncs were requested (host clock), for <see cref="ResyncLimit"/>.</summary>
        public readonly List<double> ResyncTimes = new();
        /// <summary>Host clock: don't drop this guest in again before then (it kept going out of sync).</summary>
        public double HoldUntil;
        /// <summary>Host: this guest's character was checked against the one it played before (since it last began playing).</summary>
        public bool CharacterChecked;
    }

    private enum CatchUpStep { None, Files, Running }

    /// <summary>Stage 3 (catch-up by replay) is tried before a reload in place. --sosonline-nocatchup turns it off.</summary>
    public bool CatchUpEnabled = true;

    // Host: the current area's entry save and input log.
    private readonly AreaLog _area = new();
    // Every PC: the frame of a load release whose area entry autosave is still to be taken (end of that frame).
    private int _entryFrame = -1;

    /// <summary>Joiner catching up (Stage 3).</summary>
    private sealed class CatchUpState
    {
        public int L, Seq, Total;
        public PartySpawn Party;
        public List<int> Playing;
        public bool Held, Live, Ff;
        public int LiveChecks;
        public double Since;
        // Replay speed (frames per second), measured every half second.
        public double Rate, RateSince;
        public int RateFrom = -1;
    }
    private CatchUpState _catchUp;

    /// <summary>Catching up with the cameras off (the catch-up screen draws through its own camera).</summary>
    public bool CatchUpFastForwarding => _catchUp != null && _catchUp.Ff;

    /// <summary>This PC is catching up with the session (Stage 3) and isn't in it yet.</summary>
    public bool CatchingUp => _catchUp != null && Phase == Phase.Running;

    /// <summary>
    /// Joiner, for the catch-up screen: what it is doing, progress 0..1, frames replayed and left, replay speed
    /// (frames per second, 0 until measured) and the estimated seconds left (-1: unknown).
    /// </summary>
    public (string step, float progress, int done, int left, double rate, double eta) CatchUpProgress
    {
        get
        {
            var c = _catchUp;
            if (c == null) return ("", 0, 0, 0, 0, -1);
            if (!c.Held) return ("Loading the area", 0, 0, Math.Max(0, Engine.NewestKnownFrame - c.L), 0, -1);
            int done = Math.Max(0, Engine.AppliedFrame - c.L);
            int left = Math.Max(0, Engine.NewestKnownFrame - Engine.AppliedFrame);
            if (c.Live) return ("Joining the party", 1, done, 0, c.Rate, 0);
            // The session keeps moving at 60 frames a second while this PC replays.
            double eta = c.Rate > 70 ? left / (c.Rate - 60) : -1;
            return ("Catching up", done / (float)Math.Max(1, done + left), done, left, c.Rate, eta);
        }
    }

    public ITransport Transport { get; private set; }
    public bool UsingUdp { get; private set; }
    public Role Role { get; private set; }
    public Phase Phase { get; private set; }
    public string Status { get; private set; } = "";
    public string LastError { get; private set; } = "";

    public ulong LobbyId { get; private set; }
    public ulong HostId { get; private set; }
    public int LocalSlot { get; private set; }
    public readonly string[] SlotName = new string[InputOverride.SlotCount];
    /// <summary>Slots whose player has their local pause screen open (display only: the banner's "P2 is paused").</summary>
    public readonly bool[] SlotPaused = new bool[InputOverride.SlotCount];
    public readonly ulong[] SlotPeer = new ulong[InputOverride.SlotCount];

    public readonly LockstepEngine Engine = new();
    public readonly StateSync Sync = new();

    /// <summary>Optional fixed input delay (frames). 0 = derive from ping.</summary>
    public int ConfiguredDelay;

    private readonly Dictionary<ulong, Peer> _peers = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double Now => _clock.Elapsed.TotalSeconds;

    private ulong _pendingCall;
    private double _nextHello, _nextPing, _nextLobbyPoll;
    private int _transferId;
    private int _expectedFiles, _receivedFiles;
    private bool _awaitingMirrorReload;
    private int _startSlot;
    private bool _startAuto;
    private int _desyncNotifiedFrame = -1;

    public IEnumerable<(int slot, string name, float rtt, bool ready)> Peers =>
        _peers.Values.Select(p => (p.Slot, p.Name, p.RttMs, p.Ready));

    public IEnumerable<string> PendingNames => _peers.Values.Where(p => p.Pending).Select(p => p.Name);
    public bool HasPending => _peers.Values.Any(p => p.Pending);

    /// <summary>Host: a pending joiner that drops in by catching up (Stage 3) rather than by a reload.</summary>
    private bool WantsCatchUp(Peer p) => CatchUpEnabled && !p.ForceStage2 && (p.CatchUp != CatchUpStep.None || _area.Valid);

    private bool HasStage2Pending => _peers.Values.Any(p => p.Pending && !WantsCatchUp(p) && Now >= p.HoldUntil);

    /// <summary>
    /// Re-syncs allowed in <see cref="ResyncWindow"/> seconds before the next drop-in waits <see cref="ResyncHold"/>
    /// seconds. Without it, a difference that each reload brings back made the guest join and leave every few seconds
    /// (2026-11-15).
    /// </summary>
    private const int ResyncLimit = 3;
    private const double ResyncWindow = 120, ResyncHold = 60;

    /// <summary>Guest only: we were admitted mid-session and wait to drop in.</summary>
    public bool WaitingForResync { get; private set; }

    // Drop-in (host): autosave on an agreed frame, send that save to the joiners, then everyone, joiners included,
    // reloads it in place on another agreed frame, where the party stood.
    private enum DropInStep { None, Saving, Sending }
    private DropInStep _dropIn;
    private readonly List<ulong> _joiners = new();
    private int _dropInSaveFrame;
    private int _dropInSlot = -1;
    private string _dropInLevel;
    private int _saveableFrames;
    /// <summary>Host: last lockstep frame a battle was running (this run).</summary>
    private int _lastBattleFrame = int.MinValue / 2;
    /// <summary>
    /// Frames out of battle (and level-up) before a drop-in autosave. Some battles come in rounds with short gaps where
    /// the game allows saving (the Mooncradle training: about 2 s), and the level-up starts about 5 s after the battle
    /// ends; a drop-in saved in between would only be postponed (2026-11-15).
    /// </summary>
    private const int BattleSettleFrames = 360;

    // Every PC: the party positions to restore once the in-place reload's level load is released.
    private PartySpawn _reloadSpawn;
    // Guest: the drop-in files may be applied anywhere, not only on the title screen.
    private bool _readyAnywhere;

    // Joining the game itself (the co-op Join button) after the session starts.
    private bool _autoJoin;
    private int _autoJoinFrom;
    private int _nextJoinTry;
    private int _joinTries;

    public bool InSession => Role != Role.None;
    public bool IsHost => Role == Role.Host;

    // ------------------------------------------------------------------ setup

    public void UseUdp(ushort port)
    {
        Transport?.Dispose();
        Transport = new UdpTransport(port);
        UsingUdp = true;
    }

    private bool EnsureSteam()
    {
        if (UsingUdp) return true;
        if (!SteamApi.TryInit()) { SetError("Steam is not available: " + SteamApi.InitError); return false; }
        Transport ??= new SteamTransport();
        return true;
    }

    private string LocalName => UsingUdp ? $"Player@{Transport.LocalId}" : SteamApi.LocalName;

    // ------------------------------------------------------------------ host

    public void Host()
    {
        if (InSession || !EnsureSteam()) return;
        Reset();
        LastError = "";
        Role = Role.Host;
        LocalSlot = 0;
        SlotPeer[0] = Transport.LocalId;
        SlotName[0] = LocalName;
        if (UsingUdp)
        {
            Phase = Phase.Lobby;
            Status = $"Hosting on UDP port {Transport.LocalId}";
        }
        else
        {
            _pendingCall = SteamApi.CreateLobbyAsync(InputOverride.SlotCount);
            Phase = Phase.CreatingLobby;
            Status = "Creating Steam lobby…";
        }
        Plugin.Log.LogInfo("[session] hosting");
    }

    public void InviteFriends()
    {
        if (LobbyId != 0) SteamApi.OpenInviteDialog(LobbyId);
    }

    /// <summary>Host: mirror saves to everyone, then start lockstep from the given save.</summary>
    public void StartFromSave(int slotIndex, bool autoSave)
    {
        if (!IsHost || Phase != Phase.Lobby) return;
        if (!GameBridge.AtTitleScreen) { SetError("Go to the title screen first."); return; }
        if (!ActionTable.Ready) { SetError("Input system not ready yet."); return; }
        _startSlot = slotIndex;
        _startAuto = autoSave;
        int fileCount = SendSaveFiles(_peers.Values, false);
        Phase = Phase.AwaitingReady;
        Status = $"Sending save data ({fileCount} files)…";
        Plugin.Log.LogInfo($"[session] start requested from slot {slotIndex} auto={autoSave}, {fileCount} files to {_peers.Count} guest(s)");
        TryBeginLockstep();
    }

    /// <summary>
    /// Mirrors the host's save folder to <paramref name="peers"/>. <paramref name="inPlace"/>: they apply it wherever
    /// they are (drop-in) instead of waiting for the title screen.
    /// </summary>
    private int SendSaveFiles(IEnumerable<Peer> peers, bool inPlace, List<(string name, byte[] data)> replace = null)
    {
        _transferId++;
        // The whole save folder, every slot: the game's save screens must look the same on every PC. With only the
        // session's slot, saving into a slot the guest's copy didn't have asked "overwrite?" on the host only, and one
        // PC was left in the save menu (2026-11-15).
        var files = SaveMirror.PackHostFiles();
        if (replace != null)
            foreach (var (name, data) in replace)
            {
                files.RemoveAll(f => f.name == name);
                files.Add((name, data));
            }
        foreach (var peer in peers)
        {
            peer.Ready = false;
            peer.TransferId = _transferId;
            foreach (var (name, data) in files)
            {
                var (w, ms) = Protocol.Begin(Msg.File);
                w.Write(_transferId);
                w.Write(name);
                w.WriteBytes(data);
                Send(peer.Id, ms, true);
            }
            var (w2, ms2) = Protocol.Begin(Msg.FilesDone);
            w2.Write(_transferId);
            w2.Write(files.Count);
            w2.Write(inPlace);
            Send(peer.Id, ms2, true);
        }
        return files.Count;
    }

    private void TryBeginLockstep()
    {
        if (Phase != Phase.AwaitingReady || _peers.Values.Any(p => !p.Ready)) return;

        int seed = TestHarness.TryArg("seed", out var fixedSeed) ? int.Parse(fixedSeed) : new System.Random().Next();
        int delay = ConfiguredDelay > 0 ? ConfiguredDelay : AutoDelay();
        double timeBase = NextClockBase(_peers.Values);
        var occupied = new bool[InputOverride.SlotCount];
        for (int s = 0; s < occupied.Length; s++) occupied[s] = SlotPeer[s] != 0;

        foreach (var peer in _peers.Values)
        {
            var (w, ms) = Protocol.Begin(Msg.Start);
            w.Write(seed);
            w.Write(delay);
            w.Write(_startSlot);
            w.Write(_startAuto);
            foreach (bool o in occupied) w.Write(o);
            w.Write(timeBase);
            Send(peer.Id, ms, true);
        }
        int saveSlot = _startSlot;
        bool auto = _startAuto;
        BeginLockstep(seed, delay, occupied, 0, null, timeBase, () => GameBridge.EnterGameFromSlot(saveSlot, auto));
    }

    /// <summary>
    /// Host: a session clock base that moves no PC's game time backwards (<paramref name="peers"/> reported their
    /// clocks when they got ready).
    /// </summary>
    private static double NextClockBase(IEnumerable<Peer> peers)
    {
        double lead = 0;
        foreach (var p in peers) lead = Math.Max(lead, p.ClockLead);
        return GameClock.NextBase(GameClock.Now + lead);
    }

    private const int MinAutoDelay = 4;

    private int AutoDelay()
    {
        // One-way latency plus a little jitter headroom, in 60 Hz frames. At least 4: on 2026-11-15 every drop-in at
        // delay 3 (35 ms ping) went out of sync within seconds, and none at 5, 6 or 15 did. Cause not found yet.
        float rtt = _peers.Count == 0 ? 0 : _peers.Values.Max(p => p.RttMs);
        int frames = (int)Math.Ceiling((rtt * 0.5f + 25f) / (1000f / 60f));
        return Math.Clamp(frames, MinAutoDelay, 15);
    }

    public void EndSession()
    {
        if (!IsHost || Phase != Phase.Running) return;
        foreach (var peer in _peers.Values)
        {
            var (w, ms) = Protocol.Begin(Msg.Stop);
            w.Write("Host ended the co-op session");
            Send(peer.Id, ms, true);
        }
        StopLockstep("ended by host");
    }

    /// <summary>
    /// Ends lockstep on this peer at the current frame. Called from a menu choice that every peer executes on the
    /// same frame (synced input), so no message is needed and everyone continues from an identical state.
    /// </summary>
    public void EndSessionDeterministic(string reason)
    {
        if (Phase != Phase.Running) return;
        StopLockstep(reason);
        Toast.Show("Co-op session ended. Everyone keeps playing on their own.");
    }

    // ------------------------------------------------------------------ guest

    public void JoinFriend(ulong friendId)
    {
        if (!EnsureSteam()) return;
        ulong lobby = SteamApi.FriendLobby(friendId);
        if (lobby == 0) { SetError($"{SteamApi.FriendName(friendId)} is not hosting a session."); return; }
        JoinLobby(lobby, SteamApi.FriendName(friendId));
    }

    /// <summary>Join a lobby by id (friend list, Steam invite, or +connect_lobby).</summary>
    public void JoinLobby(ulong lobby, string label = null)
    {
        if (!EnsureSteam() || lobby == 0) return;
        if (InSession)
        {
            if (LobbyId == lobby) return;
            if (Phase == Phase.Running) { Toast.Show("Finish the current co-op session before joining another one."); return; }
            Leave();
        }
        Reset();
        LastError = "";
        Role = Role.Guest;
        _pendingCall = SteamApi.JoinLobbyAsync(lobby);
        Phase = Phase.JoiningLobby;
        Status = $"Joining {label ?? "session"}…";
        Toast.Show(Status);
    }

    public void JoinUdp(ushort hostPort)
    {
        if (InSession) return;
        Reset();
        Role = Role.Guest;
        HostId = hostPort;
        Phase = Phase.Connecting;
        Status = $"Connecting to UDP port {hostPort}…";
    }

    public void Leave()
    {
        if (!InSession) return;
        foreach (var peer in _peers.Values)
        {
            var (w, ms) = Protocol.Begin(Msg.Bye);
            Send(peer.Id, ms, true);
        }
        if (Phase == Phase.Running) StopLockstep("left the session");
        if (!UsingUdp && LobbyId != 0) SteamApi.LeaveLobby(LobbyId);
        Plugin.Log.LogInfo("[session] left");
        Reset();
        Status = "Left the session.";
    }

    // ------------------------------------------------------------------ per-frame

    /// <summary>Called every frame from Update (outside of lockstep waits).</summary>
    public void Update()
    {
        // A guest goes back to its own saves once it has left and is on the title screen again.
        if (!InSession && SaveMirror.Active && GameBridge.AtTitleScreen)
            SaveMirror.SetActiveAndReload(false);
        AutoOpen();

        if (!InSession) return;
        PollCallResults();

        // This PC's own game was frozen (a save loading while it boots, say): the time it stood still is nobody's
        // silence. Without this a guest drops the host as timed out right after a long freeze.
        double now = Now;
        if (_lastUpdate > 0 && now - _lastUpdate > 2)
        {
            double frozen = now - _lastUpdate;
            foreach (var peer in _peers.Values) peer.LastHeard += frozen;
            Plugin.Log.LogInfo($"[session] this game stood still for {frozen:F1} s");
        }
        _lastUpdate = now;
        if (!Pump()) return;

        if (Role == Role.Guest && Phase == Phase.Connecting && now >= _nextHello)
        {
            _nextHello = now + 1.0;
            SendHello();
        }
        if (now >= _nextPing)
        {
            _nextPing = now + 1.0;
            foreach (var peer in _peers.Values)
            {
                var (w, ms) = Protocol.Begin(Msg.Ping);
                w.Write(now);
                Send(peer.Id, ms, false);
            }
        }
        if (IsHost && LobbyId != 0 && now >= _nextLobbyPoll)
        {
            _nextLobbyPoll = now + 1.0;
            // Opening the conversation from our side implicitly accepts the guest's messaging session.
            foreach (var member in SteamApi.LobbyMembers(LobbyId))
            {
                if (member == Transport.LocalId || _peers.ContainsKey(member)) continue;
                SteamApi.Accept(member);
                var (w, ms) = Protocol.Begin(Msg.Ping);
                w.Write(now);
                Send(member, ms, true);
            }
        }

        UpdateSoloCoop();
        UpdateCharacters();
        UpdateDropIn();
        UpdateCatchUpHost();
        UpdateCatchUpJoiner();
        if (IsHost && Phase == Phase.Running) TryReleaseLoad();
        if (IsHost && Phase == Phase.Running)
            foreach (var peer in _peers.Values.Where(p => p.LeavingAt >= 0 && Engine.AppliedFrame >= p.LeavingAt).ToList())
            {
                if (peer.Resyncing) { RequeueResynced(peer); continue; }
                DropPeer(peer.Id, "left the session");
            }

        // Drop silent peers (not during lockstep: the engine has its own stall timeout).
        if (Phase != Phase.Running)
            foreach (var peer in _peers.Values.ToList())
                if (now - peer.LastHeard > 20) DropPeer(peer.Id, "timed out");

        // Ready only once the host's saves are loaded AND we're on the title screen: everyone must enter the game
        // from the same place on the same frame.
        // A drop-in loads in place from wherever the guest is.
        if (_awaitingMirrorReload && SaveMirror.ReloadDone && !GameBridge.AtTitleScreen && !_readyAnywhere)
            Status = "Save data received. Return to the title screen to start!";
        if (_awaitingMirrorReload && SaveMirror.ReloadDone && GameBridge.Booted && (_readyAnywhere || GameBridge.AtTitleScreen))
        {
            _awaitingMirrorReload = false;
            var (w, ms) = Protocol.Begin(Msg.Ready);
            w.Write(_transferId);
            w.Write(GameClock.Now);
            Send(HostId, ms, true);
            Status = _readyAnywhere ? "Save data received. Dropping in…" : "Save data received. Waiting for host…";
        }
    }

    /// <summary>Drains the transport. Also used by the lockstep engine while it waits for input.</summary>
    public bool Pump()
    {
        if (Transport == null) return false;
        try
        {
            Transport.Poll(OnMessage);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[session] transport error: {e}");
        }
        return InSession;
    }

    private void PollCallResults()
    {
        if (_pendingCall == 0) return;
        if (Phase == Phase.CreatingLobby)
        {
            int r = SteamApi.PollLobbyCreated(_pendingCall, out ulong lobby);
            if (r == 0) return;
            _pendingCall = 0;
            if (r < 0) { SetError("Could not create a Steam lobby."); Reset(); return; }
            LobbyId = lobby;
            SteamApi.SetLobbyData(lobby, "sosonline", Protocol.Version.ToString());
            SteamApi.SetRichPresence("status", "Hosting online co-op");
            // Lets friends use "Join Game" from the Steam friends list.
            SteamApi.SetRichPresence("connect", $"+connect_lobby {lobby}");
            Phase = Phase.Lobby;
            Status = "Hosting. Invite friends or have them join from the co-op menu.";
            Plugin.Log.LogInfo($"[session] lobby {lobby} created");
        }
        else if (Phase == Phase.JoiningLobby)
        {
            int r = SteamApi.PollLobbyEntered(_pendingCall, out ulong lobby);
            if (r == 0) return;
            _pendingCall = 0;
            if (r < 0) { SetError("Could not join the lobby (full or closed)."); Reset(); return; }
            LobbyId = lobby;
            HostId = SteamApi.LobbyOwner(lobby);
            if (SteamApi.GetLobbyData(lobby, "sosonline") != Protocol.Version.ToString())
            {
                SetError("The host runs a different version of the mod.");
                SteamApi.LeaveLobby(lobby);
                Reset();
                return;
            }
            SteamApi.Accept(HostId);
            Phase = Phase.Connecting;
            Status = $"Connecting to {Transport.Describe(HostId)}…";
        }
    }

    // ------------------------------------------------------------------ messages

    private void SendHello()
    {
        var (w, ms) = Protocol.Begin(Msg.Hello);
        w.Write(Protocol.Version);
        w.Write(SessionLogs.Build);
        w.Write(Application.version ?? "");
        w.Write(ActionTable.Hash);
        w.Write(LocalName);
        Send(HostId, ms, true);
    }

    private void OnMessage(ulong from, byte[] data)
    {
        if (data.Length == 0) return;
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            var type = (Msg)r.ReadByte();
            if (_peers.TryGetValue(from, out var known)) known.LastHeard = Now;
            if (IsHost) OnHostMessage(from, type, r);
            else if (Role == Role.Guest && from == HostId) OnGuestMessage(type, r);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[session] bad message from {from}: {e}");
        }
    }

    private void OnHostMessage(ulong from, Msg type, BinaryReader r)
    {
        _peers.TryGetValue(from, out var peer);
        switch (type)
        {
            case Msg.Hello:
            {
                int proto = r.ReadInt32();
                string modVersion = r.ReadString();
                string gameVersion = r.ReadString();
                uint actionHash = r.ReadUInt32();
                string name = r.ReadString();
                if (peer != null) { SendWelcome(peer); return; } // duplicate hello
                string reason = null;
                if (proto != Protocol.Version)
                    reason = $"different mod versions (host {SessionLogs.Build}, you {modVersion}). Restart the game to get the latest version automatically, or install the same zip";
                else if (gameVersion != (Application.version ?? "")) reason = $"game version mismatch (host {Application.version}, you {gameVersion})";
                else if (ActionTable.Ready && actionHash != 0 && actionHash != ActionTable.Hash) reason = "input configuration mismatch";
                else if (Phase == Phase.AwaitingReady) reason = "the session is starting, try again in a moment";
                int slot = Array.FindIndex(SlotPeer, 1, id => id == 0);
                if (reason == null && slot < 0) reason = "the session is full";
                if (reason != null)
                {
                    var (w, ms) = Protocol.Begin(Msg.Reject);
                    w.Write(reason);
                    Send(from, ms, true);
                    Plugin.Log.LogInfo($"[session] rejected {name}: {reason}");
                    return;
                }
                peer = new Peer { Id = from, Slot = slot, Name = name, LastHeard = Now, Pending = Phase == Phase.Running };
                _peers[from] = peer;
                SlotPeer[slot] = from;
                SlotName[slot] = name;
                SendWelcome(peer);
                BroadcastRoster();
                for (int s = 0; s < InputOverride.SlotCount; s++)
                    if (SlotPaused[s]) SendPauseStatus(peer.Id, s, true);
                Status = peer.Pending ? $"{name} is joining (player {slot + 1})…" : $"{name} joined (player {slot + 1}).";
                Toast.Show(Status);
                Plugin.Log.LogInfo($"[session] {name} joined as slot {slot} (mod {modVersion})");
                if (modVersion != SessionLogs.Build)
                {
                    // Not refused (the protocol matched), but a different build is the first suspect if anything breaks.
                    Plugin.Log.LogWarning($"[session] {name} runs mod build {modVersion}, this PC {SessionLogs.Build}");
                    Toast.Show($"{name} runs a different build of the mod ({modVersion}). Use the same zip if anything goes wrong.");
                }
                break;
            }
            case Msg.Ping: Pong(from, r); break;
            case Msg.Pong: OnPong(peer, r); break;
            case Msg.Ready:
                if (peer == null) return;
                if (r.ReadInt32() == peer.TransferId) peer.Ready = true;
                peer.ClockLead = r.ReadDouble() - GameClock.Now;
                TryBeginLockstep();
                break;
            case Msg.Input:
            {
                if (peer == null) return;
                ReadInput(r, out int frame, out int slot, out var input, out var system);
                if (slot != peer.Slot || peer.Pending) return;
                if (frame > peer.LastInputFrame) peer.LastInputFrame = frame;
                Engine.OnRemoteInput(frame, slot, input, null);
                // Relay to the other guests (star topology: guests only talk to the host).
                foreach (var other in _peers.Values)
                    if (other.Id != from) SendInputTo(other.Id, frame, slot, input, null);
                break;
            }
            case Msg.Loaded:
            {
                // A joiner catching up replays loads the session finished long ago.
                if (peer == null || peer.Pending) return;
                int seq = r.ReadInt32();
                int at = r.ReadInt32();
                ReportLoaded(peer.Slot, seq, at);
                break;
            }
            case Msg.Hash:
                // Guests also run the comparison themselves; this is for the host's log.
                break;
            case Msg.CaughtUp:
                if (peer != null && peer.Pending && peer.CatchUp == CatchUpStep.Running) MergeCaughtUp(peer, r.ReadInt32());
                break;
            case Msg.CatchUpFailed:
                if (peer == null || !peer.Pending) return;
                Plugin.Log.LogWarning($"[catchup] {peer.Name}: replay diverged ({r.ReadString()}); reloading in place instead");
                peer.CatchUp = CatchUpStep.None;
                peer.ForceStage2 = true;
                break;
            case Msg.Bye:
                if (peer != null) DropPeer(from, "left");
                break;
            case Msg.PauseStatus:
            {
                int slot = r.ReadByte();
                bool paused = r.ReadBoolean();
                if (peer == null || slot != peer.Slot) return;
                OnPauseStatus(slot, paused);
                break;
            }
            case Msg.LeaveRequest:
                if (peer == null || peer.Pending || Phase != Phase.Running) return;
                LeaveScheduled(peer);
                break;
            case Msg.ResyncRequest:
            {
                string reason = r.ReadString();
                if (peer == null || peer.Pending || peer.Resyncing || Phase != Phase.Running) return;
                Plugin.Log.LogWarning($"[resync] {peer.Name}: diverged ({reason}); dropping in again with a reload");
                peer.Resyncing = true;
                double now = Now;
                peer.ResyncTimes.RemoveAll(t => now - t > ResyncWindow);
                peer.ResyncTimes.Add(now);
                if (peer.ResyncTimes.Count >= ResyncLimit)
                {
                    peer.HoldUntil = now + ResyncHold;
                    Plugin.Log.LogWarning($"[resync] {peer.Name}: {peer.ResyncTimes.Count} re-syncs in {ResyncWindow:F0} s; next drop-in in {ResyncHold:F0} s");
                    Toast.Show($"{peer.Name} keeps going out of sync. They drop in again in a minute. If it keeps happening, end the session and start again from the title screen.");
                }
                LeaveScheduled(peer);
                break;
            }
        }
    }

    private void OnGuestMessage(Msg type, BinaryReader r)
    {
        switch (type)
        {
            case Msg.Welcome:
                LocalSlot = r.ReadInt32();
                WaitingForResync = r.ReadBoolean();
                if (!_peers.ContainsKey(HostId))
                    _peers[HostId] = new Peer { Id = HostId, Slot = 0, Name = Transport.Describe(HostId), LastHeard = Now };
                if (Phase == Phase.Lobby && WaitingForResync)
                    Status = $"{SlotName[0] ?? "The host"} is playing. You drop in in a moment…";
                if (Phase == Phase.Connecting)
                {
                    Phase = Phase.Lobby;
                    Status = WaitingForResync
                        ? $"Connected as player {LocalSlot + 1}. You'll drop in as soon as the game allows saving."
                        : $"Connected as player {LocalSlot + 1}. Waiting for the host to start…";
                    Toast.Show(WaitingForResync
                        ? $"Joined {SlotName[0] ?? Transport.Describe(HostId)}'s game. You'll drop in as soon as their game allows saving."
                        : $"Joined {SlotName[0] ?? Transport.Describe(HostId)}'s session as player {LocalSlot + 1}.");
                    Plugin.Log.LogInfo($"[session] welcomed as slot {LocalSlot}");
                }
                break;
            case Msg.Reject:
                SetError("Host refused: " + r.ReadString());
                Leave();
                break;
            case Msg.Roster:
                for (int s = 0; s < InputOverride.SlotCount; s++)
                {
                    SlotPeer[s] = r.ReadUInt64();
                    SlotName[s] = r.ReadString();
                }
                if (_peers.TryGetValue(HostId, out var hp)) hp.Name = SlotName[0];
                break;
            case Msg.Ping: Pong(HostId, r); break;
            case Msg.Pong: _peers.TryGetValue(HostId, out var h); OnPong(h, r); break;
            case Msg.File:
            {
                int id = r.ReadInt32();
                string name = r.ReadString();
                byte[] bytes = r.ReadBytes();
                if (id != _transferId)
                {
                    _transferId = id;
                    _receivedFiles = 0;
                    SaveMirror.RememberRegistryBackup();
                    SaveMirror.BeginReceive();
                }
                SaveMirror.WriteFile(name, bytes);
                _receivedFiles++;
                Status = $"Receiving save data ({_receivedFiles})…";
                break;
            }
            case Msg.FilesDone:
            {
                int id = r.ReadInt32();
                _expectedFiles = r.ReadInt32();
                _readyAnywhere = r.ReadBoolean();
                if (id != _transferId) { _transferId = id; SaveMirror.BeginReceive(); _receivedFiles = 0; }
                if (!GameBridge.AtTitleScreen && !_readyAnywhere) Status = "Host is starting. Go to the title screen!";
                SaveMirror.SetActiveAndReload(true);
                _awaitingMirrorReload = true;
                Phase = Phase.AwaitingReady;
                Plugin.Log.LogInfo($"[session] mirrored {_receivedFiles}/{_expectedFiles} save files");
                break;
            }
            case Msg.Start:
            {
                int seed = r.ReadInt32();
                int delay = r.ReadInt32();
                int slot = r.ReadInt32();
                bool auto = r.ReadBoolean();
                var occupied = new bool[InputOverride.SlotCount];
                for (int s = 0; s < occupied.Length; s++) occupied[s] = r.ReadBoolean();
                double timeBase = r.ReadDouble();
                BeginLockstep(seed, delay, occupied, 0, null, timeBase, () => GameBridge.EnterGameFromSlot(slot, auto));
                break;
            }
            case Msg.DropIn:
                OnDropIn(r);
                break;
            case Msg.CatchUp:
                OnCatchUp(r);
                break;
            case Msg.InputLog:
                if (!Engine.Running) break;
                foreach (var (logFrame, channels) in AreaLog.ReadFrames(r)) Engine.OnRemoteFrame(logFrame, channels);
                break;
            case Msg.Merge:
            {
                int at = r.ReadInt32();
                int slot = r.ReadInt32();
                if (!Engine.Running) break;
                Engine.AddSlot(slot, at);
                Engine.Schedule(at, () => GameBridge.RegisterSlotPlayers(new[] { slot }));
                if (slot == LocalSlot && _catchUp != null)
                {
                    Plugin.Log.LogInfo($"[catchup] merging at f{at} (replayed {_catchUp.Total} frames in {Now - _catchUp.Since:F1} s)");
                    EndCatchUpFastForward();
                    _catchUp = null;
                    _autoJoin = true;
                    _autoJoinFrom = at + 1;
                    _nextJoinTry = 0;
                    _joinTries = 0;
                    Status = "Co-op running.";
                    Toast.Show("You're in. Joining the party…");
                }
                break;
            }
            case Msg.DropInAbort:
                if (Phase != Phase.Running) break;
                Plugin.Log.LogInfo($"[dropin] postponed by the host at f{Engine.AppliedFrame}");
                StopLockstep("drop-in postponed");
                WaitingForResync = true;
                Status = "The party is loading or in a battle. You'll drop in in a moment…";
                Toast.Show(Status);
                break;
            case Msg.DropInSave:
            {
                int at = r.ReadInt32();
                if (Engine.Running) Engine.Schedule(at, () => SaveForDropIn(at));
                break;
            }
            case Msg.DropInReload:
            {
                int at = r.ReadInt32();
                int saveSlot = r.ReadInt32();
                var slots = new List<int>();
                for (int n = r.ReadInt32(); n > 0; n--) slots.Add(r.ReadInt32());
                var spawn = r.ReadBoolean() ? PartySpawn.Read(r) : null;
                double timeBase = r.ReadDouble();
                if (!Engine.Running) break;
                foreach (int s in slots) Engine.AddSlot(s, at);
                ScheduleReloadInPlace(at, saveSlot, spawn, slots, timeBase);
                break;
            }
            case Msg.Input:
                ReadInput(r, out int frame, out int fslot, out var input, out var system);
                Engine.OnRemoteInput(frame, fslot, input, system);
                break;
            case Msg.Snapshot:
            {
                var snap = StateSnapshot.Read(r);
                // A drop-in guest gets the host's snapshots before its run starts; it has no state for those frames.
                if (Engine.Running && snap.Frame >= Engine.StartFrame) Sync.OnHostSnapshot(snap);
                break;
            }
            case Msg.LoadRelease:
            {
                int seq = r.ReadInt32();
                int at = r.ReadInt32();
                double timeBase = r.ReadDouble();
                ScheduleLoadRelease(seq, at, timeBase);
                break;
            }
            case Msg.Stop:
                StopLockstep(r.ReadString());
                break;
            case Msg.SlotLeft:
            {
                int leftSlot = r.ReadInt32();
                int leftFrame = r.ReadInt32();
                ApplySlotLeave(leftSlot, leftFrame);
                break;
            }
            case Msg.PauseStatus:
            {
                int slot = r.ReadByte();
                bool paused = r.ReadBoolean();
                if (slot != LocalSlot) OnPauseStatus(slot, paused);
                break;
            }
            case Msg.AssignCharacter:
            {
                int at = r.ReadInt32();
                int slot = r.ReadByte();
                string character = r.ReadString();
                if (!Engine.Running || CatchingUp) break;
                Engine.Schedule(at, () => GameBridge.AssignCharacter(slot, character));
                break;
            }
            case Msg.EndAt:
            {
                int at = r.ReadInt32();
                string reason = r.ReadString();
                if (Phase != Phase.Running) break;
                if (CatchingUp || at < Engine.Frame) { StopLockstep(reason); break; }
                Engine.EndAt(at, () => EndSessionDeterministic(reason));
                break;
            }
            case Msg.Bye:
                SetError("The host closed the session.");
                if (Phase == Phase.Running) StopLockstep("host left");
                Reset();
                break;
        }
    }

    private void SendWelcome(Peer peer)
    {
        var (w, ms) = Protocol.Begin(Msg.Welcome);
        w.Write(peer.Slot);
        w.Write(peer.Pending);
        Send(peer.Id, ms, true);
    }

    private void BroadcastRoster()
    {
        foreach (var peer in _peers.Values)
        {
            var (w, ms) = Protocol.Begin(Msg.Roster);
            for (int s = 0; s < InputOverride.SlotCount; s++)
            {
                w.Write(SlotPeer[s]);
                w.Write(SlotName[s] ?? "");
            }
            Send(peer.Id, ms, true);
        }
    }

    private void Pong(ulong to, BinaryReader r)
    {
        var (w, ms) = Protocol.Begin(Msg.Pong);
        w.Write(r.ReadDouble());
        Send(to, ms, false);
    }

    private void OnPong(Peer peer, BinaryReader r)
    {
        if (peer == null) return;
        double sent = r.ReadDouble();
        float rtt = (float)((Now - sent) * 1000.0);
        peer.RttSamples.Add(rtt);
        if (peer.RttSamples.Count > RttKeep) peer.RttSamples.RemoveAt(0);
        var sorted = peer.RttSamples.OrderBy(x => x).ToList();
        peer.RttMs = sorted[sorted.Count / 2];
    }

    private const int RttKeep = 9;
    /// <summary>Round trips needed before the input delay is picked from a peer's ping.</summary>
    private const int RttForDelay = 3;

    private void DropPeer(ulong id, string why)
    {
        if (!_peers.Remove(id, out var peer)) return;
        Plugin.Log.LogInfo($"[session] {peer.Name} {why}");
        if (IsHost)
        {
            if (peer.Slot > 0) { SlotPeer[peer.Slot] = 0; SlotName[peer.Slot] = null; OnPauseStatus(peer.Slot, false); }
            if (Phase == Phase.Running && !peer.Pending && peer.LeavingAt < 0 && Engine.Occupied[peer.Slot])
            {
                // Everyone has (or will get through us) this guest's input up to the last frame we received, and
                // nobody can be past the frame after it. From there on the slot is empty on every peer.
                int leaveFrame = Math.Max(peer.LastInputFrame + 1, Engine.AppliedFrame + 1);
                ApplySlotLeave(peer.Slot, leaveFrame);
                foreach (var other in _peers.Values)
                {
                    var (w, ms) = Protocol.Begin(Msg.SlotLeft);
                    w.Write(peer.Slot);
                    w.Write(leaveFrame);
                    Send(other.Id, ms, true);
                }
            }
            BroadcastRoster();
            if (Phase == Phase.AwaitingReady) TryBeginLockstep();
            _joiners.Remove(id);
            Status = $"{peer.Name} {why}.";
            Toast.Show(Phase == Phase.Running ? $"{peer.Name} {why}. Their character rejoins the party AI." : Status);
        }
        else
        {
            SetError($"Lost connection to the host ({why}).");
            if (Phase == Phase.Running) StopLockstep("host lost");
            Reset();
        }
        if (!UsingUdp) Transport.Disconnect(id);
    }

    // ------------------------------------------------------------------ lockstep glue

    /// <summary>
    /// Starts lockstep on this PC. A new run starts on frame 0; a drop-in starts on the join frame
    /// <paramref name="startFrame"/>, with the slot → player mapping the session already uses
    /// (<paramref name="playerIndex"/>). <paramref name="enterGame"/> runs on the start frame, after the session clock
    /// is set to <paramref name="timeBase"/> (NaN: <paramref name="enterGame"/> sets it).
    /// </summary>
    private void BeginLockstep(int seed, int delay, bool[] occupied, int startFrame, int[] playerIndex, double timeBase, Action enterGame)
    {
        GameBridge.LockTime();
        Application.runInBackground = true;

        Sync.Reset();
        ResetLoadBarrier();
        // An area log from an earlier run counts frames of that run.
        if (IsHost) _area.Clear("a new run started");
        _resyncing = false;
        _desyncNotifiedFrame = -1;
        Engine.SendInput = BroadcastInput;
        Engine.PollNetwork = Pump;
        Engine.FrameApplied = OnFrameApplied;
        Engine.Failed = reason =>
        {
            SetError("Co-op stopped: " + reason);
            if (IsHost)
                foreach (var peer in _peers.Values)
                {
                    var (w, ms) = Protocol.Begin(Msg.Stop);
                    w.Write(reason);
                    Send(peer.Id, ms, true);
                }
            AfterLockstep();
        };
        TestHarness.Attach(Engine);
        Engine.LocalInputFilter = (frame, channel, snap) =>
        {
            if (channel == LocalSlot) snap = AutoJoin(frame, snap);
            if (TestHarness.Filter != null) snap = TestHarness.Filter(frame, channel, snap);
            // Last: a scripted pause press must reach the local pause screen too.
            return LocalPause.Filter(frame, channel, snap);
        };
        Engine.Stalled = OnStalled;
        Engine.LeftSession = OnLeftSession;
        _releaseFrames.Clear();
        _entryFrame = -1;
        _autoJoin = LocalSlot > 0;
        // After a title-screen start the game needs a moment before Join works; a drop-in waits for its load instead.
        _autoJoinFrom = startFrame == 0 ? 240 : startFrame + 1;
        _nextJoinTry = 0;
        _joinTries = 0;
        _reloadSpawn = null;
        WaitingForResync = false;
        Engine.Start(LocalSlot, IsHost, occupied, delay, seed, startFrame);
        Engine.Hold = HoldFrame;
        if (playerIndex != null)
        {
            Array.Copy(playerIndex, GameBridge.SlotPlayerIndex, GameBridge.SlotPlayerIndex.Length);
            LockstepEngine.RemapPlayers();
        }
        ControllerRouting.RouteLocalDevicesTo(GameBridge.PlayerForSlot(LocalSlot));
        Engine.Schedule(startFrame, () =>
        {
            var slots = new List<int>();
            for (int s = 0; s < occupied.Length; s++) if (occupied[s]) slots.Add(s);
            GameBridge.RegisterSlotPlayers(slots);
            if (!double.IsNaN(timeBase)) GameClock.SetBase(timeBase, startFrame);
            enterGame();
        });
        Phase = Phase.Running;
        Status = "Co-op running.";
        Toast.Show(startFrame > 0 ? "Dropping in…" : LocalSlot > 0 ? "Co-op session started. Joining the party…" : "Co-op session started.");
    }

    private void StopLockstep(string reason)
    {
        Engine.Stop(reason);
        AfterLockstep();
        Status = "Co-op stopped: " + reason;
    }

    private void AfterLockstep()
    {
        LocalPause.Reset();
        Array.Clear(SlotPaused);
        if (_catchUp != null) { EndCatchUpFastForward(); _catchUp = null; }
        _releaseFrames.Clear();
        _entryFrame = -1;
        // A load held at the barrier would never end otherwise.
        if (_heldSeq >= 0)
        {
            Plugin.Log.LogInfo($"[barrier] lockstep stopped while load {_heldSeq} was held: releasing it");
            _heldSeq = -1;
            LoadBarrier.Release();
        }
        GameBridge.UnlockTime();
        try { GameBridge.Input?.AutoAssignUnassignedControllers(); } catch { }
        if (InSession) Phase = Phase.Lobby;
        foreach (var peer in _peers.Values) peer.Ready = false;
    }

    private void BroadcastInput(int frame, int slot, InputSnapshot input, InputSnapshot system)
    {
        if (IsHost)
            foreach (var peer in _peers.Values) SendInputTo(peer.Id, frame, slot, input, system);
        else
            SendInputTo(HostId, frame, slot, input, null);
    }

    private void SendInputTo(ulong to, int frame, int slot, InputSnapshot input, InputSnapshot system)
    {
        var (w, ms) = Protocol.Begin(Msg.Input);
        w.Write(frame);
        w.Write((byte)slot);
        w.Write(system != null);
        input.Write(w);
        system?.Write(w);
        Send(to, ms, true);
    }

    private static void ReadInput(BinaryReader r, out int frame, out int slot, out InputSnapshot input, out InputSnapshot system)
    {
        frame = r.ReadInt32();
        slot = r.ReadByte();
        bool hasSystem = r.ReadBoolean();
        input = InputSnapshot.Read(r);
        system = hasSystem ? InputSnapshot.Read(r) : null;
    }

    private void OnFrameApplied(int frame)
    {
        if (_catchUp != null && frame == _catchUp.L) CatchUpEntry(frame);
        if (frame == _entryFrame)
        {
            _entryFrame = -1;
            AreaLog.SaveAtEntry(frame, IsHost, IsHost ? _area : null, Engine);
        }
        if (IsHost) _area.OnFrame(frame, InputOverride.Current);
        var snap = Sync.OnFrame(frame);
        if (snap != null && IsHost && _area.L >= 0 && frame >= _area.L) _area.Snapshots.Add(snap);
        if (snap != null)
        {
            TestHarness.OnSnapshot(snap);
            SessionLogs.Snapshot(snap);
            if (IsHost)
                foreach (var peer in _peers.Values)
                {
                    var (w, ms) = Protocol.Begin(Msg.Snapshot);
                    snap.Write(w);
                    Send(peer.Id, ms, true);
                }
        }
        if (_catchUp != null && Sync.Desynced)
        {
            // The replay doesn't reproduce the live game: give up on catching up, the host reloads in place instead.
            Plugin.Log.LogWarning($"[catchup] f{frame} replay diverged: {Sync.LastDesyncReason}");
            var (w, ms) = Protocol.Begin(Msg.CatchUpFailed);
            w.Write(Sync.LastDesyncReason ?? "");
            Send(HostId, ms, true);
            StopLockstep("catch-up diverged");
            WaitingForResync = true;
            Status = "Catching up failed. You'll drop in with a short reload instead…";
            return;
        }
        if (Sync.Desynced && _desyncNotifiedFrame < 0)
        {
            _desyncNotifiedFrame = frame;
            if (!IsHost && !_resyncing)
            {
                // Recover: leave on a frame the host picks, then drop in again with a reload in place (Stage 2).
                _resyncing = true;
                Plugin.Log.LogWarning($"[resync] f{frame} this PC diverged ({Sync.LastDesyncReason}); asking the host to re-sync");
                var (w, ms) = Protocol.Begin(Msg.ResyncRequest);
                w.Write(Sync.LastDesyncReason ?? "");
                Send(HostId, ms, true);
                Status = "Out of sync. Re-syncing with a short reload…";
                Toast.Show("Your game got out of sync. Re-syncing…");
            }
            else SetError("Desync detected: " + Sync.LastDesyncReason);
        }
        TestHarness.OnFrame(frame);
    }

    // ------------------------------------------------------------------ loading barrier

    private int _loadSeq;                 // level loads that reached their end during this lockstep run
    private int _heldSeq = -1;            // load whose end is held locally, waiting for the release frame
    private int _heldSince;
    private readonly Dictionary<int, Dictionary<int, int>> _loadReports = new(); // host: seq → slot → frame

    /// <summary>A level load is ending sooner than it was released for.</summary>
    public int LoadBarrierLateReleases { get; private set; }

    private void ResetLoadBarrier()
    {
        _loadSeq = 0;
        _heldSeq = -1;
        _loadReports.Clear();
    }

    /// <summary>
    /// Called from <c>LevelManager.EndCurrentLevelLoading</c> during lockstep. Returns true if the call is held: it
    /// runs later, on a release frame the host picks, identical on every PC.
    /// </summary>
    public bool HoldLevelLoadEnd()
    {
        if (!Engine.Running) return false;
        int seq = ++_loadSeq;
        int frame = Engine.AppliedFrame;
        _heldSeq = seq;
        _heldSince = frame;
        Plugin.Log.LogInfo($"[barrier] load {seq} reached its end at f{frame}, held");
        if (_catchUp != null)
        {
            // Catching up: the session released this load long ago; its release frame is already scheduled here.
            if (!_catchUp.Held && seq == _catchUp.Seq)
            {
                _catchUp.Held = true;
                Plugin.Log.LogInfo($"[catchup] area entry save loaded in {Now - _catchUp.Since:F1} s; replaying from f{_catchUp.L}");
            }
            return true;
        }
        if (IsHost) ReportLoaded(LocalSlot, seq, frame);
        else
        {
            var (w, ms) = Protocol.Begin(Msg.Loaded);
            w.Write(seq);
            w.Write(frame);
            Send(HostId, ms, true);
        }
        return true;
    }

    /// <summary>Host: a PC (possibly the host) reached the end of load <paramref name="seq"/> at <paramref name="frame"/>.</summary>
    private void ReportLoaded(int slot, int seq, int frame)
    {
        if (!_loadReports.TryGetValue(seq, out var bySlot)) _loadReports[seq] = bySlot = new Dictionary<int, int>();
        bySlot[slot] = frame;
        TryReleaseLoad();
    }

    /// <summary>Host: once every PC in the session reached the end of the load, schedule its release everywhere.</summary>
    private void TryReleaseLoad()
    {
        if (!Engine.Running || _loadReports.Count == 0) return;
        int now = Engine.AppliedFrame;
        foreach (int seq in _loadReports.Keys.OrderBy(k => k).ToList())
        {
            var bySlot = _loadReports[seq];
            bool all = true;
            for (int s = 0; s < InputOverride.SlotCount; s++)
                if (Engine.InSessionAt(s, now) && !bySlot.ContainsKey(s)) { all = false; break; }
            if (!all) return;
            // Guests can run at most Delay + 1 frames ahead of the host's applied frame (they need its system input),
            // and they receive this message before any input that would take them past the release frame.
            int release = now + Engine.Delay + 3;
            double timeBase = GameClock.BaseIn(release - now);
            Plugin.Log.LogInfo($"[barrier] load {seq}: reached at {string.Join(", ", bySlot.OrderBy(kv => kv.Key).Select(kv => $"P{kv.Key + 1} f{kv.Value}"))}; release at f{release}");
            foreach (var peer in _peers.Values.Where(p => !p.Pending || p.CatchUp == CatchUpStep.Running))
            {
                var (w, ms) = Protocol.Begin(Msg.LoadRelease);
                w.Write(seq);
                w.Write(release);
                w.Write(timeBase);
                Send(peer.Id, ms, true);
            }
            _loadReports.Remove(seq);
            ScheduleLoadRelease(seq, release, timeBase);
        }
    }

    /// <summary>(load sequence, release frame) whenever a load's release is scheduled on this PC.</summary>
    public static event Action<int, int> LoadReleaseScheduled;

    /// <summary>
    /// Optional: (load sequence, clock base the host picked) → the clock base to use. A run that replays another
    /// run's inputs adopts that run's clock at the area entry (spike B; Stage 3's joiner does the same).
    /// </summary>
    public static Func<int, double, double> ReleaseClock;

    /// <summary>
    /// Load <paramref name="seq"/> ends on <paramref name="frame"/> on every PC, and every PC's game clock reads
    /// <paramref name="timeBase"/> from that frame on.
    /// </summary>
    private void ScheduleLoadRelease(int seq, int frame, double timeBase)
    {
        if (!Engine.Running) return;
        if (ReleaseClock != null) timeBase = ReleaseClock(seq, timeBase);
        LoadReleaseScheduled?.Invoke(seq, frame);
        _releaseFrames[frame] = seq;
        if (frame < Engine.Frame)
        {
            LoadBarrierLateReleases++;
            Plugin.Log.LogError($"[barrier] load {seq}: release frame {frame} already passed (now f{Engine.Frame})");
        }
        Engine.Schedule(frame, () =>
        {
            if (_heldSeq != seq)
            {
                Plugin.Log.LogError($"[barrier] load {seq}: release at f{frame} but this PC holds load {_heldSeq}");
                return;
            }
            _heldSeq = -1;
            _releaseFrames.Remove(frame);
            Plugin.Log.LogInfo($"[barrier] load {seq} released at f{frame} (held {frame - _heldSince} frames)");
            GameClock.SetBase(timeBase, frame + Engine.RngFrameOffset);
            LoadBarrier.Release();
            // Every PC autosaves at the end of this frame: a later joiner can catch up from here (Stage 3).
            _entryFrame = frame;
            if (IsHost) _area.OnLoadReleased(seq, frame, timeBase);
            if (_reloadSpawn != null)
            {
                // The game re-forms the followers around the leader as the level starts; put them back after it.
                var spawn = _reloadSpawn;
                _reloadSpawn = null;
                Engine.Schedule(frame + 2, () => Plugin.Log.LogInfo($"[dropin] f{frame + 2} restored positions: {GameBridge.RestoreParty(spawn)}"));
            }
        });
    }

    // ------------------------------------------------------------------ drop-out

    /// <summary>
    /// Slot leaves on <paramref name="frame"/> on this peer (every peer does the same). <paramref name="fromInput"/>:
    /// the leave comes from synced input (the pause menu), which a Stage 3 replay reproduces by itself; any other
    /// leave goes into the area log for joiners to replay.
    /// </summary>
    private void ApplySlotLeave(int slot, int frame, bool fromInput = false)
    {
        if (slot <= 0 || !Engine.Running) return;
        if (IsHost && !fromInput) _area.LogRoster(frame, slot, false);
        Engine.SetLeaveFrame(slot, frame);
        Engine.Schedule(frame, () => { GameBridge.MakeLeave(slot); SlotPaused[slot] = false; });
        Plugin.Log.LogInfo($"[session] slot {slot} leaves at frame {frame}");
    }

    /// <summary>
    /// "Leave session" chosen in the (synced) pause menu: runs on every peer on the same frame. The leaving player's
    /// inputs already sent up to frame + Delay are still used; from the frame after, the slot is empty everywhere.
    /// </summary>
    public void LeaveSlotDeterministic(int slot)
    {
        if (Phase != Phase.Running || slot <= 0) return;
        int frame = Engine.AppliedFrame + Engine.Delay + 1;
        ApplySlotLeave(slot, frame, fromInput: true);
        if (IsHost)
        {
            var peer = _peers.Values.FirstOrDefault(p => p.Slot == slot);
            if (peer != null) peer.LeavingAt = frame;
        }
    }

    // ------------------------------------------------------------------ Stage 1: co-op from the host's own game

    private bool _hadGuests, _resyncing;
    private double _lastUpdate;
    private double _nextAutoOpen;

    /// <summary>"Open to friends": a Steam lobby is open whenever this PC plays its own game, no hosting step.</summary>
    private void AutoOpen()
    {
        if (InSession || UsingUdp || !Settings.OpenToFriends) return;
        if (Now < _nextAutoOpen || !GameBridge.Booted || GameBridge.AtTitleScreen || GameBridge.Party?.Leader == null) return;
        _nextAutoOpen = Now + 15; // Steam unavailable or a failed lobby: try again later, not every frame
        Plugin.Log.LogInfo("[session] open to friends: opening a lobby");
        Host();
    }

    /// <summary>The host plays its own game (not the title screen) with no lockstep running.</summary>
    private bool HostInGameplay => IsHost && Phase == Phase.Lobby && GameBridge.Booted && !GameBridge.AtTitleScreen
        && !GameBridge.LoadingLevel && GameBridge.Party?.Leader != null;

    /// <summary>
    /// Host. Solo to co-op: a friend connected while the host plays its own game, so lockstep starts right there with
    /// the host alone, and the friend is queued like any joiner (drops in at the next saveable moment by a reload in
    /// place). Co-op to solo: the last guest left, so lockstep stops and the host keeps playing; the lobby stays open.
    /// </summary>
    private void UpdateSoloCoop()
    {
        if (!IsHost) return;
        // Wait for a few round trips: the first reply after connecting can be very slow (1630 ms on 2026-11-15), and
        // the input delay picked from it stays for the whole session.
        if (HostInGameplay && _peers.Count > 0 && ActionTable.Ready && _peers.Values.All(p => p.RttSamples.Count >= RttForDelay))
            StartFromGameplay();
        if (Phase != Phase.Running) { _hadGuests = false; return; }
        if (_peers.Count > 0) _hadGuests = true;
        else if (_hadGuests) BackToSolo();
    }

    private void StartFromGameplay()
    {
        int seed = TestHarness.TryArg("seed", out var fixedSeed) ? int.Parse(fixedSeed) : new System.Random().Next();
        int delay = ConfiguredDelay > 0 ? ConfiguredDelay : AutoDelay();
        var occupied = new bool[InputOverride.SlotCount];
        occupied[0] = true;
        string names = string.Join(", ", _peers.Values.Select(p => p.Name));
        Plugin.Log.LogInfo($"[session] co-op starts from the host's game ({names} joining, delay {delay})");
        BeginLockstep(seed, delay, occupied, 0, null, NextClockBase(Array.Empty<Peer>()), () => { });
        foreach (var peer in _peers.Values)
        {
            peer.Pending = true;
            peer.Ready = false;
            SendWelcome(peer);
        }
        BroadcastRoster();
        Toast.Show($"{names} is joining. Keep playing.");
    }

    private void BackToSolo()
    {
        _hadGuests = false;
        StopLockstep("the last guest left");
        Status = "Playing solo. Friends can still join.";
        Toast.Show("Everyone left. You keep playing; friends can still join.");
        Plugin.Log.LogInfo("[session] back to solo");
    }

    /// <summary>Host: a guest that re-syncs reached its leave frame. It stays connected and drops in again by a reload.</summary>
    private void RequeueResynced(Peer peer)
    {
        peer.Resyncing = false;
        peer.LeavingAt = -1;
        peer.Pending = true;
        peer.Ready = false;
        peer.ForceStage2 = true;
        peer.CatchUp = CatchUpStep.None;
        peer.LastInputFrame = -1;
        Plugin.Log.LogInfo($"[resync] {peer.Name} left the run at f{Engine.AppliedFrame}; queued to drop in again");
    }

    // ------------------------------------------------------------------ Stage 1: characters

    /// <summary>Host: the character each guest (by Steam id) last played. Given back when they rejoin.</summary>
    private readonly Dictionary<ulong, string> _lastCharacter = new();
    private bool _swapTestDone;

    /// <summary>
    /// Host, every frame of a run: when a guest begins playing (the game hands it the next free character), give it
    /// back the character it played before, if that one is free. Every PC reassigns on the same frame
    /// (<see cref="Msg.AssignCharacter"/>), and the area log is invalidated: a later joiner can't replay it, so it
    /// drops in by a reload.
    /// </summary>
    private void UpdateCharacters()
    {
        if (!IsHost || Phase != Phase.Running) return;
        // Test: at this frame, act as if every guest had just begun playing and had last played the last party member.
        if (TestHarness.TryArg("swapat", out var swapAt) && Engine.AppliedFrame >= int.Parse(swapAt) && !_swapTestDone)
        {
            _swapTestDone = true;
            Plugin.Log.LogInfo($"[test] f{Engine.AppliedFrame} forcing a character check for every guest");
            foreach (var peer in _peers.Values.Where(p => !p.Pending))
            {
                peer.CharacterChecked = false;
                _lastCharacter[peer.Id] = GameBridge.LastPartyCharacter();
            }
        }
        foreach (var peer in _peers.Values)
        {
            if (peer.Pending || peer.LeavingAt >= 0) continue;
            if (!GameBridge.IsPlaying(peer.Slot)) { peer.CharacterChecked = false; continue; }
            string now = GameBridge.CharacterOf(peer.Slot);
            if (string.IsNullOrEmpty(now)) continue;
            if (!peer.CharacterChecked)
            {
                peer.CharacterChecked = true;
                if (TestHarness.TryArg("rememberchar", out var test) && !_lastCharacter.ContainsKey(peer.Id))
                    _lastCharacter[peer.Id] = test == "last" ? GameBridge.LastPartyCharacter() : test;
                if (_lastCharacter.TryGetValue(peer.Id, out var before) && !string.IsNullOrEmpty(before) && before != now
                    && GameBridge.CanAssignCharacter(peer.Slot, before))
                {
                    int at = FirstSafeFrame;
                    Plugin.Log.LogInfo($"[characters] {peer.Name} played {before} before (has {now}): giving it back at f{at}");
                    foreach (var other in _peers.Values)
                    {
                        if (other.Pending) continue;
                        var (w, ms) = Protocol.Begin(Msg.AssignCharacter);
                        w.Write(at);
                        w.Write((byte)peer.Slot);
                        w.Write(before);
                        Send(other.Id, ms, true);
                    }
                    int slot = peer.Slot;
                    Engine.Schedule(at, () => GameBridge.AssignCharacter(slot, before));
                    _area.Invalidate("a character was reassigned");
                    // A joiner replaying the log would miss it: it drops in by a reload instead.
                    foreach (var p in _peers.Values.Where(p => p.Pending && p.CatchUp != CatchUpStep.None).ToList())
                        CancelCatchUp(p, "a character was reassigned");
                    continue; // remembered again once the swap shows
                }
            }
            _lastCharacter[peer.Id] = now;
        }
    }

    // ------------------------------------------------------------------ local pause screen

    /// <summary>This PC's local pause screen opened or closed: tell the others (display only).</summary>
    public void SetLocalPaused(bool paused)
    {
        if (Phase != Phase.Running) return;
        SlotPaused[LocalSlot] = paused;
        if (IsHost) { foreach (var peer in _peers.Values) SendPauseStatus(peer.Id, LocalSlot, paused); }
        else SendPauseStatus(HostId, LocalSlot, paused);
    }

    private void OnPauseStatus(int slot, bool paused)
    {
        if (slot < 0 || slot >= InputOverride.SlotCount || SlotPaused[slot] == paused) return;
        SlotPaused[slot] = paused;
        Plugin.Log.LogInfo($"[localpause] f{Engine.AppliedFrame} P{slot + 1} {(paused ? "paused" : "resumed")} on their PC");
        if (IsHost)
            foreach (var peer in _peers.Values)
                if (peer.Slot != slot) SendPauseStatus(peer.Id, slot, paused);
    }

    private void SendPauseStatus(ulong to, int slot, bool paused)
    {
        var (w, ms) = Protocol.Begin(Msg.PauseStatus);
        w.Write((byte)slot);
        w.Write(paused);
        Send(to, ms, true);
    }

    /// <summary>
    /// The first frame the host can schedule for every PC: no guest has run it when the message arrives. A guest
    /// can't be past the frames whose System input the host has sent (up to AppliedFrame + Delay + 1), and reliable
    /// ordering delivers the message before anything the host sends later.
    /// </summary>
    private int FirstSafeFrame => Engine.AppliedFrame + Engine.Delay + 2;

    /// <summary>Guest: "Leave session" in the local pause screen. The host picks the frame (deterministic for everyone).</summary>
    public void RequestLeave()
    {
        if (IsHost || Phase != Phase.Running) return;
        Plugin.Log.LogInfo($"[localpause] f{Engine.AppliedFrame} asking the host to leave");
        var (w, ms) = Protocol.Begin(Msg.LeaveRequest);
        Send(HostId, ms, true);
    }

    /// <summary>Host: a guest asked to leave. Same as a drop-out, on a frame every PC can still schedule.</summary>
    private void LeaveScheduled(Peer peer)
    {
        if (peer.LeavingAt >= 0 || !Engine.Occupied[peer.Slot]) return;
        int frame = Math.Max(FirstSafeFrame, peer.LastInputFrame + 1);
        peer.LeavingAt = frame;
        ApplySlotLeave(peer.Slot, frame);
        foreach (var other in _peers.Values)
        {
            var (w, ms) = Protocol.Begin(Msg.SlotLeft);
            w.Write(peer.Slot);
            w.Write(frame);
            Send(other.Id, ms, true);
        }
    }

    /// <summary>Host: "End session" in the local pause screen. Every PC stops lockstep on the same frame.</summary>
    public void EndSessionScheduled(string reason)
    {
        if (!IsHost || Phase != Phase.Running) return;
        int frame = FirstSafeFrame;
        Plugin.Log.LogInfo($"[localpause] ending the session at f{frame}");
        foreach (var peer in _peers.Values)
        {
            if (peer.Pending)
            {
                // Not in the live run yet: nothing to keep in step with.
                var (ws, mss) = Protocol.Begin(Msg.Stop);
                ws.Write(reason);
                Send(peer.Id, mss, true);
                continue;
            }
            var (w, ms) = Protocol.Begin(Msg.EndAt);
            w.Write(frame);
            w.Write(reason);
            Send(peer.Id, ms, true);
        }
        Engine.EndAt(frame, () => EndSessionDeterministic(reason));
    }

    /// <summary>Our own slot reached its leave frame: keep playing solo from here.</summary>
    private void OnLeftSession()
    {
        if (_resyncing)
        {
            // Not leaving: the host puts us back in the queue and we drop in again with its next autosave.
            AfterLockstep();
            WaitingForResync = true;
            Status = "Re-syncing: waiting for the host's save…";
            Plugin.Log.LogInfo("[resync] left the run; waiting to drop in again");
            return;
        }
        Toast.Show("You left the co-op session. You keep playing on your own (saves go to the session copy).");
        AfterLockstep();
        Leave();
    }

    /// <summary>Silence after which the host drops a guest that holds up the simulation.</summary>
    public const int SilentPeerDropMs = 2000;

    /// <summary>
    /// The engine is waiting for input. Host: drop a missing guest at once if the transport reports the connection
    /// lost, otherwise after <see cref="SilentPeerDropMs"/> of silence; the others continue without them.
    /// Guest: stop at once if the connection to the host is lost.
    /// </summary>
    private void OnStalled(List<int> missing, long waitedMs)
    {
        if (!IsHost)
        {
            if (Transport != null && Transport.IsLost(HostId))
            {
                SetError("Lost connection to the host.");
                StopLockstep("host lost");
            }
            return;
        }
        foreach (int slot in missing)
        {
            var peer = _peers.Values.FirstOrDefault(p => p.Slot == slot && !p.Pending);
            if (peer == null) continue;
            bool lost = Transport != null && Transport.IsLost(peer.Id);
            if (lost || waitedMs >= SilentPeerDropMs)
            {
                Plugin.Log.LogInfo($"[session] dropping slot {slot} after {waitedMs} ms ({(lost ? "connection lost" : "silent")})");
                DropPeer(peer.Id, "lost connection");
            }
        }
    }

    // ------------------------------------------------------------------ drop-in

    /// <summary>
    /// Host, every frame. Drop-in without the title screen, while lockstep keeps running:
    /// <list type="number">
    /// <item>Once the game has allowed saving for half a second, every PC in the session autosaves on an agreed frame.</item>
    /// <item>The host sends its save folder to the joiners, who load it into their mirror and report ready.</item>
    /// <item>The host picks a join frame a few frames ahead and sends it, with where the party stands now, to
    /// everyone. Joiners start their lockstep run on that frame, with the inputs the host has buffered for it.</item>
    /// <item>On the join frame every PC, joiners included, loads the autosave in place behind the game's fade, the
    /// leader spawning where it stood. The loading barrier ends the load on one frame everywhere, and two frames
    /// later every party member is put back on its own position.</item>
    /// </list>
    /// Progress made between the autosave and the join frame (a second or two) is undone, except positions.
    /// </summary>
    private void UpdateDropIn()
    {
        if (!IsHost) return;
        if (Phase != Phase.Running) { _dropIn = DropInStep.None; _joiners.Clear(); _saveableFrames = 0; _lastBattleFrame = int.MinValue / 2; return; }
        _joiners.RemoveAll(id => !_peers.ContainsKey(id));
        if (GameBridge.InBattle || GameBridge.InLevelUp) _lastBattleFrame = Engine.AppliedFrame;
        switch (_dropIn)
        {
            case DropInStep.None:
            {
                if (!HasStage2Pending) { _saveableFrames = 0; return; }
                // Free gameplay where the game itself allows saving (no battle, cutscene or loading), and no battle
                // for a few seconds (battles in rounds).
                bool settled = Engine.AppliedFrame - _lastBattleFrame >= BattleSettleFrames;
                _saveableFrames = GameBridge.CanSaveNow && settled ? _saveableFrames + 1 : 0;
                if (_saveableFrames < 30) return;
                _saveableFrames = 0;
                // A joiner catching up would miss this reload: it comes in with it instead.
                foreach (var p in _peers.Values.Where(p => p.Pending && p.CatchUp != CatchUpStep.None)) CancelCatchUp(p, "a reload in place is starting");
                _joiners.Clear();
                _joiners.AddRange(_peers.Values.Where(p => p.Pending && Now >= p.HoldUntil).Select(p => p.Id));
                // Guests can be up to Delay + 1 frames ahead of us; they get this before any input that takes them further.
                _dropInSaveFrame = Engine.AppliedFrame + Engine.Delay + 3;
                _dropInSlot = -1;
                _dropInLevel = GameBridge.CurrentLevelId;
                foreach (var peer in _peers.Values.Where(p => !p.Pending))
                {
                    var (w, ms) = Protocol.Begin(Msg.DropInSave);
                    w.Write(_dropInSaveFrame);
                    Send(peer.Id, ms, true);
                }
                int at = _dropInSaveFrame;
                Engine.Schedule(at, () => SaveForDropIn(at));
                _dropIn = DropInStep.Saving;
                string names = string.Join(", ", _joiners.Select(id => _peers[id].Name));
                Toast.Show($"Joining: {names}…");
                Plugin.Log.LogInfo($"[dropin] {names}: autosave at f{at}");
                break;
            }

            case DropInStep.Saving:
                if (Engine.AppliedFrame < _dropInSaveFrame || GameBridge.Saving) return;
                if (_joiners.Count == 0) { _dropIn = DropInStep.None; return; }
                if (_dropInSlot < 0)
                {
                    // Every PC tried to save on the same frame and failed the same way; start over.
                    Plugin.Log.LogWarning("[dropin] autosave failed, retrying");
                    _dropIn = DropInStep.None;
                    return;
                }
                int files = SendSaveFiles(_joiners.Select(id => _peers[id]), true);
                Plugin.Log.LogInfo($"[dropin] autosave slot {_dropInSlot} done at f{Engine.AppliedFrame}; sent {files} files to {_joiners.Count} joiner(s)");
                _dropIn = DropInStep.Sending;
                break;

            case DropInStep.Sending:
            {
                if (_joiners.Count == 0) { _dropIn = DropInStep.None; return; }
                if (_joiners.Any(id => !_peers[id].Ready)) return;
                // The guests' own autosaves must be written too (spike A: 120 frames is plenty).
                if (Engine.AppliedFrame < _dropInSaveFrame + 120) return;
                if (GameBridge.CurrentLevelId != _dropInLevel)
                {
                    Plugin.Log.LogInfo("[dropin] the party changed area since the autosave; saving again");
                    // Out of the queue: with the new area's log a joiner may now catch up instead (Stage 3).
                    _joiners.Clear();
                    _dropIn = DropInStep.None;
                    return;
                }
                if (!GameBridge.CanSaveNow) return; // a battle or cutscene started: wait for it to end
                if (Engine.AppliedFrame - _dropInSaveFrame > StaleDropInSaveFrames)
                {
                    // The joiners took long (booting, a slow transfer). Reloading this save would undo everything
                    // since; take a fresh one, which a ready joiner applies in a moment.
                    Plugin.Log.LogInfo($"[dropin] autosave from f{_dropInSaveFrame} is stale at f{Engine.AppliedFrame}; saving again");
                    _joiners.Clear();
                    _dropIn = DropInStep.None;
                    _saveableFrames = 30;
                    return;
                }
                JoinNow();
                _dropIn = DropInStep.None;
                break;
            }
        }
    }

    /// <summary>A drop-in autosave older than this (frames) when the joiners are ready is replaced by a fresh one.</summary>
    private const int StaleDropInSaveFrames = 300;

    /// <summary>Every PC in the session, on the agreed frame: autosave for the drop-in.</summary>
    private void SaveForDropIn(int frame)
    {
        int slot = GameBridge.AutoSave();
        if (IsHost) _dropInSlot = slot;
        Plugin.Log.LogInfo($"[dropin] f{frame} autosave -> slot {slot}");
    }

    /// <summary>Host: the joiners have the save. Pick the join frame and tell everyone.</summary>
    private void JoinNow()
    {
        int at = Engine.AppliedFrame + Engine.Delay + 3;
        var spawn = GameBridge.CaptureParty();
        var slots = _joiners.Select(id => _peers[id].Slot).OrderBy(s => s).ToList();
        _area.Invalidate("a player joined");
        // Everyone's clock moves to one base on the join frame; the joiners' clocks came from their own game.
        double timeBase = NextClockBase(_joiners.Select(id => _peers[id]));

        // Everyone already playing: add the slots and reload in place on the join frame.
        foreach (var peer in _peers.Values.Where(p => !p.Pending))
        {
            var (w, ms) = Protocol.Begin(Msg.DropInReload);
            w.Write(at);
            w.Write(_dropInSlot);
            w.Write(slots.Count);
            foreach (int s in slots) w.Write(s);
            w.Write(spawn != null);
            spawn?.Write(w);
            w.Write(timeBase);
            Send(peer.Id, ms, true);
        }

        foreach (int s in slots) Engine.AddSlot(s, at);
        var occupied = new bool[InputOverride.SlotCount];
        for (int s = 0; s < occupied.Length; s++) occupied[s] = Engine.InSessionAt(s, at);

        // Joiners: start lockstep on the join frame, then get every input already buffered for it and later.
        foreach (ulong id in _joiners)
        {
            var peer = _peers[id];
            var (w, ms) = Protocol.Begin(Msg.DropIn);
            w.Write(Engine.Seed);
            w.Write(Engine.Delay);
            w.Write(at);
            foreach (bool o in occupied) w.Write(o);
            foreach (int p in GameBridge.SlotPlayerIndex) w.Write(p);
            w.Write(_dropInSlot);
            w.Write(spawn != null);
            spawn?.Write(w);
            w.Write(timeBase);
            Send(id, ms, true);

            foreach (var (frame, ch) in Engine.BufferedFrom(at).OrderBy(f => f.frame).ToList())
                for (int s = 0; s < InputOverride.SlotCount; s++)
                {
                    if (ch[s] == null || s == peer.Slot) continue;
                    SendInputTo(id, frame, s, ch[s], s == LocalSlot ? ch[InputOverride.SystemChannel] : null);
                }
            peer.Pending = false;
            peer.LastInputFrame = at + Engine.Delay - 1;
        }

        ScheduleReloadInPlace(at, _dropInSlot, spawn, slots, timeBase);
        BroadcastRoster();
        string names = string.Join(", ", _joiners.Select(id => _peers[id].Name));
        Status = $"{names} joined.";
        Plugin.Log.LogInfo($"[dropin] {names} (slots {string.Join(",", slots)}) join at f{at}, reload slot {_dropInSlot}, spawn {spawn}");
        _joiners.Clear();
    }

    /// <summary>Guest dropping in: start lockstep on the join frame and load the autosave in place on it.</summary>
    private void OnDropIn(BinaryReader r)
    {
        int seed = r.ReadInt32();
        int delay = r.ReadInt32();
        int at = r.ReadInt32();
        var occupied = new bool[InputOverride.SlotCount];
        for (int s = 0; s < occupied.Length; s++) occupied[s] = r.ReadBoolean();
        var playerIndex = new int[InputOverride.SlotCount];
        for (int s = 0; s < playerIndex.Length; s++) playerIndex[s] = r.ReadInt32();
        int saveSlot = r.ReadInt32();
        var spawn = r.ReadBoolean() ? PartySpawn.Read(r) : null;
        double timeBase = r.ReadDouble();
        if (Phase == Phase.Running) return;
        _awaitingMirrorReload = false;
        Plugin.Log.LogInfo($"[dropin] joining at f{at}, reload slot {saveSlot}, spawn {spawn}");
        BeginLockstep(seed, delay, occupied, at, playerIndex, double.NaN, () => ReloadInPlace(at, saveSlot, spawn, timeBase));
    }

    /// <summary>Every PC already in the session: on the join frame, register the new slots and reload in place.</summary>
    private void ScheduleReloadInPlace(int at, int saveSlot, PartySpawn spawn, List<int> newSlots, double timeBase)
    {
        // Test: a level load in progress on the join frame (the party walked through a door, say). Once per run.
        if (TestHarness.TryArg("loadatjoin", out _) && !_loadAtJoinDone)
        {
            _loadAtJoinDone = true;
            Engine.Schedule(at - 1, () =>
            {
                Plugin.Log.LogInfo($"[test] f{at - 1} starting a level load before the join frame");
                GameBridge.ReloadInPlace(saveSlot, true, spawn?.Leader, spawn?.Angle ?? -1);
            });
        }
        Engine.Schedule(at, () =>
        {
            // The same state on every PC already in, so they all decide the same way. The joiner reloads anyway; it
            // is told to stop and wait.
            if (GameBridge.DropInBlocker != null)
            {
                PostponeDropIn(at, newSlots);
                return;
            }
            GameBridge.RegisterSlotPlayers(newSlots);
            ReloadInPlace(at, saveSlot, spawn, timeBase);
        });
    }

    private static bool _loadAtJoinDone;

    /// <summary>
    /// Every PC already in, on the join frame: a level load or a battle is running, so the autosave can't be
    /// reloaded now (it may also be of another area). The joining slots leave again on this frame; the host puts the
    /// joiners back in the queue and the drop-in starts over with a fresh autosave.
    /// </summary>
    private void PostponeDropIn(int at, List<int> slots)
    {
        Plugin.Log.LogWarning($"[dropin] f{at} {GameBridge.DropInBlocker ?? "something"} is running: join of slot(s) {string.Join(",", slots)} postponed");
        foreach (int s in slots) Engine.SetLeaveFrame(s, at);
        if (!IsHost) return;
        foreach (var peer in _peers.Values.Where(p => slots.Contains(p.Slot)))
        {
            peer.Pending = true;
            peer.Ready = false;
            peer.LastInputFrame = -1;
            var (w, ms) = Protocol.Begin(Msg.DropInAbort);
            Send(peer.Id, ms, true);
        }
        Toast.Show($"{string.Join(", ", _peers.Values.Where(p => slots.Contains(p.Slot)).Select(p => p.Name))} joins after this.");
    }

    private void ReloadInPlace(int frame, int saveSlot, PartySpawn spawn, double timeBase)
    {
        GameClock.SetBase(timeBase, frame);
        if (GameBridge.Saving) Plugin.Log.LogError($"[dropin] f{frame} still saving");
        if (GameBridge.LoadingLevel) Plugin.Log.LogWarning($"[dropin] f{frame} a level load is in progress");
        // Co-op players are not in the save, and they survive an in-place load: guests already playing would keep
        // theirs while a joiner starts without them. Every PC lets them all leave, and each guest joins again through
        // synced input once the load is released.
        for (int s = 1; s < InputOverride.SlotCount; s++) GameBridge.MakeLeave(s);
        if (LocalSlot > 0)
        {
            _autoJoin = true;
            _autoJoinFrom = frame + 1;
            _nextJoinTry = 0;
            _joinTries = 0;
        }
        // The reload's load is the first of a new count on every PC, joiners included.
        ResetLoadBarrier();
        _reloadSpawn = spawn;
        Plugin.Log.LogInfo($"[dropin] f{frame} reloading autosave {saveSlot} in place");
        if (!GameBridge.ReloadInPlace(saveSlot, true, spawn?.Leader, spawn?.Angle ?? -1))
            SetError("Could not reload the autosave for the drop-in.");
    }

    /// <summary>Starts the auto-join again (after an in-place reload removed the co-op players).</summary>
    public void RearmAutoJoin()
    {
        if (LocalSlot <= 0) return;
        _autoJoin = true;
        _nextJoinTry = 0;
        _joinTries = 0;
    }

    /// <summary>
    /// Guests press the game's own co-op Join for themselves once the game is playable (through synced input, so
    /// every peer sees the same join). Retries until the game reports the player as playing.
    /// </summary>
    private InputSnapshot AutoJoin(int frame, InputSnapshot snap)
    {
        if (!_autoJoin) return snap;
        if (GameBridge.IsPlaying(LocalSlot)) { _autoJoin = false; return snap; }
        if (Engine.AppliedFrame < _autoJoinFrom || GameBridge.LoadingLevel || GameBridge.Party?.Leader == null) return snap;
        if (frame < _nextJoinTry) return snap;
        if (++_joinTries > 12) { _autoJoin = false; Toast.Show("Press Join on your controller to take over a party member."); return snap; }
        _nextJoinTry = frame + 90;
        int join = ActionTable.IndexOf("Join");
        if (join >= 0) snap.Bits[join] |= ActionBits.Button | ActionBits.ButtonDown;
        Plugin.Log.LogInfo($"[session] auto-join press for slot {LocalSlot} at frame {frame}");
        return snap;
    }

    // ------------------------------------------------------------------ catch-up (Stage 3)

    /// <summary>Frames on which a load is released (→ load seq). A frame doesn't run before its load is held here.</summary>
    private readonly Dictionary<int, int> _releaseFrames = new();

    /// <summary>
    /// The engine doesn't advance while this is true:
    /// <list type="bullet">
    /// <item>a joiner's area-entry save is still loading (frame L must be the frame that load ends on);</item>
    /// <item>a load is released on the next frame and hasn't reached its end here yet. Only a joiner replaying
    /// uncapped gets there: everyone else reported the end before the host picked the frame.</item>
    /// </list>
    /// </summary>
    private bool HoldFrame()
    {
        if (_catchUp != null && !_catchUp.Held) return true;
        return _releaseFrames.TryGetValue(Engine.Frame, out int seq) && _heldSeq != seq && GameBridge.LoadingLevel;
    }

    /// <summary>
    /// Host, every frame: joiners that can catch up get the area entry save, then the input log since then, then the
    /// live inputs like everyone else. Nobody already playing waits for them.
    /// </summary>
    private void UpdateCatchUpHost()
    {
        if (!IsHost || Phase != Phase.Running) return;
        double now = Now;
        foreach (var peer in _peers.Values.ToList())
        {
            if (!peer.Pending) continue;
            switch (peer.CatchUp)
            {
                case CatchUpStep.None:
                    if (!WantsCatchUp(peer) || _dropIn != DropInStep.None || _joiners.Contains(peer.Id)) break;
                    peer.CatchUp = CatchUpStep.Files;
                    peer.CatchUpL = _area.L;
                    peer.CatchUpSince = now;
                    int files = SendSaveFiles(new[] { peer }, true, _area.SaveFiles);
                    Status = $"{peer.Name} is catching up…";
                    Toast.Show($"{peer.Name} is joining. Keep playing.");
                    Plugin.Log.LogInfo($"[catchup] {peer.Name}: catching up from the area entry at f{_area.L} ({Engine.AppliedFrame - _area.L} frames ago); sent {files} files");
                    break;

                case CatchUpStep.Files:
                    if (!_area.Valid || _area.L != peer.CatchUpL)
                    {
                        // A new area since the files were sent (or the log became unusable): start over from what is there now.
                        Plugin.Log.LogInfo($"[catchup] {peer.Name}: area log changed ({_area.InvalidReason}); starting over");
                        peer.CatchUp = CatchUpStep.None;
                        break;
                    }
                    if (!peer.Ready)
                    {
                        if (now - peer.CatchUpSince > CatchUpTimeoutS) CancelCatchUp(peer, "the save transfer took too long");
                        break;
                    }
                    SendCatchUp(peer);
                    peer.CatchUp = CatchUpStep.Running;
                    break;

                case CatchUpStep.Running:
                    if (now - peer.CatchUpSince > CatchUpTimeoutS) CancelCatchUp(peer, "catching up took too long");
                    break;
            }
        }
    }

    /// <summary>A catch-up that hasn't merged after this long (seconds, from the save transfer) falls back to Stage 2.</summary>
    private const double CatchUpTimeoutS = 120;

    /// <summary>Host: stop a joiner's catch-up; it drops in by a reload in place (Stage 2) instead.</summary>
    private void CancelCatchUp(Peer peer, string why)
    {
        Plugin.Log.LogInfo($"[catchup] {peer.Name}: cancelled, {why}");
        if (peer.CatchUp == CatchUpStep.Running)
        {
            var (w, ms) = Protocol.Begin(Msg.DropInAbort);
            Send(peer.Id, ms, true);
        }
        peer.CatchUp = CatchUpStep.None;
        peer.ForceStage2 = true;
        peer.Ready = false;
    }

    /// <summary>Host: the joiner has the area entry save. Send it where to start, and every input since.</summary>
    private void SendCatchUp(Peer peer)
    {
        var (w, ms) = Protocol.Begin(Msg.CatchUp);
        w.Write(Engine.Seed);
        w.Write(Engine.Delay);
        w.Write(_area.L);
        w.Write(_area.Seq);
        w.Write(_area.ClockBase);
        foreach (bool o in _area.Occupied) w.Write(o);
        foreach (int p in _area.PlayerIndex) w.Write(p);
        w.Write(_area.Playing.Count);
        foreach (int s in _area.Playing) w.Write(s);
        w.Write(_area.SaveSlot);
        _area.Party.Write(w);
        w.Write(Engine.AppliedFrame);
        _area.WriteRoster(w);
        Send(peer.Id, ms, true);

        int blocks = 0;
        for (int f = _area.L; f <= _area.LastFrame; f += InputLogBlock)
        {
            var (bw, bms) = Protocol.Begin(Msg.InputLog);
            _area.WriteFrames(bw, f, Math.Min(InputLogBlock, _area.LastFrame - f + 1));
            Send(peer.Id, bms, true);
            blocks++;
        }
        // The host's state along the way, so the joiner finds a divergence while it replays, not after it joined.
        foreach (var snap in _area.Snapshots)
        {
            var (sw, sms) = Protocol.Begin(Msg.Snapshot);
            snap.Write(sw);
            Send(peer.Id, sms, true);
        }
        // Frames not applied yet: whatever input has arrived for them. Later input is relayed to the joiner as usual.
        foreach (var (frame, ch) in Engine.BufferedFrom(Engine.AppliedFrame + 1).OrderBy(f => f.frame).ToList())
            for (int s = 0; s < InputOverride.SlotCount; s++)
            {
                if (ch[s] == null || s == peer.Slot) continue;
                SendInputTo(peer.Id, frame, s, ch[s], s == LocalSlot ? ch[InputOverride.SystemChannel] : null);
            }
        Plugin.Log.LogInfo($"[catchup] {peer.Name}: start at f{_area.L}, {_area.FrameCount} logged frames in {blocks} blocks, {_area.Snapshots.Count} snapshots, {_area.Roster.Count} roster changes, live at f{Engine.AppliedFrame}");
    }

    /// <summary>Frames per input log message.</summary>
    private const int InputLogBlock = 600;

    /// <summary>Host: the joiner replayed up to the live session. It joins on a frame a little ahead, on every PC.</summary>
    private void MergeCaughtUp(Peer peer, int joinerFrame)
    {
        int at = Engine.AppliedFrame + Engine.Delay + 3;
        // Joiners already replaying get it live; the ones not started yet get it with the area log.
        foreach (var p in _peers.Values.Where(p => !p.Pending || p == peer || p.CatchUp == CatchUpStep.Running))
        {
            var (w, ms) = Protocol.Begin(Msg.Merge);
            w.Write(at);
            w.Write(peer.Slot);
            Send(p.Id, ms, true);
        }
        Engine.AddSlot(peer.Slot, at);
        int slot = peer.Slot;
        Engine.Schedule(at, () => GameBridge.RegisterSlotPlayers(new[] { slot }));
        peer.Pending = false;
        peer.CatchUp = CatchUpStep.None;
        peer.LastInputFrame = at + Engine.Delay - 1;
        _area.LogRoster(at, slot, true);
        BroadcastRoster();
        Status = $"{peer.Name} joined.";
        Toast.Show($"{peer.Name} joined.");
        Plugin.Log.LogInfo($"[catchup] {peer.Name} caught up (at f{joinerFrame}, {Now - peer.CatchUpSince:F1} s after the transfer started); joins as slot {slot} at f{at}");
    }

    /// <summary>
    /// Joiner: start lockstep on the area entry frame L, behind the session. The area entry save loads first (the
    /// engine waits); then the logged inputs replay as fast as this PC can go, and the live ones follow.
    /// </summary>
    private void OnCatchUp(BinaryReader r)
    {
        int seed = r.ReadInt32();
        int delay = r.ReadInt32();
        int L = r.ReadInt32();
        int seq = r.ReadInt32();
        double clockBase = r.ReadDouble();
        var occupied = new bool[InputOverride.SlotCount];
        for (int s = 0; s < occupied.Length; s++) occupied[s] = r.ReadBoolean();
        var playerIndex = new int[InputOverride.SlotCount];
        for (int s = 0; s < playerIndex.Length; s++) playerIndex[s] = r.ReadInt32();
        var playing = new List<int>();
        for (int n = r.ReadInt32(); n > 0; n--) playing.Add(r.ReadInt32());
        int saveSlot = r.ReadInt32();
        var party = PartySpawn.Read(r);
        int live = r.ReadInt32();
        var roster = AreaLog.ReadRoster(r);
        if (Phase == Phase.Running) return;
        _awaitingMirrorReload = false;
        Plugin.Log.LogInfo($"[catchup] start at f{L} (load {seq}, clock {clockBase:R}), {live - L} frames behind; save slot {saveSlot}, playing {string.Join(",", playing.Select(s => $"P{s + 1}"))}, party {party}");

        BeginLockstep(seed, delay, occupied, L, playerIndex, double.NaN, () => { });
        _autoJoin = false; // not in the session until the merge
        // Joins and leaves since L, on their frames, as the session had them (before any logged input arrives).
        foreach (var (frame, slot, join) in roster)
        {
            int s = slot;
            if (join)
            {
                Engine.AddSlot(s, frame);
                Engine.Schedule(frame, () => GameBridge.RegisterSlotPlayers(new[] { s }));
            }
            else
            {
                Engine.SetLeaveFrame(s, frame);
                Engine.Schedule(frame, () => GameBridge.MakeLeave(s));
            }
            Plugin.Log.LogInfo($"[catchup] replaying roster change: P{s + 1} {(join ? "joins" : "leaves")} at f{frame}");
        }
        _catchUp = new CatchUpState { L = L, Seq = seq, Party = party, Playing = playing, Since = Now, Total = live - L };
        // The save's load is load <seq> of the session's count; it ends on frame L, as it did for everyone.
        _loadSeq = seq - 1;
        ScheduleLoadRelease(seq, L, clockBase);
        Status = "Catching up with the party…";
        if (!GameBridge.ReloadInPlace(saveSlot, true, party.Leader, party.Angle))
        {
            var (w, ms) = Protocol.Begin(Msg.CatchUpFailed);
            w.Write("could not load the area entry save");
            Send(HostId, ms, true);
            StopLockstep("catch-up failed");
            WaitingForResync = true;
        }
    }

    /// <summary>Joiner, on frame L after the load's release: what the save doesn't hold, as on the host then.</summary>
    private void CatchUpEntry(int frame)
    {
        string moved = GameBridge.RestoreParty(_catchUp.Party);
        foreach (int s in _catchUp.Playing)
        {
            var p = GameBridge.CoopPlayer(s);
            try { if (p != null && !p.Playing) p.Join(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[catchup] Join P{s + 1} failed: {e.Message}"); }
        }
        Plugin.Log.LogInfo($"[catchup] f{frame} entry: positions {moved}; co-op players {string.Join(",", _catchUp.Playing.Select(s => $"P{s + 1}={GameBridge.IsPlaying(s)}"))}");
    }

    /// <summary>Joiner, every frame: fast-forward while behind; tell the host once this PC runs with the session.</summary>
    private void UpdateCatchUpJoiner()
    {
        var c = _catchUp;
        if (c == null || !c.Held || !Engine.Running) return;
        int behind = Engine.NewestKnownFrame - Engine.AppliedFrame;
        if (!c.Live && behind > 30 && !c.Ff) StartCatchUpFastForward();
        if (c.Ff) KeepCamerasOff();
        double now = Now;
        if (c.RateFrom < 0) { c.RateFrom = Engine.AppliedFrame; c.RateSince = now; }
        else if (now - c.RateSince >= 0.5)
        {
            double rate = (Engine.AppliedFrame - c.RateFrom) / (now - c.RateSince);
            c.Rate = c.Rate <= 0 ? rate : 0.5 * c.Rate + 0.5 * rate;
            c.RateFrom = Engine.AppliedFrame;
            c.RateSince = now;
        }
        if (c.Live) return;
        // Caught up: no more than the input delay (plus a frame or two in flight) behind the host, and the last check
        // against the host's state agreed.
        if (behind <= Engine.Delay + 2 && Sync.DesyncStreak == 0) c.LiveChecks++;
        else c.LiveChecks = 0;
        if (c.LiveChecks < 30) return;
        c.Live = true;
        EndCatchUpFastForward();
        Plugin.Log.LogInfo($"[catchup] caught up at f{Engine.AppliedFrame} after {now - c.Since:F1} s ({Sync.Checks} state checks, {Sync.Corrections} corrections)");
        var (w, ms) = Protocol.Begin(Msg.CaughtUp);
        w.Write(Engine.AppliedFrame);
        Send(HostId, ms, true);
    }

    private readonly List<Camera> _ffCameras = new();
    private int _ffCameraCheck;

    /// <summary>Uncapped frame rate, no rendering, no sound (spike C: about 16× real time).</summary>
    private void StartCatchUpFastForward()
    {
        if (_catchUp == null || _catchUp.Ff) return;
        _catchUp.Ff = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        try { AkSoundEngine.SetOutputVolume(0, 0f); } catch (Exception e) { Plugin.Log.LogWarning($"[catchup] mute failed: {e.Message}"); }
        _ffCameraCheck = 0;
        KeepCamerasOff();
        Plugin.Log.LogInfo($"[catchup] fast-forwarding from f{Engine.AppliedFrame}");
    }

    private void KeepCamerasOff()
    {
        // Levels loaded during the replay bring their own cameras.
        if (_ffCameraCheck-- > 0) return;
        _ffCameraCheck = 30;
        foreach (var cam in Camera.allCameras)
            if (cam != null && cam.enabled && cam != CatchUpScreen.Camera) { cam.enabled = false; _ffCameras.Add(cam); }
    }

    private void EndCatchUpFastForward()
    {
        if (_catchUp == null || !_catchUp.Ff) return;
        _catchUp.Ff = false;
        foreach (var cam in _ffCameras) if (cam != null) cam.enabled = true;
        _ffCameras.Clear();
        Application.targetFrameRate = 60; // lockstep's cap (GameBridge.LockTime)
        try { AkSoundEngine.SetOutputVolume(0, 1f); } catch { }
        Plugin.Log.LogInfo($"[catchup] fast-forward off at f{Engine.AppliedFrame}");
    }

    // ------------------------------------------------------------------ helpers

    private void Send(ulong to, MemoryStream ms, bool reliable)
    {
        if (to == 0 || Transport == null) return;
        Transport.Send(to, ms.GetBuffer(), (int)ms.Length, reliable);
    }

    private void SetError(string message)
    {
        LastError = message;
        Status = message;
        Toast.Show(message);
        Plugin.Log.LogWarning("[session] " + message);
    }

    private void Reset()
    {
        _peers.Clear();
        Role = Role.None;
        Phase = Phase.Idle;
        LobbyId = HostId = 0;
        _pendingCall = 0;
        _nextHello = _nextPing = _nextLobbyPoll = 0;
        _awaitingMirrorReload = false;
        _dropIn = DropInStep.None;
        _joiners.Clear();
        _saveableFrames = 0;
        _lastBattleFrame = int.MinValue / 2;
        _reloadSpawn = null;
        _readyAnywhere = false;
        _autoJoin = false;
        WaitingForResync = false;
        LocalSlot = 0;
        Array.Clear(SlotPeer);
        Array.Clear(SlotName);
        Array.Clear(SlotPaused);
        LocalPause.Reset();
        if (!UsingUdp && SteamApi.Available)
        {
            SteamApi.SetRichPresence("status", "");
            SteamApi.SetRichPresence("connect", "");
        }
    }
}
