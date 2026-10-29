using System;
using System.Collections.Generic;
using System.Globalization;
using Steamworks;

namespace SuperflightCoop.Net
{
    /// <summary>Steam peer-to-peer through the game's own Steamworks.NET (ISteamNetworking, relay allowed). Steam
    /// provides reliability and ordering for the reliable send type, so this layer only frames each datagram with a
    /// kind byte (0 = payload, 3 = keep-alive), accepts sessions from lobby members and times silent peers out.
    /// Peer keys: the SteamID as a decimal string on the host, "host" on a client. Nothing here touches Unity.</summary>
    public sealed class SteamTransport : ITransport
    {
        public const float TimeoutSeconds = 15f;
        const int Channel = 0;

        readonly bool isHost;
        readonly CSteamID hostId;                 // client: who we talk to
        readonly Func<CSteamID, bool> allowed;    // host: is this SteamID a lobby member
        readonly Func<float> now;
        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        readonly List<string> failed = new List<string>();
        byte[] buffer = new byte[4096];
        Callback<P2PSessionRequest_t> onRequest;
        Callback<P2PSessionConnectFail_t> onFail;
        public long Accepted, Refused, ConnectFailures;

        sealed class Peer
        {
            public CSteamID Id;
            public float LastHeard;
            public float LastSent;
        }

        public SteamTransport(bool isHost, CSteamID host, Func<CSteamID, bool> allowed, Func<float> clock)
        {
            this.isHost = isHost;
            hostId = host;
            this.allowed = allowed;
            now = clock;
            SteamNetworking.AllowP2PPacketRelay(true);
            onRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            onFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
            if (!isHost) peers["host"] = new Peer { Id = host, LastHeard = clock() };
        }

        public bool IsHost { get { return isHost; } }
        public string LocalId { get { return isHost ? "host" : Key(SteamUser.GetSteamID()); } }
        /// <summary>Relay selection plus the host's accept can take a while; be patient before giving up.</summary>
        public float ConnectTimeout { get { return 30f; } }
        public IEnumerable<string> Peers { get { return new List<string>(peers.Keys); } }

        static string Key(CSteamID id) { return id.m_SteamID.ToString(CultureInfo.InvariantCulture); }
        static bool Same(CSteamID a, CSteamID b) { return a.m_SteamID == b.m_SteamID; }

        bool Accepts(CSteamID id)
        {
            if (isHost) return allowed == null || allowed(id);
            return Same(id, hostId);
        }

        string KeyFor(CSteamID id) { return isHost ? Key(id) : "host"; }

        void OnSessionRequest(P2PSessionRequest_t r)
        {
            if (Accepts(r.m_steamIDRemote))
            {
                SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote);
                Accepted++;
                Plugin.Log.LogInfo("steam: accepted p2p session from " + Key(r.m_steamIDRemote));
            }
            else
            {
                Refused++;
                Plugin.Log.LogWarning("steam: refused p2p session from " + Key(r.m_steamIDRemote) + " (not in the lobby)");
            }
        }

        void OnConnectFail(P2PSessionConnectFail_t f)
        {
            ConnectFailures++;
            string key = KeyFor(f.m_steamIDRemote);
            Plugin.Log.LogWarning("steam: p2p session with " + Key(f.m_steamIDRemote) + " failed, error " + f.m_eP2PSessionError);
            if (peers.ContainsKey(key) && (isHost || Same(f.m_steamIDRemote, hostId))) failed.Add(key);
        }

        public void Poll(Action<string, byte[]> onMessage, Action<string> onPeerLost)
        {
            float t = now();
            uint size;
            for (int guard = 0; guard < 256 && SteamNetworking.IsP2PPacketAvailable(out size, Channel); guard++)
            {
                if (size > buffer.Length) buffer = new byte[Math.Max(size, (uint)buffer.Length * 2)];
                uint read; CSteamID from;
                if (!SteamNetworking.ReadP2PPacket(buffer, (uint)buffer.Length, out read, out from, Channel)) break;
                if (read < 1 || !Accepts(from)) continue;
                string key = KeyFor(from);
                Peer peer;
                if (!peers.TryGetValue(key, out peer))
                {
                    if (!isHost) continue;
                    peer = new Peer { Id = from, LastHeard = t };
                    peers[key] = peer;
                }
                peer.LastHeard = t;
                if (buffer[0] == 0 && read > 1)
                {
                    var body = new byte[read - 1];
                    Array.Copy(buffer, 1, body, 0, body.Length);
                    onMessage(key, body);
                }
                // kind 3 = keep-alive: LastHeard already updated
            }

            var lost = new List<string>(failed);
            failed.Clear();
            foreach (var kv in peers)
            {
                Peer peer = kv.Value;
                if (t - peer.LastSent > 1f) SendRaw(peer, new byte[] { 3 }, false);
                if (t - peer.LastHeard > TimeoutSeconds && !lost.Contains(kv.Key)) lost.Add(kv.Key);
            }
            foreach (var key in lost)
            {
                Peer peer;
                if (peers.TryGetValue(key, out peer)) { peers.Remove(key); SteamNetworking.CloseP2PSessionWithUser(peer.Id); }
                onPeerLost(key);
            }
        }

        public void Send(string peerId, byte[] data, bool reliable)
        {
            Peer peer;
            if (!peers.TryGetValue(peerId, out peer)) return;
            var packet = new byte[1 + data.Length];
            packet[0] = 0;
            Array.Copy(data, 0, packet, 1, data.Length);
            SendRaw(peer, packet, reliable);
        }

        void SendRaw(Peer peer, byte[] packet, bool reliable)
        {
            var kind = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
            try { SteamNetworking.SendP2PPacket(peer.Id, packet, (uint)packet.Length, kind, Channel); peer.LastSent = now(); }
            catch (Exception e) { Plugin.Log.LogWarning("steam: send failed: " + e.Message); }
        }

        public void Drop(string peerId)
        {
            Peer peer;
            if (!peers.TryGetValue(peerId, out peer)) return;
            peers.Remove(peerId);
            SteamNetworking.CloseP2PSessionWithUser(peer.Id);
        }

        public void Close()
        {
            foreach (var p in peers.Values) { try { SteamNetworking.CloseP2PSessionWithUser(p.Id); } catch { } }
            peers.Clear();
            if (onRequest != null) { onRequest.Dispose(); onRequest = null; }
            if (onFail != null) { onFail.Dispose(); onFail = null; }
        }
    }
}
