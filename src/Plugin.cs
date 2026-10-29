using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SuperflightCoop
{
    /// <summary>Entry point. Reads config and command line, creates the session object and the bridge, applies the
    /// Harmony patches. Everything per-frame lives in <see cref="CoopManager"/>.</summary>
    [BepInPlugin(Id, "Superflight Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Id = "org.coopanything.superflight";
        public const string Version = "0.1.0";

        public static ManualLogSource Log;
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> DefaultPort;
        public static ConfigEntry<bool> BlockScoresInSession;
        public static ConfigEntry<bool> ShowUiAtStart;
        public static ConfigEntry<int> BridgePort;
        public static ConfigEntry<float> SendRate;

        // command line, for automated tests: --coop-host PORT, --coop-join HOST:PORT, --coop-name NAME,
        // --coop-bridge PORT, --coop-endpoint-file PATH, --coop-autoflight, --coop-bot
        public static int CliHostPort = -1;
        public static string CliJoin;
        public static string CliName;
        public static int CliBridgePort = -1;
        public static string CliEndpointFile;
        public static bool CliAutoFlight;
        public static bool CliBot;
        public static bool CliBotAutoChoose;

        void Awake()
        {
            Log = Logger;
            PlayerName = Config.Bind("Player", "Name", "", "Name shown to other players. Empty = your Steam name.");
            DefaultPort = Config.Bind("Network", "Port", 4444, "UDP port for direct hosting and joining.");
            SendRate = Config.Bind("Network", "SendRate", 20f, "Position updates per second.");
            BlockScoresInSession = Config.Bind("Fairness", "BlockLeaderboardsInSession", true,
                "Do not submit runs to the Steam leaderboards while connected to a co-op session.");
            ShowUiAtStart = Config.Bind("UI", "ShowAtStart", true, "Show the co-op window when the game starts (F1 toggles it).");
            BridgePort = Config.Bind("Testing", "BridgePort", 0, "Loopback TCP port of the test bridge; 0 = off unless --coop-bridge is given.");
            ParseCommandLine(Environment.GetCommandLineArgs());

            try
            {
                var harmony = new Harmony(Id);
                harmony.PatchAll(typeof(Plugin).Assembly); // PatchAll(Type) skips nested patch classes
                int n = 0;
                foreach (var m in harmony.GetPatchedMethods()) { n++; Log.LogDebug("patched " + m.DeclaringType.Name + "." + m.Name); }
                Log.LogInfo("patches applied: " + n + " methods");
                if (n == 0) Log.LogError("no method was patched; the co-op hooks are not live");
            }
            catch (Exception e)
            {
                Log.LogError("patching failed: " + e);
            }

            Application.runInBackground = true; // a Unity player pauses without focus; two copies on one PC need this
            var go = new GameObject("SuperflightCoop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<CoopManager>();
            Log.LogInfo("Superflight Co-op " + Version + " (" + BuildInfo.Commit + ") loaded");
        }

        static void ParseCommandLine(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--coop-host": if (next != null) int.TryParse(next, out CliHostPort); i++; break;
                    case "--coop-join": CliJoin = next; i++; break;
                    case "--coop-name": CliName = next; i++; break;
                    case "--coop-bridge": if (next != null) int.TryParse(next, out CliBridgePort); i++; break;
                    case "--coop-endpoint-file": CliEndpointFile = next; i++; break;
                    case "--coop-autoflight": CliAutoFlight = true; break;
                    case "--coop-bot": CliBot = true; break;
                    case "--coop-bot-autochoose": CliBot = true; CliBotAutoChoose = true; break;
                }
            }
        }
    }
}
