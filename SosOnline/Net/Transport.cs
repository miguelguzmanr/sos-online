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

    private void Raw(ulong peer, byte[] packet)
    {
        try { _socket.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, (int)peer)); } catch (SocketException) { }
    }

    public void Poll(Action<ulong, byte[]> onMessage)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, p) in _peers)
            foreach (var (seq, entry) in p.Unacked)
                if ((now - entry.sent).TotalMilliseconds > 100) Raw(id, entry.data);

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
