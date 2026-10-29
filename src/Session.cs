using System;
using System.Collections.Generic;
using System.IO;
using SuperflightCoop.Net;
using UnityEngine;

namespace SuperflightCoop
{
    /// <summary>One co-op session: host-authoritative. The host assigns ids, owns the world seed and relays every
    /// player's state to the others. Nothing here touches Unity objects except through <see cref="Game"/>.</summary>
    public sealed class Session
    {
        public enum Role { None, Host, Client }

        public sealed class Remote
        {
            public PlayerInfo Info;
            public PlayerState Last;
            public float LastSeen;
            public Puppet Puppet;
            public string Peer; // host only: transport peer key
        }

        public Role CurrentRole = Role.None;
        public ITransport Transport;
        public int LocalId = -1;
        public string LocalName = "";
        public float LocalHue;
        public int Seed;                 // the session's world seed (host: what we generated; client: what the host said)
        public bool Joined;              // client: Welcome received
        public string LastError = "";
        public readonly Dictionary<int, Remote> Remotes = new Dictionary<int, Remote>();
        readonly Dictionary<string, int> peerToId = new Dictionary<string, int>(); // host
        int nextId = 1;
        float lastStateSend;
        float connectStarted;
        float lastHello;
        /// <summary>Raised when an active session ends, for whatever reason (Leave, refused, host lost).</summary>
        public Action Ended;

        // counters for the bridge
        public long Sent, Received, StatesApplied, Relayed;

        public bool Active { get { return CurrentRole != Role.None; } }
        public bool Connected { get { return CurrentRole == Role.Host || (CurrentRole == Role.Client && Joined); } }

        public void Host(ITransport transport)
        {
            Leave();
            Transport = transport;
            CurrentRole = Role.Host;
            LocalId = 0;
            Joined = true;
            Seed = Game.CurrentSeed();
            Plugin.Log.LogInfo("hosting, seed " + Seed + ", local id 0");
            CoopManager.Instance.Bridge.PushEvent("coop.host", null);
        }

        public void Join(ITransport transport)
        {
            Leave();
            Transport = transport;
            CurrentRole = Role.Client;
            Joined = false;
            connectStarted = Time.realtimeSinceStartup;
            SendHello();
            Plugin.Log.LogInfo("joining as '" + LocalName + "'");
        }

        void SendHello()
        {
            lastHello = Time.realtimeSinceStartup;
            Transport.Send("host", Protocol.Hello(LocalName, LocalHue), true);
            Sent++;
        }

        public void Leave()
        {
            if (Transport != null)
            {
                try
                {
                    foreach (var p in Transport.Peers) Transport.Send(p, Protocol.Bye(), false);
                }
                catch { }
                Transport.Close();
            }
            foreach (var r in Remotes.Values) if (r.Puppet != null) r.Puppet.Destroy();
            Remotes.Clear();
            peerToId.Clear();
            Transport = null;
            LevelChoice.PendingSeed = 0;
            if (LevelChoice.Open) LevelChoice.Close();
            bool wasActive = CurrentRole != Role.None;
            CurrentRole = Role.None;
            LocalId = -1;
            Joined = false;
            nextId = 1;
            if (wasActive)
            {
                CoopManager.Instance.Bridge.PushEvent("coop.left", null);
                if (Ended != null) Ended();
            }
        }

        /// <summary>Host: a transport peer is known to be gone (for example it left the Steam lobby).</summary>
        public void DropPeer(string peer, string why)
        {
            int id;
            if (CurrentRole == Role.Host && peerToId.TryGetValue(peer, out id)) RemoveRemote(id, why);
        }

        public void Update()
        {
            if (Transport == null) return;
            Transport.Poll(OnMessage, OnPeerLost);

            if (CurrentRole == Role.Client && !Joined)
            {
                if (Time.realtimeSinceStartup - connectStarted > Transport.ConnectTimeout)
                {
                    LastError = "no answer from host";
                    Plugin.Log.LogWarning(LastError);
                    Leave();
                    return;
                }
                // the first Hello can be lost while a Steam session is still being set up; the host answers duplicates
                if (Time.realtimeSinceStartup - lastHello > 2f) SendHello();
            }

            float interval = 1f / Mathf.Max(1f, Plugin.SendRate.Value);
            if (Connected && Time.realtimeSinceStartup - lastStateSend >= interval)
            {
                lastStateSend = Time.realtimeSinceStartup;
                PlayerState s;
                if (Game.ReadLocalState(LocalId, out s))
                {
                    var bytes = Protocol.State(s);
                    if (CurrentRole == Role.Host) Broadcast(bytes, false, null);
                    else Transport.Send("host", bytes, false);
                    Sent++;
                }
            }

            foreach (var r in Remotes.Values) if (r.Puppet != null) r.Puppet.Tick();
        }

        void OnPeerLost(string peer)
        {
            if (CurrentRole == Role.Host)
            {
                int id;
                if (peerToId.TryGetValue(peer, out id)) RemoveRemote(id, "timed out");
            }
            else
            {
                LastError = "lost the host";
                Plugin.Log.LogWarning(LastError);
                Leave();
            }
        }

        void OnMessage(string peer, byte[] data)
        {
            Received++;
            Msg kind;
            BinaryReader r;
            try { r = Protocol.Open(data, out kind); }
            catch { return; }
            try
            {
                if (CurrentRole == Role.Host) HostMessage(peer, kind, r, data);
                else ClientMessage(kind, r);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("bad " + kind + " from " + peer + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------- host

        void HostMessage(string peer, Msg kind, BinaryReader r, byte[] raw)
        {
            int id;
            bool known = peerToId.TryGetValue(peer, out id);
            switch (kind)
            {
                case Msg.Hello:
                {
                    int version = r.ReadUInt16();
                    string name = Protocol.ReadStr(r);
                    float hue = r.ReadSingle();
                    if (version != Protocol.Version)
                    {
                        Transport.Send(peer, Protocol.Refused("protocol " + version + ", host has " + Protocol.Version), true);
                        Transport.Drop(peer);
                        return;
                    }
                    if (known) { SendWelcome(peer, id); return; } // duplicate Hello (resend): answer again
                    id = nextId++;
                    var info = new PlayerInfo { Id = id, Name = Sanitize(name, id), Hue = hue };
                    var remote = new Remote { Info = info, Peer = peer, LastSeen = Time.realtimeSinceStartup };
                    Remotes[id] = remote;
                    peerToId[peer] = id;
                    remote.Puppet = Puppet.Create(info);
                    SendWelcome(peer, id);
                    Broadcast(Protocol.PlayerJoined(info), true, peer);
                    Plugin.Log.LogInfo("player " + id + " '" + info.Name + "' joined from " + peer);
                    CoopManager.Instance.Bridge.PushEvent("coop.joined", Describe(info));
                    break;
                }
                case Msg.State:
                {
                    if (!known) return;
                    var s = Protocol.ReadState(r);
                    s.Id = id; // never trust the sender's id
                    ApplyState(s);
                    Broadcast(Protocol.State(s), false, peer);
                    Relayed++;
                    break;
                }
                case Msg.Bye:
                    if (known) RemoveRemote(id, "left");
                    Transport.Drop(peer);
                    break;
            }
        }

        void SendWelcome(string peer, int id)
        {
            var others = new List<PlayerInfo>();
            others.Add(new PlayerInfo { Id = 0, Name = LocalName, Hue = LocalHue });
            foreach (var kv in Remotes) if (kv.Key != id) others.Add(kv.Value.Info);
            Transport.Send(peer, Protocol.Welcome(id, Seed, others), true);
            Sent++;
        }

        void RemoveRemote(int id, string why)
        {
            Remote remote;
            if (!Remotes.TryGetValue(id, out remote)) return;
            Plugin.Log.LogInfo("player " + id + " '" + remote.Info.Name + "' " + why);
            if (remote.Puppet != null) remote.Puppet.Destroy();
            Remotes.Remove(id);
            if (remote.Peer != null) { peerToId.Remove(remote.Peer); Transport.Drop(remote.Peer); }
            if (CurrentRole == Role.Host) Broadcast(Protocol.PlayerLeft(id), true, null);
            CoopManager.Instance.Bridge.PushEvent("coop.left_player", Describe(remote.Info));
        }

        void Broadcast(byte[] bytes, bool reliable, string except)
        {
            foreach (var kv in Remotes)
            {
                if (kv.Value.Peer == null || kv.Value.Peer == except) continue;
                Transport.Send(kv.Value.Peer, bytes, reliable);
                Sent++;
            }
        }

        /// <summary>Host: the local game generated a level (portal, reset, new world). Tell everyone.</summary>
        public void OnLocalLevelGenerated(int seed)
        {
            if (CurrentRole != Role.Host) return;
            if (seed == Seed) return;
            Seed = seed;
            Broadcast(Protocol.Seed(seed), true, null);
            Plugin.Log.LogInfo("new world seed " + seed + " sent");
            CoopManager.Instance.Bridge.PushEvent("coop.seed", seed);
        }

        // ---------------------------------------------------------------- client

        void ClientMessage(Msg kind, BinaryReader r)
        {
            switch (kind)
            {
                case Msg.Welcome:
                {
                    if (Joined) return;
                    LocalId = r.ReadInt32();
                    int seed = r.ReadInt32();
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++) AddRemote(Protocol.ReadInfo(r));
                    Joined = true;
                    LevelChoice.PendingSeed = 0;
                    LastError = "";
                    Plugin.Log.LogInfo("joined as id " + LocalId + ", " + n + " other players, seed " + seed);
                    ApplySeed(seed);
                    CoopManager.Instance.Bridge.PushEvent("coop.welcome", LocalId);
                    break;
                }
                case Msg.Refused:
                    LastError = "refused: " + Protocol.ReadStr(r);
                    Plugin.Log.LogWarning(LastError);
                    Leave();
                    break;
                case Msg.PlayerJoined:
                    AddRemote(Protocol.ReadInfo(r));
                    break;
                case Msg.PlayerLeft:
                    RemoveRemote(r.ReadInt32(), "left");
                    break;
                case Msg.State:
                {
                    var s = Protocol.ReadState(r);
                    if (s.Id == LocalId) return;
                    ApplyState(s);
                    break;
                }
                case Msg.Seed:
                {
                    int seed = r.ReadInt32();
                    Seed = seed;
                    if (Game.CurrentSeed() != seed) { LevelChoice.PendingSeed = seed; Plugin.Log.LogInfo("host moved on to " + seed + ", pending"); }
                    else LevelChoice.PendingSeed = 0;
                    CoopManager.Instance.Bridge.PushEvent("coop.seed", seed);
                    break;
                }
                case Msg.Bye:
                    LastError = "host closed the session";
                    Leave();
                    break;
            }
        }

        void AddRemote(PlayerInfo info)
        {
            if (info.Id == LocalId || Remotes.ContainsKey(info.Id)) return;
            var remote = new Remote { Info = info, LastSeen = Time.realtimeSinceStartup };
            remote.Puppet = Puppet.Create(info);
            Remotes[info.Id] = remote;
            Plugin.Log.LogInfo("player " + info.Id + " '" + info.Name + "' is here");
            CoopManager.Instance.Bridge.PushEvent("coop.joined", Describe(info));
        }

        void ApplyState(PlayerState s)
        {
            Remote remote;
            if (!Remotes.TryGetValue(s.Id, out remote)) return;
            remote.Last = s;
            remote.LastSeen = Time.realtimeSinceStartup;
            StatesApplied++;
            if (remote.Puppet != null) remote.Puppet.Target(s);
        }

        void ApplySeed(int seed)
        {
            Seed = seed;
            if (Game.CurrentSeed() == seed) return;
            Plugin.Log.LogInfo("switching to host world " + seed);
            Game.LoadSeed(seed);
            CoopManager.Instance.Bridge.PushEvent("coop.seed", seed);
        }

        static string Sanitize(string name, int id)
        {
            if (name == null) name = "";
            name = name.Trim();
            if (name.Length > 24) name = name.Substring(0, 24);
            return name.Length == 0 ? "Player " + id : name;
        }

        public static Dictionary<string, object> Describe(PlayerInfo p)
        {
            var d = new Dictionary<string, object>();
            d["id"] = p.Id; d["name"] = p.Name; d["hue"] = p.Hue;
            return d;
        }
    }
}
