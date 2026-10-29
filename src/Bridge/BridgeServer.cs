using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SuperflightCoop.Bridge
{
    /// <summary>JSON-lines RPC over loopback TCP, following coop-anything's tools/bridge PROTOCOL.md (version 1).
    /// Transport thread accepts and reads; "ping", "version", "methods" answer on that thread; every other request
    /// waits for <see cref="Pump"/>, which the plugin calls once per frame on the main thread.</summary>
    public sealed class BridgeServer
    {
        public delegate object Handler(Dictionary<string, object> parameters);

        readonly string game;
        readonly string build;
        readonly int pid;
        readonly Dictionary<string, Handler> mainThread = new Dictionary<string, Handler>();
        readonly Dictionary<string, Handler> direct = new Dictionary<string, Handler>();
        readonly Queue<Request> queue = new Queue<Request>();
        readonly object queueLock = new object();
        readonly List<Event> events = new List<Event>();
        readonly object eventLock = new object();
        TcpListener listener;
        Thread thread;
        volatile bool running;
        long frames;
        DateTime lastPump = DateTime.MinValue;
        long eventSeq;
        readonly DateTime start = DateTime.UtcNow;
        public Func<bool> Ready = () => true;

        sealed class Request
        {
            public object Id;
            public string Method;
            public Dictionary<string, object> Params;
            public Conn Conn;
            public DateTime Deadline;
        }

        sealed class Event
        {
            public long Seq;
            public long TMs;
            public string Type;
            public object Data;
        }

        sealed class Conn
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object SendLock = new object();
        }

        public BridgeServer(string game, string build)
        {
            this.game = game;
            this.build = build;
            pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        public int Port { get; private set; }

        public void Register(string method, Handler handler, bool onMainThread)
        {
            if (onMainThread) mainThread[method] = handler; else direct[method] = handler;
        }

        /// <summary>Start listening on 127.0.0.1:port (0 picks a free port). Writes the endpoint file when given.</summary>
        public void Start(int port, string endpointFile)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            running = true;
            thread = new Thread(AcceptLoop) { IsBackground = true, Name = "coop-bridge" };
            thread.Start();
            if (!string.IsNullOrEmpty(endpointFile))
            {
                var d = new Dictionary<string, object>();
                d["protocol"] = 1; d["game"] = game; d["pid"] = pid;
                d["endpoints"] = new List<object> { "tcp://127.0.0.1:" + Port };
                File.WriteAllText(endpointFile, Json.Write(d));
            }
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
        }

        public void PushEvent(string type, object data)
        {
            lock (eventLock)
            {
                events.Add(new Event { Seq = ++eventSeq, TMs = (long)(DateTime.UtcNow - start).TotalMilliseconds, Type = type, Data = data });
                if (events.Count > 1024) events.RemoveAt(0);
            }
        }

        /// <summary>Main thread, once per frame: run queued requests (at most 16).</summary>
        public void Pump()
        {
            frames++;
            lastPump = DateTime.UtcNow;
            for (int n = 0; n < 16; n++)
            {
                Request req;
                lock (queueLock)
                {
                    if (queue.Count == 0) return;
                    req = queue.Dequeue();
                }
                if (DateTime.UtcNow > req.Deadline) { Reply(req, null, Error(-32001, "request timed out in queue")); continue; }
                Handler h;
                if (!mainThread.TryGetValue(req.Method, out h)) { Reply(req, null, Error(-32601, "unknown method " + req.Method)); continue; }
                try { Reply(req, h(req.Params), null); }
                catch (BridgeError e) { Reply(req, null, Error(e.Code, e.Message)); }
                catch (Exception e) { Reply(req, null, Error(-32000, e.GetType().Name + ": " + e.Message)); }
            }
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }
                var conn = new Conn { Client = client, Stream = client.GetStream() };
                var t = new Thread(() => ReadLoop(conn)) { IsBackground = true };
                t.Start();
            }
        }

        void ReadLoop(Conn conn)
        {
            var buf = new byte[65536];
            var line = new MemoryStream();
            try
            {
                while (running)
                {
                    int n = conn.Stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] == (byte)'\n')
                        {
                            string text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                            line.SetLength(0);
                            if (text.Length > 0) HandleLine(conn, text);
                        }
                        else
                        {
                            line.WriteByte(buf[i]);
                            if (line.Length > 4 * 1024 * 1024) throw new IOException("line too long");
                        }
                    }
                }
            }
            catch { }
            try { conn.Client.Close(); } catch { }
        }

        void HandleLine(Conn conn, string text)
        {
            Dictionary<string, object> obj;
            try { obj = Json.Parse(text) as Dictionary<string, object>; }
            catch (Exception e) { Send(conn, null, null, Error(-32700, "parse error: " + e.Message)); return; }
            if (obj == null) { Send(conn, null, null, Error(-32600, "request must be an object")); return; }
            object id; obj.TryGetValue("id", out id);
            object m; obj.TryGetValue("method", out m);
            string method = m as string;
            if (method == null) { Send(conn, id, null, Error(-32600, "method must be a string")); return; }
            object p; obj.TryGetValue("params", out p);
            var prm = p as Dictionary<string, object>;

            Handler d;
            if (method == "ping") { Send(conn, id, Ping(), null); return; }
            if (method == "version") { Send(conn, id, Version(), null); return; }
            if (method == "methods") { Send(conn, id, Methods(), null); return; }
            if (method == "events") { Send(conn, id, Events(prm), null); return; }
            if (direct.TryGetValue(method, out d))
            {
                try { Send(conn, id, d(prm), null); }
                catch (Exception e) { Send(conn, id, null, Error(-32000, e.Message)); }
                return;
            }
            double timeoutMs = Json.Num(prm, "timeoutMs", 30000);
            var req = new Request { Id = id, Method = method, Params = prm, Conn = conn, Deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs + 2000) };
            lock (queueLock)
            {
                if (queue.Count >= 256) { Send(conn, id, null, Error(-32003, "too many pending requests")); return; }
                queue.Enqueue(req);
            }
        }

        object Ping()
        {
            var d = new Dictionary<string, object>();
            d["pong"] = true; d["protocol"] = 1; d["game"] = game; d["pid"] = pid; d["frames"] = frames;
            d["mainLoopAgeMs"] = lastPump == DateTime.MinValue ? -1 : (long)(DateTime.UtcNow - lastPump).TotalMilliseconds;
            d["ready"] = Ready(); d["loadedCount"] = 0;
            return d;
        }

        object Version()
        {
            var d = new Dictionary<string, object>();
            d["protocol"] = 1; d["game"] = game; d["build"] = build; d["server"] = "superflight-coop-bridge 1"; d["pid"] = pid;
            return d;
        }

        object Methods()
        {
            var l = new List<object> { "ping", "version", "methods", "events" };
            foreach (var k in direct.Keys) l.Add(k);
            foreach (var k in mainThread.Keys) l.Add(k);
            var d = new Dictionary<string, object>(); d["methods"] = l;
            return d;
        }

        object Events(Dictionary<string, object> prm)
        {
            long since = (long)Json.Num(prm, "since", 0);
            long limit = (long)Json.Num(prm, "limit", 0);
            if (limit <= 0) limit = 1000;
            var list = new List<object>();
            long next = since, last = 0, dropped = 0;
            lock (eventLock)
            {
                if (events.Count > 0)
                {
                    last = events[events.Count - 1].Seq;
                    long oldest = events[0].Seq;
                    if (since + 1 < oldest) dropped = oldest - since - 1;
                }
                foreach (var e in events)
                {
                    if (e.Seq <= since) continue;
                    if (list.Count >= limit) break;
                    var d = new Dictionary<string, object>();
                    d["seq"] = e.Seq; d["tMs"] = e.TMs; d["type"] = e.Type; d["data"] = e.Data;
                    list.Add(d);
                    next = e.Seq;
                }
            }
            var r = new Dictionary<string, object>();
            r["events"] = list; r["next"] = next; r["dropped"] = dropped; r["last"] = last;
            return r;
        }

        static Dictionary<string, object> Error(int code, string message)
        {
            var d = new Dictionary<string, object>(); d["code"] = code; d["message"] = message;
            return d;
        }

        void Reply(Request req, object result, Dictionary<string, object> error) { Send(req.Conn, req.Id, result, error); }

        void Send(Conn conn, object id, object result, Dictionary<string, object> error)
        {
            var d = new Dictionary<string, object>();
            d["id"] = id;
            if (error != null) d["error"] = error; else d["result"] = result;
            var bytes = Encoding.UTF8.GetBytes(Json.Write(d) + "\n");
            lock (conn.SendLock)
            {
                try { conn.Stream.Write(bytes, 0, bytes.Length); } catch { }
            }
        }
    }

    public sealed class BridgeError : Exception
    {
        public readonly int Code;
        public BridgeError(int code, string message) : base(message) { Code = code; }
    }
}
