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

        /// <summary>Portal: the choice screen instead of the game's immediate next level (host and client).</summary>
        [HarmonyPatch(typeof(LocalGameManager), "OnPortalEnter")]
        static class PortalChoice
        {
            static bool Prefix() { return !LevelChoice.Intercept(LevelChoice.Reason.Portal); }
        }

        /// <summary>Falling out of the level also generates a new world in the game: same choice.</summary>
        [HarmonyPatch(typeof(LocalGameManager), "OnEndPlaneEnter")]
        static class FellChoice
        {
            static bool Prefix() { return !LevelChoice.Intercept(LevelChoice.Reason.Fell); }
        }

        /// <summary>While the choice screen is up, the game's own reset must not respawn behind our back.</summary>
        [HarmonyPatch(typeof(LocalGameManager), "QuickReset")]
        static class ResetDuringChoice
        {
            static bool Prefix() { return !LevelChoice.Open || Game.AllowReset; }
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
                __result = new World(LevelChoice.PendingSeed != 0 ? LevelChoice.PendingSeed : s.Seed);
                return false;
            }
        }

        /// <summary>Test bot: after the game read the keyboard, overwrite the axes with the bot's steering.</summary>
        [HarmonyPatch(typeof(PlayerMovement), "GetInput")]
        static class BotInput
        {
            static void Postfix(PlayerMovement __instance)
            {
                if (Bot.Enabled) Bot.Steer(__instance);
            }
        }

        [HarmonyPatch(typeof(LocalGameManager), "OnPlayerDeath")]
        static class Death
        {
            static void Postfix()
            {
                if (CoopManager.Instance != null) CoopManager.Instance.Bridge.PushEvent("game.death", null);
                LevelChoice.Intercept(LevelChoice.Reason.Crash);
            }
        }
    }
}
