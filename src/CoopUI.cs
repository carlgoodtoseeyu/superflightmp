using System.Globalization;
using UnityEngine;

namespace SuperflightCoop
{
    /// <summary>IMGUI window (F1) with host, join and leave, plus name tags over puppets.</summary>
    public sealed class CoopUI
    {
        readonly CoopManager m;
        bool show;
        Rect rect = new Rect(10, 10, 360, 230);
        string port;
        string join = "127.0.0.1:4444";
        string name;
        GUIStyle tag, tagShadow;

        public CoopUI(CoopManager manager)
        {
            m = manager;
            show = Plugin.ShowUiAtStart.Value;
            port = Plugin.DefaultPort.Value.ToString(CultureInfo.InvariantCulture);
        }

        public void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) show = !show;
            if (show) Cursor.visible = true;
        }

        public void Draw()
        {
            DrawTags();
            if (!show) return;
            rect = GUI.Window(0x5f00, rect, Window, "Superflight Co-op " + Plugin.Version + "   (F1 hides)");
        }

        void Window(int id)
        {
            var s = m.Session;
            if (name == null || (s.Active && name != s.LocalName)) name = s.Active ? s.LocalName : m.ResolveName();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(50));
            name = GUILayout.TextField(name, 24);
            GUILayout.EndHorizontal();

            if (!s.Active)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(50));
                port = GUILayout.TextField(port, 5, GUILayout.Width(60));
                if (GUILayout.Button("Host (direct IP)"))
                {
                    int p; if (!int.TryParse(port, out p)) p = Plugin.DefaultPort.Value;
                    s.LocalName = name; m.HostUdp(p);
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Join", GUILayout.Width(50));
                join = GUILayout.TextField(join, 64);
                if (GUILayout.Button("Join", GUILayout.Width(60))) { s.LocalName = name; m.JoinUdp(join); }
                GUILayout.EndHorizontal();
                GUILayout.Label("Steam friends: coming next (lobby + invite).");
            }
            else
            {
                string status = s.CurrentRole == Session.Role.Host ? "Hosting on UDP port " + port
                    : s.Joined ? "Joined as player " + s.LocalId : "Connecting...";
                GUILayout.Label(status + "   world " + s.Seed);
                GUILayout.Label("Players: you (" + s.LocalName + ")");
                foreach (var r in s.Remotes.Values)
                    GUILayout.Label("  " + r.Info.Name + "  " + r.Last.Score + (r.Puppet != null && r.Puppet.Visible ? "" : "  (not flying)"));
                if (GUILayout.Button("Leave")) m.Leave();
            }
            if (!string.IsNullOrEmpty(s.LastError)) GUILayout.Label(s.LastError);
            GUI.DragWindow();
        }

        void DrawTags()
        {
            var s = m.Session;
            if (s.Remotes.Count == 0) return;
            var cam = Camera.main;
            if (cam == null) return;
            if (tag == null)
            {
                tag = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
                tag.normal.textColor = Color.white;
                tagShadow = new GUIStyle(tag);
                tagShadow.normal.textColor = Color.black;
            }
            foreach (var r in s.Remotes.Values)
            {
                if (r.Puppet == null || !r.Puppet.Visible) continue;
                Vector3 p = cam.WorldToScreenPoint(r.Puppet.Position + Vector3.up * 8f);
                if (p.z <= 0) continue;
                string text = r.Info.Name + "  " + r.Last.Score.ToString(CultureInfo.InvariantCulture);
                var size = tag.CalcSize(new GUIContent(text));
                var rc = new Rect(p.x - size.x / 2, Screen.height - p.y - size.y, size.x, size.y);
                GUI.Label(new Rect(rc.x + 1, rc.y + 1, rc.width, rc.height), text, tagShadow);
                GUI.Label(rc, text, tag);
            }
        }
    }
}
