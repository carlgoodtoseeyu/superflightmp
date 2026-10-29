using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SuperflightCoop.Bridge
{
    /// <summary>The bridge methods this game answers: state, exec, eval, waitFor and coop.status.</summary>
    public sealed class CoopBridge
    {
        readonly CoopManager m;
        public CoopBridge(CoopManager manager) { m = manager; }

        public void Register(BridgeServer s)
        {
            s.Register("state", State, true);
            s.Register("exec", Exec, true);
            s.Register("eval", Eval, true);
            s.Register("waitFor", WaitFor, true);
            s.Register("coop.status", p => m.State(), true);
            s.Register("screenshot", Screenshot, true);
        }

        object State(Dictionary<string, object> p)
        {
            var all = m.State();
            object sections;
            if (p != null && p.TryGetValue("sections", out sections) && sections is List<object>)
            {
                var cut = new Dictionary<string, object>();
                foreach (var k in (List<object>)sections) { object v; if (all.TryGetValue(k.ToString(), out v)) cut[k.ToString()] = v; }
                return cut;
            }
            return all;
        }

        /// <summary>Commands: host [port] | join host:port | leave | flight | menu | seed N | name X</summary>
        object Exec(Dictionary<string, object> p)
        {
            string cmd = Json.Str(p, "cmd", null);
            if (cmd == null) throw new BridgeError(-32602, "exec requires string param 'cmd'");
            var parts = cmd.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool ok = true; string output = "ok";
            if (parts.Length == 0) { ok = false; output = "empty"; }
            else switch (parts[0])
            {
                case "host":
                {
                    int port = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : Plugin.DefaultPort.Value;
                    ok = m.HostUdp(port); if (!ok) output = m.Session.LastError;
                    break;
                }
                case "join":
                    if (parts.Length < 2) { ok = false; output = "join needs host:port"; break; }
                    ok = m.JoinUdp(parts[1]); if (!ok) output = m.Session.LastError;
                    break;
                case "leave": m.Leave(); break;
                case "steamhost": ok = m.HostSteam(); if (!ok) output = m.Session.LastError; break;
                case "steamjoin":
                {
                    ulong lobby = 0;
                    if (parts.Length < 2 || !ulong.TryParse(parts[1], out lobby)) { ok = false; output = "steamjoin needs a lobby id"; break; }
                    ok = m.JoinSteam(lobby); if (!ok) output = m.Session.LastError;
                    break;
                }
                case "invite": ok = m.Invite(); if (!ok) output = "not in a Steam lobby"; break;
                case "flight": Game.StartFlight(); output = Game.InFlight() ? "in flight" : "requested"; break;
                case "menu": { var l = LocalGameManager.Singleton; if (l != null) l.LoadMenu(); break; }
                case "seed":
                    if (parts.Length < 2) { ok = false; output = "seed needs a number"; break; }
                    Game.LoadSeed(int.Parse(parts[1], CultureInfo.InvariantCulture)); output = "seed " + Game.CurrentSeed();
                    break;
                case "name": m.Session.LocalName = cmd.Substring(4).Trim(); break;
                case "bot": Bot.Enabled = parts.Length < 2 || parts[1] != "off"; output = "bot " + (Bot.Enabled ? "on" : "off"); break;
                case "portal": { var l = LocalGameManager.Singleton; if (l != null && Game.InFlight()) l.OnPortalEnter(); else { ok = false; output = "not in flight"; } break; }
                case "endplane": { var l = LocalGameManager.Singleton; if (l != null && Game.InFlight()) l.OnEndPlaneEnter(); else { ok = false; output = "not in flight"; } break; }
                case "die": { var pm = PlayerMovement.Singleton; if (pm != null && LocalGameManager.Singleton.playerState == LocalGameManager.PlayerState.Flying) pm.Die(); else { ok = false; output = "not flying"; } break; }
                case "choose":
                    if (!LevelChoice.Open) { ok = false; output = "no choice open"; break; }
                    LevelChoice.Choose(parts.Length > 1 && parts[1] == "proceed"); break;
                case "quit": Application.Quit(); break;
                default: ok = false; output = "unknown command " + parts[0]; break;
            }
            var r = new Dictionary<string, object>(); r["ok"] = ok; r["output"] = output;
            return r;
        }

        static int shots;
        object Screenshot(Dictionary<string, object> p)
        {
            string path = Json.Str(p, "path", null);
            if (path == null) path = System.IO.Path.GetFullPath("coop-shot-" + (++shots) + ".png");
            ScreenCapture.CaptureScreenshot(path);
            var r = new Dictionary<string, object>(); r["ok"] = true; r["path"] = path;
            return r;
        }

        object Eval(Dictionary<string, object> p)
        {
            string expr = Json.Str(p, "expr", null);
            if (expr == null) throw new BridgeError(-32602, "eval requires string param 'expr'");
            var r = new Dictionary<string, object>(); r["value"] = Evaluate(expr);
            return r;
        }

        object Evaluate(string expr)
        {
            var s = m.Session;
            switch (expr)
            {
                case "playerCount": return s.Active ? s.Remotes.Count + 1 : 0;
                case "connected": return s.Connected;
                case "joined": return s.Joined;
                case "role": return s.CurrentRole.ToString().ToLowerInvariant();
                case "seed": return s.Seed;
                case "gameSeed": return Game.CurrentSeed();
                case "seedsMatch": return s.Active && s.Seed == Game.CurrentSeed();
                case "inFlight": return Game.InFlight();
                case "localId": return s.LocalId;
                case "statesApplied": return s.StatesApplied;
                case "received": return s.Received;
                case "sent": return s.Sent;
                case "visiblePuppets": { int n = 0; foreach (var r in s.Remotes.Values) if (r.Puppet != null && r.Puppet.Visible) n++; return n; }
                case "renderedPuppets": { int n = 0; foreach (var r in s.Remotes.Values) if (r.Puppet != null && r.Puppet.RenderedCount > 0) n++; return n; }
                case "clonedPuppets": { int n = 0; foreach (var r in s.Remotes.Values) if (r.Puppet != null && r.Puppet.BodyIsClone) n++; return n; }
                case "lastError": return s.LastError;
                case "pendingSeed": return LevelChoice.PendingSeed;
                case "choiceOpen": return LevelChoice.Open;
                case "choiceReason": return LevelChoice.Why.ToString();
                case "timeScale": return (double)Time.timeScale;
                case "scoreboardRows": return LevelChoice.Rows().Count;
                case "levelCode": return Game.LevelCode(Game.CurrentSeed());
                case "frames": return Time.frameCount;
                case "transport": return m.TransportName;
                case "steamReady": return SteamLobby.Ready;
                case "lobbyId": return SteamLobby.LobbyCode;
                case "inLobby": return SteamLobby.InLobby;
                case "lobbyMembers": return SteamLobby.MemberCount;
                case "lobbyBusy": return SteamLobby.Busy;
                case "steamStatus": return SteamLobby.Status;
            }
            if (expr.StartsWith("player."))
            {
                // player.<id>.score | pos.x | pos.y | pos.z | ageMs | visible | puppet.x
                var parts = expr.Split('.');
                int id = int.Parse(parts[1], CultureInfo.InvariantCulture);
                Session.Remote r;
                if (!s.Remotes.TryGetValue(id, out r)) throw new BridgeError(-32010, "no player " + id);
                string what = parts.Length > 2 ? parts[2] : "";
                string axis = parts.Length > 3 ? parts[3] : "";
                switch (what)
                {
                    case "score": return r.Last.Score;
                    case "name": return r.Info.Name;
                    case "visible": return r.Puppet != null && r.Puppet.Visible;
                    case "rendered": return r.Puppet == null ? 0 : r.Puppet.RenderedCount;
                    case "ageMs": return (long)((Time.realtimeSinceStartup - r.LastSeen) * 1000);
                    case "pos": return Axis(r.Last.Position, axis);
                    case "puppet": return r.Puppet == null ? 0.0 : Axis(r.Puppet.Position, axis);
                    case "distance": { Vector3 lp = Game.PlayerRoot() == null ? Vector3.zero : Game.PlayerRoot().position; return (double)Vector3.Distance(lp, r.Last.Position); }
                }
            }
            if (expr.StartsWith("local.pos."))
            {
                var t = Game.PlayerRoot();
                return t == null ? 0.0 : Axis(t.position, expr.Substring(10));
            }
            throw new BridgeError(-32011, "unknown expression " + expr);
        }

        static double Axis(Vector3 v, string axis)
        {
            switch (axis) { case "x": return v.x; case "y": return v.y; case "z": return v.z; }
            return v.magnitude;
        }

        /// <summary>Polling wait on the main thread: evaluates every frame until the condition holds or time runs out.
        /// The whole wait blocks this one request's slot, not the game: it is re-queued each frame.</summary>
        object WaitFor(Dictionary<string, object> p)
        {
            string expr = Json.Str(p, "expr", null);
            if (expr == null) throw new BridgeError(-32602, "waitFor needs 'expr'");
            string op = Json.Str(p, "op", "truthy");
            double value = Json.Num(p, "value", 0);
            string contains = Json.Str(p, "contains", null);
            double timeoutMs = Json.Num(p, "timeoutMs", 10000);
            // Evaluate once now; the Python client polls, so a false answer with timedOut=false is fine for it to retry.
            // (The reference protocol polls inside the server; a frame-blocking loop is not possible in a MonoBehaviour,
            // so this server answers each poll at once and the client re-asks until satisfied.)
            object v = Evaluate(expr);
            bool ok = Compare(v, op, value, contains);
            var r = new Dictionary<string, object>();
            r["satisfied"] = ok; r["timedOut"] = false; r["value"] = v; r["elapsedMs"] = 0; r["pollOnce"] = true; r["timeoutMs"] = timeoutMs;
            return r;
        }

        static bool Compare(object v, string op, double value, string contains)
        {
            if (v is string) return contains != null && ((string)v).Contains(contains);
            double d = v is bool ? ((bool)v ? 1 : 0) : Convert.ToDouble(v, CultureInfo.InvariantCulture);
            switch (op)
            {
                case "truthy": return d != 0;
                case "==": return d == value;
                case "!=": return d != value;
                case "<": return d < value;
                case "<=": return d <= value;
                case ">": return d > value;
                case ">=": return d >= value;
            }
            return false;
        }
    }
}
