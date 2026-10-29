using System;
using System.Collections.Generic;
using System.Globalization;
using SuperflightCoop.Bridge;
using SuperflightCoop.Net;
using UnityEngine;

namespace SuperflightCoop
{
    /// <summary>The per-frame driver: polls the session, pumps the bridge, draws the UI and name tags, and runs the
    /// command-line automation used by the tests.</summary>
    public sealed class CoopManager : MonoBehaviour
    {
        public static CoopManager Instance;
        public Session Session = new Session();
        public BridgeServer Bridge;
        public CoopUI Ui;
        bool cliDone;
        float started;

        void Awake()
        {
            Instance = this;
            started = Time.realtimeSinceStartup;
            Bridge = new BridgeServer("superflight", BuildInfo.Commit);
            Bridge.Ready = () => LocalGameManager.Singleton != null;
            new CoopBridge(this).Register(Bridge);
            int port = Plugin.CliBridgePort >= 0 ? Plugin.CliBridgePort : Plugin.BridgePort.Value;
            if (port > 0 || Plugin.CliEndpointFile != null)
            {
                try
                {
                    Bridge.Start(Math.Max(0, port), Plugin.CliEndpointFile);
                    Plugin.Log.LogInfo("bridge listening on tcp://127.0.0.1:" + Bridge.Port);
                }
                catch (Exception e) { Plugin.Log.LogError("bridge: " + e.Message); }
            }
            Ui = new CoopUI(this);
        }

        void Update()
        {
            if (!Application.runInBackground) Application.runInBackground = true;
            Bridge.Pump();
            try { Session.Update(); }
            catch (Exception e) { Plugin.Log.LogError("session: " + e); }
            if (!cliDone && LocalGameManager.Singleton != null && Time.realtimeSinceStartup - started > 1f) RunCommandLine();
            Ui.Update();
            LevelChoice.Update();
            Bot.Update();
        }

        void OnGUI() { Ui.Draw(); LevelChoice.Draw(); }

        void OnApplicationQuit()
        {
            Session.Leave();
            Bridge.Stop();
        }

        void RunCommandLine()
        {
            cliDone = true;
            if (!string.IsNullOrEmpty(Plugin.CliName)) Session.LocalName = Plugin.CliName;
            if (Plugin.CliHostPort > 0) HostUdp(Plugin.CliHostPort);
            else if (!string.IsNullOrEmpty(Plugin.CliJoin)) JoinUdp(Plugin.CliJoin);
            if (Plugin.CliAutoFlight) Game.StartFlight();
            if (Plugin.CliBot) Bot.Enabled = true;
            if (Plugin.CliBotAutoChoose) Bot.AutoChoose = true;
        }

        public string ResolveName()
        {
            if (!string.IsNullOrEmpty(Session.LocalName)) return Session.LocalName;
            string n = Plugin.PlayerName.Value;
            if (string.IsNullOrEmpty(n)) n = Game.LocalPlayerName();
            if (string.IsNullOrEmpty(n)) n = "Pilot" + (Environment.TickCount % 1000).ToString(CultureInfo.InvariantCulture);
            return n;
        }

        void Prepare()
        {
            Session.LocalName = ResolveName();
            if (Session.LocalHue == 0f) Session.LocalHue = (float)new System.Random().NextDouble();
            Session.LastError = "";
        }

        public bool HostUdp(int port)
        {
            try
            {
                Prepare();
                Session.Host(UdpTransport.Host(port, () => Time.realtimeSinceStartup));
                return true;
            }
            catch (Exception e)
            {
                Session.LastError = "host failed: " + e.Message;
                Plugin.Log.LogError(Session.LastError);
                return false;
            }
        }

        public bool JoinUdp(string address)
        {
            try
            {
                Prepare();
                string host = address; int port = Plugin.DefaultPort.Value;
                int colon = address.LastIndexOf(':');
                if (colon > 0) { host = address.Substring(0, colon); int.TryParse(address.Substring(colon + 1), out port); }
                Session.Join(UdpTransport.Connect(host, port, () => Time.realtimeSinceStartup));
                return true;
            }
            catch (Exception e)
            {
                Session.LastError = "join failed: " + e.Message;
                Plugin.Log.LogError(Session.LastError);
                return false;
            }
        }

        public void Leave() { Session.Leave(); }

        /// <summary>Snapshot for the bridge and the UI.</summary>
        public Dictionary<string, object> State()
        {
            var d = new Dictionary<string, object>();
            d["role"] = Session.CurrentRole.ToString().ToLowerInvariant();
            d["connected"] = Session.Connected;
            d["localId"] = Session.LocalId;
            d["localName"] = Session.LocalName;
            d["seed"] = Session.Seed;
            d["gameSeed"] = Game.CurrentSeed();
            d["inFlight"] = Game.InFlight();
            d["playerState"] = Game.PlayerStateName();
            d["lastError"] = Session.LastError;
            d["pendingSeed"] = LevelChoice.PendingSeed;
            d["choiceOpen"] = LevelChoice.Open;
            d["choiceReason"] = LevelChoice.Why.ToString();
            d["timeScale"] = (double)Time.timeScale;
            PlayerState local;
            if (Game.ReadLocalState(Session.LocalId, out local))
            {
                d["localPos"] = Vec(local.Position);
                d["score"] = local.Score;
            }
            var players = new List<object>();
            foreach (var r in Session.Remotes.Values)
            {
                var p = Session.Describe(r.Info);
                p["pos"] = Vec(r.Last.Position);
                p["puppetPos"] = r.Puppet == null ? null : Vec(r.Puppet.Position);
                p["visible"] = r.Puppet != null && r.Puppet.Visible;
                p["score"] = r.Last.Score;
                p["seed"] = r.Last.Seed;
                p["flags"] = (int)r.Last.Flags;
                p["ageMs"] = (long)((Time.realtimeSinceStartup - r.LastSeen) * 1000);
                players.Add(p);
            }
            d["players"] = players;
            d["playerCount"] = Session.Active ? Session.Remotes.Count + 1 : 0;
            var c = new Dictionary<string, object>();
            c["sent"] = Session.Sent; c["received"] = Session.Received; c["statesApplied"] = Session.StatesApplied; c["relayed"] = Session.Relayed;
            d["counters"] = c;
            return d;
        }

        static List<object> Vec(Vector3 v) { return new List<object> { (double)v.x, (double)v.y, (double)v.z }; }
    }
}
