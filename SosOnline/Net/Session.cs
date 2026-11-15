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
        public float RttMs;
        public double LastHeard;
        public bool Ready;
        /// <summary>Joined while a session was running: waits for the next resync (area change).</summary>
        public bool Pending;
        /// <summary>Last lockstep frame we received this guest's input for.</summary>
        public int LastInputFrame = -1;
        /// <summary>Leaving at this frame (deterministic leave); removed once the session is past it.</summary>
        public int LeavingAt = -1;
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

    /// <summary>Guest only: we were admitted mid-session and wait for the next area change.</summary>
    public bool WaitingForResync { get; private set; }

    // Drop-in: the session restarts from a fresh autosave at the next area change.
    private enum ResyncStep { None, Saving, ToTitle }
    private ResyncStep _resync;
    private int _resyncSlot = -1;
    private double _resyncAt;
    private string _pendingSinceLevel;
    private bool _areaChanged;
    private int _saveableFrames;
    private bool _resyncRequested;

    // Joining the game itself (the co-op Join button) after the session starts.
    private bool _autoJoin;
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
        _transferId++;
        var files = SaveMirror.PackHostFiles();
        foreach (var peer in _peers.Values)
        {
            peer.Ready = false;
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
            Send(peer.Id, ms2, true);
        }
        Phase = Phase.AwaitingReady;
        Status = $"Sending save data ({files.Count} files)…";
        Plugin.Log.LogInfo($"[session] start requested from slot {slotIndex} auto={autoSave}, {files.Count} files to {_peers.Count} guest(s)");
        TryBeginLockstep();
    }

    private void TryBeginLockstep()
    {
        if (Phase != Phase.AwaitingReady || _peers.Values.Any(p => !p.Ready)) return;

        int seed = new System.Random().Next();
        int delay = ConfiguredDelay > 0 ? ConfiguredDelay : AutoDelay();
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
            Send(peer.Id, ms, true);
        }
        BeginLockstep(seed, delay, _startSlot, _startAuto, occupied);
    }

    private int AutoDelay()
    {
        // One-way latency plus a little jitter headroom, in 60 Hz frames.
        float rtt = _peers.Count == 0 ? 0 : _peers.Values.Max(p => p.RttMs);
        int frames = (int)Math.Ceiling((rtt * 0.5f + 25f) / (1000f / 60f));
        return Math.Clamp(frames, 2, 15);
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

        if (!InSession) return;
        PollCallResults();
        if (!Pump()) return;

        double now = Now;
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

        UpdateDropIn();
        if (IsHost && Phase == Phase.Running) TryReleaseLoad();
        if (IsHost && Phase == Phase.Running)
            foreach (var peer in _peers.Values.Where(p => p.LeavingAt >= 0 && Engine.AppliedFrame >= p.LeavingAt).ToList())
                DropPeer(peer.Id, "left the session");

        // Drop silent peers (not during lockstep: the engine has its own stall timeout).
        if (Phase != Phase.Running)
            foreach (var peer in _peers.Values.ToList())
                if (now - peer.LastHeard > 20) DropPeer(peer.Id, "timed out");

        // Ready only once the host's saves are loaded AND we're on the title screen: everyone must enter the game
        // from the same place on the same frame.
        if (_awaitingMirrorReload && SaveMirror.ReloadDone && !GameBridge.AtTitleScreen)
            Status = "Save data received. Return to the title screen to start!";
        if (_awaitingMirrorReload && SaveMirror.ReloadDone && GameBridge.AtTitleScreen)
        {
            _awaitingMirrorReload = false;
            var (w, ms) = Protocol.Begin(Msg.Ready);
            w.Write(_transferId);
            Send(HostId, ms, true);
            Status = "Save data received. Waiting for host…";
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
        w.Write(Plugin.Version);
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
                if (proto != Protocol.Version) reason = $"mod protocol mismatch (host {Protocol.Version}, you {proto})";
                else if (gameVersion != (Application.version ?? "")) reason = $"game version mismatch (host {Application.version}, you {gameVersion})";
                else if (ActionTable.Ready && actionHash != 0 && actionHash != ActionTable.Hash) reason = "input configuration mismatch";
                else if (Phase == Phase.AwaitingReady || _resync != ResyncStep.None) reason = "the session is starting, try again in a moment";
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
                if (peer.Pending)
                {
                    if (_pendingSinceLevel == null) { _pendingSinceLevel = GameBridge.CurrentLevelId; _areaChanged = false; }
                    Status = $"{name} will join at the next area change (player {slot + 1}).";
                }
                else Status = $"{name} joined (player {slot + 1}).";
                Toast.Show(Status);
                Plugin.Log.LogInfo($"[session] {name} joined as slot {slot} (mod {modVersion})");
                break;
            }
            case Msg.Ping: Pong(from, r); break;
            case Msg.Pong: OnPong(peer, r); break;
            case Msg.Ready:
                if (peer == null) return;
                if (r.ReadInt32() == _transferId) peer.Ready = true;
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
                if (peer == null) return;
                int seq = r.ReadInt32();
                int at = r.ReadInt32();
                ReportLoaded(peer.Slot, seq, at);
                break;
            }
            case Msg.Hash:
                // Guests also run the comparison themselves; this is for the host's log.
                break;
            case Msg.Bye:
                if (peer != null) DropPeer(from, "left");
                break;
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
                if (Phase == Phase.Connecting)
                {
                    Phase = Phase.Lobby;
                    Status = WaitingForResync
                        ? $"Connected as player {LocalSlot + 1}. You'll enter the game at the party's next area change."
                        : $"Connected as player {LocalSlot + 1}. Waiting for the host to start…";
                    Toast.Show(WaitingForResync
                        ? $"Joined {SlotName[0] ?? Transport.Describe(HostId)}'s game. You'll drop in at their next area change."
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
                if (id != _transferId) { _transferId = id; SaveMirror.BeginReceive(); _receivedFiles = 0; }
                if (!GameBridge.AtTitleScreen) Status = "Host is starting. Go to the title screen!";
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
                BeginLockstep(seed, delay, slot, auto, occupied);
                break;
            }
            case Msg.Input:
                ReadInput(r, out int frame, out int fslot, out var input, out var system);
                Engine.OnRemoteInput(frame, fslot, input, system);
                break;
            case Msg.Snapshot:
                Sync.OnHostSnapshot(StateSnapshot.Read(r));
                break;
            case Msg.LoadRelease:
            {
                int seq = r.ReadInt32();
                int at = r.ReadInt32();
                ScheduleLoadRelease(seq, at);
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
            case Msg.Resync:
                WaitingForResync = false;
                if (Phase == Phase.Running) StopLockstep("resync");
                GameBridge.ReturnToTitle();
                Status = "A player is joining: everyone reloads from the host's autosave…";
                Toast.Show(r.ReadString());
                break;
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
        peer.RttMs = peer.RttMs <= 0 ? rtt : peer.RttMs * 0.8f + rtt * 0.2f;
    }

    private void DropPeer(ulong id, string why)
    {
        if (!_peers.Remove(id, out var peer)) return;
        Plugin.Log.LogInfo($"[session] {peer.Name} {why}");
        if (IsHost)
        {
            if (peer.Slot > 0) { SlotPeer[peer.Slot] = 0; SlotName[peer.Slot] = null; }
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
            if (!HasPending) _pendingSinceLevel = null;
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

    private void BeginLockstep(int seed, int delay, int saveSlot, bool autoSave, bool[] occupied)
    {
        GameBridge.LockTime();
        Application.runInBackground = true;
        GameBridge.ResetSlotPlayers();
        ControllerRouting.RouteLocalDevicesTo(GameBridge.PlayerForSlot(LocalSlot));

        Sync.Reset();
        ResetLoadBarrier();
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
            return TestHarness.Filter != null ? TestHarness.Filter(frame, channel, snap) : snap;
        };
        Engine.Stalled = OnStalled;
        Engine.LeftSession = OnLeftSession;
        _autoJoin = LocalSlot > 0;
        _nextJoinTry = 0;
        _joinTries = 0;
        WaitingForResync = false;
        Engine.Start(LocalSlot, IsHost, occupied, delay, seed);
        Engine.Schedule(0, () =>
        {
            var slots = new List<int>();
            for (int s = 0; s < occupied.Length; s++) if (occupied[s]) slots.Add(s);
            GameBridge.RegisterSlotPlayers(slots);
            GameBridge.EnterGameFromSlot(saveSlot, autoSave);
        });
        Phase = Phase.Running;
        Status = "Co-op running.";
        Toast.Show(LocalSlot > 0 ? "Co-op session started. Joining the party…" : "Co-op session started.");
    }

    private void StopLockstep(string reason)
    {
        Engine.Stop(reason);
        AfterLockstep();
        Status = "Co-op stopped: " + reason;
    }

    private void AfterLockstep()
    {
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
        var snap = Sync.OnFrame(frame);
        if (snap != null)
        {
            TestHarness.OnSnapshot(snap);
            if (IsHost)
                foreach (var peer in _peers.Values)
                {
                    var (w, ms) = Protocol.Begin(Msg.Snapshot);
                    snap.Write(w);
                    Send(peer.Id, ms, true);
                }
        }
        if (Sync.Desynced && _desyncNotifiedFrame < 0)
        {
            _desyncNotifiedFrame = frame;
            SetError("Desync detected: " + Sync.LastDesyncReason);
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
            Plugin.Log.LogInfo($"[barrier] load {seq}: reached at {string.Join(", ", bySlot.OrderBy(kv => kv.Key).Select(kv => $"P{kv.Key + 1} f{kv.Value}"))}; release at f{release}");
            foreach (var peer in _peers.Values.Where(p => !p.Pending))
            {
                var (w, ms) = Protocol.Begin(Msg.LoadRelease);
                w.Write(seq);
                w.Write(release);
                Send(peer.Id, ms, true);
            }
            _loadReports.Remove(seq);
            ScheduleLoadRelease(seq, release);
        }
    }

    private void ScheduleLoadRelease(int seq, int frame)
    {
        if (!Engine.Running) return;
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
            Plugin.Log.LogInfo($"[barrier] load {seq} released at f{frame} (held {frame - _heldSince} frames)");
            LoadBarrier.Release();
        });
    }

    // ------------------------------------------------------------------ drop-out

    /// <summary>Slot leaves on <paramref name="frame"/> on this peer (every peer does the same).</summary>
    private void ApplySlotLeave(int slot, int frame)
    {
        if (slot <= 0 || !Engine.Running) return;
        Engine.SetLeaveFrame(slot, frame);
        Engine.Schedule(frame, () => GameBridge.MakeLeave(slot));
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
        ApplySlotLeave(slot, frame);
        if (IsHost)
        {
            var peer = _peers.Values.FirstOrDefault(p => p.Slot == slot);
            if (peer != null) peer.LeavingAt = frame;
        }
    }

    /// <summary>Our own slot reached its leave frame: keep playing solo from here.</summary>
    private void OnLeftSession()
    {
        Toast.Show("You left the co-op session. You keep playing on your own (saves go to the session copy).");
        AfterLockstep();
        Leave();
    }

    /// <summary>Host: a guest has sent nothing for a while; let the others continue without them.</summary>
    private void OnStalled(List<int> missing, long waitedMs)
    {
        if (!IsHost || waitedMs < 6000) return;
        foreach (int slot in missing)
        {
            var peer = _peers.Values.FirstOrDefault(p => p.Slot == slot && !p.Pending);
            if (peer != null) DropPeer(peer.Id, "lost connection");
        }
    }

    // ------------------------------------------------------------------ drop-in

    /// <summary>Host: admit waiting players now (at the next moment saving is allowed) instead of the next area change.</summary>
    public void RequestResync()
    {
        if (IsHost && Phase == Phase.Running && HasPending) _resyncRequested = true;
    }

    /// <summary>Host, every frame while running with players waiting to join.</summary>
    private void UpdateDropIn()
    {
        if (!IsHost) return;
        switch (_resync)
        {
            case ResyncStep.None:
                if (Phase != Phase.Running || !HasPending) { _saveableFrames = 0; return; }
                if (!_areaChanged && _pendingSinceLevel != null && !GameBridge.LoadingLevel && GameBridge.CurrentLevelId != _pendingSinceLevel)
                    _areaChanged = true;
                if (!_areaChanged && !_resyncRequested) return;
                // Wait for free gameplay where the game itself allows saving (no battle, cutscene, loading).
                _saveableFrames = GameBridge.CanSaveNow ? _saveableFrames + 1 : 0;
                if (_saveableFrames < 30) return;
                _resyncSlot = GameBridge.AutoSave();
                if (_resyncSlot < 0) { _saveableFrames = 0; return; }
                _resync = ResyncStep.Saving;
                _resyncAt = Now + 0.5;
                Plugin.Log.LogInfo($"[session] drop-in: autosaved slot {_resyncSlot} at frame {Engine.AppliedFrame}");
                break;

            case ResyncStep.Saving:
                if (Now < _resyncAt || GameBridge.Saving) return;
                string names = string.Join(", ", PendingNames);
                foreach (var peer in _peers.Values.Where(p => !p.Pending))
                {
                    var (w, ms) = Protocol.Begin(Msg.Resync);
                    w.Write($"{names} joined: reloading together from the latest autosave…");
                    Send(peer.Id, ms, true);
                }
                foreach (var peer in _peers.Values) peer.Pending = false;
                StopLockstep("resync");
                GameBridge.ReturnToTitle();
                Toast.Show($"{names} joined: reloading together from the latest autosave…");
                _resync = ResyncStep.ToTitle;
                _resyncAt = Now + 2.0;
                break;

            case ResyncStep.ToTitle:
                if (!GameBridge.AtTitleScreen) { _resyncAt = Now + 2.0; return; }
                if (Now < _resyncAt || Phase != Phase.Lobby) return;
                _resync = ResyncStep.None;
                _pendingSinceLevel = null;
                _areaChanged = _resyncRequested = false;
                _saveableFrames = 0;
                StartFromSave(_resyncSlot, true);
                break;
        }
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
        if (Engine.AppliedFrame < 240 || GameBridge.LoadingLevel || GameBridge.Party?.Leader == null) return snap;
        if (frame < _nextJoinTry) return snap;
        if (++_joinTries > 12) { _autoJoin = false; Toast.Show("Press Join on your controller to take over a party member."); return snap; }
        _nextJoinTry = frame + 90;
        int join = ActionTable.IndexOf("Join");
        if (join >= 0) snap.Bits[join] |= ActionBits.Button | ActionBits.ButtonDown;
        Plugin.Log.LogInfo($"[session] auto-join press for slot {LocalSlot} at frame {frame}");
        return snap;
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
        _resync = ResyncStep.None;
        _pendingSinceLevel = null;
        _areaChanged = _resyncRequested = false;
        _autoJoin = false;
        WaitingForResync = false;
        LocalSlot = 0;
        Array.Clear(SlotPeer);
        Array.Clear(SlotName);
        if (!UsingUdp && SteamApi.Available)
        {
            SteamApi.SetRichPresence("status", "");
            SteamApi.SetRichPresence("connect", "");
        }
    }
}
