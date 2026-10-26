using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Shares chests, ore veins and herbs (all are Container in Gedonia): what
    // one player takes is gone for everyone, and an emptied chest is empty
    // everywhere. Enemy bodies keep their own loot per player.
    //
    //  - Opening asks the host for the container's lock first. While someone
    //    has it open, others get "<name> is searching this", so two players
    //    can never take the same item.
    //  - The holder reports every take (which items left the list); everyone
    //    removes the same items. A take for a container that isn't loaded
    //    here waits until it is.
    //  - Takes are sent three times with an id and applied once, because the
    //    plain UDP transport doesn't guarantee delivery.
    public class LootSync
    {
        class Take { public int Container; public List<KeyValuePair<int, KeyValuePair<int, string>>> Items; } // (type, (dbId, name))

        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        readonly Func<int, string> nameOf;
        public static LootSync Instance;

        readonly Dictionary<Container, int> idOf = new Dictionary<Container, int>();
        readonly Dictionary<int, Container> byId = new Dictionary<int, Container>();
        readonly Dictionary<int, KeyValuePair<int, float>> locks = new Dictionary<int, KeyValuePair<int, float>>(); // host: id -> (holder, since)
        readonly HashSet<string> appliedTakes = new HashSet<string>();
        readonly Dictionary<int, List<Take>> waiting = new Dictionary<int, List<Take>>(); // takes for containers not loaded here
        readonly List<KeyValuePair<float, byte[]>> resends = new List<KeyValuePair<float, byte[]>>();

        int myLock;                   // container id we may open
        float myLockSince;
        bool windowSeen;
        Container pendingOpen;
        float nextRequest;
        int takeSeq;
        List<Item> before;            // container contents before a take, while we hold its lock
        float nextScan;
        public int Grants, Denials, TakesSent, TakesApplied, TakesWaiting, Mismatches;

        public LootSync(Net net, Func<IEnumerable<int>> peers, Func<int, string> nameOf)
        {
            this.net = net;
            this.peers = peers;
            this.nameOf = nameOf;
            Instance = this;
            SceneManager.sceneLoaded += (s, m) => Index(s);
            for (int i = 0; i < SceneManager.sceneCount; i++) Index(SceneManager.GetSceneAt(i));
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        void Index(Scene scene)
        {
            if (!scene.IsValid()) return;
            int n = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Container c in root.GetComponentsInChildren<Container>(true))
                {
                    int id = WorldIds.Of(c.transform, scene.name);
                    idOf[c] = id;
                    byId[id] = c;
                    n++;
                }
            }
            if (n > 0) Plugin.Log.LogInfo("Container ids: " + n + " in scene " + scene.name);
        }

        public int IdOf(Container c) { int id; return c != null && idOf.TryGetValue(c, out id) ? id : 0; }

        // ---------- opening: ask the host for the lock ----------

        [HarmonyPatch(typeof(Container), "Interact")]
        static class InteractPatch
        {
            static bool Prefix(Container __instance)
            {
                LootSync s = Instance;
                if (s == null || !s.Connected) return true;
                int id = s.IdOf(__instance);
                if (id == 0 || s.myLock == id) return true;
                s.RequestLock(__instance, id);
                return false;
            }
        }

        void RequestLock(Container c, int id)
        {
            if (myLock != 0) Release(); // one container at a time
            pendingOpen = c;
            nextRequest = 0f;
        }

        void SendRequest(float now)
        {
            if (pendingOpen == null || now < nextRequest) return;
            nextRequest = now + 1f;
            int id = IdOf(pendingOpen);
            if (net.IsHost) { HostLock(id, net.LocalId); return; }
            net.SendMessage(Net.MsgLootLock, 0, BitConverter.GetBytes(id), true);
        }

        void HostLock(int id, int who)
        {
            KeyValuePair<int, float> cur;
            float now = Time.realtimeSinceStartup;
            bool free = !locks.TryGetValue(id, out cur) || cur.Key == who || now - cur.Value > 60f ||
                (cur.Key != net.LocalId && !Has(peers(), cur.Key));
            if (free) locks[id] = new KeyValuePair<int, float>(who, now);
            int holder = free ? who : cur.Key;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(id); w.Write(who); w.Write(free); w.Write(holder); w.Flush();
                byte[] payload = ms.ToArray();
                net.SendMessage(Net.MsgLootGrant, 0, payload, true); // everyone: the chest looks open
                Grant(payload, net.LocalId);
            }
        }

        static bool Has(IEnumerable<int> list, int x) { foreach (int i in list) if (i == x) return true; return false; }

        void Grant(byte[] payload, int sender)
        {
            int id, who, holder; bool ok;
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload))) { id = r.ReadInt32(); who = r.ReadInt32(); ok = r.ReadBoolean(); holder = r.ReadInt32(); }
            Container c;
            if (ok && byId.TryGetValue(id, out c) && c != null && c.anim != null) c.anim.SetBool("isOpen", true);
            if (who != net.LocalId || pendingOpen == null || IdOf(pendingOpen) != id) return;
            Container open = pendingOpen;
            pendingOpen = null;
            if (!ok)
            {
                Denials++;
                string msg = (nameOf(holder) ?? "Someone") + " is searching this";
                Plugin.Log.LogInfo(Plugin.Stamp() + " Container " + id + " is locked by " + holder + ": " + msg);
                if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessages.Add(msg, Color.yellow, 1f);
                return;
            }
            Grants++;
            myLock = id;
            myLockSince = Time.realtimeSinceStartup;
            windowSeen = false;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Container " + id + " lock granted; opening (" + open.loot.Count + " items)");
            open.Interact(); // runs for real now: myLock == id
        }

        void Release()
        {
            if (myLock == 0) return;
            int id = myLock;
            myLock = 0;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Container " + id + " lock released");
            if (net.IsHost) { locks.Remove(id); return; }
            byte[] payload = BitConverter.GetBytes(id);
            SendTwiceMore(Net.MsgLootRelease, payload);
            net.SendMessage(Net.MsgLootRelease, 0, payload, true);
        }

        // The loot window opens after a gathering animation (up to 3 s) and
        // the lock ends when it closes again, or if it never opens.
        void WatchLock(float now)
        {
            if (myLock == 0 || InterfaceManager.instance == null) return;
            bool open = InterfaceManager.instance.lootWindow.activeSelf;
            if (open) windowSeen = true;
            if ((windowSeen && !open) || (!windowSeen && now - myLockSince > 6f) || now - myLockSince > 120f) Release();
        }

        // ---------- taking ----------

        [HarmonyPatch(typeof(InterfaceManager), "TakeLootItem")]
        static class TakeOnePatch
        {
            static void Prefix() { if (Instance != null) Instance.BeforeTake(); }
            static void Postfix() { if (Instance != null) Instance.AfterTake(); }
        }

        [HarmonyPatch(typeof(InterfaceManager), "TakeAllLoot")]
        static class TakeAllPatch
        {
            static void Prefix() { if (Instance != null) Instance.BeforeTake(); }
            static void Postfix() { if (Instance != null) Instance.AfterTake(); }
        }

        Container LootedContainer()
        {
            GameObject t = GameManager.instance != null ? GameManager.instance.lootedTarget : null;
            return t != null ? t.GetComponent<Container>() : null;
        }

        void BeforeTake()
        {
            before = null;
            Container c = LootedContainer();
            if (c == null || IdOf(c) == 0 || IdOf(c) != myLock) return;
            before = new List<Item>(c.loot);
        }

        void AfterTake()
        {
            Container c = LootedContainer();
            if (before == null || c == null) return;
            // What left the list, in order.
            List<Item> left = new List<Item>(c.loot);
            List<KeyValuePair<int, KeyValuePair<int, string>>> taken = new List<KeyValuePair<int, KeyValuePair<int, string>>>();
            foreach (Item it in before)
            {
                int i = left.IndexOf(it);
                if (i >= 0) { left.RemoveAt(i); continue; }
                taken.Add(new KeyValuePair<int, KeyValuePair<int, string>>((int)it.itemType, new KeyValuePair<int, string>(it.databaseId, it.name)));
            }
            before = null;
            if (taken.Count == 0) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(net.LocalId); w.Write(++takeSeq); w.Write(IdOf(c)); w.Write(c.loot.Count);
                w.Write(taken.Count);
                foreach (KeyValuePair<int, KeyValuePair<int, string>> t in taken) { w.Write(t.Key); w.Write(t.Value.Key); w.Write(t.Value.Value ?? ""); }
                w.Flush();
                byte[] payload = ms.ToArray();
                net.SendMessage(Net.MsgLootTake, 0, payload, true);
                SendTwiceMore(Net.MsgLootTake, payload);
            }
            TakesSent++;
            string names = "";
            foreach (KeyValuePair<int, KeyValuePair<int, string>> t in taken) names += (names.Length > 0 ? ", " : "") + t.Value.Value;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Took from container " + IdOf(c) + ": " + names + " (" + c.loot.Count + " left)");
        }

        void SendTwiceMore(byte type, byte[] payload)
        {
            float now = Time.realtimeSinceStartup;
            byte[] tagged = new byte[payload.Length + 1];
            tagged[0] = type;
            payload.CopyTo(tagged, 1);
            resends.Add(new KeyValuePair<float, byte[]>(now + 0.5f, tagged));
            resends.Add(new KeyValuePair<float, byte[]>(now + 1.5f, tagged));
        }

        void ApplyTakeMessage(byte[] payload)
        {
            Take t = new Take { Items = new List<KeyValuePair<int, KeyValuePair<int, string>>>() };
            int sender, number, leftThere;
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
            {
                sender = r.ReadInt32(); number = r.ReadInt32(); t.Container = r.ReadInt32(); leftThere = r.ReadInt32();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int type = r.ReadInt32(), db = r.ReadInt32();
                    t.Items.Add(new KeyValuePair<int, KeyValuePair<int, string>>(type, new KeyValuePair<int, string>(db, r.ReadString())));
                }
            }
            if (!appliedTakes.Add(sender + ":" + number)) return; // a resend
            Container c;
            if (!byId.TryGetValue(t.Container, out c) || c == null || !c.gameObject.activeInHierarchy)
            {
                List<Take> list;
                if (!waiting.TryGetValue(t.Container, out list)) waiting[t.Container] = list = new List<Take>();
                list.Add(t);
                TakesWaiting++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Take from container " + t.Container + " by " + sender + " saved until it loads here");
                return;
            }
            Apply(c, t, leftThere);
        }

        void Apply(Container c, Take t, int leftThere)
        {
            int removed = 0;
            foreach (KeyValuePair<int, KeyValuePair<int, string>> it in t.Items)
            {
                int i = c.loot.FindIndex(x => x != null && (int)x.itemType == it.Key && x.databaseId == it.Value.Key && x.name == it.Value.Value);
                if (i >= 0) { c.loot.RemoveAt(i); removed++; }
            }
            TakesApplied++;
            bool empty = c.loot.Count == 0;
            if (leftThere >= 0 && leftThere != c.loot.Count) Mismatches++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Container " + IdOf(c) + ": " + removed + "/" + t.Items.Count + " taken by a friend removed here, " +
                c.loot.Count + " left" + (leftThere >= 0 && leftThere != c.loot.Count ? " (MISMATCH: " + leftThere + " left there)" : ""));
            // Same follow-up as the game's own take: save the contents, and
            // when empty remove the sparkle, minimap icon and collider (a
            // destroyOnLoot container then saves itself as "0").
            c.SendMessage("UpdateDatabase", false, SendMessageOptions.DontRequireReceiver);
            if (empty)
            {
                if (c.anim != null) c.anim.SetBool("isOpen", true);
                if (c.resourceActivateGo != null) c.resourceActivateGo.SetActive(true);
                if (c.resourceDeactivateGo != null) c.resourceDeactivateGo.SetActive(false);
                c.SendMessage("RemoveLootEffect", SendMessageOptions.DontRequireReceiver);
            }
            // If our own loot window shows this container, refresh it.
            if (GameManager.instance != null && GameManager.instance.lootedTarget == c.gameObject && InterfaceManager.instance.lootWindow.activeSelf)
            {
                if (empty) InterfaceManager.instance.CLoseLootWindow();
                else InterfaceManager.instance.RefreshLootWIndow();
            }
        }

        // Takes saved for containers that loaded later. Applied a moment after
        // they appear, once the save system has restored their contents.
        void ApplyWaiting(float now)
        {
            if (waiting.Count == 0) return;
            List<int> done = new List<int>();
            foreach (KeyValuePair<int, List<Take>> w in waiting)
            {
                Container c;
                if (!byId.TryGetValue(w.Key, out c) || c == null || !c.gameObject.activeInHierarchy) continue;
                foreach (Take t in w.Value) Apply(c, t, -1);
                done.Add(w.Key);
            }
            foreach (int id in done) waiting.Remove(id);
        }

        // ---------- loop ----------

        public void Tick(float now)
        {
            foreach (Net.Message m in net.Messages)
            {
                try
                {
                    switch (m.Type)
                    {
                        case Net.MsgLootLock: if (net.IsHost) HostLock(BitConverter.ToInt32(m.Payload, 0), m.Sender); break;
                        case Net.MsgLootGrant: Grant(m.Payload, m.Sender); break;
                        case Net.MsgLootRelease:
                            if (net.IsHost)
                            {
                                int id = BitConverter.ToInt32(m.Payload, 0);
                                KeyValuePair<int, float> cur;
                                if (locks.TryGetValue(id, out cur) && cur.Key == m.Sender) locks.Remove(id);
                            }
                            break;
                        case Net.MsgLootTake: ApplyTakeMessage(m.Payload); break;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Loot message " + m.Type + " from " + m.Sender + " failed: " + e);
                }
            }
            for (int i = resends.Count - 1; i >= 0; i--)
            {
                if (now < resends[i].Key) continue;
                byte[] tagged = resends[i].Value;
                byte[] payload = new byte[tagged.Length - 1];
                Buffer.BlockCopy(tagged, 1, payload, 0, payload.Length);
                net.SendMessage(tagged[0], 0, payload, true);
                resends.RemoveAt(i);
            }
            SendRequest(now);
            WatchLock(now);
            if (now >= nextScan) { nextScan = now + 2f; ApplyWaiting(now); }
        }

        public string Summary()
        {
            return "loot grants=" + Grants + " denied=" + Denials + " takesSent=" + TakesSent + " applied=" + TakesApplied +
                " waiting=" + waiting.Count + " mismatches=" + Mismatches + (net.IsHost ? " locks=" + locks.Count : "");
        }

        // ---------- test helpers ----------

        // Nearest container with at least two items: a plain chest if there is
        // one in range, else a gathering spot.
        public Container Nearest(Vector3 from, float max)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Container best = null;
                float bestD = max;
                foreach (KeyValuePair<Container, int> kv in idOf)
                {
                    Container c = kv.Key;
                    if (c == null || !c.gameObject.activeInHierarchy || c.loot.Count < 2 || c.requiresItemToOpen != null) continue;
                    if (pass == 0 && c.craftingAnimation != Container.CraftingAnimation.None) continue;
                    float d = Vector3.Distance(from, c.transform.position);
                    if (d < bestD) { bestD = d; best = c; }
                }
                if (best != null) return best;
            }
            return null;
        }

        public Container ById(int id) { Container c; return byId.TryGetValue(id, out c) ? c : null; }

        // Test helper: plain chests (no gathering animation) loaded right now.
        public List<string> Chests(Vector3 from)
        {
            List<string> list = new List<string>();
            foreach (KeyValuePair<Container, int> kv in idOf)
            {
                Container c = kv.Key;
                if (c == null || c.craftingAnimation != Container.CraftingAnimation.None) continue;
                int gold = 0;
                foreach (Item it in c.loot) if (it != null && it.itemType == ItemType.Money) gold += it.databaseId;
                list.Add(kv.Value + " '" + c.name + "' at " + c.transform.position.ToString("F0") + " d=" + Vector3.Distance(from, c.transform.position).ToString("F0") +
                    " items=" + c.loot.Count + " gold=" + gold + " anim=" + (c.anim != null) + " key=" + (c.requiresItemToOpen != null ? c.requiresItemToOpen.name : "-") +
                    " active=" + c.gameObject.activeInHierarchy);
            }
            return list;
        }

        // Test helper: was this container indexed here, and did the game destroy it since?
        public string Whereabouts(int id)
        {
            Container c;
            if (!byId.TryGetValue(id, out c)) return "not loaded here";
            if (c != null) return "loaded";
            return "destroyed here (the game removed it, e.g. world state \"0\" = gathered)";
        }

        public static string Describe(Container c)
        {
            if (c == null) return "none";
            string names = "";
            int gold = 0;
            foreach (Item it in c.loot)
            {
                names += (names.Length > 0 ? ", " : "") + (it != null ? it.name : "null");
                if (it != null && it.itemType == ItemType.Money) gold += it.databaseId;
            }
            bool open = c.anim != null && c.anim.isActiveAndEnabled && c.anim.GetBool("isOpen");
            return "container " + (Instance != null ? Instance.IdOf(c) : 0) + " '" + c.name + "' at " + c.transform.position.ToString("F0") +
                " items=" + c.loot.Count + " gold=" + gold + (c.anim != null ? " lidOpen=" + open : "") + " [" + names + "]";
        }
    }
}
