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
        struct Village { public string World, Name; public float X, Z; public bool Fixed; public float SX, SZ; public bool HasSpot; }
        static readonly Village[] Villages =
        {
            // measured 2026-10-26 (survey run, 15 s settle, room around, 8 m from water; screenshots checked)
            G("Weystane", 728.0f, 627.0f), G("Steepport", 93.1f, 353.6f), G("Stawford", 1440.2f, 266.8f), G("Fayreach", 762.2f, -701.4f),
            G("Korram", 2123.3f, 420.0f), G("Ertoron", 1238.0f, -397.0f), G("Explorer's camp", 562.3f, -1025.9f),
            N("Erynard", -440f, 240f, -440.0f, 240.0f), N("Ironclad clan", 220f, -835f, 220.0f, -829.0f), N("Sons of Thunder", -1070f, 1255f, -1064.0f, 1255.0f),
        };
        static Village G(string name, float sx, float sz) { return new Village { World = "Gedonia", Name = name, SX = sx, SZ = sz, HasSpot = true }; }
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

        public void Tick(PlayerScript player, bool connected, float now)
        {
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

            if (invincibleUntil > 0f && now >= invincibleUntil) { Traverse.Create(player).Field("invincibility").SetValue(false); invincibleUntil = -1f; }
            if (!connected) { reviveAt = -1f; Banner = ""; return; }
            if (player.isDead && reviveAt < 0f)
            {
                reviveAt = now + ReviveDelay;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Fallen in co-op; reviving in " + ReviveDelay + " s at " + TargetName(player));
            }
            if (reviveAt >= 0f)
            {
                if (!player.isDead) { reviveAt = -1f; Banner = ""; return; } // revived by other means
                { string at = TargetName(player); string de = Lang.German ? Translator.German(at) : null; Banner = Lang.T("You have fallen.  Respawning in " + Mathf.CeilToInt(reviveAt - now) + " s at " + at, "Du bist gefallen.  Wiederbelebung in " + Mathf.CeilToInt(reviveAt - now) + " s bei " + (de ?? at)); }
                if (now >= reviveAt) Revive(player, now);
            }
        }

        void PlaceAndBind(PlayerScript player)
        {
            if (GameManager.instance == null) return;
            Vector3 me = player.transform.position;
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
                    GameObject proto = Resources.Load<GameObject>("Expeditions/MinableCrystals");
                    if (proto == null) continue;
                    GameObject g = UnityEngine.Object.Instantiate(proto, at, Quaternion.identity);
                    // Only its look: no script (it is a minable crystal) and nothing
                    // to hit or interact with.
                    foreach (MonoBehaviour mb in g.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                    foreach (Collider col in g.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(col);
                    g.name = "CoopRespawnCrystal (" + v + ")";
                    // Something the game's interaction finds (OverlapSphere 2.7 m on
                    // Default/NPCs/Player, in front of the camera): F binds.
                    GameObject spot = new GameObject("CoopCrystalInteract");
                    spot.transform.SetParent(g.transform, false);
                    spot.transform.localPosition = new Vector3(0f, 1f, 0f);
                    spot.layer = 0;
                    try { spot.tag = "Interactable"; } catch (Exception) { }
                    SphereCollider sc = spot.AddComponent<SphereCollider>();
                    sc.isTrigger = true;
                    sc.radius = 1.2f;
                    CoopCrystal cc = spot.AddComponent<CoopCrystal>();
                    cc.Village = v;
                    cc.Owner = this;
                    g.SetActive(true);
                    stones[v] = g;
                    spots[v] = at;
                    visited.Add(v);
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Respawn crystal of " + v + " placed at " + at.ToString("F1") + " (" + why + ")");
                    if (pendingVillage == v)
                    {
                        // Revived into this village before its crystal stood: step to it.
                        pendingVillage = "";
                        player._char.enabled = false;
                        player.transform.position = at + new Vector3(2f, 1f, 0f);
                        player._char.enabled = true;
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Moved to the crystal of " + v + " after the revive");
                    }
                }
            }
        }

        public bool IsBound(string v) { return bound == v; }


        // The player pressed F at the crystal of v.
        public void Bind(string v)
        {
            PlayerScript player = PlayerScript.instance;
            if (player == null || player.isDead || !stones.ContainsKey(v) || stones[v] == null) return;
            if (Vector3.Distance(player.transform.position, stones[v].transform.position) > BindRange + 2f) return;
            if (bound == v)
            {
                if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessages.Add("You are already bound to this crystal.", Color.white, 2f);
                return;
            }
            bound = v;
            SaveBinding(v);
            Binds++;
            if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessagesLarge.Add("Respawn crystal: " + v, Color.white, 3f);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Bound to the respawn crystal of " + v + " (F)");
        }

        // A spot on terrain (not a roof), not under a water surface (layer
        // "Water" above the ground; the sea plane lies far below land), and not
        // steep - the nearest one to the middle, in rings 6 m apart.
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
                        if (!c.name.StartsWith("Terrain") && c.GetComponentInParent<PlayerScript>() == null) { crowded = true; break; }
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

        void Revive(PlayerScript player, float now)
        {
            reviveAt = -1f;
            Banner = "";
            string name;
            Vector3 to = Target(player, out name) + new Vector3(2f, 1f, 0f);
            foreach (Village v in Villages) if (v.World == world && v.Name == name && !spots.ContainsKey(name)) pendingVillage = name;
            if (InterfaceManager.instance != null) InterfaceManager.instance.panels[8].SetActive(false);
            player.isDead = false;
            if (player.anim != null)
            {
                player.anim.SetBool("isDead", false);
                player.anim.Rebind(); // out of the death pose; the game sets its parameters again each frame
            }
            player.health = player.maxHealth;
            if (InterfaceManager.instance != null)
            {
                InterfaceManager.instance.healthBar.value = 1f;
                InterfaceManager.instance.healthLabel.text = player.health + "/" + player.maxHealth;
            }
            Traverse.Create(player).Field("invincibility").SetValue(true);
            invincibleUntil = now + 3f;
            GameManager.instance.StartCoroutine(GameManager.instance.MoveToRoutine(to, Quaternion.identity));
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
            return "respawn bound='" + bound + "' crystals=" + stones.Count + " binds=" + Binds + " revives=" + Revives;
        }
    }

    // On a respawn crystal: the game's interaction (F) binds the player to it.
    // EnterInteract/ExitInteract show the hover box like DescriptionOnHover.
    public class CoopCrystal : MonoBehaviour
    {
        public string Village;
        public RespawnStones Owner;

        // English: the label's translation (lang/de-09-extra.tsv) makes it German.
        string Text()
        {
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
            Owner.Bind(Village);
            if (InterfaceManager.instance != null) InterfaceManager.instance.tooltipHover.text = Text();
        }
    }
}
