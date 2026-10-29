using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace SuperflightCoop.Net
{
    /// <summary>A peer link: datagrams in both directions, with an optional reliable flag.
    /// Peer ids are opaque strings (an "ip:port" for UDP, a SteamID for Steam).</summary>
    public interface ITransport
    {
        bool IsHost { get; }
        string LocalId { get; }
        /// <summary>Client: how long to wait for the host's Welcome before giving up.</summary>
        float ConnectTimeout { get; }
        /// <summary>Called on the main thread for every datagram that arrived.</summary>
        void Poll(Action<string, byte[]> onMessage, Action<string> onPeerLost);
        void Send(string peer, byte[] data, bool reliable);
        /// <summary>Host: forget a peer. Client: ignored.</summary>
        void Drop(string peer);
        void Close();
        /// <summary>Peers known to this transport (host: every client; client: the host).</summary>
        IEnumerable<string> Peers { get; }
    }

    /// <summary>UDP with a tiny reliability layer: every datagram carries a 1-byte kind
    /// (0 = unreliable, 1 = reliable with a sequence, 2 = ack). Reliable messages are resent every
    /// 250 ms until acknowledged and de-duplicated on receipt. Keep-alives every second, a peer that
    /// stays silent for TimeoutSeconds is dropped. Everything runs on the main thread (non-blocking socket).</summary>
    public sealed class UdpTransport : ITransport
    {
        public const float TimeoutSeconds = 10f;
        const int MaxPacket = 1400;

        readonly UdpClient socket;
        readonly bool isHost;
        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        readonly Func<float> now;

        sealed class Peer
        {
            public IPEndPoint Address;
            public uint NextSend = 1;         // next reliable sequence to send
            public uint LastDelivered = 0;    // highest reliable sequence delivered in order
            public readonly Dictionary<uint, byte[]> Held = new Dictionary<uint, byte[]>(); // out-of-order reliable
            public readonly List<Pending> Unacked = new List<Pending>();
            public float LastHeard;
            public float LastSent;
        }

        sealed class Pending
        {
            public uint Seq;
            public byte[] Packet;
            public float NextResend;
        }

        UdpTransport(UdpClient socket, bool isHost, Func<float> clock)
        {
            this.socket = socket;
            this.isHost = isHost;
            this.now = clock;
            socket.Client.Blocking = false;
            try { socket.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch { } // SIO_UDP_CONNRESET off: a closed peer must not throw
        }

        public static UdpTransport Host(int port, Func<float> clock)
        {
            return new UdpTransport(new UdpClient(new IPEndPoint(IPAddress.Any, port)), true, clock);
        }

        public static UdpTransport Connect(string host, int port, Func<float> clock)
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var t = new UdpTransport(client, false, clock);
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                var all = Dns.GetHostAddresses(host);
                ip = null;
                foreach (var a in all) if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                if (ip == null) throw new SocketException((int)SocketError.HostNotFound);
            }
            t.peers["host"] = new Peer { Address = new IPEndPoint(ip, port), LastHeard = clock() };
            return t;
        }

        public bool IsHost { get { return isHost; } }
        public string LocalId { get { return isHost ? "host" : socket.Client.LocalEndPoint.ToString(); } }
        public float ConnectTimeout { get { return TimeoutSeconds; } }
        public IEnumerable<string> Peers { get { return new List<string>(peers.Keys); } }

        static string KeyFor(IPEndPoint ep) { return ep.ToString(); }

        public void Poll(Action<string, byte[]> onMessage, Action<string> onPeerLost)
        {
            float t = now();
            for (int guard = 0; guard < 256 && socket.Available > 0; guard++)
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] packet;
                try { packet = socket.Receive(ref from); }
                catch (SocketException) { break; }
                if (packet.Length < 1) continue;

                string key = isHost ? KeyFor(from) : "host";
                Peer peer;
                if (!peers.TryGetValue(key, out peer))
                {
                    if (!isHost) continue; // a client only talks to its host
                    peer = new Peer { Address = from, LastHeard = t };
                    peers[key] = peer;
                }
                else if (!isHost && !peer.Address.Equals(from)) continue;
                peer.LastHeard = t;

                byte kind = packet[0];
                if (kind == 0)
                {
                    if (packet.Length > 1) onMessage(key, Slice(packet, 1));
                }
                else if (kind == 1 && packet.Length >= 5)
                {
                    uint seq = BitConverter.ToUInt32(packet, 1);
                    SendRaw(peer, Ack(seq));
                    if (seq <= peer.LastDelivered || peer.Held.ContainsKey(seq)) continue; // duplicate
                    peer.Held[seq] = Slice(packet, 5);
                    byte[] body;
                    while (peer.Held.TryGetValue(peer.LastDelivered + 1, out body))
                    {
                        peer.Held.Remove(peer.LastDelivered + 1);
                        peer.LastDelivered++;
                        onMessage(key, body);
                    }
                }
                else if (kind == 2 && packet.Length >= 5)
                {
                    uint seq = BitConverter.ToUInt32(packet, 1);
                    peer.Unacked.RemoveAll(p => p.Seq == seq);
                }
                // kind 3 = keep-alive: LastHeard already updated
            }

            // resend, keep-alive, timeouts
            var lost = new List<string>();
            foreach (var kv in peers)
            {
                Peer peer = kv.Value;
                foreach (var p in peer.Unacked)
                    if (t >= p.NextResend) { SendRaw(peer, p.Packet); p.NextResend = t + 0.25f; }
                if (t - peer.LastSent > 1f) SendRaw(peer, new byte[] { 3 });
                if (t - peer.LastHeard > TimeoutSeconds) lost.Add(kv.Key);
            }
            foreach (var key in lost)
            {
                peers.Remove(key);
                onPeerLost(key);
            }
        }

        public void Send(string peerId, byte[] data, bool reliable)
        {
            Peer peer;
            if (!peers.TryGetValue(peerId, out peer)) return;
            if (data.Length + 5 > MaxPacket) throw new ArgumentException("packet too large: " + data.Length);
            byte[] packet;
            if (reliable)
            {
                uint seq = peer.NextSend++;
                packet = new byte[5 + data.Length];
                packet[0] = 1;
                Array.Copy(BitConverter.GetBytes(seq), 0, packet, 1, 4);
                Array.Copy(data, 0, packet, 5, data.Length);
                peer.Unacked.Add(new Pending { Seq = seq, Packet = packet, NextResend = now() + 0.25f });
            }
            else
            {
                packet = new byte[1 + data.Length];
                packet[0] = 0;
                Array.Copy(data, 0, packet, 1, data.Length);
            }
            SendRaw(peer, packet);
        }

        void SendRaw(Peer peer, byte[] packet)
        {
            try { socket.Send(packet, packet.Length, peer.Address); peer.LastSent = now(); }
            catch (SocketException) { }
        }

        static byte[] Ack(uint seq)
        {
            var p = new byte[5];
            p[0] = 2;
            Array.Copy(BitConverter.GetBytes(seq), 0, p, 1, 4);
            return p;
        }

        static byte[] Slice(byte[] src, int from)
        {
            var dst = new byte[src.Length - from];
            Array.Copy(src, from, dst, 0, dst.Length);
            return dst;
        }

        public void Drop(string peer) { peers.Remove(peer); }

        public void Close()
        {
            try { socket.Close(); } catch { }
            peers.Clear();
        }
    }
}
