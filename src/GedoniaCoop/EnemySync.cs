using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Pathfinding;
using Pathfinding.RVO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Shares enemies (FighterNPCModule) between players, following the
    // playbook's actor-ownership rules:
    //  - every machine reports which enemies it has active near its player;
    //  - the host gives each enemy exactly one owner (itself if it has the
    //    enemy, else the nearest player), with an epoch that rises on every
    //    change and a hold time against flapping;
    //  - the owner runs the enemy's AI and publishes position, animation and
    //    health; everyone else skips its AI and follows those updates;
    //  - a hit on a non-owned enemy is sent to the owner, which applies it
    //    with the hitter's puppet as attacker (so the AI fights back at it);
    //  - an enemy's hit on a puppet is sent to that player as real damage;
    //  - a death on the owner kills the enemy everywhere (each player gets the
    //    XP and a lootable body, as in single player).
    //
    // Enemies are identified by WorldIds (scene + hierarchy path, taken when
    // their scene loads), so both machines compute the same id. Enemies
    // spawned at runtime have no id and stay local for now.
    public class EnemySync
    {
        enum Role { Local, Owner, Replica }

        class Enemy
        {
            public int Id;
            public FighterNPCModule F;   // null for companions
            public GameObject Go;
            public NPC Npc;
            // Companions and summons: always simulated by the player they
            // follow (FixedOwner), shown elsewhere as mirrors made from the
            // same Resources/Companions prefab.
            public int FixedOwner;
            public string Prefab;
            public float LastRoster;
            public Animator Anim;
            public Role Role = Role.Local;
            public int Owner, Epoch;
            // Replica side
            public bool HasTarget;
            public Vector3 TargetPos;
            public float TargetYaw, LastUpdate;
            public int[] States = new int[0];
            public float[] Times = new float[0];
            public float[] Values = new float[0];
            public readonly AnimMirror Mirror = new AnimMirror();
            public bool SavedRootMotion;
            // Owner side
            public List<AnimatorControllerParameter> SendParams;
        }

        class OwnRec
        {
            public int Owner, Epoch;
            public float Since;
        }

        const float PresenceRange = 150f;
        const float HoldTime = 1.5f;

        readonly Net net;
        readonly Func<int, Puppet> puppetOf;
        readonly Func<IEnumerable<Puppet>> puppets;
        readonly Dictionary<int, Enemy> byId = new Dictionary<int, Enemy>();
        readonly Dictionary<NPC, Enemy> byNpc = new Dictionary<NPC, Enemy>();
        readonly Dictionary<FighterNPCModule, int> idOf = new Dictionary<FighterNPCModule, int>();
        // Enemies created inside a story event: the same function runs on every
        // machine, so (event number, n-th spawn) names the same enemy everywhere.
        readonly Dictionary<NPC, int> storySpawnId = new Dictionary<NPC, int>();
        readonly HashSet<string> missingPrefabs = new HashSet<string>();
        int companionCounter;
        float nextRoster;
        readonly HashSet<int> dead = new HashSet<int>();
        readonly Dictionary<int, KeyValuePair<int, int>> view = new Dictionary<int, KeyValuePair<int, int>>(); // id -> (owner, epoch)

        // Host only
        readonly Dictionary<int, OwnRec> recs = new Dictionary<int, OwnRec>();
        readonly Dictionary<int, Dictionary<int, float>> presence = new Dictionary<int, Dictionary<int, float>>(); // player -> id -> distance
        readonly Dictionary<int, float> presenceAt = new Dictionary<int, float>();

        float nextScan, nextPresence, nextOwnTick, nextTable, nextPublish, nextAggro, nextStats, nextOwnedLog, nextFollowerLog;
        public int DamageSent, DamageApplied, HitsSent, HitsTaken, DeathsSent, DeathsApplied, StatesApplied, StatesRejected, OwnChanges, DungeonEnemies, EventEnemies;
        public int HealthTaken;
        public int DamageIgnoredDead, DamageIgnoredNotOwner, DamageIgnoredUnknown;

        public static EnemySync Instance;
        static bool applying; // we are applying a remote hit or death: let it through
        public static bool Applying { get { return applying; } }

        // Other shared things that need one owner (world events) report their
        // presence through the same host decision; WorldEventSync reads OwnerOf.
        public Func<Vector3, Dictionary<int, float>> ExtraPresence;

        public int OwnerOf(int id)
        {
            KeyValuePair<int, int> own;
            return view.TryGetValue(id, out own) ? own.Key : 0;
        }

        // A runtime-spawned enemy that every machine names the same way
        // (e.g. a world event's spawn): share it like a scene enemy.
        public void RegisterSpawned(FighterNPCModule f, int id)
        {
            if (f != null) idOf[f] = id;
        }

        // An id some enemy has (or had: killed ones stay dead for everyone).
        public bool IdUsed(int id) { return byId.ContainsKey(id) || dead.Contains(id); }

        // Whether this machine runs the NPC's AI (owner, or nobody decided).
        public bool RunsHere(NPC n)
        {
            Enemy e;
            return n != null && (!byNpc.TryGetValue(n, out e) || e.Role != Role.Replica);
        }

        public bool HasEnemy(int id) { Enemy e; return byId.TryGetValue(id, out e) && e.Go != null; }

        // SoundSync: the shared enemy or follower this machine simulates that t belongs to.
        public bool SimulatedActor(Transform t, out int id, out Transform root)
        {
            id = 0; root = null;
            NPC n = t.GetComponentInParent<NPC>();
            Enemy e;
            if (n == null || !byNpc.TryGetValue(n, out e) || e.Go == null || e.Npc == null) return false;
            if (e.FixedOwner != 0 ? e.FixedOwner != Me : e.Role != Role.Owner) return false;
            id = e.Id; root = e.Go.transform;
            return true;
        }

        // EffectSync: the shared enemy or follower t belongs to, whoever simulates it.
        public bool SharedActor(Transform t, out int id, out Transform root)
        {
            id = 0; root = null;
            NPC n = t.GetComponentInParent<NPC>();
            Enemy e;
            if (n == null || !byNpc.TryGetValue(n, out e) || e.Go == null || e.Npc == null) return false;
            id = e.Id; root = e.Go.transform;
            return true;
        }

        public Transform ActorRoot(int id)
        {
            Enemy e;
            return byId.TryGetValue(id, out e) && e.Go != null ? e.Go.transform : null;
        }

        // SoundSync: the mirror of someone else's enemy or follower.
        public Transform MirrorRoot(int id)
        {
            Enemy e;
            if (!byId.TryGetValue(id, out e) || e.Go == null || e.Npc == null) return null;
            if (e.FixedOwner != 0 ? e.FixedOwner == Me : e.Role != Role.Replica) return null;
            return e.Go.transform;
        }

        public EnemySync(Net net, Func<int, Puppet> puppetOf, Func<IEnumerable<Puppet>> puppets)
        {
            this.net = net;
            this.puppetOf = puppetOf;
            this.puppets = puppets;
            Instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) IndexScene(SceneManager.GetSceneAt(i));
            PuppetBody.Hit += OnPuppetHit;
        }

        int Me { get { return net.LocalId; } }

        // ---------- ids ----------

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) { IndexScene(scene); }

        void IndexScene(Scene scene)
        {
            if (!scene.IsValid()) return;
            int n = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (FighterNPCModule f in root.GetComponentsInChildren<FighterNPCModule>(true))
                {
                    if (f.GetComponent<CompanionNPCModule>() != null) continue;
                    idOf[f] = WorldIds.Of(f.transform, scene.name);
                    n++;
                }
            }
            if (n > 0) Plugin.Log.LogInfo("Enemy ids: " + n + " in scene " + scene.name);
        }

        // ---------- main loop ----------

        public void Tick(float now, bool inWorld, Vector3 me)
        {
            foreach (Net.Message m in net.Messages) Handle(m, now);
            if (!inWorld) return;

            if (now >= nextScan) { nextScan = now + 1f; Scan(); ScanCompanions(); PruneMirrors(now); }
            if (now >= nextRoster) { nextRoster = now + 3f; SendRoster(null); }
            if (now >= nextPresence) { nextPresence = now + 1f; SendPresence(me, now); }
            if (net.IsHost && now >= nextOwnTick) { nextOwnTick = now + 0.5f; HostDecide(now); }
            if (net.IsHost && now >= nextTable) { nextTable = now + 2f; SendTable(); }
            if (now >= nextPublish) { nextPublish = now + 0.1f; Publish(); }
            if (now >= nextAggro) { nextAggro = now + 1f; AggroPuppets(); }
            UpdateReplicas(now);
            if (now >= nextStats) { nextStats = now + 10f; LogStats(); }
            if (now >= nextFollowerLog)
            {
                // For comparing a mirror with its owner's real follower at the same time.
                nextFollowerLog = now + 5f;
                string s = "";
                foreach (Enemy e in byId.Values)
                    if (e.FixedOwner != 0 && Alive(e))
                        s += " " + e.Id + "(" + e.Prefab + (e.FixedOwner == Me && e.Go.GetComponent<CompanionNPCModule>() != null ? "," + e.Go.GetComponent<CompanionNPCModule>().state : "") + ")" + (e.FixedOwner == Me ? "=own" : "=mirror") + "@" + e.Go.transform.position.ToString("F1").Replace(" ", "") + "/hp" + e.Npc.health;
                if (s.Length > 0) Plugin.Log.LogInfo(Plugin.Stamp() + " [followers]" + s);
            }
            if (now >= nextOwnedLog)
            {
                // For tools/check-ownership.ps1: the enemies this machine simulates right now.
                nextOwnedLog = now + 2f;
                Plugin.Log.LogInfo(Plugin.Stamp() + " [owned] " + string.Join(",", OwnedIds().ConvertAll(x => x.ToString()).ToArray()));
            }
        }

        void Scan()
        {
            // Forget enemies whose cell was unloaded (Unity-destroyed objects).
            List<int> gone = null;
            foreach (KeyValuePair<int, Enemy> kv in byId)
                if (kv.Value.Go == null || kv.Value.Npc == null) { if (gone == null) gone = new List<int>(); gone.Add(kv.Key); }
            if (gone != null)
            {
                foreach (int id in gone)
                {
                    Enemy e = byId[id];
                    byId.Remove(id);
                    // Unity-null keys still compare by reference, so this finds them.
                    List<NPC> stale = new List<NPC>();
                    foreach (KeyValuePair<NPC, Enemy> kv in byNpc) if (kv.Value == e) stale.Add(kv.Key);
                    foreach (NPC n in stale) byNpc.Remove(n);
                }
            }

            foreach (FighterNPCModule f in UnityEngine.Object.FindObjectsOfType<FighterNPCModule>())
            {
                int id;
                if (!idOf.TryGetValue(f, out id))
                {
                    // Spawned at runtime: shared if a story event made it,
                    // otherwise (world events, abilities) it stays local.
                    NPC sn = f.npc != null ? f.npc : f.GetComponent<NPC>();
                    if (sn == null || f.GetComponent<CompanionNPCModule>() != null) continue;
                    // In a generated dungeon (same seed everywhere, so the same
                    // tiles): named by its place below the dungeon's root.
                    DunGen.Dungeon dungeon = f.GetComponentInParent<DunGen.Dungeon>();
                    if (dungeon != null)
                    {
                        Vector3 r = dungeon.transform.position;
                        id = WorldIds.Under(f.transform, dungeon.transform, "dungen/" + Mathf.RoundToInt(r.x) + "," + Mathf.RoundToInt(r.y) + "," + Mathf.RoundToInt(r.z));
                        idOf[f] = id;
                        DungeonEnemies++;
                        Plugin.Log.LogInfo("Dungeon enemy '" + sn.npcName + "' gets id " + id);
                    }
                    else if (EventChildId(f, out id))
                    {
                        idOf[f] = id;
                        EventEnemies++;
                        Plugin.Log.LogInfo("Event enemy '" + sn.npcName + "' gets id " + id);
                        // Cloned here at runtime (a reveal ritual): the others build it too.
                        if (f.gameObject.name.EndsWith("(Clone)") && WorldEventSync.Instance != null)
                            WorldEventSync.Instance.AnnounceClone(f.GetComponentInParent<WorldEventBase>(), sn, id);
                    }
                    else if (TravellerSync.IdFor(sn, out id))
                    {
                        // A merchant/bard walking its path: same index everywhere.
                        idOf[f] = id;
                        Plugin.Log.LogInfo("Traveller '" + sn.npcName + "' #" + sn.travellerID + " gets id " + id);
                    }
                    else if (!storySpawnId.TryGetValue(sn, out id)) continue;
                    else
                    {
                        idOf[f] = id;
                        Plugin.Log.LogInfo("Story-spawned enemy '" + sn.npcName + "' gets id " + id);
                    }
                }
                Enemy e;
                if (byId.TryGetValue(id, out e) && e.F == f) continue;
                NPC npc = f.npc != null ? f.npc : f.GetComponent<NPC>();
                if (npc == null) continue;
                e = new Enemy { Id = id, F = f, Go = f.gameObject, Npc = npc, Anim = npc.anim != null ? npc.anim : f.GetComponentInChildren<Animator>() };
                byId[id] = e;
                byNpc[npc] = e;
                if (dead.Contains(id) && !npc.isDead)
                {
                    // Killed by someone while this machine didn't have it loaded.
                    Plugin.Log.LogInfo("Enemy " + id + " (" + npc.npcName + ") was killed elsewhere; removing");
                    KillLocally(e);
                    continue;
                }
                KeyValuePair<int, int> own;
                if (view.TryGetValue(id, out own)) SetRole(e, own.Key);
            }
        }

        // ---------- companions and summons ----------

        // Our own followers: register each new one and announce it.
        static readonly string[] Pets = { "Cat", "Wasp", "Eagle", "Pig", "Crab", "Rabbit", "Penguin", "FurSeal", "PolarbearCub" };
        float nextPetScan;

        static string PrefabName(CompanionNPCModule c) { return c.gameObject.name.Replace("(Clone)", "").Trim(); }

        void ScanCompanions()
        {
            PlayerScript player = PlayerScript.instance;
            if (player == null) return;
            // Pets (GameManager.Summon*Pet, from Resources/Companions/Pets) follow
            // the player but are not in currentCOmpanions; a mirror's own pet copy
            // is in byNpc already, so only our own new ones are left here.
            List<CompanionNPCModule> mine = new List<CompanionNPCModule>(player.currentCOmpanions);
            if (Time.realtimeSinceStartup >= nextPetScan)
            {
                nextPetScan = Time.realtimeSinceStartup + 1f;
                foreach (CompanionNPCModule c in UnityEngine.Object.FindObjectsOfType<CompanionNPCModule>())
                    if (c != null && c.npc != null && !byNpc.ContainsKey(c.npc) && Array.IndexOf(Pets, PrefabName(c)) >= 0 && !mine.Contains(c)) mine.Add(c);
            }
            foreach (CompanionNPCModule c in mine)
            {
                if (c == null || c.npc == null || byNpc.ContainsKey(c.npc)) continue;
                int id = unchecked(Me * 31 + (++companionCounter) * 7919);
                if (id == 0) id = 1;
                string prefab = PrefabName(c);
                if (Array.IndexOf(Pets, prefab) >= 0) prefab = "Pets/" + prefab;
                Enemy e = new Enemy
                {
                    Id = id, Go = c.gameObject, Npc = c.npc, FixedOwner = Me, Prefab = prefab, Role = Role.Owner, Epoch = 1, Owner = Me,
                    Anim = c.npc.anim != null ? c.npc.anim : c.GetComponentInChildren<Animator>()
                };
                byId[id] = e;
                byNpc[c.npc] = e;
                view[id] = new KeyValuePair<int, int>(Me, 1);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Own companion '" + c.npc.npcName + "' (" + prefab + ") shared as " + id);
                SendRoster(e);
            }
        }

        // Owner -> everyone: (id, prefab, position, yaw) of our living
        // followers. One entry right after a new one appears, the full list
        // every 3 s (covers lost messages, late joiners and despawns).
        void SendRoster(Enemy only)
        {
            List<Enemy> list = new List<Enemy>();
            foreach (Enemy e in byId.Values) if (e.FixedOwner == Me && Alive(e) && (only == null || e == only)) list.Add(e);
            if (only != null && list.Count == 0) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(only == null); // full list?
                w.Write(list.Count);
                foreach (Enemy e in list)
                {
                    Vector3 p = e.Go.transform.position;
                    w.Write(e.Id); w.Write(e.Prefab ?? ""); w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(e.Go.transform.eulerAngles.y);
                }
                w.Flush();
                net.SendMessage(Net.MsgActorRoster, 0, ms.ToArray(), only != null);
            }
        }

        void ApplyRoster(Net.Message m, float now)
        {
            if (PlayerScript.instance == null) return;
            // Followers of a player in another hub world are not here: drop
            // their mirrors now instead of after the roster timeout.
            if (!Plugin.SameWorld(m.Sender))
            {
                foreach (Enemy e in byId.Values)
                    if (e.FixedOwner == m.Sender && e.FixedOwner != Me && e.LastRoster > -1000f) e.LastRoster = -1000f;
                return;
            }
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                r.ReadBoolean();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadInt32();
                    string prefab = r.ReadString();
                    Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    float yaw = r.ReadSingle();
                    Enemy e;
                    if (byId.TryGetValue(id, out e)) { if (e.FixedOwner == m.Sender) e.LastRoster = now; continue; }
                    if (dead.Contains(id)) continue;
                    CreateMirror(id, prefab, p, yaw, m.Sender, now);
                }
            }
        }

        // A friend's follower, built from the same prefab with its AI off
        // (NPC.Update is skipped for mirrors) and moved by its owner's updates.
        void CreateMirror(int id, string prefab, Vector3 pos, float yaw, int owner, float now)
        {
            UnityEngine.Object res = Resources.Load("Companions/" + prefab);
            if (res == null)
            {
                if (missingPrefabs.Add(prefab)) Plugin.Log.LogWarning("Can't show a friend's companion: no prefab Companions/" + prefab);
                return;
            }
            GameObject holder = new GameObject("CoopMirrorHolder");
            holder.SetActive(false);
            GameObject go = UnityEngine.Object.Instantiate(res, pos, Quaternion.Euler(0f, yaw, 0f), holder.transform) as GameObject;
            go.transform.SetParent(null, true);
            go.name = prefab + " (friend's)";
            NPC npc = go.GetComponent<NPC>();
            if (npc == null) { UnityEngine.Object.Destroy(go); UnityEngine.Object.Destroy(holder); return; }
            Enemy e = new Enemy
            {
                Id = id, Go = go, Npc = npc, FixedOwner = owner, Prefab = prefab, Owner = owner, Epoch = 1, LastRoster = now,
                Anim = npc.anim != null ? npc.anim : go.GetComponentInChildren<Animator>()
            };
            byId[id] = e;
            byNpc[npc] = e;
            view[id] = new KeyValuePair<int, int>(owner, 1);
            go.SetActive(true);
            UnityEngine.Object.Destroy(holder);
            if (e.Anim == null) e.Anim = npc.anim;
            SetRole(e, owner);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Friend's companion '" + npc.npcName + "' (" + prefab + ") shown as " + id + " for " + owner);
        }

        // Mirrors whose owner stopped listing them (dismissed, expired, owner left).
        void PruneMirrors(float now)
        {
            List<int> gone = null;
            foreach (Enemy e in byId.Values)
                if (e.FixedOwner != 0 && e.FixedOwner != Me && now - e.LastRoster > 7f) { if (gone == null) gone = new List<int>(); gone.Add(e.Id); }
            if (gone == null) return;
            foreach (int id in gone)
            {
                Enemy e = byId[id];
                Plugin.Log.LogInfo(Plugin.Stamp() + " Friend's companion " + id + " gone (owner no longer lists it)");
                byId.Remove(id);
                if (e.Npc != null) byNpc.Remove(e.Npc);
                if (e.Go != null) UnityEngine.Object.Destroy(e.Go);
            }
        }

        // An enemy placed inside a registered world event built at runtime (an
        // expedition's packs and bosses): named by its place below the event,
        // which every machine builds alike.
        static bool EventChildId(FighterNPCModule f, out int id)
        {
            id = 0;
            WorldEventBase ev = f.GetComponentInParent<WorldEventBase>();
            int eventId = ev != null && WorldEventSync.Instance != null ? WorldEventSync.Instance.EventIdOf(ev) : 0;
            if (eventId == 0) return false;
            id = WorldIds.Under(f.transform, ev.transform, "wevobj/" + eventId);
            return true;
        }

        public static void RecordSpawn(NPC npc)
        {
            EnemySync s = Instance;
            if (s == null || !StorySync.Applying || StorySync.CurrentEvent <= 0) return;
            int index = StorySync.NextSpawnIndex();
            uint h = 2166136261;
            foreach (char c in "story/" + StorySync.CurrentEvent + "/" + index) { h ^= c; h *= 16777619; }
            s.storySpawnId[npc] = unchecked((int)h) == 0 ? 1 : unchecked((int)h);
        }

        [HarmonyPatch(typeof(NPC), "Awake")]
        static class SpawnPatch
        {
            static void Postfix(NPC __instance) { RecordSpawn(__instance); }
        }

        bool Alive(Enemy e) { return e.Go != null && e.Npc != null && !e.Npc.isDead && e.Go.activeInHierarchy; }

        // ---------- presence and ownership ----------

        void SendPresence(Vector3 me, float now)
        {
            Dictionary<int, float> mine = new Dictionary<int, float>();
            foreach (Enemy e in byId.Values)
            {
                if (!Alive(e) || e.FixedOwner != 0) continue; // followers belong to their player, not to the host's choice
                float d = Vector3.Distance(me, e.Go.transform.position);
                if (d < PresenceRange) mine[e.Id] = d;
            }
            if (ExtraPresence != null)
                foreach (KeyValuePair<int, float> kv in ExtraPresence(me)) mine[kv.Key] = kv.Value;
            if (net.IsHost)
            {
                Dictionary<int, float> before;
                if (presence.TryGetValue(Me, out before))
                {
                    foreach (int id in before.Keys)
                    {
                        if (mine.ContainsKey(id)) continue;
                        Enemy e;
                        string why = !byId.TryGetValue(id, out e) ? "unknown" : e.Go == null ? "destroyed" : e.Npc.isDead ? "dead"
                            : !e.Go.activeInHierarchy ? "inactive" : "out of range (" + Vector3.Distance(me, e.Go.transform.position).ToString("F0") + " m)";
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Host no longer has enemy " + id + ": " + why);
                    }
                }
                presence[Me] = mine; presenceAt[Me] = now;
                return;
            }
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(mine.Count);
                foreach (KeyValuePair<int, float> kv in mine) { w.Write(kv.Key); w.Write((ushort)Mathf.Min(kv.Value * 10f, 65535f)); }
                w.Flush();
                net.SendMessage(Net.MsgPresence, 0, ms.ToArray(), false);
            }
        }

        void HostDecide(float now)
        {
            Dictionary<int, List<KeyValuePair<int, float>>> candidates = new Dictionary<int, List<KeyValuePair<int, float>>>();
            foreach (KeyValuePair<int, Dictionary<int, float>> p in presence)
            {
                float at;
                if (!presenceAt.TryGetValue(p.Key, out at) || now - at > 3f) continue;
                if (p.Key != Me && puppetOf(p.Key) == null) continue; // player gone
                foreach (KeyValuePair<int, float> kv in p.Value)
                {
                    if (dead.Contains(kv.Key)) continue;
                    List<KeyValuePair<int, float>> list;
                    if (!candidates.TryGetValue(kv.Key, out list)) candidates[kv.Key] = list = new List<KeyValuePair<int, float>>();
                    list.Add(new KeyValuePair<int, float>(p.Key, kv.Value));
                }
            }

            List<int> changed = new List<int>();
            foreach (KeyValuePair<int, List<KeyValuePair<int, float>>> c in candidates)
            {
                OwnRec rec;
                if (!recs.TryGetValue(c.Key, out rec)) recs[c.Key] = rec = new OwnRec();
                int best = Choose(rec, c.Value, now);
                if (best != rec.Owner)
                {
                    if (rec.Owner != 0)
                    {
                        string who = "";
                        foreach (KeyValuePair<int, float> k in c.Value) who += " " + (k.Key == Me ? "host" : k.Key.ToString()) + "@" + k.Value.ToString("F0") + "m";
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Owner of enemy " + c.Key + ": " + rec.Owner + " -> " + best + " (epoch " + (rec.Epoch + 1) + "; present:" + who + ")");
                    }
                    rec.Owner = best; rec.Epoch++; rec.Since = now; changed.Add(c.Key);
                }
            }
            foreach (KeyValuePair<int, OwnRec> r in recs)
            {
                if (r.Value.Owner != 0 && !candidates.ContainsKey(r.Key))
                {
                    r.Value.Owner = 0; r.Value.Epoch++; r.Value.Since = now; changed.Add(r.Key); // released
                }
            }
            if (changed.Count > 0) SendOwn(changed, false);
        }

        // Prefer the host, then the nearest player; keep an owner at least
        // HoldTime and only hand over to a clearly (25 %) closer player.
        int Choose(OwnRec rec, List<KeyValuePair<int, float>> cands, float now)
        {
            float ownerDist = -1f, hostDist = -1f, bestDist = float.MaxValue;
            int best = 0;
            foreach (KeyValuePair<int, float> c in cands)
            {
                if (c.Key == rec.Owner) ownerDist = c.Value;
                if (c.Key == Me) hostDist = c.Value;
                if (c.Value < bestDist) { bestDist = c.Value; best = c.Key; }
            }
            if (ownerDist >= 0f && now - rec.Since < HoldTime) return rec.Owner;
            if (hostDist >= 0f) return Me;
            if (ownerDist >= 0f && bestDist > ownerDist * 0.75f) return rec.Owner;
            return best;
        }

        void SendOwn(List<int> ids, bool full)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(full);
                w.Write(ids.Count);
                foreach (int id in ids) { OwnRec r = recs[id]; w.Write(id); w.Write(r.Owner); w.Write(r.Epoch); }
                w.Write(full ? dead.Count : 0);
                if (full) foreach (int id in dead) w.Write(id);
                w.Flush();
                byte[] payload = ms.ToArray();
                net.SendMessage(Net.MsgOwn, 0, payload, true);
                ApplyOwn(payload); // the host follows its own decisions through the same path
            }
            if (!full) OwnChanges += ids.Count;
        }

        void SendTable()
        {
            if (recs.Count == 0 && dead.Count == 0) return;
            SendOwn(new List<int>(recs.Keys), true);
        }

        void ApplyOwn(byte[] payload)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
            {
                r.ReadBoolean();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadInt32(), owner = r.ReadInt32(), epoch = r.ReadInt32();
                    KeyValuePair<int, int> cur;
                    // Epochs only move forward: an older statement never wins.
                    if (view.TryGetValue(id, out cur) && epoch <= cur.Value) continue;
                    view[id] = new KeyValuePair<int, int>(owner, epoch);
                    Enemy e;
                    if (!byId.TryGetValue(id, out e) || e.FixedOwner != 0) continue;
                    // One bad entry must not stop the rest of the table.
                    try { e.Epoch = epoch; SetRole(e, owner); }
                    catch (Exception ex) { Plugin.Log.LogError("Owner change for enemy " + id + " failed: " + ex); }
                }
                int d = r.ReadInt32();
                for (int i = 0; i < d; i++) MarkDead(r.ReadInt32());
            }
        }

        void SetRole(Enemy e, int owner)
        {
            KeyValuePair<int, int> own;
            if (view.TryGetValue(e.Id, out own)) e.Epoch = own.Value;
            e.Owner = owner;
            // Unloaded here (its streamed cell went away): nothing to switch.
            // If the cell loads again, Scan makes a new entry with this owner.
            if (e.Go == null || e.Npc == null) return;
            Role role = owner == 0 ? Role.Local : owner == Me ? Role.Owner : Role.Replica;
            if (role == e.Role) return;
            if (e.Role != Role.Local || role == Role.Owner)
                Plugin.Log.LogInfo(Plugin.Stamp() + " Enemy " + e.Id + " (" + e.Npc.npcName + "): " + e.Role + " -> " + role + " (epoch " + e.Epoch + ")");
            if (role == Role.Replica)
            {
                // Its AI runs on the owner; here it only follows updates.
                e.SavedRootMotion = e.Anim != null && e.Anim.applyRootMotion;
                if (e.Anim != null) e.Anim.applyRootMotion = false;
                SetAgent(e, false);
                e.HasTarget = false;
            }
            else if (e.Role == Role.Replica)
            {
                if (e.Anim != null) e.Anim.applyRootMotion = e.SavedRootMotion;
                if (!e.Npc.isDead) SetAgent(e, true);
            }
            e.Role = role;
        }

        static void SetAgent(Enemy e, bool on)
        {
            AIPath agent = e.Npc._agent;
            if (agent != null) agent.enabled = on;
            RVOController rvo = e.Go.GetComponent<RVOController>();
            if (rvo != null) rvo.enabled = on;
        }

        // ---------- owner: publish state ----------

        void Publish()
        {
            List<Enemy> owned = new List<Enemy>();
            foreach (Enemy e in byId.Values) if (e.Role == Role.Owner && Alive(e)) owned.Add(e);
            if (owned.Count == 0) return;
            MemoryStream ms = null;
            BinaryWriter w = null;
            int count = 0;
            foreach (Enemy e in owned)
            {
                if (ms == null) { ms = new MemoryStream(); w = new BinaryWriter(ms); w.Write(0); count = 0; }
                Vector3 p = e.Go.transform.position;
                w.Write(e.Id); w.Write(e.Epoch);
                w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(e.Go.transform.eulerAngles.y);
                w.Write(e.Npc.health);
                WriteAnimator(w, e);
                count++;
                if (ms.Length > 900) { Flush(ms, w, count); ms = null; }
            }
            if (ms != null) Flush(ms, w, count);
        }

        void WriteAnimator(BinaryWriter w, Enemy e)
        {
            Animator a = e.Anim;
            if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) { w.Write((byte)0); w.Write((byte)0); return; }
            if (e.SendParams == null || e.SendParams.Count == 0) e.SendParams = LocalPlayer.SyncableParams(a);
            w.Write((byte)a.layerCount);
            for (int l = 0; l < a.layerCount; l++)
            {
                AnimatorStateInfo info = a.IsInTransition(l) ? a.GetNextAnimatorStateInfo(l) : a.GetCurrentAnimatorStateInfo(l);
                w.Write(info.fullPathHash); w.Write(info.normalizedTime);
            }
            w.Write((byte)Mathf.Min(e.SendParams.Count, 255));
            for (int i = 0; i < e.SendParams.Count && i < 255; i++)
            {
                AnimatorControllerParameter p = e.SendParams[i];
                float v = p.type == AnimatorControllerParameterType.Float ? a.GetFloat(p.nameHash)
                    : p.type == AnimatorControllerParameterType.Int ? a.GetInteger(p.nameHash)
                    : a.GetBool(p.nameHash) ? 1f : 0f;
                w.Write(v);
            }
        }

        void Flush(MemoryStream ms, BinaryWriter w, int count)
        {
            w.Flush();
            byte[] data = ms.ToArray();
            BitConverter.GetBytes(count).CopyTo(data, 0);
            net.SendMessage(Net.MsgEnemyStates, 0, data, false);
        }

        // ---------- replica: apply state ----------

        void ApplyStates(Net.Message m, float now)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadInt32(), epoch = r.ReadInt32();
                    Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    float yaw = r.ReadSingle();
                    int health = r.ReadInt32();
                    int layers = r.ReadByte();
                    int[] states = new int[layers]; float[] times = new float[layers];
                    for (int l = 0; l < layers; l++) { states[l] = r.ReadInt32(); times[l] = r.ReadSingle(); }
                    float[] values = new float[r.ReadByte()];
                    for (int k = 0; k < values.Length; k++) values[k] = r.ReadSingle();

                    Enemy e;
                    KeyValuePair<int, int> own;
                    // Only the owner at the current epoch may move an enemy.
                    if (!byId.TryGetValue(id, out e) || !view.TryGetValue(id, out own) || own.Key != m.Sender || own.Value != epoch || e.Role != Role.Replica)
                    {
                        StatesRejected++;
                        continue;
                    }
                    StatesApplied++;
                    e.TargetPos = p; e.TargetYaw = yaw; e.HasTarget = true; e.LastUpdate = now;
                    e.States = states; e.Times = times; e.Values = values;
                    if (!e.Npc.isDead) e.Npc.health = health;
                }
            }
        }

        void UpdateReplicas(float now)
        {
            float dt = Time.deltaTime;
            foreach (Enemy e in byId.Values)
            {
                if (e.Role != Role.Replica || !Alive(e)) continue;
                // The game switches travellers' agents on again after spawning them.
                if (e.Npc._agent != null && e.Npc._agent.enabled) SetAgent(e, false);
                if (!e.HasTarget) continue;
                Transform t = e.Go.transform;
                if ((t.position - e.TargetPos).sqrMagnitude > 15f * 15f) t.position = e.TargetPos;
                else t.position = Vector3.Lerp(t.position, e.TargetPos, 1f - Mathf.Exp(-12f * dt));
                t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, e.TargetYaw, 0f), 1f - Mathf.Exp(-12f * dt));
                bool inTarget;
                e.Mirror.Apply(e.Anim, e.States, e.Times, e.Values, now, out inTarget, null);
            }
        }

        // Harmony: an enemy's AI and patrol run from NPC.Update (FighterNPCModule
        // hooks in via npc.updateFunction); on a mirror they must not run.
        [HarmonyPatch(typeof(NPC), "Update")]
        static class UpdatePatch
        {
            static bool Prefix(NPC __instance)
            {
                EnemySync s = Instance;
                Enemy e;
                return s == null || !s.byNpc.TryGetValue(__instance, out e) || e.Role != Role.Replica;
            }
        }

        // ---------- damage ----------

        // Harmony: a hit on an enemy someone else owns goes to the owner.
        [HarmonyPatch(typeof(NPC), "Damage", new Type[] { typeof(int), typeof(NPC.DamageType), typeof(GameObject), typeof(bool), typeof(KnockPower), typeof(bool), typeof(bool) })]
        static class DamagePatch
        {
            static bool Prefix(NPC __instance, int amount, NPC.DamageType damageType, GameObject attacker, bool isCritical, KnockPower knockPower, bool isSharp, bool knockBack)
            {
                EnemySync s = Instance;
                if (s == null || applying) return true;
                Enemy e;
                if (!s.byNpc.TryGetValue(__instance, out e) || e.Role != Role.Replica || __instance.isDead) return true;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(e.Id); w.Write(amount); w.Write((int)damageType); w.Write(isCritical);
                    w.Write((int)knockPower); w.Write(isSharp); w.Write(knockBack);
                    // Who hit: a shared enemy or follower (by id), else the local player (0).
                    Enemy by;
                    NPC hitter = attacker != null ? attacker.GetComponent<NPC>() : null;
                    w.Write(hitter != null && s.byNpc.TryGetValue(hitter, out by) ? by.Id : 0);
                    w.Flush();
                    s.net.SendMessage(Net.MsgDamageRequest, e.Owner, ms.ToArray(), true);
                }
                s.DamageSent++;
                return false;
            }
        }

        void ApplyDamageRequest(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int id = r.ReadInt32(), amount = r.ReadInt32();
                NPC.DamageType type = (NPC.DamageType)r.ReadInt32();
                bool crit = r.ReadBoolean();
                KnockPower knock = (KnockPower)r.ReadInt32();
                bool sharp = r.ReadBoolean(), knockBack = r.ReadBoolean();
                int hitterId = r.ReadInt32();
                Enemy e;
                if (!byId.TryGetValue(id, out e)) { DamageIgnoredUnknown++; return; }
                if (e.Role != Role.Owner) { DamageIgnoredNotOwner++; return; }
                if (!Alive(e)) { DamageIgnoredDead++; return; }
                // The attacker as this machine knows it: the shared enemy or
                // follower that hit, else the hitting player's puppet.
                Enemy hitter;
                GameObject attacker = null;
                if (hitterId != 0 && byId.TryGetValue(hitterId, out hitter) && hitter.Go != null) attacker = hitter.Go;
                if (attacker == null) { Puppet p = puppetOf(m.Sender); attacker = p != null ? p.Root : null; }
                applying = true;
                try { e.Npc.Damage(amount, type, attacker, crit, knock, sharp, knockBack); DamageApplied++; }
                catch (Exception ex) { Plugin.Log.LogError("Remote hit on enemy " + id + " failed: " + ex); }
                finally { applying = false; }
            }
        }

        // An enemy hit a friend's puppet: that friend takes the damage at home.
        void OnPuppetHit(PuppetBody body, int amount, NPC.DamageType type, GameObject attacker, bool crit, KnockPower knock, bool sharp, bool knockBack)
        {
            int enemyId = 0;
            FighterNPCModule f = attacker != null ? attacker.GetComponent<FighterNPCModule>() : null;
            if (f != null) idOf.TryGetValue(f, out enemyId);
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(enemyId); w.Write(amount); w.Write((int)type); w.Write(crit);
                w.Write((int)knock); w.Write(sharp); w.Write(knockBack);
                w.Flush();
                net.SendMessage(Net.MsgPlayerHit, body.PlayerId, ms.ToArray(), true);
            }
            HitsSent++;
        }

        void ApplyPlayerHit(Net.Message m)
        {
            PlayerScript player = PlayerScript.instance;
            if (player == null) return;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int enemyId = r.ReadInt32(), amount = r.ReadInt32();
                NPC.DamageType type = (NPC.DamageType)r.ReadInt32();
                bool crit = r.ReadBoolean();
                KnockPower knock = (KnockPower)r.ReadInt32();
                bool sharp = r.ReadBoolean(), knockBack = r.ReadBoolean();
                Enemy e;
                GameObject attacker = byId.TryGetValue(enemyId, out e) && e.Go != null ? e.Go : null;
                int before = player.health;
                try { player.Damage(amount, type, attacker, crit, knock, sharp, knockBack); }
                catch (Exception ex) { Plugin.Log.LogError("Hit from enemy " + enemyId + " failed: " + ex); }
                HitsTaken++;
                HealthTaken += Math.Max(0, before - player.health);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Hit by enemy " + enemyId + " via the owner: " + amount + " " + type + ", health " + before + " -> " + player.health);
            }
        }

        // ---------- death ----------

        [HarmonyPatch(typeof(NPC), "Death")]
        static class DeathPatch
        {
            static void Postfix(NPC __instance)
            {
                // Only the owner announces. Deaths caused by a friend's hit that
                // the owner is applying count too (they happen inside Damage).
                EnemySync s = Instance;
                if (s == null || !__instance.isDead) return;
                Enemy e;
                if (!s.byNpc.TryGetValue(__instance, out e) || e.Role != Role.Owner) return;
                s.MarkDead(e.Id);
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(e.Id); w.Flush(); s.net.SendMessage(Net.MsgEnemyDeath, 0, ms.ToArray(), true); }
                s.DeathsSent++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Enemy " + e.Id + " (" + __instance.npcName + ") died on its owner; loot here: " + __instance.loot.Count);
            }
        }

        void MarkDead(int id)
        {
            if (!dead.Add(id)) return;
            Enemy e;
            if (byId.TryGetValue(id, out e) && e.Role != Role.Owner && e.Npc != null && !e.Npc.isDead) KillLocally(e);
        }

        // True while a death copied from the enemy's owner runs here (XpSync:
        // the owner shares the XP of it).
        public static bool RemoteDeath;

        void KillLocally(Enemy e)
        {
            applying = true;
            RemoteDeath = true;
            try
            {
                // Through the game's own death: local XP, lootable body, save entry.
                if (e.Anim != null) e.Anim.applyRootMotion = e.SavedRootMotion;
                e.Npc.Death(PlayerScript.instance != null ? PlayerScript.instance.gameObject : null);
                DeathsApplied++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Enemy " + e.Id + " (" + e.Npc.npcName + ") died: its owner killed it; loot here: " + e.Npc.loot.Count);
            }
            catch (Exception ex) { Plugin.Log.LogError("Remote death of enemy " + e.Id + " failed: " + ex); }
            finally { applying = false; RemoteDeath = false; }
        }

        // ---------- puppets as targets ----------

        // The game's own target search only looks for the "Player" tag, which
        // puppets must not carry (zones and quests react to it). Owned idle
        // enemies notice puppets here instead, with the game's own sight check.
        void AggroPuppets()
        {
            foreach (Enemy e in byId.Values)
            {
                if (e.Role == Role.Replica || !Alive(e) || e.F == null || e.F.state != NPCState.Idle) continue;
                if (e.Npc.allianceToPlayer() != ReputationLevel.Enemy) continue;
                foreach (Puppet p in puppets())
                {
                    if (!p.Visible || p.Root == null) continue;
                    float d = Vector3.Distance(p.Root.transform.position, e.F.transform.position);
                    if (d < e.F.aggroRange && e.Npc.CanSee(p.Root))
                    {
                        e.F.AddAggro(p.Root, 1f);
                        Plugin.Log.LogInfo("Enemy " + e.Id + " (" + e.Npc.npcName + ") noticed '" + p.Latest.Name + "' at " + d.ToString("F0") + " m");
                        break;
                    }
                }
            }
        }

        // ---------- messages ----------

        void Handle(Net.Message m, float now)
        {
            try
            {
                switch (m.Type)
                {
                    case Net.MsgPresence:
                        if (!net.IsHost) break;
                        using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
                        {
                            Dictionary<int, float> d = new Dictionary<int, float>();
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++) d[r.ReadInt32()] = r.ReadUInt16() / 10f;
                            presence[m.Sender] = d;
                            presenceAt[m.Sender] = now;
                        }
                        break;
                    case Net.MsgOwn: if (!net.IsHost) ApplyOwn(m.Payload); break;
                    case Net.MsgEnemyStates: ApplyStates(m, now); break;
                    case Net.MsgActorRoster: ApplyRoster(m, now); break;
                    case Net.MsgDamageRequest: ApplyDamageRequest(m); break;
                    case Net.MsgPlayerHit: ApplyPlayerHit(m); break;
                    case Net.MsgEnemyDeath:
                        using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload))) MarkDead(r.ReadInt32());
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Enemy message " + m.Type + " from " + m.Sender + " failed: " + ex);
            }
        }

        public void PlayerLeft(int player)
        {
            presence.Remove(player);
            presenceAt.Remove(player);
        }

        public string OwnedSummary()
        {
            int owner = 0, replica = 0, local = 0, myFollowers = 0, friendsFollowers = 0;
            foreach (Enemy e in byId.Values)
            {
                if (!Alive(e)) continue;
                if (e.FixedOwner != 0) { if (e.FixedOwner == Me) myFollowers++; else friendsFollowers++; continue; }
                if (e.Role == Role.Owner) owner++; else if (e.Role == Role.Replica) replica++; else local++;
            }
            return "enemies owned=" + owner + " mirrored=" + replica + " unassigned=" + local + " dead=" + dead.Count +
                " followers mine=" + myFollowers + " friends'=" + friendsFollowers + " dungeonEnemies=" + DungeonEnemies + " eventEnemies=" + EventEnemies;
        }

        public List<int> OwnedIds()
        {
            List<int> ids = new List<int>();
            foreach (Enemy e in byId.Values) if (e.Role == Role.Owner && Alive(e)) ids.Add(e.Id);
            ids.Sort();
            return ids;
        }

        void LogStats()
        {
            // "ignored": updates for enemies not mirrored here (not loaded, ours, or a stale epoch).
            Plugin.Log.LogInfo("[enemies] " + OwnedSummary() + " ownChanges=" + OwnChanges + " statesApplied=" + StatesApplied +
                " ignored=" + StatesRejected + " damageSent=" + DamageSent + " damageApplied=" + DamageApplied +
                " damageIgnored(dead/notOwner/unknown)=" + DamageIgnoredDead + "/" + DamageIgnoredNotOwner + "/" + DamageIgnoredUnknown +
                " hitsSent=" + HitsSent + " hitsTaken=" + HitsTaken + " healthLost=" + HealthTaken +
                " deathsSent=" + DeathsSent + " deathsApplied=" + DeathsApplied + " owned=[" + string.Join(",", OwnedIds().ConvertAll(x => x.ToString()).ToArray()) + "]");
        }

        // Test bot helper: nearest living hostile enemy with an id.
        public GameObject NearestHostile(Vector3 from, float maxDist, out int id, out string name)
        {
            id = 0; name = "";
            GameObject best = null;
            float bestD = maxDist;
            foreach (Enemy e in byId.Values)
            {
                if (!Alive(e) || e.Npc.allianceToPlayer() != ReputationLevel.Enemy) continue;
                float d = Vector3.Distance(from, e.Go.transform.position);
                if (d < bestD) { bestD = d; best = e.Go; id = e.Id; name = e.Npc.npcName; }
            }
            return best;
        }

        public string Describe(int id)
        {
            Enemy e;
            if (!byId.TryGetValue(id, out e)) return "enemy " + id + " unknown";
            return "enemy " + id + " '" + e.Npc.npcName + "' role=" + e.Role + " owner=" + e.Owner + " epoch=" + e.Epoch +
                " hp=" + e.Npc.health + "/" + e.Npc.maxHealth + " dead=" + e.Npc.isDead + " state=" + (e.F != null ? e.F.state.ToString() : "companion") +
                " pos=" + e.Go.transform.position.ToString("F1");
        }
    }

    // On a puppet: enemies that hit it really hit the friend it stands for.
    public class PuppetBody : Damagable
    {
        public int PlayerId;
        public static Action<PuppetBody, int, NPC.DamageType, GameObject, bool, KnockPower, bool, bool> Hit;

        public override void Damage(int amount, NPC.DamageType damageType, GameObject attacker, bool isCritical, KnockPower knockPower, bool isSharp, bool knockBack)
        {
            // Only real enemies; never the local player's own swings or companions.
            NPC stand = GetComponent<NPC>();
            if ((stand != null && stand.isDead) || attacker == null || attacker.GetComponent<FighterNPCModule>() == null || attacker.GetComponent<CompanionNPCModule>() != null) return;
            if (Hit != null) Hit(this, amount, damageType, attacker, isCritical, knockPower, isSharp, knockBack);
        }
    }
}
