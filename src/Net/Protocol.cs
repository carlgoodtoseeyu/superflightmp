using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SuperflightCoop.Net
{
    /// <summary>Message ids. The first byte of every message body.</summary>
    public enum Msg : byte
    {
        Hello = 1,          // client -> host: protocol, name, colour hue
        Welcome = 2,        // host -> client: your id, world seed, current players
        Refused = 3,        // host -> client: reason
        PlayerJoined = 4,   // host -> all: id, name, hue
        PlayerLeft = 5,     // host -> all: id
        State = 6,          // any -> host -> others: id, pos, rot, score, flags (unreliable)
        Seed = 7,           // host -> all: new world seed
        Bye = 8,            // any: leaving on purpose
    }

    public struct PlayerInfo
    {
        public int Id;
        public string Name;
        public float Hue;
    }

    public struct PlayerState
    {
        public int Id;
        public Vector3 Position;
        public Quaternion Rotation;
        public int Score;
        public int Combo;
        public byte Flags;    // bit 0: in flight, bit 1: dead, bit 2: in the level choice
        public int Seed;      // the world this player is in
        public float Time;    // sender's clock, for interpolation
    }

    /// <summary>Binary encoding. Little endian, strings as UTF-8 with a 2-byte length.</summary>
    public static class Protocol
    {
        public const int Version = 2;

        public static byte[] Hello(string name, float hue)
        {
            var w = Begin(Msg.Hello);
            w.Write((ushort)Version);
            Str(w, name);
            w.Write(hue);
            return End(w);
        }

        public static byte[] Welcome(int id, int seed, IList<PlayerInfo> players)
        {
            var w = Begin(Msg.Welcome);
            w.Write(id);
            w.Write(seed);
            w.Write((ushort)players.Count);
            foreach (var p in players) { w.Write(p.Id); Str(w, p.Name); w.Write(p.Hue); }
            return End(w);
        }

        public static byte[] Refused(string reason) { var w = Begin(Msg.Refused); Str(w, reason); return End(w); }
        public static byte[] PlayerJoined(PlayerInfo p) { var w = Begin(Msg.PlayerJoined); w.Write(p.Id); Str(w, p.Name); w.Write(p.Hue); return End(w); }
        public static byte[] PlayerLeft(int id) { var w = Begin(Msg.PlayerLeft); w.Write(id); return End(w); }
        public static byte[] Seed(int seed) { var w = Begin(Msg.Seed); w.Write(seed); return End(w); }
        public static byte[] Bye() { return End(Begin(Msg.Bye)); }

        public static byte[] State(PlayerState s)
        {
            var w = Begin(Msg.State);
            w.Write(s.Id);
            w.Write(s.Position.x); w.Write(s.Position.y); w.Write(s.Position.z);
            w.Write(s.Rotation.x); w.Write(s.Rotation.y); w.Write(s.Rotation.z); w.Write(s.Rotation.w);
            w.Write(s.Score); w.Write(s.Combo);
            w.Write(s.Flags);
            w.Write(s.Time);
            w.Write(s.Seed);
            return End(w);
        }

        public static PlayerState ReadState(BinaryReader r)
        {
            var s = new PlayerState();
            s.Id = r.ReadInt32();
            s.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s.Rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s.Score = r.ReadInt32(); s.Combo = r.ReadInt32();
            s.Flags = r.ReadByte();
            s.Time = r.ReadSingle();
            s.Seed = r.ReadInt32();
            return s;
        }

        public static PlayerInfo ReadInfo(BinaryReader r)
        {
            var p = new PlayerInfo();
            p.Id = r.ReadInt32(); p.Name = ReadStr(r); p.Hue = r.ReadSingle();
            return p;
        }

        public static BinaryReader Open(byte[] data, out Msg kind)
        {
            var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            kind = (Msg)r.ReadByte();
            return r;
        }

        public static string ReadStr(BinaryReader r)
        {
            int n = r.ReadUInt16();
            return Encoding.UTF8.GetString(r.ReadBytes(n));
        }

        static BinaryWriter Begin(Msg kind)
        {
            var w = new BinaryWriter(new MemoryStream(), Encoding.UTF8);
            w.Write((byte)kind);
            return w;
        }

        static byte[] End(BinaryWriter w)
        {
            w.Flush();
            return ((MemoryStream)w.BaseStream).ToArray();
        }

        static void Str(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            if (b.Length > 200) Array.Resize(ref b, 200);
            w.Write((ushort)b.Length);
            w.Write(b);
        }
    }
}
