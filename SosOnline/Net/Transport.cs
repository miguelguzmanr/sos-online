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

    public string Describe(ulong peer) => SteamApi.FriendName(peer) is { Length: > 0 } n ? n : peer.ToString();

    public void Send(ulong peer, byte[] data, int length, bool reliable) => SteamApi.Send(peer, data, length, reliable, Channel);

    public void Poll(Action<ulong, byte[]> onMessage) => SteamApi.Receive(Channel, onMessage);

    public void Disconnect(ulong peer) => SteamApi.Close(peer);

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
        public readonly SortedDictionary<uint, byte[]> OutOfOrder = new();
    }

    private const byte KindData = 1, KindAck = 2, KindUnreliable = 3;

    public UdpTransport(ushort port)
    {
        _port = port;
        _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _socket.Client.Blocking = false;
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
        var packet = new byte[length + 5];
        Buffer.BlockCopy(data, 0, packet, 5, length);
        if (reliable)
        {
            var p = Peer(peer);
            uint seq = p.NextSendSeq++;
            packet[0] = KindData;
            BitConverter.TryWriteBytes(packet.AsSpan(1), seq);
            p.Unacked[seq] = (packet, DateTime.UtcNow);
        }
        else packet[0] = KindUnreliable;
        Raw(peer, packet);
    }

    // ---- Simulated network (tests): --sosonline-lag=MS, --sosonline-jitter=MS, --sosonline-spikes=PERCENT.
    // Loopback delivers in well under a millisecond, so anything that depends on real time instead of the session's
    // clock only breaks over the internet. Each outgoing packet waits lag + random jitter (and, for a share of them,
    // a 150-300 ms spike like a real connection's), in order per peer.
    public static int LagMs, JitterMs, SpikePercent;
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
        try { _socket.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, (int)peer)); } catch (SocketException) { }
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
            if (packet.Length < 5) continue;
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
                    var ack = new byte[5];
                    ack[0] = KindAck;
                    BitConverter.TryWriteBytes(ack.AsSpan(1), seq);
                    Raw(peer, ack);
                    if (seq < p.NextRecvSeq) break;
                    p.OutOfOrder[seq] = packet.AsSpan(5).ToArray();
                    while (p.OutOfOrder.Remove(p.NextRecvSeq, out var payload))
                    {
                        p.NextRecvSeq++;
                        onMessage(peer, payload);
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
