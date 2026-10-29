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
            if (LevelChoice.Open) s.Flags |= 4;
            s.Seed = CurrentSeed();
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

        static readonly System.Reflection.MethodInfo QuickResetMethod = AccessTools.Method(typeof(LocalGameManager), "QuickReset", Type.EmptyTypes);
        static readonly System.Reflection.MethodInfo LoadCurrentWorldMethod = AccessTools.Method(typeof(LocalGameManager), "LoadCurrentWorld", Type.EmptyTypes);
        static readonly System.Reflection.FieldInfo AlphaSeedField = AccessTools.Field(typeof(World), "alphanumericSeed");
        public static bool AllowReset;   // set while the plugin itself calls QuickReset (the patch blocks the game's own calls during a choice)

        /// <summary>A fresh run at the spawn of the current level (the game's QuickReset).</summary>
        public static void Respawn()
        {
            var lgm = LocalGameManager.Singleton;
            if (lgm == null || QuickResetMethod == null) return;
            AllowReset = true;
            try { QuickResetMethod.Invoke(lgm, null); } finally { AllowReset = false; }
        }

        /// <summary>Rebuild the current level and put the player at its spawn, without changing the seed.</summary>
        public static void RegenerateCurrent()
        {
            var lgm = LocalGameManager.Singleton;
            if (lgm == null) return;
            var w = WorldManager.currentWorld ?? new World(CurrentSeed());
            if (InFlight()) lgm.OnSeedEnter(w);
            else { WorldManager.currentWorld = w; if (LoadCurrentWorldMethod != null) LoadCurrentWorldMethod.Invoke(lgm, null); }
        }

        /// <summary>Host: run the game's own portal code (new world, reset), bypassing the choice-screen patch.</summary>
        public static void PortalProceed()
        {
            var lgm = LocalGameManager.Singleton;
            if (lgm == null) return;
            LevelChoice.PassThrough = true;
            try { lgm.OnPortalEnter(); } finally { LevelChoice.PassThrough = false; }
        }

        /// <summary>Client after a crash: switch the level and start a fresh run there.</summary>
        public static void LoadSeedAfterCrash(int seed)
        {
            var lgm = LocalGameManager.Singleton;
            if (lgm == null) return;
            WorldManager.currentWorld = new World(seed);
            if (LoadCurrentWorldMethod != null) LoadCurrentWorldMethod.Invoke(lgm, null);
            Respawn();
        }

        public static string LevelCode(int seed)
        {
            try
            {
                var w = WorldManager.currentWorld != null && WorldManager.currentWorld.seed == seed ? WorldManager.currentWorld : new World(seed);
                var code = AlphaSeedField == null ? null : AlphaSeedField.GetValue(w) as string;
                return string.IsNullOrEmpty(code) ? seed.ToString(System.Globalization.CultureInfo.InvariantCulture) : code;
            }
            catch { return seed.ToString(System.Globalization.CultureInfo.InvariantCulture); }
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
