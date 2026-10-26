using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Expeditions (ExpeditionsManager): excavation sites that each PC rolls
    // with UnityEngine.Random - the list on a new day or rank, the event's
    // objects when a site is activated, the dungeon layout (DunGen) when one
    // is entered. Here the host's expeditions count:
    //  - the host sends its list (and rank) whenever it changes and to every
    //    new player; friends don't roll their own and take the host's,
    //    rebuilding the site markers;
    //  - activating a site seeds UnityEngine.Random with the site's id, so the
    //    event, its objects and spawn points come out the same everywhere. A
    //    friend activating asks the host, the host activates it too, and its
    //    next list makes everyone else activate it;
    //  - the runtime event is registered with WorldEventSync (one owner,
    //    shared spawns, phases, completion and rewards);
    //  - in co-op DunGen uses the site's stored seed instead of a fresh one,
    //    so generated dungeons have the same tiles (EnemySync names their
    //    enemies by their place below the dungeon root).
    public class ExpeditionSync
    {
        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        public static ExpeditionSync Instance;
        static bool applying;
        readonly HashSet<int> known = new HashSet<int>();
        readonly List<Net.Message> inbox = new List<Net.Message>();
        string lastSent, lastApplied;
        float nextCheck;
        public int ListsSent, ListsApplied, Activations, StartsAsked, StartsDone, RefreshSkipped, DungeonsSeeded;

        public ExpeditionSync(Net net, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.peers = peers;
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }
        static ExpeditionsManager Em { get { return ExpeditionsManager.instance; } }

        public void Tick(float now, bool inWorld)
        {
            foreach (Net.Message m in net.Messages)
                if (m.Type == Net.MsgExpeditions && !net.IsHost) inbox.Add(m);
                else if (m.Type == Net.MsgExpeditionStart && net.IsHost) inbox.Add(m);
            if (!inWorld || Em == null) return;
            foreach (Net.Message m in inbox)
            {
                try
                {
                    if (m.Type == Net.MsgExpeditions) ApplyList(m.Payload);
                    else StartAsked(m);
                }
                catch (Exception e) { Plugin.Log.LogError("Expedition message from " + m.Sender + " failed: " + e); }
            }
            inbox.Clear();

            if (!net.IsHost || now < nextCheck) return;
            nextCheck = now + 1f;
            known.RemoveWhere(p => { foreach (int q in peers()) if (q == p) return false; return true; });
            bool newPeer = false;
            foreach (int p in peers()) if (known.Add(p)) newPeer = true;
            if (!Connected) return;
            byte[] data = Encode();
            string key = Convert.ToBase64String(data);
            if (key == lastSent && !newPeer) return;
            lastSent = key;
            net.SendMessage(Net.MsgExpeditions, 0, data, true);
            ListsSent++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Sent expeditions (rank " + Em.currentRank + "): " + Describe());
        }

        // ---------- host ----------

        static byte[] Encode()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(Em.currentRank);
                w.Write(Em.expeditions.Count);
                foreach (ExpeditionsManager.Expedition e in Em.expeditions)
                {
                    w.Write(e.spot.x); w.Write(e.spot.y); w.Write(e.spot.z);
                    w.Write(e.difficulty);
                    for (int i = 0; i < 3; i++) w.Write(e.eventsTypes != null && i < e.eventsTypes.Length ? e.eventsTypes[i] ?? "" : "");
                    w.Write(e.isCreated); w.Write(e.isSolved); w.Write(e.savedId);
                    for (int i = 0; i < 3; i++) w.Write(e.dungeonIds != null && i < e.dungeonIds.Length ? e.dungeonIds[i] : 0);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        void StartAsked(Net.Message m)
        {
            int savedId = BitConverter.ToInt32(m.Payload, 0);
            int i = Em.expeditions.FindIndex(e => e.savedId == savedId);
            if (i < 0) { Plugin.Log.LogWarning("Expedition " + savedId + " started by " + m.Sender + " is not in the host's list"); return; }
            ExpeditionsManager.Expedition x = Em.expeditions[i];
            Plugin.Log.LogInfo(Plugin.Stamp() + " Expedition " + savedId + " started by " + m.Sender + (x.isCreated || x.isSolved ? " (already running here)" : ""));
            if (x.isCreated || x.isSolved) return;
            RemoveMarker(x.spot);
            Spark(x.spot);
            Em.ActivateExpedition(x.spot);
            StartsDone++;
        }

        // ---------- friend ----------

        void ApplyList(byte[] data)
        {
            string key = Convert.ToBase64String(data);
            if (key == lastApplied) return;
            lastApplied = key;
            ExpeditionsManager em = Em;
            List<ExpeditionsManager.Expedition> host = new List<ExpeditionsManager.Expedition>();
            int rank;
            using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
            {
                rank = r.ReadInt32();
                int n = r.ReadInt32();
                for (int k = 0; k < n; k++)
                {
                    ExpeditionsManager.Expedition e = new ExpeditionsManager.Expedition();
                    e.spot = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    e.difficulty = r.ReadInt32();
                    e.eventsTypes = new string[] { r.ReadString(), r.ReadString(), r.ReadString() };
                    e.isCreated = r.ReadBoolean(); e.isSolved = r.ReadBoolean(); e.savedId = r.ReadInt32();
                    e.dungeonIds = new int[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
                    host.Add(e);
                }
            }

            Dictionary<int, ExpeditionsManager.Expedition> mine = new Dictionary<int, ExpeditionsManager.Expedition>();
            foreach (ExpeditionsManager.Expedition e in em.expeditions) mine[e.savedId] = e;
            HashSet<int> hostIds = new HashSet<int>();
            foreach (ExpeditionsManager.Expedition e in host) hostIds.Add(e.savedId);
            // Our own sites the host doesn't have: their events go.
            foreach (ExpeditionsManager.Expedition e in em.expeditions)
                if (!hostIds.Contains(e.savedId) && e.worldEvent != null) UnityEngine.Object.Destroy(e.worldEvent.gameObject);
            foreach (GameObject g in em.createdActivations) if (g != null) UnityEngine.Object.Destroy(g);
            em.createdActivations.Clear();

            List<ExpeditionsManager.Expedition> next = new List<ExpeditionsManager.Expedition>();
            List<Vector3> toActivate = new List<Vector3>();
            foreach (ExpeditionsManager.Expedition h in host)
            {
                ExpeditionsManager.Expedition e = h;
                ExpeditionsManager.Expedition old;
                bool running = mine.TryGetValue(h.savedId, out old) && old.isCreated && old.worldEvent != null;
                if (h.isSolved)
                {
                    // Solved by the host; our copy of the event (if any) goes like the game's own.
                    if (running) UnityEngine.Object.Destroy(old.worldEvent.gameObject, 10f);
                    e.worldEvent = null;
                }
                else if (h.isCreated)
                {
                    if (running) e.worldEvent = old.worldEvent;
                    else { e.isCreated = false; toActivate.Add(h.spot); }
                }
                next.Add(e);
            }
            em.currentRank = rank;
            em.expeditions.Clear();
            em.expeditions.AddRange(next);
            foreach (ExpeditionsManager.Expedition e in em.expeditions)
                if (!e.isSolved && !e.isCreated && !toActivate.Contains(e.spot))
                    em.createdActivations.Add(UnityEngine.Object.Instantiate(em.activatePrefab, e.spot, Quaternion.identity));
            applying = true;
            try { foreach (Vector3 spot in toActivate) { Spark(spot); em.ActivateExpedition(spot); } }
            finally { applying = false; }
            ListsApplied++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Took the host's expeditions (rank " + rank + ", " + toActivate.Count + " started here): " + Describe());
        }

        // The spark GameManager.ActivateExpedition shows at a marker when a
        // player uses it - here for a site someone else started, if in sight.
        public static int Sparks;

        static void Spark(Vector3 spot)
        {
            if (Em == null || Em.effectPrefab == null || PlayerScript.instance == null) return;
            if (Vector3.Distance(PlayerScript.instance.transform.position, spot) > 150f) return;
            UnityEngine.Object.Instantiate(Em.effectPrefab, spot, Quaternion.identity).SetActive(true);
            Sparks++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Expedition started by someone else at " + spot.ToString("F0") + "; spark shown here");
        }

        static void RemoveMarker(Vector3 spot)
        {
            for (int i = Em.createdActivations.Count - 1; i >= 0; i--)
            {
                GameObject g = Em.createdActivations[i];
                if (g == null || Vector3.Distance(g.transform.position, spot) < 5f)
                {
                    if (g != null) UnityEngine.Object.Destroy(g);
                    Em.createdActivations.RemoveAt(i);
                }
            }
        }

        public static string Describe()
        {
            if (Em == null) return "-";
            List<string> parts = new List<string>();
            foreach (ExpeditionsManager.Expedition e in Em.expeditions)
                parts.Add(e.savedId + (e.isSolved ? "S" : e.isCreated ? "C" : "") + "[" + string.Join("+", e.eventsTypes ?? new string[0]) + "]@" + e.spot.ToString("F0"));
            return Em.expeditions.Count + " sites, " + Em.createdActivations.Count + " markers: " + string.Join(" ", parts.ToArray());
        }

        public string Summary()
        {
            return net.IsHost ? "expeditions sent=" + ListsSent + " startsDone=" + StartsDone + " activations=" + Activations + " dungeonsSeeded=" + DungeonsSeeded
                : "expeditions applied=" + ListsApplied + " startsAsked=" + StartsAsked + " activations=" + Activations + " refreshSkipped=" + RefreshSkipped + " dungeonsSeeded=" + DungeonsSeeded;
        }

        static int EventId(int savedId) { return WorldIds.Hash("exp/" + savedId); }

        // The site ActivateExpedition will pick (its own test: within 5 m, not solved).
        static int SiteAt(Vector3 position)
        {
            if (Em == null) return -1;
            for (int i = 0; i < Em.expeditions.Count; i++)
                if (Vector3.Distance(position, Em.expeditions[i].spot) < 5f && !Em.expeditions[i].isSolved) return i;
            return -1;
        }

        // ---------- patches ----------

        [HarmonyPatch(typeof(ExpeditionsManager), "RefreshExpeditions")]
        static class RefreshPatch
        {
            static bool Prefix()
            {
                ExpeditionSync s = Instance;
                if (s == null || s.net.IsHost || !s.Connected) return true;
                s.RefreshSkipped++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Expedition re-roll skipped (the host's list counts)");
                return false;
            }
        }

        [HarmonyPatch(typeof(ExpeditionsManager), "ActivateExpedition")]
        static class ActivatePatch
        {
            static void Prefix(Vector3 position, out UnityEngine.Random.State __state)
            {
                __state = UnityEngine.Random.state;
                int i = SiteAt(position);
                // Same objects, spots and spawn points on every machine (also
                // for sites restored from a save).
                if (i >= 0) UnityEngine.Random.InitState(Em.expeditions[i].savedId);
            }

            static void Postfix(Vector3 position, UnityEngine.Random.State __state)
            {
                UnityEngine.Random.state = __state;
                ExpeditionSync s = Instance;
                if (Em == null) return;
                foreach (ExpeditionsManager.Expedition e in Em.expeditions)
                {
                    if (e.worldEvent == null || Vector3.Distance(position, e.spot) >= 5f) continue;
                    if (WorldEventSync.Instance != null) WorldEventSync.Instance.Register(e.worldEvent, EventId(e.savedId));
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Expedition " + e.savedId + " [" + string.Join("+", e.eventsTypes) + "] started at " + e.spot.ToString("F0") +
                        " layout=" + LayoutHash(e.worldEvent.transform));
                    if (s == null) break;
                    s.Activations++;
                    if (!s.net.IsHost && s.Connected && !applying)
                    {
                        s.net.SendMessage(Net.MsgExpeditionStart, 0, BitConverter.GetBytes(e.savedId), true);
                        s.StartsAsked++;
                    }
                    break;
                }
            }
        }

        // Log the reward: the badge goes to the inventory, the chest (rolled per
        // machine: personal loot) appears at the site.
        [HarmonyPatch(typeof(ExpeditionsManager), "SolveExpedition")]
        static class SolvePatch
        {
            static void Postfix()
            {
                if (Em == null) return;
                foreach (ExpeditionsManager.Expedition e in Em.expeditions)
                {
                    if (!e.isSolved) continue;
                    foreach (Container c in UnityEngine.Object.FindObjectsOfType<Container>())
                        if (Vector3.Distance(c.transform.position, e.spot) < 1f)
                            Plugin.Log.LogInfo(Plugin.Stamp() + " Expedition " + e.savedId + " solved here: reward chest with " + c.loot.Count + " items");
                }
            }
        }

        // A check value for "the same objects in the same places".
        public static int LayoutHash(Transform root)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                sb.Append(t.name).Append(t.localPosition.ToString("F2")).Append(';');
            return WorldIds.Hash(sb.ToString());
        }

        [HarmonyPatch(typeof(DunGen.DungeonGenerator), "Generate")]
        static class DungeonSeedPatch
        {
            static void Prefix(DunGen.DungeonGenerator __instance, out UnityEngine.Random.State __state)
            {
                __state = UnityEngine.Random.state;
                ExpeditionSync s = Instance;
                if (s == null || !s.Connected) return;
                // The stored seed (an expedition's dungeon id), not a fresh one.
                __instance.ShouldRandomizeSeed = false;
                UnityEngine.Random.InitState(__instance.Seed);
                s.DungeonsSeeded++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Generating dungeon with shared seed " + __instance.Seed);
            }

            static void Postfix(DunGen.DungeonGenerator __instance, UnityEngine.Random.State __state)
            {
                UnityEngine.Random.state = __state;
                if (Instance != null && Instance.Connected && __instance.Root != null)
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Dungeon generated (seed " + __instance.ChosenSeed + ") layout=" + LayoutHash(__instance.Root.transform) +
                        " tiles=" + __instance.Root.transform.childCount);
            }
        }
    }
}
