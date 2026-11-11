using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Co-op death: Gedonia has no revive - its death screen only loads a save,
    // which would turn back the world (and, for the host, everyone's). In
    // co-op a fallen player stands up again after a short wait at a respawn
    // crystal instead:
    //  - every village has one (placed when the player comes near);
    //  - touching one binds the player to it (kept per save in BepInEx/config);
    //  - unbound, the nearest village already explored counts; in a world
    //    without crystals, the point where the player arrived in it.
    // Alone (not connected) the game's own death screen stays as it is.
    public class RespawnStones
    {
        // Settlements with a crystal. Gedonia: SubZone names (their colliders give
        // the middle). The Northern Kingdoms have no village SubZones: middles
        // where buildings cluster near the world map labels (region 11 of the map).
        // Spot: where the crystal stands (x, z), measured once with everything
        // loaded (test step gotocrystal with -coop-test-crystalwait). Searching
        // on each PC placed it differently where the water came in late (a
        // friend's Weystane crystal stood in the pond).
        struct Village { public string World, Name; public float X, Z; public bool Fixed; public float SX, SZ; public bool HasSpot, Statue; }
        static readonly Village[] Villages =
        {
            // measured 2026-10-26 (survey run, 15 s settle, room around, 8 m from water; screenshots checked)
            S("Weystane", 733.4f, 638.6f), G("Steepport", 93.1f, 353.6f), G("Stawford", 1440.2f, 266.8f), G("Fayreach", 762.2f, -701.4f),
            G("Korram", 2123.3f, 420.0f), S("Ertoron", 1302.1f, -423.6f), G("Explorer's camp", 566.3f, -1021.9f),
            N("Erynard", -440f, 240f, -440.0f, 240.0f), N("Ironclad clan", 220f, -835f, 220.0f, -829.0f), N("Sons of Thunder", -1070f, 1255f, -1076.0f, 1244.6f),
        };
        static Village G(string name, float sx, float sz) { return new Village { World = "Gedonia", Name = name, SX = sx, SZ = sz, HasSpot = true }; }
        // The village has an angel statue of its own (SM_Prop_Statue_Angel_01): that is the point.
        static Village S(string name, float sx, float sz) { return new Village { World = "Gedonia", Name = name, SX = sx, SZ = sz, HasSpot = true, Statue = true }; }
        static Village N(string name, float x, float z, float sx, float sz) { return new Village { World = "NorthernKingdoms", Name = name, X = x, Z = z, Fixed = true, SX = sx, SZ = sz, HasSpot = true }; }

        // The ground at a measured spot (not water); false while it hasn't streamed in.
        static bool Ground(Village v, out Vector3 at)
        {
            at = Vector3.zero;
            int water = LayerMask.NameToLayer("Water");
            int mask = water >= 0 ? ~(1 << water) : ~0;
            bool found = false;
            // The terrain itself (a tree crown or roof above must not carry it).
            foreach (RaycastHit hit in Physics.RaycastAll(new Vector3(v.SX, 600f, v.SZ), Vector3.down, 1200f, mask, QueryTriggerInteraction.Ignore))
                if (hit.collider.name.StartsWith("Terrain") && (!found || hit.point.y > at.y)) { at = hit.point; found = true; }
            return found;
        }
        readonly HashSet<string> visited = new HashSet<string>(); // crystals placed this session: been there

        // The village's middle, or false when unknown (its SubZone not found).
        static bool Middle(Village v, out Vector3 center)
        {
            center = Vector3.zero;
            if (v.Fixed) { center = new Vector3(v.X, 200f, v.Z); return true; }
            SubZone z = GameManager.instance != null && GameManager.instance.subLocations != null ? Zone(v.Name) : null;
            Collider c = z != null ? z.GetComponent<Collider>() : null;
            if (c == null) return false;
            center = c.bounds.center;
            return true;
        }
        const float BindRange = 3f, ReviveDelay = 10f, SpawnRange = 120f; // water surfaces stream in late: place close up
        public static float SettleSeconds = 3f; // test -coop-test-crystalwait (survey of the spots)
        public static bool Survey; // test: search the spots again instead of using the measured ones

        readonly Dictionary<string, GameObject> stones = new Dictionary<string, GameObject>();
        readonly Dictionary<string, Vector3> spots = new Dictionary<string, Vector3>();
        readonly Dictionary<string, float> nearSince = new Dictionary<string, float>();
        string bound = "", loadedFor = "", world = "", pendingVillage = "";
        Vector3 arrival;
        float reviveAt = -1f, invincibleUntil = -1f;
        public static string Banner = "";
        public int Revives, Binds;

        static string ConfigFile { get { return Path.Combine(Paths.ConfigPath, "gedonia-coop-respawn.txt"); } }
        // Per character: save names rotate (autosave slots), the character's name stays.
        static string SaveKey { get { return PlayerScript.instance != null && !string.IsNullOrEmpty(PlayerScript.instance.characterName) ? "character:" + PlayerScript.instance.characterName : "?"; } }

        public void Tick(PlayerScript player, bool connected, float now, Net net)
        {
            if (net != null) ReadRevives(player, net, now);
            string w = GameManager.instance != null ? GameManager.instance.currentLoadedWorld ?? "" : "";
            if (w != world)
            {
                // New (or reloaded) world: crystals of the old one are gone with it.
                world = w;
                arrival = player.transform.position;
                foreach (GameObject g in stones.Values) if (g != null) UnityEngine.Object.Destroy(g);
                stones.Clear();
                spots.Clear();
            }
            if (loadedFor != SaveKey) { loadedFor = SaveKey; bound = LoadBinding(); }
            PlaceAndBind(player);
            DeathButtons(connected);
            TickMarkers(player);
            PauseButtons(connected);

            if (invincibleUntil > 0f && now >= invincibleUntil) { Traverse.Create(player).Field("invincibility").SetValue(false); invincibleUntil = -1f; }
            if (!connected) { reviveAt = -1f; Banner = ""; HelpHint = ""; HelpProgress = -1f; Spectate.Tick(player, false); return; }
            TickHelper(player, net, now);
            Spectate.Tick(player, player.isDead && reviveAt >= 0f);
            if (player.isDead && reviveAt < 0f)
            {
                // A living teammate in this world can come and stand us up: wait longer.
                downed = AnyHelper(player);
                float wait = downed ? DownTime : ReviveDelay;
                reviveAt = now + wait;
                downSince = now;
                SessionStats.Died();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Fallen in co-op; " + (downed ? "down, a teammate can stand us up; " : "") + "reviving in " + wait + " s at " + TargetName(player));
            }
            if (reviveAt >= 0f)
            {
                if (!player.isDead) { reviveAt = -1f; Banner = ""; return; } // revived by other means
                // Nobody left who could come (all fallen or gone): no point waiting the long time.
                if (downed && !AnyHelper(player) && reviveAt - now > ReviveDelay) { downed = false; reviveAt = now + ReviveDelay; Plugin.Log.LogInfo(Plugin.Stamp() + " No teammate can help any more; reviving in " + ReviveDelay + " s"); }
                // Space: give up waiting, straight to the respawn point.
                if (downed && now - downSince > 1f && (Input.GetKeyDown(KeyCode.Space) || GiveUpTest)) { GiveUpTest = false; reviveAt = now; Plugin.Log.LogInfo(Plugin.Stamp() + " Down: respawn now (Space)"); }
                int secs = Mathf.CeilToInt(reviveAt - now);
                string at = TargetName(player); string de = Lang.German ? Translator.German(at) : null;
                if (downed)
                {
                    Banner = Lang.T("You are down.  A teammate can help you up (hold F).  Respawn in " + secs + " s at " + at + "  (Space: now)",
                        "Du liegst am Boden.  Ein Mitspieler kann dir aufhelfen (F halten).  Wiederbelebung in " + secs + " s bei " + (de ?? at) + "  (Leertaste: sofort)");
                    Countdown = Lang.T("A teammate can help you up\nRespawn in " + secs + " s at " + at + "\nSpace: respawn now",
                        "Ein Mitspieler kann dir aufhelfen\nWiederbelebung in " + secs + " s bei " + (de ?? at) + "\nLeertaste: sofort wiederbeleben");
                }
                else
                {
                    Banner = Lang.T("You have fallen.  Respawning in " + secs + " s at " + at, "Du bist gefallen.  Wiederbelebung in " + secs + " s bei " + (de ?? at));
                    Countdown = Lang.T("Respawning in " + secs + " s\nat " + at, "Wiederbelebung in " + secs + " s\nbei " + (de ?? at));
                }
                if (Spectate.Active) Countdown += Lang.T("\nWatching " + Spectate.Watching, "\nDu schaust " + Spectate.Watching + " zu");
                ShowCountdown();
                if (now >= reviveAt) Revive(player, now, null);
            }
        }

        // ---------- teammates help a fallen player up ----------
        //
        // A fallen player lies down for DownTime while a living teammate is in
        // the same world. Holding F (the game's Interact) next to the body for
        // HelpHold seconds sends MsgRevive to that player, who stands up on the
        // spot with half health. Otherwise the respawn at the statue follows.
        const float DownTime = 30f, HelpRange = 2.6f, HelpHold = 3f;
        bool downed;
        float downSince;
        int helping;
        float helpSince = -1f;
        public static string HelpHint = "";
        public static float HelpProgress = -1f; // 0..1 while holding F, else -1
        public static bool HelpHeldTest, GiveUpTest; // test: hold F / press Space
        public int Helped, HelpedUp;

        // A living teammate close enough to come over (250 m; a portal dungeon lies far outside the world, so
        // someone outside it does not count).
        const float HelperRange = 250f;
        static bool AnyHelper(PlayerScript player)
        {
            foreach (Puppet p in Plugin.AllPuppets())
                if (p.Latest != null && !p.Latest.Dead && Plugin.SameWorld(p.Id) && Vector3.Distance(new Vector3(p.Latest.X, p.Latest.Y, p.Latest.Z), player.transform.position) <= HelperRange) return true;
            return false;
        }

        void TickHelper(PlayerScript player, Net net, float now)
        {
            HelpHint = "";
            HelpProgress = -1f;
            Puppet target = null;
            if (!player.isDead && !player.isMounted && net != null)
            {
                float best = HelpRange;
                foreach (Puppet p in Plugin.AllPuppets())
                {
                    if (p.Latest == null || !p.Latest.Dead || !Plugin.SameWorld(p.Id)) continue;
                    float d = Vector3.Distance(player.transform.position, p.Position);
                    if (d <= best) { best = d; target = p; }
                }
            }
            if (target == null) { helping = 0; helpSince = -1f; return; }
            string name = target.Latest.Name;
            bool held = HelpHeldTest;
            try { if (!held && vp_Input.Instance != null) held = vp_Input.GetButton("Interact"); } catch (Exception) { }
            if (!held || helping != target.Id)
            {
                if (helping != 0 && helpSince >= 0f) Plugin.Log.LogInfo(Plugin.Stamp() + " Help: let go of " + name + " after " + (now - helpSince).ToString("F1") + " s");
                helping = held ? target.Id : 0;
                helpSince = held ? now : -1f;
                if (held) Plugin.Log.LogInfo(Plugin.Stamp() + " Help: holding F next to " + name);
            }
            if (!held) { HelpHint = Lang.T("Hold F: help " + name + " up", "F halten: " + name + " aufhelfen"); return; }
            HelpProgress = Mathf.Clamp01((now - helpSince) / HelpHold);
            HelpHint = Lang.T("Helping " + name + " up ...", "Hilfst " + name + " auf ...");
            if (HelpProgress < 1f) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(Plugin.LocalName);
                w.Flush();
                net.SendMessage(Net.MsgRevive, target.Id, ms.ToArray(), true);
            }
            Helped++;
            helping = 0; helpSince = -1f; HelpHeldTest = false;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Help: stood " + name + " (" + target.Id + ") up");
            if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessages.Add(Lang.T("You helped " + name + " up.", "Du hast " + name + " aufgeholfen."), Color.white, 2.5f);
        }

        void ReadRevives(PlayerScript player, Net net, float now)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgRevive || m.Sender == net.LocalId || m.Target != net.LocalId) continue;
                string by = "?";
                try { using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload))) by = r.ReadString(); } catch (Exception) { }
                if (!player.isDead) { Plugin.Log.LogInfo(Plugin.Stamp() + " Help from " + by + " ignored (not down)"); continue; }
                Revive(player, now, by);
            }
        }

        // The death screen (InterfaceManager.panels[8], "DeathMessage") offers "Load
        // game" and "Return to menu". Loading would turn back this player's world
        // (for the host: everyone's), and the player stands up again by itself. So
        // while other players are connected both buttons are hidden and the panel
        // shows only the respawn countdown, in a copy of its own title label (the
        // game's font, colour and outline). Alone the panel is the game's again.
        public static bool HideButtons;
        public static string Countdown = "";
        public static bool CountdownShown; // in the panel: no extra banner
        GameObject deathLoad, deathMenu;
        UILabel deathTitle, countdownLabel;

        bool FindDeathPanel()
        {
            if (deathLoad != null) return true;
            InterfaceManager im = InterfaceManager.instance;
            if (im == null || im.panels == null || im.panels.Length <= 8 || im.panels[8] == null) return false;
            foreach (UIButton b in im.panels[8].GetComponentsInChildren<UIButton>(true))
                foreach (EventDelegate d in b.onClick)
                {
                    if (d == null) continue;
                    if (d.methodName == "OnLoad") deathLoad = b.gameObject;
                    else if (d.methodName == "OnReturnToMainMenu") deathMenu = b.gameObject;
                }
            if (deathLoad == null) return false;
            foreach (UILabel l in im.panels[8].GetComponentsInChildren<UILabel>(true))
                if (l.GetComponentInParent<UIButton>() == null) { deathTitle = l; break; }
            Plugin.Log.LogInfo("Death screen: buttons " + (deathLoad != null) + "/" + (deathMenu != null) + ", title " + (deathTitle != null ? "'" + deathTitle.text + "'" : "none"));
            return true;
        }

        void DeathButtons(bool connected)
        {
            HideButtons = connected;
            if (!connected) CountdownShown = false;
            if (!FindDeathPanel()) return;
            if (!connected && countdownLabel != null && countdownLabel.gameObject.activeSelf) countdownLabel.gameObject.SetActive(false);
            if (deathLoad.activeSelf != connected) return; // already as it should be
            deathLoad.SetActive(!connected);
            if (deathMenu != null) deathMenu.SetActive(!connected);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Death screen: buttons " + (connected ? "hidden (players connected)" : "shown again (alone)"));
        }

        // The countdown where the buttons were (at the lower one: the panel's middle).
        void ShowCountdown()
        {
            if (!HideButtons || !FindDeathPanel()) return;
            if (countdownLabel == null && deathTitle != null)
            {
                GameObject g = UnityEngine.Object.Instantiate(deathTitle.gameObject, deathTitle.transform.parent);
                g.name = "CoopRespawnCountdown";
                foreach (UILocalize loc in g.GetComponentsInChildren<UILocalize>(true)) UnityEngine.Object.Destroy(loc);
                countdownLabel = g.GetComponent<UILabel>();
                Vector3 a = deathLoad.transform.position, b = deathMenu != null ? deathMenu.transform.position : a;
                g.transform.position = b; // about the middle of the panel
                g.transform.localScale = deathTitle.transform.localScale;
                countdownLabel.overflowMethod = UILabel.Overflow.ResizeFreely;
                countdownLabel.alignment = NGUIText.Alignment.Center;
                countdownLabel.pivot = UIWidget.Pivot.Center;
                Plugin.Log.LogInfo("Death screen: countdown label added at " + g.transform.localPosition);
            }
            if (countdownLabel == null) return;
            if (!countdownLabel.gameObject.activeSelf) countdownLabel.gameObject.SetActive(true);
            if (countdownLabel.text != Countdown) countdownLabel.text = Countdown;
            CountdownShown = true;
        }

        // The pause menu's "Load game" (same OnLoad) is hidden too while players are
        // connected; the buttons below it move up into the gap ("Save game" stays).
        // Set every frame by the plugin (also outside the world).
        public static bool Connected;
        GameObject pauseLoad;
        readonly List<Transform> pauseBelow = new List<Transform>();
        float pauseStep;

        void PauseButtons(bool connected)
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null || im.pauseMenuSP == null) return;
            if (pauseLoad == null)
            {
                foreach (UIButton b in im.pauseMenuSP.GetComponentsInChildren<UIButton>(true))
                    foreach (EventDelegate d in b.onClick)
                        if (d != null && d.methodName == "OnLoad") pauseLoad = b.gameObject;
                if (pauseLoad == null) return;
                Transform parent = pauseLoad.transform.parent;
                float y = pauseLoad.transform.localPosition.y, next = float.MinValue;
                foreach (UIButton b in parent.GetComponentsInChildren<UIButton>(true))
                {
                    if (b.transform.parent != parent || b.gameObject == pauseLoad) continue;
                    float by = b.transform.localPosition.y;
                    if (by < y - 1f) { pauseBelow.Add(b.transform); if (by > next) next = by; }
                }
                pauseStep = next > float.MinValue ? y - next : 0f;
                Plugin.Log.LogInfo("Pause menu: load button found, " + pauseBelow.Count + " buttons below, step " + pauseStep);
            }
            if (pauseLoad.activeSelf != connected) return; // already as it should be
            pauseLoad.SetActive(!connected);
            foreach (Transform t in pauseBelow) t.localPosition += new Vector3(0f, connected ? pauseStep : -pauseStep, 0f);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Pause menu: load button " + (connected ? "hidden (players connected)" : "shown again (alone)"));
        }

        // Keyboard or gamepad could still reach a hidden button: in the world, with
        // players connected, loading does nothing, and the death screen's "return to
        // menu" neither. The main menu's loading is not affected.
        [HarmonyPatch(typeof(InterfaceManager), "OnLoad")]
        static class DeathLoadPatch
        {
            static bool Prefix(InterfaceManager __instance)
            {
                if (!Connected || PlayerScript.instance == null) return true;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Load blocked in co-op");
                return false;
            }
        }

        [HarmonyPatch(typeof(InterfaceManager), "OnReturnToMainMenu")]
        static class DeathMenuPatch
        {
            static bool Prefix(InterfaceManager __instance)
            {
                if (!Connected || __instance.panels == null || __instance.panels.Length <= 8 || __instance.panels[8] == null || !__instance.panels[8].activeSelf) return true;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Death screen: return to menu blocked in co-op");
                return false;
            }
        }

        // ---------- respawn points: angel statues ----------
        //
        // Villages with an angel statue of their own (Weystane, Ertoron) use it: only
        // an invisible spot for F is added. Everywhere else a copy of that statue on
        // a plinth stands at the measured spot, built from the game's own meshes
        // (SM_Prop_Statue_Angel_01, SM_Prop_Statue_Plinth_03, material
        // PolygonFantasyKingdom_Mat_01_A) once they are loaded - until then the old
        // crystal stands in and is swapped as soon as the model can be had.
        readonly Dictionary<string, string> kinds = new Dictionary<string, string>();
        static Mesh angelMesh, plinthMesh;
        static Material statueMat;
        static float nextModelTry;

        static bool StatueModel()
        {
            if (angelMesh != null && statueMat != null) return true;
            if (Time.realtimeSinceStartup < nextModelTry) return false;
            nextModelTry = Time.realtimeSinceStartup + 5f;
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                if (m == null) continue;
                if (m.name == "SM_Prop_Statue_Angel_01") angelMesh = m;
                else if (m.name == "SM_Prop_Statue_Plinth_03" || (plinthMesh == null && m.name.StartsWith("SM_Prop_Statue_Plinth"))) plinthMesh = m;
            }
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.name == "PolygonFantasyKingdom_Mat_01_A") { statueMat = m; break; }
            if (angelMesh != null && statueMat != null)
                Plugin.Log.LogInfo("Respawn statue model found: " + angelMesh.name + " (" + angelMesh.bounds.size.ToString("F2") + "), plinth " + (plinthMesh != null ? plinthMesh.name + " (" + plinthMesh.bounds.size.ToString("F2") + ")" : "none"));
            return angelMesh != null && statueMat != null;
        }

        static GameObject Part(string name, Transform parent, Mesh mesh, Vector3 local)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = statueMat;
            return go;
        }

        GameObject BuildPoint(Village vil, Vector3 at, Vector3 middle, out string kind)
        {
            GameObject g;
            float interactY = 1f, radius = 1.2f;
            if (vil.Statue)
            {
                g = new GameObject("CoopRespawnStatue (" + vil.Name + ", the village's own)");
                g.transform.position = at;
                kind = "the village's own angel statue";
                interactY = 1.8f; radius = 1.9f;
            }
            else if (StatueModel())
            {
                g = new GameObject("CoopRespawnStatue (" + vil.Name + ")");
                g.transform.position = at;
                // Facing the village's middle.
                Vector3 to = middle - at; to.y = 0f;
                if (to.sqrMagnitude > 1f) g.transform.rotation = Quaternion.LookRotation(to.normalized);
                float top = 0f;
                if (plinthMesh != null) { Part("Plinth", g.transform, plinthMesh, Vector3.zero); top = plinthMesh.bounds.max.y; }
                Part("Angel", g.transform, angelMesh, new Vector3(0f, top, 0f));
                // Solid, so nobody walks through it.
                Bounds b = angelMesh.bounds;
                BoxCollider box = g.AddComponent<BoxCollider>();
                float w = plinthMesh != null ? Mathf.Max(plinthMesh.bounds.size.x, plinthMesh.bounds.size.z) : Mathf.Max(b.size.x, b.size.z) * 0.6f;
                box.size = new Vector3(w, top + b.size.y, w);
                box.center = new Vector3(0f, (top + b.size.y) / 2f, 0f);
                kind = "angel statue copy";
                interactY = Mathf.Min(1.5f, top + 0.5f); radius = Mathf.Max(1.6f, w * 0.75f);
            }
            else
            {
                GameObject proto = Resources.Load<GameObject>("Expeditions/MinableCrystals");
                if (proto == null) { kind = "none"; return null; }
                g = UnityEngine.Object.Instantiate(proto, at, Quaternion.identity);
                // Only its look: no script (it is a minable crystal) and nothing
                // to hit or interact with.
                foreach (MonoBehaviour mb in g.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                foreach (Collider col in g.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(col);
                g.name = "CoopRespawnCrystal (" + vil.Name + ")";
                kind = "crystal (statue model not loaded yet)";
            }
            // Something the game's interaction finds (OverlapSphere 2.7 m on
            // Default/NPCs/Player, in front of the camera): F binds.
            GameObject spot = new GameObject("CoopCrystalInteract");
            spot.transform.SetParent(g.transform, false);
            spot.transform.localPosition = new Vector3(0f, interactY, 0f);
            spot.layer = 0;
            try { spot.tag = "Interactable"; } catch (Exception) { }
            SphereCollider sc = spot.AddComponent<SphereCollider>();
            sc.isTrigger = true;
            sc.radius = radius;
            CoopCrystal cc = spot.AddComponent<CoopCrystal>();
            cc.Village = vil.Name;
            cc.Owner = this;
            cc.Statue = !kind.StartsWith("crystal");
            g.SetActive(true);
            return g;
        }

        // ---------- map symbols for the respawn points ----------
        //
        // A small gold angel (halo, wings, robe; drawn here, no game art) on the
        // minimap and the world map for every respawn point of the world we are in;
        // the one we are bound to is brighter and larger. Placed with the formulas
        // MapMarkers uses for the players.
        class PointMarker { public GameObject Mini, World, Compass; public UITexture MiniTex, WorldTex, CompassTex; }
        readonly Dictionary<string, PointMarker> markers = new Dictionary<string, PointMarker>();
        static Texture2D angelIcon;
        public static bool NoMarkers; // -NoFix respawnicons

        static Texture2D AngelIcon()
        {
            if (angelIcon != null) return angelIcon;
            const int N = 48;
            angelIcon = new Texture2D(N, N, TextureFormat.RGBA32, false);
            angelIcon.filterMode = FilterMode.Bilinear;
            Color fill = new Color(1f, 0.86f, 0.42f, 1f), edge = new Color(0.12f, 0.08f, 0.02f, 1f), clear = new Color(0, 0, 0, 0);
            // Shape as distance fields (y up): halo ring, head, robe (trapezoid), two wings.
            System.Func<float, float, bool> inside = (x, y) =>
            {
                float hx = x - 24f, hy = y - 41f;
                float halo = Mathf.Sqrt(hx * hx / 36f + hy * hy / 4f);
                if (halo > 0.75f && halo < 1.15f) return true;
                float dx = x - 24f, dy = y - 33f;
                if (dx * dx + dy * dy < 16f) return true; // head
                if (y > 6f && y < 29f) { float half = 3f + (29f - y) * 0.32f; if (Mathf.Abs(x - 24f) < half) return true; } // robe
                for (int s = -1; s <= 1; s += 2) // wings: ellipses tilted up and out
                {
                    float wx = (x - 24f) * s - 10f, wy = y - 24f;
                    float rx = wx * 0.82f + wy * 0.57f, ry = -wx * 0.57f + wy * 0.82f;
                    if (rx * rx / 110f + ry * ry / 22f < 1f && (x - 24f) * s > 3f) return true;
                }
                return false;
            };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    bool i = inside(px, py);
                    bool near = false;
                    if (!i) for (int k = 0; k < 8 && !near; k++) near = inside(px + 2f * Mathf.Cos(k * Mathf.PI / 4f), py + 2f * Mathf.Sin(k * Mathf.PI / 4f));
                    angelIcon.SetPixel(x, y, i ? fill : near ? edge : clear);
                }
            angelIcon.Apply();
            return angelIcon;
        }

        void TickMarkers(PlayerScript player)
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null || im.minimapPlayer == null || im.worldMapPlayer == null || NoMarkers) return;
            UITexture own = im.minimapPlayer.GetComponentInChildren<UITexture>(true);
            UITexture ownWorld = im.worldMapPlayer.GetComponentInChildren<UITexture>(true);
            bool dungeon = player.transform.position.y < -500f || (GameManager.instance != null && GameManager.instance.currentLoadedWorld == null);
            Camera cam = MinimapCamera.instance != null ? MinimapCamera.instance.GetComponent<Camera>() : null;
            float metres = cam != null && cam.orthographic ? cam.orthographicSize : 40f;
            foreach (Village vil in Villages)
            {
                PointMarker m;
                if (!markers.TryGetValue(vil.Name, out m))
                {
                    m = new PointMarker();
                    if (own != null)
                    {
                        m.Mini = new GameObject("CoopRespawnMini (" + vil.Name + ")");
                        m.Mini.layer = own.gameObject.layer;
                        m.Mini.transform.SetParent(im.minimapPlayer.parent, false);
                        m.MiniTex = m.Mini.AddComponent<UITexture>();
                        m.MiniTex.mainTexture = AngelIcon();
                        m.MiniTex.width = m.MiniTex.height = 48;
                        m.MiniTex.depth = own.depth - 2; // under the player arrows
                    }
                    if (ownWorld != null)
                    {
                        m.World = new GameObject("CoopRespawnWorld (" + vil.Name + ")");
                        m.World.layer = ownWorld.gameObject.layer;
                        m.World.transform.SetParent(im.worldMapPlayer.parent, false);
                        m.WorldTex = m.World.AddComponent<UITexture>();
                        m.WorldTex.mainTexture = AngelIcon();
                        m.WorldTex.width = m.WorldTex.height = 40;
                        m.WorldTex.depth = ownWorld.depth; // over the fog of unexplored land, like the player arrow
                    }
                    markers[vil.Name] = m;
                }
                bool here = vil.World == world && !dungeon;
                bool isBound = bound == vil.Name;
                Color tint = isBound ? Color.white : new Color(0.85f, 0.8f, 0.7f, 0.9f);
                Vector3 p;
                if (!spots.TryGetValue(vil.Name, out p)) p = new Vector3(vil.SX, 0f, vil.SZ);
                // Minimap: north-up picture, own arrow in the middle; only those on it.
                if (m.Mini != null)
                {
                    Transform t = m.Mini.transform;
                    float rim = 0.10f / Mathf.Max(1e-6f, t.parent.lossyScale.x);
                    Vector3 d = p - player.transform.position;
                    Vector3 off = new Vector3(d.x, d.z, 0f) / metres * rim;
                    bool show = here && im.minimapNotCompass && off.magnitude < rim * 0.95f;
                    if (m.Mini.activeSelf != show) m.Mini.SetActive(show);
                    if (show)
                    {
                        t.localPosition = im.minimapPlayer.localPosition + off;
                        t.localRotation = Quaternion.identity;
                        t.localScale = (own != null ? own.transform.localScale : Vector3.one) * (isBound ? 0.75f : 0.6f);
                        m.MiniTex.color = tint;
                    }
                }
                // Compass strip (players who use it instead of the minimap): the
                // bound point always (at the edge when behind), others within 200 m
                // when they lie in the strip's view. Same scale as the quest marker.
                if (m.Compass == null && im.markerOnMinimap != null)
                {
                    m.Compass = new GameObject("CoopRespawnCompass (" + vil.Name + ")");
                    m.Compass.layer = im.markerOnMinimap.layer;
                    m.Compass.transform.SetParent(im.markerOnMinimap.transform.parent.parent, false);
                    m.CompassTex = m.Compass.AddComponent<UITexture>();
                    m.CompassTex.mainTexture = AngelIcon();
                    m.CompassTex.width = m.CompassTex.height = 34;
                    UIWidget qw = im.markerOnMinimap.GetComponentInChildren<UIWidget>(true);
                    m.CompassTex.depth = qw != null ? qw.depth : 10;
                }
                if (m.Compass != null)
                {
                    Vector3 d = p - player.transform.position;
                    float dist = new Vector2(d.x, d.z).magnitude;
                    float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                    float x = Mathf.DeltaAngle(player.transform.eulerAngles.y, bearing) * 2.777777f;
                    UIPanel strip = m.Compass.transform.parent.GetComponent<UIPanel>();
                    bool clipped = strip != null && strip.clipping != UIDrawCall.Clipping.None;
                    float half = clipped ? strip.finalClipRegion.z / 2f - 12f : 300f;
                    float centre = clipped ? strip.finalClipRegion.x : 0f;
                    bool show = here && !im.minimapNotCompass && (isBound || (dist < 200f && Mathf.Abs(x) <= half));
                    if (m.Compass.activeSelf != show) m.Compass.SetActive(show);
                    if (show)
                    {
                        x = Mathf.Clamp(x, -half, half);
                        m.Compass.transform.localPosition = new Vector3(centre + x, im.markerOnMinimap.transform.localPosition.y + 2f, 0f);
                        m.Compass.transform.localScale = Vector3.one * (isBound ? 1.15f : 0.9f);
                        m.CompassTex.color = tint;
                    }
                }
                if (m.World != null)
                {
                    Vector3 lp = new Vector3(p.x, p.z, 0f) * im.worldmapScale + im.worldmapdelta + im.hubWorldDelta * im.worldmapScale;
                    bool show = here && Mathf.Abs(lp.x) < 430f && Mathf.Abs(lp.y) < 390f;
                    if (m.World.activeSelf != show) m.World.SetActive(show);
                    if (show)
                    {
                        m.World.transform.localPosition = lp;
                        m.World.transform.rotation = im.worldMapPlayer.parent.rotation;
                        m.World.transform.localScale = Vector3.one * (isBound ? 1.25f : 1f);
                        m.WorldTex.color = tint;
                    }
                }
            }
        }

        // Test step respawnmarkers: what the map symbols show.
        public string DescribeMarkers()
        {
            string s = "";
            foreach (KeyValuePair<string, PointMarker> kv in markers)
                s += " " + kv.Key + "[mini=" + (kv.Value.Mini != null && kv.Value.Mini.activeInHierarchy ? kv.Value.Mini.transform.localPosition.ToString("F0") : "off") + " compass=" + (kv.Value.Compass != null && kv.Value.Compass.activeInHierarchy ? kv.Value.Compass.transform.localPosition.x.ToString("F0") : "off") +
                    " world=" + (kv.Value.World != null && kv.Value.World.activeSelf ? kv.Value.World.transform.localPosition.ToString("F0") : "off") + "]";
            string k2 = "";
            foreach (KeyValuePair<string, string> k in kinds) k2 += " " + k.Key + "=" + k.Value;
            return "points:" + k2 + " | markers:" + s;
        }

        // Where a revived player stands: beside the point (a statue is wider).
        Vector3 ReviveOffset(string village)
        {
            string k;
            return kinds.TryGetValue(village, out k) && !k.StartsWith("crystal") ? new Vector3(2.8f, 1f, 0f) : new Vector3(2f, 1f, 0f);
        }

        void PlaceAndBind(PlayerScript player)
        {
            if (GameManager.instance == null) return;
            Vector3 me = player.transform.position;
            // A crystal that stood in for the statue goes once the model is there.
            foreach (Village vil in Villages)
            {
                string k;
                if (vil.World != world || !kinds.TryGetValue(vil.Name, out k) || !k.StartsWith("crystal") || !StatueModel()) continue;
                GameObject old;
                if (stones.TryGetValue(vil.Name, out old) && old != null) UnityEngine.Object.Destroy(old);
                stones.Remove(vil.Name);
                kinds.Remove(vil.Name);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Respawn point of " + vil.Name + ": the crystal makes way for the statue");
            }
            foreach (Village vil in Villages)
            {
                if (vil.World != world) continue;
                string v = vil.Name;
                if (!stones.ContainsKey(v) || stones[v] == null)
                {
                    Vector3 center;
                    if (!Middle(vil, out center)) continue;
                    if (Vector3.Distance(new Vector3(me.x, 0f, me.z), new Vector3(center.x, 0f, center.z)) > SpawnRange) { nearSince.Remove(v); continue; }
                    // Wait until the area around has streamed in (water surfaces come late).
                    bool loading = InterfaceManager.instance != null && InterfaceManager.instance.panels[10].activeSelf;
                    float since;
                    if (loading || !nearSince.TryGetValue(v, out since)) { nearSince[v] = Time.realtimeSinceStartup; continue; }
                    if (Time.realtimeSinceStartup - since < SettleSeconds) continue;
                    // On dry, open ground nearest to the village's middle (Weystane's
                    // middle is a pond): rings around it until a spot fits.
                    Vector3 at;
                    string why;
                    if (vil.HasSpot && !Survey) { if (!Ground(vil, out at)) continue; why = "measured spot"; }
                    else if (!DrySpot(center, out at, out why)) continue;
                    string kind;
                    GameObject g = BuildPoint(vil, at, center, out kind);
                    if (g == null) continue;
                    stones[v] = g;
                    kinds[v] = kind;
                    spots[v] = at;
                    visited.Add(v);
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Respawn point of " + v + " placed at " + at.ToString("F1") + ": " + kind + " (" + why + ")");
                    if (pendingVillage == v)
                    {
                        // Revived into this village before its crystal stood: step to it.
                        pendingVillage = "";
                        player._char.enabled = false;
                        player.transform.position = at + ReviveOffset(v);
                        player._char.enabled = true;
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Moved to the crystal of " + v + " after the revive");
                    }
                }
            }
        }

        public bool IsBound(string v) { return bound == v; }

        // The respawn point of this world nearest to a position (where a teammate is).
        bool NearestVillage(Vector3 near, out Village best, out Vector3 at)
        {
            best = default(Village); at = Vector3.zero;
            float d = float.MaxValue;
            bool found = false;
            foreach (Village v in Villages)
            {
                Vector3 p;
                if (v.World != world || !Spot(v, out p)) continue;
                Vector2 flat = new Vector2(p.x - near.x, p.z - near.z);
                if (flat.sqrMagnitude < d) { d = flat.sqrMagnitude; best = v; at = p; found = true; }
            }
            return found;
        }

        public string NearestStatueName(Vector3 near)
        {
            Village v; Vector3 at;
            return NearestVillage(near, out v, out at) ? v.Name : "?";
        }

        // Statue menu: travel to the statue nearest to a teammate, like a revive goes
        // (MoveToRoutine; not placed yet -> the village middle, then onto the statue).
        public bool TravelNear(Vector3 near, out string name)
        {
            Village v; Vector3 at;
            name = "";
            PlayerScript player = PlayerScript.instance;
            if (player == null || !NearestVillage(near, out v, out at)) return false;
            name = v.Name;
            if (!spots.ContainsKey(v.Name)) pendingVillage = v.Name;
            Vector3 to = at + ReviveOffset(v.Name);
            GameManager.instance.StartCoroutine(GameManager.instance.MoveToRoutine(to, Quaternion.identity));
            Plugin.Log.LogInfo(Plugin.Stamp() + " Travel to the statue of " + v.Name + " " + to.ToString("F1") + " (teammate at " + near.ToString("F0") + ")");
            return true;
        }
        bool IsStatue(string v) { string k; return kinds.TryGetValue(v, out k) && !k.StartsWith("crystal"); }


        // The player pressed F at the crystal of v.
        public void Bind(string v)
        {
            PlayerScript player = PlayerScript.instance;
            if (player == null || player.isDead || !stones.ContainsKey(v) || stones[v] == null) return;
            if (Vector3.Distance(player.transform.position, stones[v].transform.position) > BindRange + 2f) return;
            if (bound == v)
            {
                if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessages.Add(IsStatue(v) ? Lang.T("You are already bound to this angel statue.", "Du bist schon an diese Engelsstatue gebunden.") : "You are already bound to this crystal.", Color.white, 2f);
                return;
            }
            bound = v;
            SaveBinding(v);
            Binds++;
            string de = Lang.German ? Translator.German(v) : null;
            if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessagesLarge.Add(IsStatue(v) ? Lang.T("Respawn point: " + v, "Wiederbelebungspunkt: " + (de ?? v)) : "Respawn crystal: " + v, Color.white, 3f);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Bound to the respawn crystal of " + v + " (F)");
        }

        // A spot on terrain (not a roof), not under a water surface (layer
        // "Water" above the ground; the sea plane lies far below land), and not
        // steep - the nearest one to the middle, in rings 6 m apart.
        // Test step statuesurvey: loaded objects whose name sounds like a statue or
        // shrine, near the player (300 m), with mesh, scene and whether they are
        // part of a prefab we could load.
        public static void TestStatues(Vector3 near)
        {
            string[] words = { "statue", "angel", "shrine", "idol", "monument", "goddess", "altar", "wing" };
            int n = 0;
            foreach (MeshFilter mf in Resources.FindObjectsOfTypeAll<MeshFilter>())
            {
                if (mf == null || !mf.gameObject.scene.IsValid()) continue;
                string nm = mf.gameObject.name.ToLowerInvariant(), mesh = mf.sharedMesh != null ? mf.sharedMesh.name.ToLowerInvariant() : "";
                bool hit = false;
                foreach (string w in words) if (nm.Contains(w) || mesh.Contains(w)) { hit = true; break; }
                if (!hit) continue;
                float d = Vector3.Distance(mf.transform.position, near);
                if (d > 300f) continue;
                string path = mf.name; for (Transform t = mf.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                Renderer r = mf.GetComponent<Renderer>();
                Plugin.Log.LogInfo("[statue] " + path + " mesh=" + (mf.sharedMesh != null ? mf.sharedMesh.name : "-") + " mat=" + (r != null && r.sharedMaterial != null ? r.sharedMaterial.name : "-") +
                    " at " + mf.transform.position.ToString("F1") + " d=" + d.ToString("F0") + " scale=" + mf.transform.lossyScale.ToString("F2") + " scene=" + mf.gameObject.scene.name + " active=" + mf.gameObject.activeInHierarchy +
                    " colliders=" + mf.GetComponentsInChildren<Collider>(true).Length);
                if (++n > 60) return;
            }
            Plugin.Log.LogInfo("[statue] " + n + " near " + near.ToString("F0"));
            // The statue models as assets (not batched scene copies) and prefabs holding them.
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
                if (m != null && m.name.IndexOf("Statue", StringComparison.OrdinalIgnoreCase) >= 0) Plugin.Log.LogInfo("[statue] mesh asset " + m.name + " verts=" + m.vertexCount + " readable=" + m.isReadable);
            foreach (GameObject g in Resources.FindObjectsOfTypeAll<GameObject>())
                if (g != null && !g.scene.IsValid() && g.name.IndexOf("Statue", StringComparison.OrdinalIgnoreCase) >= 0) Plugin.Log.LogInfo("[statue] prefab " + g.name);
            foreach (MeshFilter mf in Resources.FindObjectsOfTypeAll<MeshFilter>())
                if (mf != null && mf.gameObject.scene.IsValid() && mf.gameObject.name.StartsWith("SM_Prop_Statue_Angel")) Plugin.Log.LogInfo("[statue] angel renderer " + mf.name + " mesh=" + (mf.sharedMesh != null ? mf.sharedMesh.name + " sub=" + mf.sharedMesh.subMeshCount + " verts=" + mf.sharedMesh.vertexCount : "-") + " static=" + mf.gameObject.isStatic + " at " + mf.transform.position.ToString("F1"));
        }

        // Test step crystalspots:<village>: every ring point near the village middle
        // with the reason it is no good (or free), and how close the nearest
        // non-terrain collider is - to pick a crystal spot by hand.
        public static void TestSpots(string village)
        {
            foreach (Village v in Villages)
            {
                if (v.Name != village.Replace("_", " ")) continue;
                Vector3 center;
                if (!Middle(v, out center)) { Plugin.Log.LogWarning("Test: no middle for " + v.Name); return; }
                int water = LayerMask.NameToLayer("Water");
                for (int ring = 0; ring <= 6; ring++)
                    for (int s = 0; s < (ring == 0 ? 1 : 12); s++)
                    {
                        float a = s * Mathf.PI * 2f / 12f;
                        Vector3 p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ring * 4f;
                        RaycastHit ground;
                        string why = "free";
                        if (!Physics.Raycast(new Vector3(p.x, center.y + 200f, p.z), Vector3.down, out ground, 500f, ~0, QueryTriggerInteraction.Ignore)) why = "no ground";
                        else if (!ground.collider.name.StartsWith("Terrain")) why = "on " + ground.collider.name;
                        else if (ground.normal.y < 0.9f) why = "steep";
                        float nearest = 99f; string what = "-";
                        if (why == "free")
                            foreach (Collider c in Physics.OverlapSphere(ground.point + Vector3.up * 1.4f, 4f, ~0, QueryTriggerInteraction.Ignore))
                            {
                                if (c.name.StartsWith("Terrain") || c.GetComponentInParent<PlayerScript>() != null || c.GetComponentInParent<NPC>() != null) continue;
                                float d = Vector3.Distance(c.ClosestPoint(ground.point + Vector3.up * 1.4f), ground.point + Vector3.up * 1.4f);
                                if (d < nearest) { nearest = d; what = c.name; }
                            }
                        bool wetNear = water >= 0 && why == "free" && Physics.CheckSphere(ground.point, 8f, 1 << water, QueryTriggerInteraction.Collide);
                        Plugin.Log.LogInfo("[crystalspot] " + v.Name + " " + p.x.ToString("F1") + "/" + p.z.ToString("F1") + " y=" + ground.point.y.ToString("F1") + " " + why + (why == "free" ? " nearest=" + nearest.ToString("F1") + " " + what + (wetNear ? " (water within 8 m)" : "") : ""));
                    }
                return;
            }
            Plugin.Log.LogWarning("Test: no village " + village);
        }

        static bool DrySpot(Vector3 center, out Vector3 at, out string why)
        {
            int water = LayerMask.NameToLayer("Water");
            for (int ring = 0; ring <= 15; ring++)
            {
                int steps = ring == 0 ? 1 : 12;
                for (int s = 0; s < steps; s++)
                {
                    float a = s * Mathf.PI * 2f / steps;
                    Vector3 p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ring * 6f;
                    RaycastHit ground;
                    if (!Physics.Raycast(new Vector3(p.x, center.y + 200f, p.z), Vector3.down, out ground, 500f, ~0, QueryTriggerInteraction.Ignore)) continue;
                    if (!ground.collider.name.StartsWith("Terrain") || ground.normal.y < 0.9f) continue;
                    bool wet = false;
                    foreach (RaycastHit h in Physics.RaycastAll(new Vector3(p.x, center.y + 200f, p.z), Vector3.down, 500f, ~0, QueryTriggerInteraction.Collide))
                        if (h.collider.gameObject.layer == water && h.point.y > ground.point.y - 0.1f) wet = true;
                    if (wet) continue;
                    // Room around it (no wall, rock, stair or tent rope) and no water close by.
                    bool crowded = false;
                    foreach (Collider c in Physics.OverlapSphere(ground.point + Vector3.up * 1.4f, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                        if (!c.name.StartsWith("Terrain") && c.GetComponentInParent<PlayerScript>() == null && c.GetComponentInParent<NPC>() == null) { crowded = true; break; } // people walk away; walls, rocks and tent ropes stay
                    if (crowded || (water >= 0 && Physics.CheckSphere(ground.point, 8f, 1 << water, QueryTriggerInteraction.Collide))) continue;
                    at = ground.point;
                    why = ring * 6 + " m from the village middle " + center.ToString("F0");
                    return true;
                }
            }
            at = Vector3.zero;
            why = "no dry spot";
            return false;
        }

        static SubZone Zone(string name)
        {
            foreach (SubZone z in GameManager.instance.subLocations) if (z != null && z.nameofZone == name) return z;
            return null;
        }

        // Where a revive goes: the bound crystal, else the nearest explored
        // village, else where the player came into this world.
        Vector3 Target(PlayerScript player, out string name)
        {
            Vector3 p;
            foreach (Village v in Villages)
                if (v.World == world && v.Name == bound && Spot(v, out p)) { name = bound; return p; }
            // Unbound (or bound in another world): the nearest village of this
            // world already been to (Gedonia's map remembers explored areas).
            float best = float.MaxValue;
            string pick = null;
            Vector3 pickAt = Vector3.zero;
            foreach (Village v in Villages)
            {
                if (v.World != world) continue;
                SubZone z = !v.Fixed && GameManager.instance != null && GameManager.instance.subLocations != null ? Zone(v.Name) : null;
                bool been = visited.Contains(v.Name) || (z != null && z.explored);
                if (!been || !Spot(v, out p)) continue;
                float d = Vector3.Distance(player.transform.position, p);
                if (d < best) { best = d; pick = v.Name; pickAt = p; }
            }
            if (pick != null) { name = pick; return pickAt; }
            name = "the place you arrived";
            return arrival;
        }

        string TargetName(PlayerScript player) { string n; Target(player, out n); return n; }

        bool Spot(Village v, out Vector3 p)
        {
            if (spots.TryGetValue(v.Name, out p)) return true;
            // Not placed yet (far away): the village's middle; once there, the
            // player steps to the crystal when it is placed (pendingVillage).
            return Middle(v, out p);
        }

        // helper != null: a teammate helped us up - stand up on the spot with half health.
        void Revive(PlayerScript player, float now, string helper)
        {
            reviveAt = -1f;
            Banner = "";
            downed = false;
            string name;
            Vector3 to;
            if (helper != null) { name = "the spot (helped up by " + helper + ")"; to = player.transform.position; }
            else
            {
                to = Target(player, out name);
                to += ReviveOffset(name);
                foreach (Village v in Villages) if (v.World == world && v.Name == name && !spots.ContainsKey(name)) pendingVillage = name;
            }
            if (InterfaceManager.instance != null) InterfaceManager.instance.panels[8].SetActive(false);
            player.isDead = false;
            if (player.anim != null)
            {
                player.anim.SetBool("isDead", false);
                player.anim.Rebind(); // out of the death pose; the game sets its parameters again each frame
            }
            player.health = helper != null ? Mathf.Max(1, player.maxHealth / 2) : player.maxHealth;
            if (InterfaceManager.instance != null)
            {
                InterfaceManager.instance.healthBar.value = (float)player.health / Mathf.Max(1, player.maxHealth);
                InterfaceManager.instance.healthLabel.text = player.health + "/" + player.maxHealth;
            }
            Traverse.Create(player).Field("invincibility").SetValue(true);
            invincibleUntil = now + 3f;
            if (helper != null)
            {
                HelpedUp++;
                if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessagesLarge.Add(Lang.T(helper + " helped you up", helper + " hat dir aufgeholfen"), Color.white, 3f);
            }
            else GameManager.instance.StartCoroutine(GameManager.instance.MoveToRoutine(to, Quaternion.identity));
            Revives++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Revived at " + name + " " + to.ToString("F1"));
        }

        string LoadBinding()
        {
            try
            {
                if (File.Exists(ConfigFile))
                    foreach (string line in File.ReadAllLines(ConfigFile))
                        if (line.StartsWith(SaveKey + "=")) return line.Substring(SaveKey.Length + 1);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Reading the respawn binding failed: " + e.Message); }
            return "";
        }

        void SaveBinding(string village)
        {
            try
            {
                List<string> lines = new List<string>();
                if (File.Exists(ConfigFile))
                    foreach (string line in File.ReadAllLines(ConfigFile)) if (!line.StartsWith(SaveKey + "=")) lines.Add(line);
                lines.Add(SaveKey + "=" + village);
                File.WriteAllLines(ConfigFile, lines.ToArray());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Saving the respawn binding failed: " + e.Message); }
        }

        public string Summary()
        {
            return "respawn bound='" + bound + "' crystals=" + stones.Count + " binds=" + Binds + " revives=" + Revives + " helpedOthers=" + Helped + " helpedUp=" + HelpedUp + " down=" + downed;
        }
    }

    // On a respawn crystal: the game's interaction (F) binds the player to it.
    // EnterInteract/ExitInteract show the hover box like DescriptionOnHover.
    public class CoopCrystal : MonoBehaviour
    {
        public string Village;
        public RespawnStones Owner;
        public bool Statue;

        // English: the label's translation (lang/de-09-extra.tsv) makes it German.
        string Text()
        {
            if (Statue)
            {
                string de = Lang.German ? Translator.German(Village) : null;
                bool b = Owner != null && Owner.IsBound(Village);
                return Lang.T("Angel statue (" + Village + ")\n" + (b ? "You are bound here" : "Respawn point") + " - F: bind, travel, attributes",
                    "Engelsstatue (" + (de ?? Village) + ")\n" + (b ? "Du bist hier gebunden" : "Wiederbelebungspunkt") + " - F: binden, reisen, Attribute");
            }
            return Owner != null && Owner.IsBound(Village)
                ? "Respawn crystal (" + Village + ")\nYou are bound here"
                : "Respawn crystal (" + Village + ")\nBind yourself here";
        }

        public void EnterInteract()
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null) return;
            im.tooltipHoverPanel.PlayForward();
            im.tooltipHover.text = Text();
            im.StartCoroutine(im.HoverDisable());
        }

        public void ExitInteract()
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null) return;
            im.tooltipHoverPanel.PlayReverse();
            im.tooltipHover.text = "";
        }

        public void Interact()
        {
            if (Owner == null) return;
            if (Statue && !Plugin.NoFix("statuemenu")) StatueMenu.Instance.Show(Owner, Village);
            else Owner.Bind(Village);
            if (InterfaceManager.instance != null) InterfaceManager.instance.tooltipHover.text = Text();
        }
    }
}
