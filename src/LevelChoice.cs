using System.Collections.Generic;
using System.Globalization;
using SuperflightCoop.Net;
using UnityEngine;

namespace SuperflightCoop
{
    /// <summary>The "Proceed to next level / Respawn in this level" screen with the scoreboard.
    /// Host: opens at every portal or fall-out; Proceed runs the game's own portal code (new world, sent to all).
    /// Client: opens at a portal or fall-out, and after a crash once the host has moved on; Proceed jumps to the
    /// host's pending level, Respawn stays. The game is paused (timeScale 0) while the screen is up after a portal.</summary>
    public static class LevelChoice
    {
        public enum Reason { None, Portal, Fell, Crash }

        public static bool Open;
        public static Reason Why = Reason.None;
        /// <summary>Client: the host's newest seed that this player has not followed yet; 0 = none.</summary>
        public static int PendingSeed;
        public static bool PassThrough;   // true while we call the game's own portal code from Proceed
        static bool paused;
        static Rect rect;
        static GUIStyle title, row, hint;

        static Session S { get { return CoopManager.Instance == null ? null : CoopManager.Instance.Session; } }

        /// <summary>Called by the patches. Returns true when the screen took over (the game's own code must not run).</summary>
        public static bool Intercept(Reason why)
        {
            var s = S;
            if (s == null || !s.Active) return false;
            if (PassThrough) return false;
            if (why == Reason.Crash)
            {
                if (s.CurrentRole != Session.Role.Client || PendingSeed == 0) return false;
                Show(why, false);
                return false; // the crash itself still happens; we only add the screen
            }
            if (Open) return true;
            Show(why, true);
            return true;
        }

        static void Show(Reason why, bool pause)
        {
            Open = true;
            Why = why;
            if (pause && Time.timeScale != 0f) { Time.timeScale = 0f; paused = true; }
            Cursor.visible = true;
            Plugin.Log.LogInfo("level choice: " + why + (PendingSeed != 0 ? ", host at " + PendingSeed : ""));
            if (CoopManager.Instance != null) CoopManager.Instance.Bridge.PushEvent("coop.choice", why.ToString());
        }

        public static void Close()
        {
            if (paused) { Time.timeScale = 1f; paused = false; }
            Open = false;
            Why = Reason.None;
        }

        public static void Choose(bool proceed)
        {
            if (!Open) return;
            var s = S;
            Reason why = Why;
            Close();
            if (s == null || !s.Active) return;
            if (s.CurrentRole == Session.Role.Host)
            {
                if (proceed) Game.PortalProceed();          // the game's own next-level code; seed goes out via the GenerateLevel postfix
                else if (why == Reason.Crash) Game.Respawn();
                else Game.RegenerateCurrent();
                return;
            }
            // client
            if (proceed && PendingSeed != 0)
            {
                int seed = PendingSeed;
                PendingSeed = 0;
                if (why == Reason.Crash) Game.LoadSeedAfterCrash(seed); else Game.LoadSeed(seed);
                s.Seed = seed;
            }
            else
            {
                if (why == Reason.Crash) Game.Respawn(); else Game.RegenerateCurrent();
            }
        }

        public static void Update()
        {
            if (!Open) return;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)) Choose(true);
            else if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Backspace)) Choose(false);
        }

        public static void Draw()
        {
            if (!Open) return;
            if (title == null)
            {
                title = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                row = new GUIStyle(GUI.skin.label) { fontSize = 16 };
                hint = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            }
            float w = 520, h = 190 + 24 * (Rows().Count + 1);
            rect = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            GUI.Window(0x5f01, rect, Window, "");
        }

        static void Window(int id)
        {
            var s = S;
            bool client = s != null && s.CurrentRole == Session.Role.Client;
            string head = Why == Reason.Portal ? "Level complete!" : Why == Reason.Fell ? "Out of the level" : "Crashed";
            GUILayout.Label(head, title);
            string sub;
            if (!client) sub = "You are the host. Proceed takes everyone to a new level when they are ready.";
            else if (PendingSeed != 0) sub = "The host moved on to level " + Game.LevelCode(PendingSeed) + ". Proceed to join them, or respawn here.";
            else sub = "The host is still in this level. Respawn and keep flying.";
            GUILayout.Label(sub, hint);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Player", row, GUILayout.Width(220));
            GUILayout.Label("Score", row, GUILayout.Width(90));
            GUILayout.Label("Where", row);
            GUILayout.EndHorizontal();
            foreach (var r in Rows())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(r.Name, row, GUILayout.Width(220));
                GUILayout.Label(r.Score.ToString(CultureInfo.InvariantCulture), row, GUILayout.Width(90));
                GUILayout.Label(r.Where, row);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            bool canProceed = !client || PendingSeed != 0;
            GUI.enabled = canProceed;
            if (GUILayout.Button(client ? "Proceed to the host's level  (Enter)" : "Proceed to next level  (Enter)", GUILayout.Height(32))) Choose(true);
            GUI.enabled = true;
            if (GUILayout.Button("Respawn in this level  (R)", GUILayout.Height(32))) Choose(false);
            GUILayout.EndHorizontal();
        }

        public struct Row { public string Name; public int Score; public string Where; public bool IsLocal; }

        public static List<Row> Rows()
        {
            var rows = new List<Row>();
            var s = S;
            if (s == null) return rows;
            int hostSeed = s.CurrentRole == Session.Role.Host ? Game.CurrentSeed() : (PendingSeed != 0 ? PendingSeed : s.Seed);
            PlayerState local;
            if (Game.ReadLocalState(s.LocalId, out local))
                rows.Add(new Row { Name = s.LocalName + " (you)", Score = local.Score, Where = Where(local, hostSeed), IsLocal = true });
            foreach (var r in s.Remotes.Values)
                rows.Add(new Row { Name = r.Info.Name, Score = r.Last.Score, Where = Where(r.Last, hostSeed) });
            rows.Sort((a, b) => b.Score.CompareTo(a.Score));
            return rows;
        }

        static string Where(PlayerState p, int hostSeed)
        {
            string level = p.Seed == hostSeed ? "host's level" : "previous level";
            if ((p.Flags & 4) != 0) return "choosing, " + level;
            if ((p.Flags & 2) != 0) return "crashed, " + level;
            if ((p.Flags & 1) == 0) return "in the menu";
            return "flying, " + level;
        }
    }
}
