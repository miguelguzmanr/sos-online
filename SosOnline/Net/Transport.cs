using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using SosOnline.Steam;

namespace SosOnline.Net;

/// <summary>Peer-to-peer datagram transport. Peers are identified by a 64-bit id (SteamID or UDP port).</summary>
internal interface ITransport : IDisposable
{
    ulong LocalId { get; }
    string Describe(ulong peer);
    void Send(ulong peer, byte[] data, int length, bool reliable);
    /// <summary>
    /// Reliable, but sent only as fast as the link takes it, so it never holds up the game's own messages.
    /// No order is kept against other messages.
    /// </summary>
    void SendBulk(ulong peer, byte[] data, int length);
    void Poll(Action<ulong, byte[]> onMessage);
    void Disconnect(ulong peer);
    /// <summary>The transport knows the peer is gone (connection closed or failed), without waiting for silence.</summary>
    bool IsLost(ulong peer);
}

/// <summary>Steam Networking Messages (relayed through Valve's network, NAT-traversal handled by Steam).</summary>
internal sealed class SteamTransport : ITransport
{
    private const int Channel = 7;

    public ulong LocalId => SteamApi.LocalSteamId;

    public string Describe(ulong peer) => SteamApi.FriendName(peer) is { Length: > 0 } n ? UI.PlayerNames.Clean(n, "the host") : peer.ToString();

    // Steam holds about 512 KB of reliable data per peer and refuses anything beyond (LimitExceeded). A refused
    // reliable message used to be dropped: 84 of 85 pieces of a 5 MB log zip, and catch-up frames. Now a refused
    // message waits here, in order, and every later reliable message to that peer waits behind it.
    private readonly Dictionary<ulong, Queue<byte[]>> _held = new();
    private readonly Dictionary<ulong, Queue<byte[]>> _bulk = new();
    private const int BulkBudgetBytes = 128 * 1024; // bulk goes out only while Steam holds less than this
    private const long MaxHeldBytes = 48L * 1024 * 1024;

    public void Send(ulong peer, byte[] data, int length, bool reliable)
    {
        if (!reliable) { SteamApi.Send(peer, data, length, false, Channel); return; }
        if (_held.TryGetValue(peer, out var held) && held.Count > 0) { Hold(peer, held, data, length); return; }
        int result = SteamApi.Send(peer, data, length, true, Channel);
        if (result == SteamApi.ResultOK) return;
        if (!_held.TryGetValue(peer, out held)) _held[peer] = held = new Queue<byte[]>();
        Plugin.Log.LogWarning($"[steam] send queue to {peer} full (result {result}); holding messages until it drains");
        Hold(peer, held, data, length);
    }

    private void Hold(ulong peer, Queue<byte[]> held, byte[] data, int length)
    {
        var copy = new byte[length];
        Buffer.BlockCopy(data, 0, copy, 0, length);
        held.Enqueue(copy);
        long total = 0;
        foreach (var m in held) total += m.Length;
        if (total <= MaxHeldBytes) return;
        held.Clear(); // the peer isn't taking anything; the session drops it on silence
        Plugin.Log.LogWarning($"[steam] over {MaxHeldBytes >> 20} MB held for {peer}; dropped");
    }

    public void SendBulk(ulong peer, byte[] data, int length)
    {
        if (!_bulk.TryGetValue(peer, out var q)) _bulk[peer] = q = new Queue<byte[]>();
        var copy = new byte[length];
        Buffer.BlockCopy(data, 0, copy, 0, length);
        q.Enqueue(copy);
    }

    public void Poll(Action<ulong, byte[]> onMessage)
    {
        foreach (var (peer, held) in _held)
        {
            while (held.Count > 0 && SteamApi.Send(peer, held.Peek(), held.Peek().Length, true, Channel) == SteamApi.ResultOK) held.Dequeue();
            if (held.Count == 0 && _wasHolding.Remove(peer)) Plugin.Log.LogInfo($"[steam] send queue to {peer} drained");
            else if (held.Count > 0) _wasHolding.Add(peer);
        }
        foreach (var (peer, q) in _bulk)
        {
            if (q.Count == 0 || (_held.TryGetValue(peer, out var held) && held.Count > 0)) continue;
            int pending = SteamApi.PendingReliableBytes(peer);
            if (pending < 0 || pending >= BulkBudgetBytes) continue;
            if (SteamApi.Send(peer, q.Peek(), q.Peek().Length, true, Channel) == SteamApi.ResultOK) q.Dequeue();
        }
        SteamApi.Receive(Channel, onMessage);
    }

    private readonly HashSet<ulong> _wasHolding = new();

    public void Disconnect(ulong peer)
    {
        _held.Remove(peer);
        _bulk.Remove(peer);
        _wasHolding.Remove(peer);
        SteamApi.Close(peer);
    }

    public bool IsLost(ulong peer) => SteamApi.SessionLost(peer);

    public void Dispose() { }
}

/// <summary>
/// Development transport: UDP on 127.0.0.1, peer id = port. Lets two game instances on one PC play together
/// without two Steam accounts. Messages are framed with a tiny reliability layer (sequence + ack + resend),
/// which is plenty for loopback.
/// </summary>
internal sealed class UdpTransport : ITransport
{
    private readonly UdpClient _socket;
    private readonly ushort _port;
    private readonly Dictionary<ulong, PeerState> _peers = new();

    private sealed class PeerState
    {
        public uint NextSendSeq = 1;
        public uint NextRecvSeq = 1;
        public readonly SortedDictionary<uint, (byte[] data, DateTime sent)> Unacked = new();
        public readonly SortedDictionary<uint, (byte kind, byte[] payload)> OutOfOrder = new();
        public System.IO.MemoryStream Assemble;
    }

    private const byte KindData = 1, KindAck = 2, KindUnreliable = 3, KindDataPart = 4;
    private const int MaxChunkBytes = 32 * 1024;

    public UdpTransport(ushort port)
    {
        _port = port;
        _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _socket.Client.Blocking = false;
        try { _socket.Client.SendBufferSize = 512 * 1024; _socket.Client.ReceiveBufferSize = 512 * 1024; } catch { }
        // Ignore ICMP port unreachable resets when the other instance isn't up yet.
        const int SIO_UDP_CONNRESET = -1744830452;
        try { _socket.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
    }

    public ulong LocalId => _port;
    public string Describe(ulong peer) => $"udp:{peer}";

    private PeerState Peer(ulong id)
    {
        if (!_peers.TryGetValue(id, out var p)) _peers[id] = p = new PeerState();
        return p;
    }

    public void Send(ulong peer, byte[] data, int length, bool reliable)
    {
        if (!reliable)
        {
            var packet = new byte[length + 5];
            packet[0] = KindUnreliable;
            Buffer.BlockCopy(data, 0, packet, 5, length);
            Raw(peer, packet);
            return;
        }
        var p = Peer(peer);
        int offset = 0;
        do
        {
            int chunk = Math.Min(MaxChunkBytes, length - offset);
            bool last = offset + chunk >= length;
            var packet = new byte[chunk + 5];
            packet[0] = last ? KindData : KindDataPart;
            uint seq = p.NextSendSeq++;
            BitConverter.TryWriteBytes(packet.AsSpan(1), seq);
            if (chunk > 0) Buffer.BlockCopy(data, offset, packet, 5, chunk);
            p.Unacked[seq] = (packet, DateTime.UtcNow);
            Raw(peer, packet);
            offset += chunk;
        }
        while (offset < length);
    }

    public void SendBulk(ulong peer, byte[] data, int length) => Send(peer, data, length, true);

    // ---- Simulated network (tests): --sosonline-lag=MS, --sosonline-jitter=MS, --sosonline-spikes=PERCENT.
    // Loopback delivers in well under a millisecond, so anything that depends on real time instead of the session's
    // clock only breaks over the internet. Each outgoing packet waits lag + random jitter (and, for a share of them,
    // a 150-300 ms spike like a real connection's), in order per peer.
    public static int LagMs, JitterMs, SpikePercent;
    /// <summary>Test (--sosonline-netdrop): until then this PC sends and receives nothing, like a dropped connection.</summary>
    public static DateTime BlackoutUntil;
    private readonly Random _rng = new(Environment.TickCount);
    private readonly List<(DateTime due, ulong peer, byte[] packet)> _delayed = new();
    private readonly Dictionary<ulong, DateTime> _lastDue = new();

    private void Raw(ulong peer, byte[] packet)
    {
        if (LagMs > 0 || JitterMs > 0 || SpikePercent > 0)
        {
            double ms = LagMs + (JitterMs > 0 ? _rng.Next(JitterMs + 1) : 0);
            if (SpikePercent > 0 && _rng.Next(100) < SpikePercent) ms += 150 + _rng.Next(151);
            var due = DateTime.UtcNow.AddMilliseconds(ms);
            if (_lastDue.TryGetValue(peer, out var last) && last > due) due = last; // in order, like one route
            _lastDue[peer] = due;
            _delayed.Add((due, peer, packet));
            return;
        }
        RawNow(peer, packet);
    }

    private void RawNow(ulong peer, byte[] packet)
    {
        if (DateTime.UtcNow < BlackoutUntil) return;
        try { _socket.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, (int)peer)); }
        catch (SocketException e) { Plugin.Log.LogWarning($"[udp] send {packet.Length} B to {peer} failed: {e.SocketErrorCode}"); }
    }

    private void FlushDelayed()
    {
        if (_delayed.Count == 0) return;
        var now = DateTime.UtcNow;
        int sent = 0;
        foreach (var (due, peer, packet) in _delayed)
        {
            if (due > now) break; // in due order per peer; across peers close enough for a test
            RawNow(peer, packet);
            sent++;
        }
        if (sent > 0) _delayed.RemoveRange(0, sent);
    }

    public void Poll(Action<ulong, byte[]> onMessage)
    {
        FlushDelayed();
        var now = DateTime.UtcNow;
        // Resend what hasn't been acknowledged after a round trip, then wait another round trip (not every poll).
        double resendMs = 100 + 2 * (LagMs + JitterMs);
        foreach (var (id, p) in _peers)
        {
            List<uint> due = null;
            foreach (var (seq, entry) in p.Unacked)
                if ((now - entry.sent).TotalMilliseconds > resendMs) (due ??= new List<uint>()).Add(seq);
            if (due == null) continue;
            foreach (var seq in due)
            {
                var entry = p.Unacked[seq];
                Raw(id, entry.data);
                p.Unacked[seq] = (entry.data, now);
            }
        }

        while (_socket.Available > 0)
        {
            IPEndPoint from = null;
            byte[] packet;
            try { packet = _socket.Receive(ref from); } catch (SocketException) { break; }
            if (packet.Length < 5 || DateTime.UtcNow < BlackoutUntil) continue;
            ulong peer = (ulong)from.Port;
            uint seq = BitConverter.ToUInt32(packet, 1);
            var p = Peer(peer);
            switch (packet[0])
            {
                case KindAck:
                    p.Unacked.Remove(seq);
                    break;
                case KindUnreliable:
                    onMessage(peer, packet.AsSpan(5).ToArray());
                    break;
                case KindData:
                case KindDataPart:
                    var ack = new byte[5];
                    ack[0] = KindAck;
                    BitConverter.TryWriteBytes(ack.AsSpan(1), seq);
                    Raw(peer, ack);
                    if (seq < p.NextRecvSeq) break;
                    p.OutOfOrder[seq] = (packet[0], packet.AsSpan(5).ToArray());
                    while (p.OutOfOrder.Remove(p.NextRecvSeq, out var entry))
                    {
                        p.NextRecvSeq++;
                        if (entry.kind == KindDataPart)
                        {
                            (p.Assemble ??= new System.IO.MemoryStream()).Write(entry.payload, 0, entry.payload.Length);
                        }
                        else if (p.Assemble != null && p.Assemble.Length > 0)
                        {
                            p.Assemble.Write(entry.payload, 0, entry.payload.Length);
                            var full = p.Assemble.ToArray();
                            p.Assemble.SetLength(0);
                            onMessage(peer, full);
                        }
                        else
                        {
                            onMessage(peer, entry.payload);
                        }
                    }
                    break;
            }
        }
    }

    public void Disconnect(ulong peer) => _peers.Remove(peer);

    /// <summary>Loopback UDP has no connection state; a lost peer shows up as silence.</summary>
    public bool IsLost(ulong peer) => false;

    public void Dispose() => _socket.Dispose();
}
