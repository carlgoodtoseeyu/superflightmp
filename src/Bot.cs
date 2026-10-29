using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SuperflightCoop
{
    /// <summary>A stand-in pilot for automated tests: weaves gently so the player keeps moving, and quick-resets a
    /// second after a death. Never on for real players unless they type "bot" at the bridge or pass --coop-bot.</summary>
    public static class Bot
    {
        public static bool Enabled;
        public static bool AutoChoose;   // tests that do not drive the choice screen: respawn 2 s after it opens
        static float choiceSince = -1f;
        static readonly FieldInfo Horizontal = AccessTools.Field(typeof(PlayerMovement), "horizontalInput");
        static readonly FieldInfo Vertical = AccessTools.Field(typeof(PlayerMovement), "verticalInput");
        static readonly MethodInfo QuickReset = AccessTools.Method(typeof(LocalGameManager), "QuickReset", System.Type.EmptyTypes);
        static float deadSince = -1f;

        public static void Steer(PlayerMovement pm)
        {
            float t = Time.time;
            // slow S-curves left and right, a mild pitch cycle; crashes are fine, the reset below handles them
            if (Horizontal != null) Horizontal.SetValue(pm, Mathf.Sin(t * 0.8f) * 0.5f);
            if (Vertical != null) Vertical.SetValue(pm, Mathf.Sin(t * 0.35f) * 0.4f);
        }

        public static void Update()
        {
            if (!Enabled) return;
            var lgm = LocalGameManager.Singleton;
            if (lgm == null) return;
            if (LevelChoice.Open)
            {
                deadSince = -1f;
                if (choiceSince < 0) choiceSince = Time.realtimeSinceStartup;
                else if (AutoChoose && Time.realtimeSinceStartup - choiceSince > 2f) { choiceSince = -1f; LevelChoice.Choose(LevelChoice.PendingSeed != 0); }
                return;
            }
            choiceSince = -1f;
            if (lgm.playerState == LocalGameManager.PlayerState.Dead)
            {
                if (deadSince < 0) deadSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - deadSince > 1f)
                {
                    deadSince = -1f;
                    Game.Respawn();
                }
            }
            else deadSince = -1f;
            if (lgm.playerState == LocalGameManager.PlayerState.Menu && !Game.InFlight()) Game.StartFlight();
        }
    }
}
