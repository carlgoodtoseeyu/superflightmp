using System;
using System.Collections.Generic;
using SuperflightCoop.Net;
using UnityEngine;

namespace SuperflightCoop
{
    /// <summary>The visible stand-in for a remote player: a clone of the local player's body meshes (or a sphere when
    /// there is none to clone yet), tinted with the player's hue, moved toward the last received state with a short
    /// interpolation delay and velocity extrapolation. Never reads input, never has physics.</summary>
    public sealed class Puppet
    {
        const float InterpDelay = 0.1f;

        public readonly PlayerInfo Info;
        public GameObject Root;
        GameObject body;
        bool bodyIsClone;
        PlayerState a, b;      // the two newest states, a older than b
        bool haveB, haveA;
        float receivedB;
        Vector3 pos;
        Quaternion rot = Quaternion.identity;
        bool placed;
        public bool Visible { get { return body != null && body.activeSelf; } }
        public Vector3 Position { get { return pos; } }

        /// <summary>How many of the puppet's renderers a camera drew last frame (0 when off screen or not yet built).</summary>
        public int RenderedCount
        {
            get
            {
                if (body == null || !Root.activeSelf) return 0;
                int n = 0;
                foreach (var r in body.GetComponentsInChildren<Renderer>()) if (r.enabled && r.isVisible) n++;
                return n;
            }
        }
        public bool BodyIsClone { get { return bodyIsClone; } }

        Puppet(PlayerInfo info) { Info = info; }

        public static Puppet Create(PlayerInfo info)
        {
            var p = new Puppet(info);
            p.Root = new GameObject("CoopPuppet_" + info.Id);
            p.Root.hideFlags = HideFlags.DontSave;
            p.EnsureBody();
            p.Root.SetActive(false);
            return p;
        }

        public Color Colour { get { return Color.HSVToRGB(Info.Hue, 0.8f, 1f); } }

        /// <summary>Clone the local player's meshes once they exist; a sphere until then.</summary>
        void EnsureBody()
        {
            if (bodyIsClone) return;
            var src = Game.BodyParts();
            if (src != null)
            {
                if (body != null) UnityEngine.Object.Destroy(body);
                body = UnityEngine.Object.Instantiate(src);
                body.name = "body";
                StripBehaviour(body);
                body.transform.SetParent(Root.transform, false);
                var root = Game.PlayerRoot();
                if (root != null)
                {
                    body.transform.localPosition = root.InverseTransformPoint(src.transform.position);
                    body.transform.localRotation = Quaternion.Inverse(root.rotation) * src.transform.rotation;
                    body.transform.localScale = src.transform.lossyScale;
                }
                foreach (var r in body.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.materials) Game.Tint(m, Colour);
                bodyIsClone = true;
                return;
            }
            if (body == null)
            {
                body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                body.name = "sphere";
                UnityEngine.Object.Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(Root.transform, false);
                body.transform.localScale = new Vector3(6, 6, 6);
                var r = body.GetComponent<Renderer>();
                r.material.color = Colour;
            }
        }

        static void StripBehaviour(GameObject go)
        {
            // order matters: scripts first, then joints, then colliders and bodies they may depend on
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.Destroy(c);
            foreach (var c in go.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.Destroy(c);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            foreach (var c in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.Destroy(c);
            foreach (var c in go.GetComponentsInChildren<ParticleSystem>(true)) UnityEngine.Object.Destroy(c.gameObject);
            foreach (var c in go.GetComponentsInChildren<TrailRenderer>(true)) UnityEngine.Object.Destroy(c);
        }

        public void Target(PlayerState s)
        {
            if (haveB) { a = b; haveA = true; }
            b = s; haveB = true;
            receivedB = Time.realtimeSinceStartup;
            if (!placed) { pos = s.Position; rot = s.Rotation; placed = true; }
        }

        public void Tick()
        {
            if (Root == null) return;
            EnsureBody();
            bool show = haveB && (b.Flags & 1) != 0 && Time.realtimeSinceStartup - receivedB < 5f;
            if (Root.activeSelf != show) Root.SetActive(show);
            if (!show) return;

            Vector3 target = b.Position;
            Quaternion targetRot = b.Rotation;
            if (haveA)
            {
                float span = Mathf.Max(0.01f, b.Time - a.Time);
                Vector3 vel = (b.Position - a.Position) / span;
                float ahead = Time.realtimeSinceStartup - receivedB - InterpDelay;
                if (ahead > 0) target += vel * Mathf.Min(ahead, 0.25f);
            }
            float k = 1f - Mathf.Exp(-Time.deltaTime * 12f);
            pos = Vector3.Lerp(pos, target, k);
            rot = Quaternion.Slerp(rot, targetRot, k);
            Root.transform.position = pos;
            Root.transform.rotation = rot;
        }

        public void Destroy()
        {
            if (Root != null) UnityEngine.Object.Destroy(Root);
            Root = null; body = null;
        }
    }
}
