using System;
using HarmonyLib;
using SuperflightCoop.Net;
using UnityEngine;

namespace SuperflightCoop
{
    /// <summary>Every touch of the game's own classes goes through here, so a game update breaks one file.
    /// Names verified against Steam build 2304714 (Assembly-CSharp.dll, Unity 2017.2.0f3).</summary>
    public static class Game
    {
        public static bool InFlight()
        {
            var lgm = LocalGameManager.Singleton;
            return lgm != null && lgm.player != null && PlayerMovement.Singleton != null;
        }

        public static int CurrentSeed()
        {
            var w = WorldManager.currentWorld;
            return w == null ? 0 : w.seed;
        }

        public static bool ReadLocalState(int id, out PlayerState s)
        {
            s = new PlayerState();
            var lgm = LocalGameManager.Singleton;
            var pm = PlayerMovement.Singleton;
            if (lgm == null) return false;
            s.Id = id;
            s.Time = Time.realtimeSinceStartup;
            if (pm != null && lgm.player != null)
            {
                var t = pm.transform;
                s.Position = t.position;
                s.Rotation = t.rotation;
                s.Flags |= 1;
            }
            s.Score = lgm.ScoreThisRun;
            s.Combo = lgm.ScoreThisCombo;
            if (lgm.playerState == LocalGameManager.PlayerState.Dead) s.Flags |= 2;
            return true;
        }

        /// <summary>Put this game into the given world. In flight the game's own OnSeedEnter does the full reset;
        /// in the menu only the level is regenerated (OnSeedEnter would dereference the missing player).</summary>
        public static void LoadSeed(int seed)
        {
            var lgm = LocalGameManager.Singleton;
            if (lgm == null) return;
            var world = new World(seed);
            if (InFlight()) lgm.OnSeedEnter(world);
            else
            {
                WorldManager.currentWorld = world;
                AccessTools.Method(typeof(LocalGameManager), "LoadCurrentWorld", Type.EmptyTypes).Invoke(lgm, null);
            }
        }

        public static void StartFlight()
        {
            var lgm = LocalGameManager.Singleton;
            if (lgm != null && !InFlight()) lgm.LoadFlight();
        }

        public static string LocalPlayerName()
        {
            try
            {
                if (SteamManager.Initialized) return Steamworks.SteamFriends.GetPersonaName();
            }
            catch { }
            return "";
        }

        /// <summary>The player's visual root (meshes), to clone for puppets. Null in the menu.</summary>
        public static GameObject BodyParts()
        {
            var pm = PlayerMaterials.Singleton;
            return pm == null ? null : pm.playerBodyParts;
        }

        public static Transform PlayerRoot()
        {
            var pm = PlayerMovement.Singleton;
            return pm == null ? null : pm.transform;
        }

        public static void Tint(Material m, Color c)
        {
            try
            {
                Utility.SetTriplanarColors(m, c, c, c, c, c);
                var w = WorldManager.currentWorld;
                if (w != null) Utility.SetCustomFog(m, w.FogSettings);
            }
            catch (Exception e) { Plugin.Log.LogDebug("tint: " + e.Message); }
        }

        public static string PlayerStateName()
        {
            var lgm = LocalGameManager.Singleton;
            return lgm == null ? "none" : lgm.playerState.ToString();
        }
    }
}
