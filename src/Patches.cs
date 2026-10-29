using HarmonyLib;

namespace SuperflightCoop
{
    /// <summary>Harmony patches. Each nested class is one patch; the plugin calls PatchAll(typeof(Patches)).</summary>
    public static class Patches
    {
        static Session S { get { return CoopManager.Instance == null ? null : CoopManager.Instance.Session; } }

        /// <summary>No leaderboard uploads while in a session (modded runs stay off the boards).</summary>
        [HarmonyPatch(typeof(SteamIntegration), "SubmitHighscore")]
        static class BlockHighscore
        {
            static bool Prefix()
            {
                var s = S;
                bool block = s != null && s.Active && Plugin.BlockScoresInSession.Value;
                if (block) Plugin.Log.LogInfo("leaderboard upload skipped (co-op session)");
                return !block;
            }
        }

        [HarmonyPatch(typeof(SteamIntegration), "SubmitCombo")]
        static class BlockCombo
        {
            static bool Prefix()
            {
                var s = S;
                return !(s != null && s.Active && Plugin.BlockScoresInSession.Value);
            }
        }

        /// <summary>Host: every generated level becomes the session's world.</summary>
        [HarmonyPatch(typeof(LevelGenerator), "GenerateLevel")]
        static class AfterGenerate
        {
            static void Postfix(World world)
            {
                var s = S;
                if (s != null && world != null) s.OnLocalLevelGenerated(world.seed);
            }
        }

        /// <summary>Client: a portal or "new world" never leaves the host's world. The game asks WorldManager for a new
        /// one; while joined it gets the host's seed back, so the level regenerates in place.</summary>
        [HarmonyPatch(typeof(WorldManager), "NewWorld")]
        static class KeepHostWorld
        {
            static bool Prefix(ref World __result)
            {
                var s = S;
                if (s == null || s.CurrentRole != Session.Role.Client || !s.Joined) return true;
                __result = new World(s.Seed);
                return false;
            }
        }

        [HarmonyPatch(typeof(LocalGameManager), "OnPlayerDeath")]
        static class Death
        {
            static void Postfix()
            {
                if (CoopManager.Instance != null) CoopManager.Instance.Bridge.PushEvent("game.death", null);
            }
        }
    }
}
