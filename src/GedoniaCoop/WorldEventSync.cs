using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // World events (raids, goblin attacks, faction battles; WorldEventBase in
    // scene "Gedonia") run on one machine and look the same everywhere.
    //
    // In single player an event runs while the local player is within its
    // activeRange: its spawners create enemies at random spots, kills and other
    // objectives call CompleteEventObjective, phases advance and CompleteEvent
    // gives the rewards. Here:
    //  - every machine reports the events in range; the host picks one owner
    //    per event through the same rule as for enemies (EnemySync);
    //  - only the owner's spawners spawn. Each spawn gets an id
    //    (event, spawner, n-th spawn) and is announced; the others build the
    //    same enemy from the same spawner prefab, and it is shared like any
    //    enemy from then on;
    //  - the owner sends phase and count; others show them, and objectives
    //    they trigger (talk, collect) are sent to the owner;
    //  - kills by a friend count (the game only counts the "Player" tag or a
    //    companion as the killer);
    //  - when the owner completes the event, everyone completes it and gets
    //    the reward (owner's policy: full rewards for everyone).
    public class WorldEventSync
    {
        readonly Net net;
        readonly EnemySync enemies;
        readonly Func<IEnumerable<int>> peers;
        public static WorldEventSync Instance;
        static bool applying;

        readonly Dictionary<WorldEventBase, int> idOf = new Dictionary<WorldEventBase, int>();
        readonly Dictionary<int, WorldEventBase> byId = new Dictionary<int, WorldEventBase>();
        readonly Dictionary<WorldEventNPCSpawner, int> serial = new Dictionary<WorldEventNPCSpawner, int>();
        readonly Dictionary<NPC, int> spawnId = new Dictionary<NPC, int>();
        float nextState, nextRoster;
        public int SpawnsSent, SpawnsBuilt, ObjectivesForwarded, ObjectivesApplied, Completions, ObjectsSent, ObjectsApplied, ObjectSpawnsSent, ObjectSpawnsBuilt, ClonesSent, ClonesBuilt, PhaseShows, ProgressShows;

        static readonly MethodInfo completeEvent = AccessTools.Method(typeof(WorldEventBase), "CompleteEvent");

        public WorldEventSync(Net net, EnemySync enemies, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.enemies = enemies;
            this.peers = peers;
            Instance = this;
            enemies.ExtraPresence = Presence;
            SceneManager.sceneLoaded += (s, m) => Index(s);
            for (int i = 0; i < SceneManager.sceneCount; i++) Index(SceneManager.GetSceneAt(i));
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }
        int Me { get { return net.LocalId; } }

        void Index(Scene scene)
        {
            if (!scene.IsValid()) return;
            int n = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (WorldEventBase ev in root.GetComponentsInChildren<WorldEventBase>(true))
                {
                    int id = WorldIds.Of(ev.transform, scene.name);
                    idOf[ev] = id;
                    byId[id] = ev;
                    n++;
                }
            if (n > 0) Plugin.Log.LogInfo("World event ids: " + n + " in scene " + scene.name);
        }

        // An event built at runtime (an expedition) with an id every machine agrees on.
        public void Register(WorldEventBase ev, int id)
        {
            if (ev == null) return;
            idOf[ev] = id;
            byId[id] = ev;
            dungeons[ev] = ev.GetComponentsInChildren<StandaloneGeneratedDungeon>(true);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Runtime world event '" + ev.eventName + "' at " + ev.transform.position.ToString("F0") + " gets id " + id);
        }

        int IdOf(WorldEventBase ev) { int id; return ev != null && idOf.TryGetValue(ev, out id) ? id : 0; }

        // Owner of an event, or 0 when nobody decided (playing alone).
        int Owner(WorldEventBase ev) { return Connected ? enemies.OwnerOf(IdOf(ev)) : 0; }
        bool Foreign(WorldEventBase ev) { int o = Owner(ev); return o != 0 && o != Me; }
        bool Mine(WorldEventBase ev) { return Owner(ev) == Me && Connected; }

        Dictionary<int, float> Presence(Vector3 me)
        {
            Dictionary<int, float> d = new Dictionary<int, float>();
            foreach (KeyValuePair<WorldEventBase, int> kv in idOf)
            {
                WorldEventBase ev = kv.Key;
                if (ev == null || !ev.isActive) continue;
                float dist = Vector3.Distance(me, ev.transform.position);
                if (dist < ev.activeRange) d[kv.Value] = dist;
                else if (InItsDungeon(ev, me)) d[kv.Value] = ev.activeRange - 1f;
            }
            return d;
        }

        // A runtime event's generated dungeon lies far away (an expedition's
        // last phase): players inside it are still at the event.
        readonly Dictionary<WorldEventBase, StandaloneGeneratedDungeon[]> dungeons = new Dictionary<WorldEventBase, StandaloneGeneratedDungeon[]>();

        bool InItsDungeon(WorldEventBase ev, Vector3 me)
        {
            StandaloneGeneratedDungeon[] list;
            if (!dungeons.TryGetValue(ev, out list)) return false;
            foreach (StandaloneGeneratedDungeon g in list)
                if (g != null && Vector3.Distance(me, g.generatedDungeonStart) < 300f) return true;
            return false;
        }

        // Spawners in a fixed order (phases, then spawners): the same index on every machine.
        static List<WorldEventNPCSpawner> Spawners(WorldEventBase ev)
        {
            List<WorldEventNPCSpawner> list = new List<WorldEventNPCSpawner>();
            foreach (WorldEventBase.Phase p in ev.phases)
                if (p.spawners != null) foreach (WorldEventNPCSpawner s in p.spawners) if (s != null && !list.Contains(s)) list.Add(s);
            return list;
        }

        static int SpawnId(int eventId, int spawnerIndex, int n)
        {
            uint h = 2166136261;
            foreach (char c in "wev/" + eventId + "/" + spawnerIndex + "/" + n) { h ^= c; h *= 16777619; }
            int id = unchecked((int)h);
            return id == 0 ? 1 : id;
        }

        // ---------- spawning ----------

        [HarmonyPatch(typeof(WorldEventNPCSpawner), "SpawnControl")]
        static class SpawnPatch
        {
            static bool Prefix(WorldEventNPCSpawner __instance, out int __state)
            {
                __state = __instance.spawnedNPCs.Count;
                WorldEventSync s = Instance;
                // Someone else runs this event: their spawns come to us.
                // In co-op only the owner spawns; with no owner decided yet (the event
                // just started) wait a moment, or everyone would spawn - and count - a wave.
                return s == null || __instance.baseEvent == null || !s.Connected || s.Mine(__instance.baseEvent);
            }

            static void Postfix(WorldEventNPCSpawner __instance, int __state)
            {
                WorldEventSync s = Instance;
                if (s == null || __instance.baseEvent == null || !s.Mine(__instance.baseEvent)) return;
                for (int i = __state; i < __instance.spawnedNPCs.Count; i++) s.Announce(__instance, __instance.spawnedNPCs[i]);
            }
        }

        // Object spawners (an expedition's unstable anomalies: survive their
        // blasts) override SpawnControl, so they need their own patch: only the
        // owner spawns; the others get the same objects at the same spots. Their
        // copies still hurt the local player but count nothing (each blast
        // counts once, at the owner).
        [HarmonyPatch(typeof(WorldEventObjectSpawner), "SpawnControl")]
        static class ObjectSpawnPatch
        {
            static bool Prefix(WorldEventObjectSpawner __instance, out int __state)
            {
                __state = __instance.baseEvent != null ? __instance.baseEvent.transform.childCount : 0;
                WorldEventSync s = Instance;
                // In co-op only the owner spawns; with no owner decided yet (the event
                // just started) wait a moment, or everyone would spawn - and count - a wave.
                return s == null || __instance.baseEvent == null || !s.Connected || s.Mine(__instance.baseEvent);
            }

            static void Postfix(WorldEventObjectSpawner __instance, int __state)
            {
                WorldEventSync s = Instance;
                if (s == null || __instance.baseEvent == null || !s.Mine(__instance.baseEvent)) return;
                Transform t = __instance.baseEvent.transform;
                List<Vector3> spots = new List<Vector3>();
                for (int i = __state; i < t.childCount; i++) spots.Add(t.GetChild(i).position);
                if (spots.Count == 0) return;
                int index = Spawners(__instance.baseEvent).IndexOf(__instance);
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write((byte)1); w.Write(s.IdOf(__instance.baseEvent)); w.Write(index); w.Write(spots.Count);
                    foreach (Vector3 p in spots) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
                    w.Flush();
                    s.net.SendMessage(Net.MsgEventObjSpawn, 0, ms.ToArray(), true);
                }
                s.ObjectSpawnsSent += spots.Count;
            }
        }

        // An enemy cloned at runtime from a template inside an event (the
        // expedition scientist's reveal ritual, run on the speaker's machine):
        // the others clone the same template at the same spot, with its id.
        public void AnnounceClone(WorldEventBase ev, NPC npc, int id)
        {
            int eventId = IdOf(ev);
            int template = -1;
            for (int i = 0; i < ev.transform.childCount; i++)
            {
                Transform c = ev.transform.GetChild(i);
                NPC tn = c.GetComponent<NPC>();
                if (c.name + "(Clone)" == npc.gameObject.name && tn != null && tn.npcName == npc.npcName) { template = i; break; }
            }
            if (eventId == 0 || template < 0) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                Vector3 p = npc.transform.position;
                w.Write((byte)2); w.Write(eventId); w.Write(template); w.Write(id);
                w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(npc.transform.eulerAngles.y);
                w.Flush();
                net.SendMessage(Net.MsgEventObjSpawn, 0, ms.ToArray(), true);
            }
            ClonesSent++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' cloned '" + npc.npcName + "' here -> shared as " + id);
        }

        void ApplyObjSpawn(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                byte kind = r.ReadByte();
                int eventId = r.ReadInt32();
                WorldEventBase ev;
                if (!byId.TryGetValue(eventId, out ev) || ev == null) return;
                if (kind == 1)
                {
                    int index = r.ReadInt32(), n = r.ReadInt32();
                    List<WorldEventNPCSpawner> list = Spawners(ev);
                    if (index < 0 || index >= list.Count) return;
                    for (int i = 0; i < n; i++)
                    {
                        Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        GameObject go = UnityEngine.Object.Instantiate(list[index].spawnPrefab, p, Quaternion.identity);
                        WorldEventAoeAttack aoe = go.GetComponent<WorldEventAoeAttack>();
                        if (aoe != null) aoe.WorldEvent = null; // counts at the owner only
                        go.SetActive(true);
                        go.transform.parent = ev.transform;
                        ObjectSpawnsBuilt++;
                    }
                }
                else
                {
                    int template = r.ReadInt32(), id = r.ReadInt32();
                    Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    float yaw = r.ReadSingle();
                    if (enemies.HasEnemy(id) || template < 0 || template >= ev.transform.childCount) return;
                    GameObject go = UnityEngine.Object.Instantiate(ev.transform.GetChild(template).gameObject, p, Quaternion.Euler(0f, yaw, 0f), ev.transform);
                    enemies.RegisterSpawned(go.GetComponent<FighterNPCModule>(), id);
                    go.SetActive(true);
                    ClonesBuilt++;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' clone '" + go.name + "' from " + m.Sender + " built here as " + id);
                }
            }
        }

        void Announce(WorldEventNPCSpawner sp, NPC npc)
        {
            if (npc == null || spawnId.ContainsKey(npc)) return;
            WorldEventBase ev = sp.baseEvent;
            int eventId = IdOf(ev);
            int index = Spawners(ev).IndexOf(sp);
            if (eventId == 0 || index < 0) return;
            // Numbered per spawner; a new owner taking over a running event goes on
            // after the numbers it saw (copies count too) and skips any id in use or
            // already dead - a reused id would be removed as "killed elsewhere".
            int n;
            serial.TryGetValue(sp, out n);
            int id;
            do { id = SpawnId(eventId, index, ++n); } while (enemies.IdUsed(id) || spawnId.ContainsValue(id));
            serial[sp] = n;
            spawnId[npc] = id;
            enemies.RegisterSpawned(npc.GetComponent<FighterNPCModule>(), id);
            SpawnsSent++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' spawned '" + npc.npcName + "' -> shared as " + id);
            SendSpawns(new List<KeyValuePair<NPC, int>> { new KeyValuePair<NPC, int>(npc, id) }, true);
        }

        // (event id, spawner index, n, id, position, yaw) per enemy; one right
        // after spawning, all living ones every 3 s for lost messages / joiners.
        void SendSpawns(List<KeyValuePair<NPC, int>> list, bool reliable)
        {
            if (list.Count == 0) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(list.Count);
                foreach (KeyValuePair<NPC, int> kv in list)
                {
                    WorldEventNPCModule mod = kv.Key.GetComponent<WorldEventNPCModule>();
                    WorldEventNPCSpawner sp = mod != null ? mod.spawner : null;
                    WorldEventBase ev = sp != null ? sp.baseEvent : null;
                    Vector3 p = kv.Key.transform.position;
                    w.Write(IdOf(ev)); w.Write(ev != null ? Spawners(ev).IndexOf(sp) : -1); w.Write(kv.Value);
                    w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(kv.Key.transform.eulerAngles.y);
                }
                w.Flush();
                net.SendMessage(Net.MsgEventSpawn, 0, ms.ToArray(), reliable);
            }
        }

        void ApplySpawns(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int eventId = r.ReadInt32(), index = r.ReadInt32(), id = r.ReadInt32();
                    Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    float yaw = r.ReadSingle();
                    WorldEventBase ev;
                    if (enemies.HasEnemy(id) || spawnId.ContainsValue(id) || !byId.TryGetValue(eventId, out ev) || ev == null) continue;
                    List<WorldEventNPCSpawner> list = Spawners(ev);
                    if (index < 0 || index >= list.Count) continue;
                    Build(list[index], id, p, yaw);
                }
            }
        }

        // The same steps as WorldEventNPCSpawner.SpawnControl, at the owner's spot.
        void Build(WorldEventNPCSpawner sp, int id, Vector3 pos, float yaw)
        {
            GameObject go = UnityEngine.Object.Instantiate(sp.spawnPrefab, pos, Quaternion.Euler(0f, yaw, 0f));
            WorldEventNPCModule mod = go.GetComponent<WorldEventNPCModule>();
            if (mod != null) { mod.path = sp.path; mod.baseEvent = sp.baseEvent; mod.spawner = sp; }
            go.SetActive(true);
            NPC npc = go.GetComponent<NPC>();
            sp.spawnedNPCs.Add(npc);
            int seen;
            serial.TryGetValue(sp, out seen);
            serial[sp] = seen + 1; // the owner's numbering, for a later takeover
            go.transform.parent = sp.baseEvent.transform;
            spawnId[npc] = id;
            enemies.RegisterSpawned(go.GetComponent<FighterNPCModule>(), id);
            SpawnsBuilt++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + sp.baseEvent.eventName + "': built the owner's '" + (npc != null ? npc.npcName : go.name) + "' as " + id);
        }

        // ---------- progress ----------

        [HarmonyPatch(typeof(WorldEventBase), "CompleteEventObjective")]
        static class ObjectivePatch
        {
            static bool Prefix(WorldEventBase __instance, bool isPlus)
            {
                WorldEventSync s = Instance;
                // A finished event counts nothing more (a second player leaving an
                // expedition dungeon would complete it - and reward - again).
                if (s != null && s.Connected && !__instance.isActive)
                {
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + __instance.eventName + "' is over; objective ignored");
                    return false;
                }
                if (s != null && !s.Foreign(__instance))
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + __instance.eventName + "' objective (" + (isPlus ? "+" : "-") + ") phase " +
                        __instance.currentPhase + " count " + __instance.currentCount + (applying ? " (a friend's)" : ""));
                if (s == null || applying || !s.Foreign(__instance)) return true;
                // A mirrored event enemy dying here: its owner already counted it.
                if (EnemySync.Applying) return false;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(s.IdOf(__instance)); w.Write(isPlus); w.Flush();
                    s.net.SendMessage(Net.MsgEventObjective, s.Owner(__instance), ms.ToArray(), true);
                }
                s.ObjectivesForwarded++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + __instance.eventName + "' objective (" + (isPlus ? "+" : "-") + ") sent to its owner");
                return false;
            }
        }

        // The game counts an event kill only if the killer is tagged "Player"
        // or is a companion; a friend's hit arrives with their puppet as attacker.
        [HarmonyPatch(typeof(WorldEventNPCModule), "Damage")]
        static class FriendHitPatch
        {
            static void Postfix(WorldEventNPCModule __instance, GameObject attacker)
            {
                if (attacker != null && attacker.GetComponent<PuppetBody>() != null)
                    Traverse.Create(__instance).Field("wasAttackedByPlayer").SetValue(true);
            }
        }

        // Usable event objects (build a fortification, light a beacon) change
        // the world where they are used; the others must see them used too, or
        // they could be used - and counted - a second time there.
        static List<WorldEventUsableObject> Usables(WorldEventBase ev)
        {
            return new List<WorldEventUsableObject>(ev.GetComponentsInChildren<WorldEventUsableObject>(true));
        }

        [HarmonyPatch(typeof(WorldEventUsableObject), "Interact")]
        static class UsePatch
        {
            static void Prefix(WorldEventUsableObject __instance, out bool __state) { __state = __instance.isUsed; }

            static void Postfix(WorldEventUsableObject __instance, bool __state)
            {
                WorldEventSync s = Instance;
                if (s == null || applying || __state || !__instance.isUsed || __instance.baseEvent == null || !s.Connected) return;
                int eventId = s.IdOf(__instance.baseEvent);
                int index = Usables(__instance.baseEvent).IndexOf(__instance);
                if (eventId == 0 || index < 0) return;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(eventId); w.Write(index); w.Flush(); s.net.SendMessage(Net.MsgEventUse, 0, ms.ToArray(), true); }
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + __instance.baseEvent.eventName + "' object '" + __instance.name + "' used here; told the others");
            }
        }

        void ApplyUse(Net.Message m)
        {
            int eventId, index;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload))) { eventId = r.ReadInt32(); index = r.ReadInt32(); }
            WorldEventBase ev;
            if (!byId.TryGetValue(eventId, out ev) || ev == null) return;
            List<WorldEventUsableObject> list = Usables(ev);
            if (index < 0 || index >= list.Count || list[index].isUsed) return;
            WorldEventUsableObject u = list[index];
            // Its visible result only; the objective counts once, at the event's owner.
            foreach (GameObject g in u.deactivateGos) if (g != null) g.SetActive(false);
            foreach (GameObject g in u.activateGos) if (g != null) g.SetActive(true);
            u.isUsed = true;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' object '" + u.name + "' was used by " + m.Sender + "; shown used here");
        }

        // Test helper: use n unused objects of an event the way the interact key does.
        public static void TestUse(WorldEventBase ev, int n)
        {
            MethodInfo interact = AccessTools.Method(typeof(WorldEventUsableObject), "Interact");
            foreach (WorldEventUsableObject u in Usables(ev))
            {
                if (n <= 0) break;
                if (u.isUsed || !u.gameObject.activeInHierarchy) continue;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: using event object '" + u.name + "'");
                interact.Invoke(u, null);
                n--;
            }
        }

        public int EventIdOf(WorldEventBase ev) { return IdOf(ev); }

        // ---------- destructible and pickable event objects ----------
        // A crystal smashed or a tablet picked up on one machine must be gone
        // on the others too, or it could be smashed or delivered - and
        // counted - a second time there. Pickables leave the event's
        // hierarchy while carried, so their order is taken once per event.
        const byte ObjDestroyed = 1, ObjPicked = 2, ObjDropped = 3, ObjUsed = 4, ObjRitual = 5;

        // The scientist's reveal ritual runs on the speaker's machine
        // (GameManager.BeginRevealRitual -> RevealRoutine): his casting pose,
        // particles and sound start there. The others show the same until the
        // event leaves that phase.
        struct Ritual { public WorldEventBase Ev; public GameObject Npc; public int Phase; }
        readonly List<Ritual> rituals = new List<Ritual>();

        static List<DialogueNPCModule> Talkers(WorldEventBase ev)
        {
            return new List<DialogueNPCModule>(ev.GetComponentsInChildren<DialogueNPCModule>(true));
        }

        [HarmonyPatch(typeof(GameManager), "BeginRevealRitual")]
        static class RitualPatch
        {
            static void Prefix(out GameObject __state) { __state = InterfaceManager.instance != null ? InterfaceManager.instance.currentTalkTarget : null; }

            static void Postfix(GameObject __state)
            {
                WorldEventSync s = Instance;
                if (s == null || applying || !s.Connected || __state == null) return;
                WorldEventBase ev = __state.GetComponentInParent<WorldEventBase>();
                if (ev == null || s.IdOf(ev) == 0) return;
                int index = Talkers(ev).IndexOf(__state.GetComponent<DialogueNPCModule>());
                if (index < 0) return;
                s.SendObject(ev, ObjRitual, index, __state.transform.position);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' ritual started here by '" + __state.name + "'; told the others");
            }
        }

        static void Casting(GameObject npc, bool on)
        {
            Animator a = npc.GetComponent<Animator>();
            if (a != null) a.SetBool("Casting2", on);
            FighterNPCModule f = npc.GetComponent<FighterNPCModule>();
            if (f == null || f.spawnPoint == null) return;
            ParticleSystem ps = f.spawnPoint.GetComponent<ParticleSystem>();
            AudioSource au = f.spawnPoint.GetComponent<AudioSource>();
            if (on) { if (ps != null) ps.Play(); if (au != null) au.Play(); }
            else { if (ps != null) ps.Stop(); if (au != null) au.Stop(); }
        }

        void StopFinishedRituals()
        {
            for (int i = rituals.Count - 1; i >= 0; i--)
            {
                Ritual r = rituals[i];
                if (r.Ev != null && r.Npc != null && r.Ev.isActive && r.Ev.currentPhase == r.Phase) continue;
                if (r.Npc != null) Casting(r.Npc, false);
                rituals.RemoveAt(i);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Ritual over; casting stopped here");
            }
        }

        static List<UsableObject> PlainUsables(WorldEventBase ev)
        {
            return new List<UsableObject>(ev.GetComponentsInChildren<UsableObject>(true));
        }

        // Plain usable objects in an event (the tower top of a platforming
        // phase) send a GameManager message; once used here, the others' copy
        // goes, so it can't be used - and counted - again. A generated
        // dungeon's exit stays: everyone needs it to get out.
        [HarmonyPatch(typeof(UsableObject), "Interact")]
        static class PlainUsePatch
        {
            static void Postfix(UsableObject __instance)
            {
                WorldEventSync s = Instance;
                if (s == null || applying || !s.Connected) return;
                WorldEventBase ev = __instance.GetComponentInParent<WorldEventBase>();
                if (ev == null || s.IdOf(ev) == 0) return;
                int index = PlainUsables(ev).IndexOf(__instance);
                if (index < 0) return;
                s.SendObject(ev, ObjUsed, index, __instance.transform.position);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' object '" + __instance.name + "' (" + __instance.message + ") used here; told the others");
            }
        }
        readonly Dictionary<WorldEventBase, List<PickableObject>> pickables = new Dictionary<WorldEventBase, List<PickableObject>>();

        static List<WorldEventDestructibleObject> Destructibles(WorldEventBase ev)
        {
            return new List<WorldEventDestructibleObject>(ev.GetComponentsInChildren<WorldEventDestructibleObject>(true));
        }

        List<PickableObject> Pickables(WorldEventBase ev)
        {
            List<PickableObject> list;
            if (!pickables.TryGetValue(ev, out list)) pickables[ev] = list = new List<PickableObject>(ev.GetComponentsInChildren<PickableObject>(true));
            return list;
        }

        // The registered event a pickable belongs to (also while carried).
        WorldEventBase EventOfPickable(PickableObject p)
        {
            foreach (KeyValuePair<WorldEventBase, List<PickableObject>> kv in pickables) if (kv.Key != null && kv.Value.Contains(p)) return kv.Key;
            WorldEventBase ev = p.GetComponentInParent<WorldEventBase>();
            if (ev != null && IdOf(ev) != 0) { Pickables(ev); return ev; }
            return null;
        }

        void SendObject(WorldEventBase ev, byte kind, int index, Vector3 pos)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(IdOf(ev)); w.Write(kind); w.Write(index); w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                w.Flush();
                net.SendMessage(Net.MsgEventObject, 0, ms.ToArray(), true);
            }
            ObjectsSent++;
        }

        void ApplyObject(Net.Message m)
        {
            int eventId, index; byte kind; Vector3 pos;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                eventId = r.ReadInt32(); kind = r.ReadByte(); index = r.ReadInt32();
                pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
            WorldEventBase ev;
            if (!byId.TryGetValue(eventId, out ev) || ev == null) return;
            if (kind == ObjDestroyed)
            {
                List<WorldEventDestructibleObject> list = Destructibles(ev);
                if (index < 0 || index >= list.Count || list[index].health == 0) return;
                WorldEventDestructibleObject d = list[index];
                // Its visible result only; the objective counts once, at the event's owner.
                d.health = 0;
                foreach (GameObject g in d.deactivateGos) if (g != null) g.SetActive(false);
                foreach (GameObject g in d.activateGos) if (g != null) g.SetActive(true);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' object '" + d.name + "' destroyed by " + m.Sender + "; shown destroyed here");
            }
            else if (kind == ObjRitual)
            {
                List<DialogueNPCModule> list = Talkers(ev);
                if (index < 0 || index >= list.Count) return;
                GameObject npc = list[index].gameObject;
                Casting(npc, true);
                NPC n = npc.GetComponent<NPC>();
                if (n != null && n.answerQuestmark != null) n.answerQuestmark.SetActive(false);
                rituals.Add(new Ritual { Ev = ev, Npc = npc, Phase = ev.currentPhase });
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' ritual started by " + m.Sender + "; '" + npc.name + "' casts here too");
            }
            else if (kind == ObjUsed)
            {
                List<UsableObject> list = PlainUsables(ev);
                if (index < 0 || index >= list.Count) return;
                UsableObject u = list[index];
                bool keep = u.message == "DungeonUseRepository";
                if (!keep) u.gameObject.SetActive(false);
                else
                {
                    // DungeonUseRepository's sight without the trip out: the entrance
                    // at the site sinks back into the ground.
                    Transform t = u.transform.parent != null && u.transform.parent.parent != null ? u.transform.parent.parent.parent : null;
                    TweenPosition tp = t != null && t.childCount > 1 ? t.GetChild(1).GetComponent<TweenPosition>() : null;
                    if (tp != null) { tp.PlayReverse(); Plugin.Log.LogInfo(Plugin.Stamp() + " Dungeon entrance '" + tp.name + "' sinks here too"); }
                }
                if (u.message == "CompletePlatformingTower")
                {
                    // CompletePlatformingTower's sight without its objective (it counts
                    // at the owner): the spark at the top, the tower sinking back.
                    if (ExpeditionsManager.instance != null && ExpeditionsManager.instance.effectPrefab != null)
                        UnityEngine.Object.Instantiate(ExpeditionsManager.instance.effectPrefab, u.transform.position, Quaternion.identity).SetActive(true);
                    Transform tower = u.transform.parent != null && u.transform.parent.parent != null ? u.transform.parent.parent.parent : null;
                    TweenPosition tp = tower != null ? tower.GetComponent<TweenPosition>() : null;
                    if (tp != null) { tp.enabled = true; tp.PlayReverse(); Plugin.Log.LogInfo(Plugin.Stamp() + " Tower '" + tower.name + "' sinks here too"); }
                }
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' object '" + u.name + "' (" + u.message + ") used by " + m.Sender + (keep ? "; kept (exit)" : "; hidden here"));
            }
            else
            {
                List<PickableObject> list = Pickables(ev);
                if (index < 0 || index >= list.Count || list[index] == null) return;
                PickableObject p = list[index];
                if (kind == ObjPicked) p.gameObject.SetActive(false);
                else { p.transform.position = pos; p.gameObject.SetActive(true); }
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' object '" + p.name + "' " + (kind == ObjPicked ? "picked up" : "put down") + " by " + m.Sender);
            }
            ObjectsApplied++;
        }

        [HarmonyPatch(typeof(WorldEventDestructibleObject), "Damage", new Type[] { typeof(int), typeof(NPC.DamageType), typeof(GameObject), typeof(bool) })]
        static class DestroyPatch
        {
            static void Prefix(WorldEventDestructibleObject __instance, out int __state) { __state = __instance.health; }

            static void Postfix(WorldEventDestructibleObject __instance, int __state)
            {
                WorldEventSync s = Instance;
                if (s == null || applying || __state == 0 || __instance.health != 0 || __instance.baseEvent == null || !s.Connected) return;
                int index = Destructibles(__instance.baseEvent).IndexOf(__instance);
                if (s.IdOf(__instance.baseEvent) == 0 || index < 0) return;
                s.SendObject(__instance.baseEvent, ObjDestroyed, index, __instance.transform.position);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + __instance.baseEvent.eventName + "' object '" + __instance.name + "' destroyed here; told the others");
            }
        }

        [HarmonyPatch(typeof(PickableObject), "Interact")]
        static class PickPatch
        {
            static void Prefix(PickableObject __instance)
            {
                // Take the event's order while the object is still in its hierarchy.
                if (Instance != null) Instance.EventOfPickable(__instance);
            }

            static void Postfix(PickableObject __instance) { Changed(__instance, true); }
        }

        [HarmonyPatch(typeof(PickableObject), "Update")]
        static class DropPatch
        {
            static void Prefix(PickableObject __instance, out bool __state) { __state = (bool)Traverse.Create(__instance).Field("pickedUp").GetValue(); }

            static void Postfix(PickableObject __instance, bool __state)
            {
                if (__state && !(bool)Traverse.Create(__instance).Field("pickedUp").GetValue()) Changed(__instance, false);
            }
        }

        static void Changed(PickableObject p, bool picked)
        {
            WorldEventSync s = Instance;
            if (s == null || applying || !s.Connected) return;
            if (picked && !(bool)Traverse.Create(p).Field("pickedUp").GetValue()) return;
            WorldEventBase ev = s.EventOfPickable(p);
            if (ev == null) return;
            int index = s.Pickables(ev).IndexOf(p);
            if (index < 0) return;
            s.SendObject(ev, picked ? ObjPicked : ObjDropped, index, p.transform.position);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' object '" + p.name + "' " + (picked ? "picked up" : "put down") + " here; told the others");
        }

        // Log only: who killed an event enemy, and whether the game counts it.
        [HarmonyPatch(typeof(WorldEventNPCModule), "WorldEventDeath")]
        static class KillLogPatch
        {
            static void Prefix(WorldEventNPCModule __instance, GameObject attacked)
            {
                if (Instance == null || EnemySync.Applying) return;
                bool byPlayer = (bool)Traverse.Create(__instance).Field("wasAttackedByPlayer").GetValue();
                string who = attacked == null ? "nobody" : attacked.GetComponent<PuppetBody>() != null ? "a friend (" + attacked.name + ")"
                    : attacked.CompareTag("Player") ? "the local player" : attacked.GetComponent<CompanionNPCModule>() != null ? "companion '" + attacked.name + "'"
                    : "'" + attacked.name + "'";
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event enemy '" + __instance.name + "' killed by " + who + "; hit by a player: " + byPlayer);
            }
        }

        [HarmonyPatch(typeof(WorldEventBase), "CompleteEvent")]
        static class CompletePatch
        {
            static void Prefix(WorldEventBase __instance, out bool __state) { __state = __instance.isActive; }

            static void Postfix(WorldEventBase __instance, bool __state)
            {
                WorldEventSync s = Instance;
                // Only a real completion (the game ignores an already finished event).
                // The owner tells everyone; so does whoever completes it while nobody
                // owns it (all players away, e.g. in transit).
                if (s == null || applying || !__state || __instance.isActive || !s.Connected || s.Foreign(__instance)) return;
                s.net.SendMessage(Net.MsgEventComplete, 0, BitConverter.GetBytes(s.IdOf(__instance)), true);
                s.Completions++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + __instance.eventName + "' completed by its owner (us); everyone completes it");
            }
        }

        void SendStates()
        {
            List<WorldEventBase> mine = new List<WorldEventBase>();
            foreach (WorldEventBase ev in idOf.Keys) if (ev != null && Mine(ev)) mine.Add(ev);
            if (mine.Count == 0) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(mine.Count);
                foreach (WorldEventBase ev in mine) { w.Write(IdOf(ev)); w.Write(ev.isActive); w.Write(ev.currentPhase); w.Write(ev.currentCount); }
                w.Flush();
                net.SendMessage(Net.MsgEventState, 0, ms.ToArray(), false);
            }
        }

        void ApplyStates(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadInt32(); bool active = r.ReadBoolean(); int phase = r.ReadInt32(), count = r.ReadInt32();
                    WorldEventBase ev;
                    if (!byId.TryGetValue(id, out ev) || ev == null || enemies.OwnerOf(id) != m.Sender) continue;
                    if (ev.currentPhase != phase)
                    {
                        // Same switch as CompleteEventObjective does on a phase change.
                        foreach (WorldEventBase.Phase p in ev.phases)
                        {
                            if (p.activatedGameObjects == null) continue;
                            if (p.id == ev.currentPhase) foreach (GameObject g in p.activatedGameObjects) if (g != null && g.GetComponent<NPC>() == null) g.SetActive(false);
                            if (p.id == phase) foreach (GameObject g in p.activatedGameObjects) if (g != null) g.SetActive(true);
                        }
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' phase " + ev.currentPhase + " -> " + phase + " (owner's)");
                        ShowPhase(ev, phase);
                    }
                    if (ev.currentPhase != phase || ev.currentCount != count)
                    {
                        ev.currentPhase = phase;
                        ev.currentCount = count;
                        ev.gameObject.SendMessage("UpdateDatabase", false, SendMessageOptions.DontRequireReceiver);
                        ShowProgress(ev);
                    }
                    if (!active && ev.isActive) Complete(ev); // missed the completion message
                }
            }
        }

        // What CompleteEventObjective shows the owner on a phase change: the
        // phase's description and big message, the phase sound, a fresh bar -
        // here too when this event is the one on our screen (or close by).
        bool OnOurScreen(WorldEventBase ev)
        {
            if (InterfaceManager.instance == null) return false;
            if (InterfaceManager.instance.currentWorldEvent == ev) return true;
            return PlayerScript.instance != null && Vector3.Distance(PlayerScript.instance.transform.position, ev.transform.position) < ev.activeRange;
        }

        void ShowPhase(WorldEventBase ev, int phase)
        {
            if (!OnOurScreen(ev)) return;
            foreach (WorldEventBase.Phase p in ev.phases)
            {
                if (p.id != phase) continue;
                bool en = Localization.language == "English";
                InterfaceManager.instance.worldEventDescription.text = en ? p.description : p.descriptionRus;
                string big = en ? p.bigMessage : p.bigMessageRus;
                if (!string.IsNullOrEmpty(big)) InterfaceManager.instance.screenMessagesLarge.Add(big, Color.white, 3f);
            }
            InterfaceManager.instance.worldEventProgressPlus.value = 0f;
            InterfaceManager.instance.worldEventProgressMinus.value = 0f;
            if (Camera.main != null && Camera.main.GetComponent<AudioSource>() != null)
                Camera.main.GetComponent<AudioSource>().PlayOneShot(InterfaceManager.instance.interfacesounds[20]);
            PhaseShows++;
        }

        void ShowProgress(WorldEventBase ev)
        {
            if (InterfaceManager.instance == null || InterfaceManager.instance.currentWorldEvent != ev) return;
            foreach (WorldEventBase.Phase p in ev.phases)
            {
                if (p.id != ev.currentPhase) continue;
                if (ev.currentCount >= 0)
                {
                    InterfaceManager.instance.worldEventProgressPlus.value = p.directionPlusAmount > 0 ? (float)ev.currentCount / p.directionPlusAmount : 0f;
                    InterfaceManager.instance.worldEventProgressMinus.value = 0f;
                }
                else
                {
                    InterfaceManager.instance.worldEventProgressMinus.value = p.directionMinusAmount != 0 ? Mathf.Abs((float)ev.currentCount / p.directionMinusAmount) : 0f;
                    InterfaceManager.instance.worldEventProgressPlus.value = 0f;
                }
            }
            ProgressShows++;
        }

        void Complete(WorldEventBase ev)
        {
            if (!ev.isActive) return;
            applying = true;
            try { completeEvent.Invoke(ev, null); Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' completed (its owner did): reward here too"); }
            catch (Exception e) { Plugin.Log.LogError("Completing event '" + ev.eventName + "' failed: " + e); }
            finally { applying = false; }
        }

        // ---------- loop ----------

        public void Tick(float now, bool inWorld)
        {
            StopFinishedRituals();
            foreach (Net.Message m in net.Messages)
            {
                try
                {
                    WorldEventBase ev;
                    switch (m.Type)
                    {
                        case Net.MsgEventState: if (inWorld) ApplyStates(m); break;
                        case Net.MsgEventSpawn: if (inWorld) ApplySpawns(m); break;
                        case Net.MsgEventObjective:
                            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
                            {
                                int id = r.ReadInt32(); bool plus = r.ReadBoolean();
                                if (!byId.TryGetValue(id, out ev) || ev == null || !Mine(ev)) break;
                                applying = true;
                                try { ev.CompleteEventObjective(plus); ObjectivesApplied++; }
                                finally { applying = false; }
                                Plugin.Log.LogInfo(Plugin.Stamp() + " Event '" + ev.eventName + "' objective (" + (plus ? "+" : "-") + ") from " + m.Sender + " applied");
                            }
                            break;
                        case Net.MsgEventUse: if (inWorld) ApplyUse(m); break;
                        case Net.MsgEventObject: if (inWorld) ApplyObject(m); break;
                        case Net.MsgEventObjSpawn: if (inWorld) ApplyObjSpawn(m); break;
                        case Net.MsgEventComplete:
                            if (byId.TryGetValue(BitConverter.ToInt32(m.Payload, 0), out ev) && ev != null) Complete(ev);
                            break;
                    }
                }
                catch (Exception e) { Plugin.Log.LogError("World event message " + m.Type + " from " + m.Sender + " failed: " + e); }
            }
            if (!inWorld || !Connected) return;
            if (now >= nextState) { nextState = now + 1f; SendStates(); }
            if (now >= nextRoster)
            {
                nextRoster = now + 3f;
                List<KeyValuePair<NPC, int>> live = new List<KeyValuePair<NPC, int>>();
                foreach (KeyValuePair<NPC, int> kv in spawnId)
                {
                    if (kv.Key == null || kv.Key.isDead) continue;
                    WorldEventNPCModule mod = kv.Key.GetComponent<WorldEventNPCModule>();
                    if (mod != null && mod.baseEvent != null && Mine(mod.baseEvent)) live.Add(kv);
                }
                SendSpawns(live, false);
            }
        }

        public string Summary()
        {
            return "events spawnsSent=" + SpawnsSent + " built=" + SpawnsBuilt + " objectivesFwd=" + ObjectivesForwarded +
                " applied=" + ObjectivesApplied + " completed=" + Completions + " objectsSent=" + ObjectsSent + " objectsApplied=" + ObjectsApplied + " objSpawnsSent=" + ObjectSpawnsSent + " objSpawnsBuilt=" + ObjectSpawnsBuilt + " clonesSent=" + ClonesSent + " clonesBuilt=" + ClonesBuilt + " phaseShows=" + PhaseShows + " progressShows=" + ProgressShows;
        }

        // Test-only: how an event is built (what counts in which phase).
        public static string Structure(WorldEventBase ev)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("Event '" + ev.eventName + "' structure:");
            foreach (WorldEventBase.Phase p in ev.phases)
            {
                sb.Append("\n  phase id=" + p.id + " plus=" + p.directionPlusAmount + " minus=" + p.directionMinusAmount + " finish=" + p.finish + " '" + p.description + "'");
                if (p.spawners != null)
                    foreach (WorldEventNPCSpawner sp in p.spawners)
                    {
                        if (sp == null) continue;
                        WorldEventNPCModule mod = sp.spawnPrefab != null ? sp.spawnPrefab.GetComponent<WorldEventNPCModule>() : null;
                        sb.Append("\n    spawner '" + sp.name + "' prefab=" + (sp.spawnPrefab != null ? sp.spawnPrefab.name : "-") + " max=" + sp.maxAllowed +
                            (mod != null ? " module plusToPhase=" + mod.plusToPhase + " isPlus=" + mod.isPlus + " anyCase=" + mod.AddsInAnyCase : " no event module"));
                    }
                if (p.activatedGameObjects != null)
                    foreach (GameObject g in p.activatedGameObjects)
                    {
                        if (g == null) continue;
                        string comps = "";
                        foreach (MonoBehaviour b in g.GetComponentsInChildren<MonoBehaviour>(true))
                            if (b != null && b.GetType().Name.StartsWith("WorldEvent")) comps += b.GetType().Name + ",";
                        sb.Append("\n    activates '" + g.name + "' [" + comps.TrimEnd(',') + "]");
                    }
            }
            return sb.ToString();
        }

        public static WorldEventBase Nearest(Vector3 near)
        {
            WorldEventSync s = Instance;
            WorldEventBase best = null;
            float bd = float.MaxValue;
            if (s == null) return null;
            foreach (WorldEventBase ev in s.idOf.Keys)
            {
                if (ev == null) continue;
                float d = Vector3.Distance(near, ev.transform.position);
                if (d < bd) { bd = d; best = ev; }
            }
            return best;
        }

        public static string Describe(Vector3 near)
        {
            WorldEventSync s = Instance;
            if (s == null) return "-";
            WorldEventBase best = null;
            float bd = float.MaxValue;
            foreach (WorldEventBase ev in s.idOf.Keys)
            {
                if (ev == null) continue;
                float d = Vector3.Distance(near, ev.transform.position);
                if (d < bd) { bd = d; best = ev; }
            }
            if (best == null) return "-";
            int alive = 0;
            foreach (WorldEventNPCSpawner sp in Spawners(best)) foreach (NPC n in sp.spawnedNPCs) if (n != null && !n.isDead) alive++;
            int owner = s.Owner(best);
            return "'" + best.eventName + "' d=" + bd.ToString("F0") + " owner=" + (owner == 0 ? "-" : owner == s.Me ? "me" : owner.ToString()) +
                " active=" + best.isActive + " phase=" + best.currentPhase + " count=" + best.currentCount + " enemiesAlive=" + alive;
        }
    }
}
