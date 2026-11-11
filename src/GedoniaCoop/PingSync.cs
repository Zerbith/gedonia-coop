using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GedoniaCoop
{
    // Pings: G marks what is in the middle of the screen (an enemy, a person,
    // a chest, or just the ground) for everyone in the same world. The mark
    // stands in the world for Life seconds with the name of who set it and the
    // distance; off screen it sits on the edge and points the way. Teammates
    // out of view get a small arrow on the edge too, so they can be found.
    // One mark per player: a new one replaces the old.
    public class PingSync
    {
        const float Life = 15f, Reach = 250f;
        public const KeyCode Key = KeyCode.G;

        class Ping { public int Sender; public string Name, Label, World; public Vector3 At; public float Until; }

        readonly Net net;
        readonly List<Ping> pings = new List<Ping>();
        public static PingSync Instance;
        public int Sent, Received;
        public static bool PingTest; // test: press G

        public PingSync(Net net) { this.net = net; Instance = this; }
        public Net Net { get { return net; } }

        public void Tick(bool inWorld, float now)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgPing || m.Sender == net.LocalId) continue;
                try { Receive(m, now); }
                catch (Exception e) { Plugin.Log.LogWarning("Ping from " + m.Sender + " unreadable: " + e.Message); }
            }
            pings.RemoveAll(p => now >= p.Until);
            if (!inWorld || PlayerScript.instance == null) return;
            bool pressed = PingTest || (Input.GetKeyDown(Key) && !ChatSync.Typing && !UICamera.inputHasFocus && InterfaceManager.instance != null && !InterfaceManager.instance.AnyPanelIsActive());
            if (pressed) { PingTest = false; Send(now); }
        }

        void Send(float now)
        {
            PlayerScript player = PlayerScript.instance;
            Camera cam = Camera.main;
            if (cam == null || player == null || player.isDead) return;
            Vector3 at; string label;
            Aim(cam, player, out at, out label);
            string world = GameManager.instance != null ? GameManager.instance.currentLoadedWorld ?? "" : "";
            Add(net.LocalId, Plugin.LocalName, label, world, at, now);
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(Plugin.LocalName); w.Write(label); w.Write(world); w.Write(at.x); w.Write(at.y); w.Write(at.z);
                w.Flush();
                net.SendMessage(Net.MsgPing, 0, ms.ToArray(), true);
            }
            Sent++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [ping] sent '" + label + "' at " + at.ToString("F1"));
        }

        // What the middle of the screen points at, not the player itself.
        static void Aim(Camera cam, PlayerScript player, out Vector3 at, out string label)
        {
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            at = ray.origin + ray.direction * 30f;
            label = Lang.T("Here", "Hier");
            RaycastHit[] hits = Physics.RaycastAll(ray, Reach, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit h in hits)
            {
                if (h.collider.GetComponentInParent<PlayerScript>() == player) continue;
                at = h.point;
                NPC n = h.collider.GetComponentInParent<NPC>();
                if (n != null && !string.IsNullOrEmpty(n.npcName))
                {
                    string nm = Lang.German ? (Translator.German(n.npcName) ?? n.npcName) : n.npcName;
                    label = n.isDead ? Lang.T(nm + " (dead)", nm + " (tot)") : n.allianceToPlayer() == ReputationLevel.Enemy ? Lang.T("Enemy: " + nm, "Gegner: " + nm) : nm;
                    at = n.transform.position + Vector3.up * 2f;
                }
                else if (h.collider.GetComponentInParent<Container>() != null) label = Lang.T("Chest", "Truhe");
                break;
            }
        }

        void Receive(Net.Message m, float now)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                string name = r.ReadString(), label = r.ReadString(), world = r.ReadString();
                Vector3 at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Add(m.Sender, name, label, world, at, now);
                Received++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " [ping] from " + name + ": '" + label + "' at " + at.ToString("F1") + " (" + world + ")");
                string mine = GameManager.instance != null ? GameManager.instance.currentLoadedWorld ?? "" : "";
                if (world == mine && InterfaceManager.instance != null)
                    InterfaceManager.instance.screenMessages.Add(Lang.T(name + " marked: " + label, name + " hat markiert: " + label), new Color(1f, 0.85f, 0.4f), 2.5f);
            }
        }

        void Add(int sender, string name, string label, string world, Vector3 at, float now)
        {
            pings.RemoveAll(p => p.Sender == sender);
            pings.Add(new Ping { Sender = sender, Name = name, Label = label, World = world, At = at, Until = now + Life });
        }

        public string Summary() { return "pings sent=" + Sent + " received=" + Received + " shown=" + pings.Count; }

        public string Describe()
        {
            string s = "";
            foreach (Ping p in pings) s += (s.Length > 0 ? "; " : "") + p.Name + ":'" + p.Label + "'@" + p.At.ToString("F0");
            return s;
        }

        // ---------- drawing (IMGUI, inside InviteUi's scaled matrix) ----------

        static readonly Color PingColor = new Color(1f, 0.82f, 0.3f), MateColor = new Color(0.45f, 0.8f, 1f);

        public static void Draw(float scale, float sw, float sh, GUIStyle style)
        {
            Camera cam = Camera.main;
            PlayerScript player = PlayerScript.instance;
            if (cam == null || player == null || InterfaceManager.instance == null || InterfaceManager.instance.AnyPanelIsActive()) return;
            string world = GameManager.instance != null ? GameManager.instance.currentLoadedWorld ?? "" : "";
            Vector3 me = player.transform.position;
            float now = Time.realtimeSinceStartup;
            placed.Clear();
            if (Instance != null)
                foreach (Ping p in Instance.pings)
                {
                    if (p.World != world) continue;
                    float left = p.Until - now;
                    Color c = PingColor; c.a = Mathf.Clamp01(left / 2f); // fades out at the end
                    Mark(cam, scale, sw, sh, style, p.At, p.Label + "\n" + p.Name + "  " + Mathf.RoundToInt(Vector3.Distance(me, p.At)) + " m", c, true);
                }
            foreach (Puppet t in Plugin.AllPuppets())
            {
                if (t.Latest == null || !Plugin.SameWorld(t.Id) || !t.Visible) continue;
                Vector3 at = t.Position + Vector3.up * 2.2f;
                Vector3 sp = cam.WorldToScreenPoint(at);
                if (sp.z > 0f && sp.x >= 0f && sp.x <= Screen.width && sp.y >= 0f && sp.y <= Screen.height) continue; // in view: its name tag shows
                Mark(cam, scale, sw, sh, style, at, t.Latest.Name + "  " + Mathf.RoundToInt(Vector3.Distance(me, t.Position)) + " m", t.Latest.Dead ? Color.gray : MateColor, false);
            }
        }

        // On screen: a diamond with the text above. Off screen (or behind): on
        // the edge, in the direction of the point, with an arrow.
        static void Mark(Camera cam, float scale, float sw, float sh, GUIStyle style, Vector3 at, string text, Color c, bool showOnScreen)
        {
            Vector3 sp = cam.WorldToScreenPoint(at);
            bool behind = sp.z < 0f;
            float x = sp.x / scale, y = (Screen.height - sp.y) / scale;
            if (behind) { x = sw - x; y = sh - y; }
            const float margin = 46f;
            bool on = !behind && x >= margin && x <= sw - margin && y >= margin && y <= sh - margin;
            if (on && !showOnScreen) return;
            Color old = GUI.color;
            GUI.color = c;
            style.fontSize = 16;
            if (on)
            {
                GUI.Label(new Rect(x - 20f, y - 20f, 40f, 40f), "◆", Big(style, 34));
                GameSkin.Label(new Rect(x - 120f, y - 66f, 240f, 44f), new GUIContent(text), style);
            }
            else
            {
                // Inside the screen minus the bottom bar (action bar, health) and the top strip.
                const float top = 70f, bottom = 140f;
                Vector2 mid = new Vector2(sw / 2f, (top + sh - bottom) / 2f), d = new Vector2(x, y) - mid;
                if (d.sqrMagnitude < 1f) d = Vector2.up;
                float halfH = (sh - bottom - top) / 2f;
                float k = Mathf.Min((sw / 2f - margin) / Mathf.Max(0.001f, Mathf.Abs(d.x)), halfH / Mathf.Max(0.001f, Mathf.Abs(d.y)));
                Vector2 e = mid + d * k;
                // Marks in the same direction: their labels stack instead of overlapping.
                Vector2 label = e - d.normalized * 40f;
                foreach (Vector2 q in placed) if (Mathf.Abs(q.x - label.x) < 150f && Mathf.Abs(q.y - label.y) < 38f) label.y = q.y + 40f;
                placed.Add(label);
                float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg + 90f;
                Matrix4x4 m = GUI.matrix;
                GUIUtility.RotateAroundPivot(ang, new Vector2(e.x * scale, e.y * scale));
                GUI.Label(new Rect(e.x - 18f, e.y - 20f, 36f, 36f), "▲", Big(style, 30));
                GUI.matrix = m;
                GameSkin.Label(new Rect(label.x - 100f, label.y - 18f, 200f, 36f), new GUIContent(text), style);
            }
            GUI.color = old;
        }

        static readonly List<Vector2> placed = new List<Vector2>();
        static GUIStyle big;
        static GUIStyle Big(GUIStyle like, int size)
        {
            if (big == null) { big = new GUIStyle(like) { alignment = TextAnchor.MiddleCenter, wordWrap = false }; big.normal.background = null; big.normal.textColor = Color.white; }
            big.fontSize = size;
            return big;
        }
    }
}
