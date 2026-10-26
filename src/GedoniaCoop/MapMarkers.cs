using System.Collections.Generic;
using UnityEngine;

namespace GedoniaCoop
{
    // The other players on the game's maps, drawn with the game's own pieces:
    //  - world map (M): a copy of the player arrow, tinted, with the name;
    //  - minimap: a copy of the minimap arrow; a player further away sits on
    //    the rim and points the way;
    //  - compass (minimap turned off in the options): a copy of the quest
    //    marker on the compass strip.
    // Only players in the same world; someone inside a dungeon is shown at
    // the last place outside, greyed, "(in a dungeon)".
    public class MapMarkers
    {
        static readonly Color Tint = new Color(0.35f, 0.75f, 1f);
        static readonly Color Away = new Color(0.8f, 0.8f, 0.8f, 1f);

        class Marker
        {
            public GameObject World, Mini, Compass;
            public UILabel Label;
            public UITexture WorldArrow, MiniArrow;
            public UISprite CompassSprite;
            public Vector3 LastOutside;
            public bool HasOutside;
        }

        readonly Dictionary<int, Marker> markers = new Dictionary<int, Marker>();
        InterfaceManager builtFor;

        public void Tick(IEnumerable<Puppet> puppets, string world, bool localInDungeon)
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null || PlayerScript.instance == null || im.worldMapPlayer == null) { Clear(); return; }
            if (im != builtFor) { Clear(); builtFor = im; } // new game scene
            HashSet<int> seen = new HashSet<int>();
            foreach (Puppet p in puppets)
            {
                PlayerState s = p.Latest;
                if (s == null) continue;
                seen.Add(s.Id);
                Marker m;
                if (!markers.TryGetValue(s.Id, out m)) { m = Build(im, s.Name); markers[s.Id] = m; }
                // Portal dungeons are built far outside the world (z 2700 and
                // more); there the map shows the last place outside too.
                bool away = s.InDungeon || OffWorld(s.X, s.Z);
                if (!away) { m.LastOutside = new Vector3(s.X, s.Y, s.Z); m.HasOutside = true; }
                bool sameWorld = s.World == world && s.World.Length > 0;
                bool show = sameWorld && m.HasOutside;
                bool localAway = localInDungeon || OffWorld(PlayerScript.instance.transform.position.x, PlayerScript.instance.transform.position.z);
                Place(im, m, s, show, away, localAway);
            }
            List<int> gone = null;
            foreach (int id in markers.Keys) if (!seen.Contains(id)) { if (gone == null) gone = new List<int>(); gone.Add(id); }
            if (gone != null) foreach (int id in gone) { Destroy(markers[id]); markers.Remove(id); }
        }

        // A white arrow pointing up with a dark rim, so the tint shows (the
        // game's own arrow is green and would only turn murky).
        static Texture2D arrow;
        static Texture2D Arrow()
        {
            if (arrow != null) return arrow;
            const int N = 64;
            arrow = new Texture2D(N, N, TextureFormat.RGBA32, false);
            arrow.filterMode = FilterMode.Bilinear;
            Vector2 a = new Vector2(N / 2f, N - 4f), b = new Vector2(8f, 6f), c = new Vector2(N - 8f, 6f), notch = new Vector2(N / 2f, 20f);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    // arrow head: triangle a-b-c minus the notch triangle b-notch-c
                    bool inside = InTri(p, a, b, notch) || InTri(p, a, notch, c);
                    float edge = Mathf.Min(Mathf.Min(Dist(p, a, b), Dist(p, b, notch)), Mathf.Min(Dist(p, notch, c), Dist(p, c, a)));
                    Color col = new Color(0, 0, 0, 0);
                    if (inside) col = edge < 3f ? new Color(0.05f, 0.05f, 0.1f, 1f) : Color.white;
                    arrow.SetPixel(x, y, col);
                }
            arrow.Apply();
            return arrow;
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static float Dist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        static bool OffWorld(float x, float z) { return Mathf.Abs(x) > 2000f || Mathf.Abs(z) > 2000f; }

        // The world map window shows about this much of the region around
        // its centre (the game itself switches regions past 440/400).
        const float MapHalfWidth = 430f, MapHalfHeight = 390f;

        Marker Build(InterfaceManager im, string name)
        {
            Marker m = new Marker();
            // World map: the player arrow (UITexture) next to the original.
            m.World = Object.Instantiate(im.worldMapPlayer.gameObject, im.worldMapPlayer.parent);
            m.World.name = "CoopPlayerIcon";
            m.WorldArrow = m.World.GetComponentInChildren<UITexture>(true);
            if (m.WorldArrow != null) { m.WorldArrow.mainTexture = Arrow(); m.WorldArrow.width = Mathf.RoundToInt(m.WorldArrow.width * 1.4f); m.WorldArrow.height = Mathf.RoundToInt(m.WorldArrow.height * 1.4f); }
            UILabel font = null;
            foreach (UILabel l in im.worldMapPlayer.parent.parent.GetComponentsInChildren<UILabel>(true)) { font = l; break; }
            if (font != null)
            {
                GameObject lg = Object.Instantiate(font.gameObject, m.World.transform);
                lg.name = "CoopName";
                foreach (Component c in lg.GetComponents<Component>())
                    if (!(c is Transform) && !(c is UILabel)) Object.Destroy(c);
                m.Label = lg.GetComponent<UILabel>();
                m.Label.fontSize = 22;
                m.Label.pivot = UIWidget.Pivot.Bottom;
                m.Label.overflowMethod = UILabel.Overflow.ResizeFreely;
                m.Label.effectStyle = UILabel.Effect.Outline;
                m.Label.effectColor = Color.black;
                m.Label.depth = (m.WorldArrow != null ? m.WorldArrow.depth : 50) + 2;
                lg.transform.localPosition = new Vector3(0f, 18f, 0f);
                lg.transform.localScale = Vector3.one;
                lg.transform.localRotation = Quaternion.identity;
            }
            // Minimap: the arrow texture, inside the moving map.
            UITexture own = im.minimapPlayer.GetComponentInChildren<UITexture>(true);
            if (own != null && im.minimapParent != null)
            {
                m.Mini = Object.Instantiate(own.gameObject, im.minimapPlayer.parent); // beside the own arrow, over the camera picture
                m.Mini.name = "CoopPlayerMini";
                m.MiniArrow = m.Mini.GetComponent<UITexture>();
                m.MiniArrow.mainTexture = Arrow();
                m.MiniArrow.depth = own.depth - 1; // under the own arrow, over the camera picture
            }
            // Compass: the quest marker on the strip.
            if (im.markerOnMinimap != null)
            {
                m.Compass = Object.Instantiate(im.markerOnMinimap, im.markerOnMinimap.transform.parent.parent); // on the fixed panel, not the scrolling strip
                m.Compass.name = "CoopPlayerCompass";
                m.CompassSprite = m.Compass.GetComponentInChildren<UISprite>(true);
            }
            Plugin.Log.LogInfo("Map markers added for " + name);
            return m;
        }

        void Place(InterfaceManager im, Marker m, PlayerState s, bool show, bool theirDungeon, bool localInDungeon)
        {
            Vector3 pos = theirDungeon ? m.LastOutside : new Vector3(s.X, s.Y, s.Z);
            Color c = theirDungeon ? Away : Tint;

            // World map: same formula as the game's own arrow.
            bool worldShown = show && !localInDungeon;
            if (m.World.activeSelf != worldShown) m.World.SetActive(worldShown);
            if (worldShown)
            {
                Vector3 lp = new Vector3(pos.x, pos.z, 0f) * im.worldmapScale + im.worldmapdelta + im.hubWorldDelta * im.worldmapScale;
                // Beyond the shown part of the map: on its edge, pointing there.
                bool edge = Mathf.Abs(lp.x) > MapHalfWidth || Mathf.Abs(lp.y) > MapHalfHeight;
                if (edge)
                {
                    float k = Mathf.Min(MapHalfWidth / Mathf.Max(1f, Mathf.Abs(lp.x)), MapHalfHeight / Mathf.Max(1f, Mathf.Abs(lp.y)));
                    lp = new Vector3(lp.x * k, lp.y * k, 0f);
                }
                m.World.transform.localPosition = lp;
                m.World.transform.localEulerAngles = new Vector3(0f, 0f, edge ? Mathf.Atan2(lp.y, lp.x) * Mathf.Rad2Deg - 90f : -s.Yaw);
                if (m.WorldArrow != null) m.WorldArrow.color = c;
                if (m.Label != null)
                {
                    m.Label.transform.rotation = im.worldMapPlayer.parent.rotation; // keep the name upright
                    m.Label.text = s.Name + (theirDungeon ? Lang.T(" (in a dungeon)", " (im Dungeon)") : "");
                    m.Label.color = c;
                }
            }

            // Minimap or compass, whichever the player uses.
            bool mini = im.minimapNotCompass && show && !localInDungeon && !theirDungeon;
            if (m.Mini != null)
            {
                if (m.Mini.activeSelf != mini) m.Mini.SetActive(mini);
                if (mini)
                {
                    // The minimap is a north-up camera picture; the own arrow sits
                    // in its middle. Offset = distance on the ground scaled to the
                    // picture, clamped to the rim for players further away.
                    Transform t = m.Mini.transform;
                    Transform ownArrow = im.minimapPlayer.GetComponentInChildren<UITexture>(true).transform;
                    Camera cam = MinimapCamera.instance != null ? MinimapCamera.instance.GetComponent<Camera>() : null;
                    float metres = cam != null && cam.orthographic ? cam.orthographicSize : 40f;
                    float rim = 0.10f / Mathf.Max(1e-6f, t.parent.lossyScale.x);
                    Vector3 d = pos - PlayerScript.instance.transform.position;
                    Vector3 off = new Vector3(d.x, d.z, 0f) / metres * rim;
                    if (off.magnitude > rim) off = off.normalized * rim;
                    t.localPosition = im.minimapPlayer.localPosition + off;
                    t.localEulerAngles = new Vector3(0f, 0f, -s.Yaw);
                    t.localScale = ownArrow.localScale * 0.85f;
                    m.MiniArrow.color = c;
                }
            }
            bool compass = !im.minimapNotCompass && show && !localInDungeon;
            if (m.Compass != null)
            {
                if (m.Compass.activeSelf != compass) m.Compass.SetActive(compass);
                if (compass)
                {
                    // The strip shows 1000 units per full turn (the game's 2.777 per
                    // degree); the middle of the panel is where the player looks.
                    Vector3 d = pos - PlayerScript.instance.transform.position;
                    float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    float x = Mathf.DeltaAngle(PlayerScript.instance.transform.eulerAngles.y, bearing) * 2.777777f;
                    UIPanel strip = m.Compass.transform.parent.GetComponent<UIPanel>();
                    float half = strip != null && strip.clipping != UIDrawCall.Clipping.None ? strip.finalClipRegion.z / 2f - 12f : 300f;
                    x = Mathf.Clamp(x, -half, half); // behind or beside: at the edge on that side
                    float centre = strip != null && strip.clipping != UIDrawCall.Clipping.None ? strip.finalClipRegion.x : 0f;
                    m.Compass.transform.localPosition = new Vector3(centre + x, im.markerOnMinimap.transform.localPosition.y, 0f);
                    if (m.CompassSprite != null) m.CompassSprite.color = c;
                }
            }
        }

        static void Destroy(Marker m)
        {
            if (m.World != null) Object.Destroy(m.World);
            if (m.Mini != null) Object.Destroy(m.Mini);
            if (m.Compass != null) Object.Destroy(m.Compass);
        }

        public void Clear()
        {
            foreach (Marker m in markers.Values) Destroy(m);
            markers.Clear();
            builtFor = null;
        }

        // Test: where the markers are (screen and map space).
        public void Log()
        {
            foreach (KeyValuePair<int, Marker> kv in markers)
                Plugin.Log.LogInfo("[map] ownYaw=" + PlayerScript.instance.transform.eulerAngles.y.ToString("F0") + " player " + kv.Key + " world=" + kv.Value.World.activeSelf + " " + kv.Value.World.transform.localPosition.ToString("F1") +
                    " mini=" + (kv.Value.Mini != null && kv.Value.Mini.activeSelf) + (kv.Value.Mini != null ? " " + kv.Value.Mini.transform.localPosition.ToString("F1") : "") +
                    " compass=" + (kv.Value.Compass != null ? kv.Value.Compass.activeSelf + " " + kv.Value.Compass.transform.localPosition.ToString("F0") : "-") + " label='" + (kv.Value.Label != null ? kv.Value.Label.text : "") + "'" +
                    (kv.Value.Mini != null ? " miniWorld=" + kv.Value.Mini.transform.position.ToString("F3") + " centre=" + InterfaceManager.instance.minimapParent.parent.position.ToString("F3") +
                        " parentLossy=" + InterfaceManager.instance.minimapParent.lossyScale.ToString("F5") + " depth=" + kv.Value.MiniArrow.depth + " panel=" + (kv.Value.MiniArrow.panel != null ? kv.Value.MiniArrow.panel.name : "-") : ""));
        }
    }
}
