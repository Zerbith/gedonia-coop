using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // The host's world state for players who join later (policy: the host's
    // story counts).
    //
    // Gedonia keeps per-object world state in SaveLoadManager.WorldDatabase
    // (id -> data string, written by SaveableItem.UpdateDatabase and story
    // functions; "0" = gone). The host remembers the database as loaded and
    // treats every entry that changes in this session as shared: dialogue
    // switches from the story, emptied chests, killed enemies, used objects.
    //  - A player seen for the first time gets all of the session's changes,
    //    everyone gets new changes every 2 s.
    //  - The receiver stores each entry in its database (objects that load
    //    later pick it up themselves) and applies it to the object if loaded
    //    (SaveableItem.AdjustData). Living enemies and followers are skipped
    //    except "0": EnemySync already moves them live.
    public class WorldStateSync
    {
        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        Dictionary<int, string> baseline, sent, db, clientDb;
        readonly HashSet<int> known = new HashSet<int>();
        float baselineAt = -1f, nextDiff;
        public int EntriesSent, EntriesApplied, EntriesStored, Skipped, BodiesKept;
        const int Chunk = 40;

        public WorldStateSync(Net net, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.peers = peers;
        }

        static Dictionary<int, string> Database
        {
            get { return SaveLoadManager.instance != null ? SaveLoadManager.instance.WorldDatabase : null; }
        }

        public void Tick(float now, bool inWorld)
        {
            // Changes can arrive while we are still in the menu or loading:
            // keep them until the world is there.
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type == Net.MsgWorldState && !net.IsHost) inbox.Add(m);
                // A friend loaded a save: its world is its save's again, so it
                // gets this session's changes once more, like on joining.
                if (m.Type == Net.MsgWorldStateRequest && net.IsHost && known.Remove(m.Sender))
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Player " + m.Sender + " loaded a save; sending it this session's world changes again");
            }
            if (!net.IsHost && inWorld && Database != null && !ReferenceEquals(clientDb, Database))
            {
                if (clientDb != null)
                {
                    net.SendMessage(Net.MsgWorldStateRequest, 0, new byte[0], true);
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Loaded a save during the session; asked the host for its world changes again");
                }
                clientDb = Database;
            }
            if (inWorld && Database != null && inbox.Count > 0)
            {
                foreach (Net.Message m in inbox)
                {
                    try { Apply(m); }
                    catch (Exception e) { Plugin.Log.LogError("World state from " + m.Sender + " failed: " + e); }
                }
                Plugin.Log.LogInfo(Plugin.Stamp() + " Applied " + inbox.Count + " world state message(s) from the host: " + Summary());
                inbox.Clear();
            }
            if (!net.IsHost || !inWorld || Database == null) return;
            // A player who dropped out (e.g. while loading) gets everything again on return.
            known.RemoveWhere(p => !Has(peers(), p));

            // A (re)loaded save replaces the dictionary: take a new baseline
            // once the world has settled.
            if (!ReferenceEquals(db, Database)) { db = Database; baseline = null; baselineAt = now + 5f; }
            if (baseline == null)
            {
                if (now < baselineAt) return;
                baseline = new Dictionary<int, string>(db);
                // After the host loads a save mid-session, what the others were
                // sent stays: the next delta tells them every entry the load
                // turned back, so the host's world still counts everywhere.
                if (sent == null) sent = new Dictionary<int, string>(db);
                else Plugin.Log.LogInfo(Plugin.Stamp() + " Host loaded a save: " + Changed(sent).Count + " world entries differ from what the others have; sending them");
                known.Clear();
                Plugin.Log.LogInfo(Plugin.Stamp() + " World state baseline: " + baseline.Count + " entries");
            }

            if (now < nextDiff) return;
            nextDiff = now + 2f;
            foreach (int p in peers())
            {
                if (known.Add(p))
                {
                    List<KeyValuePair<int, string>> session = Changed(baseline);
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Sending " + session.Count + " world changes of this session to new player " + p);
                    Send(session, p);
                }
            }
            List<KeyValuePair<int, string>> fresh = Changed(sent);
            foreach (KeyValuePair<int, string> kv in fresh) sent[kv.Key] = kv.Value;
            if (fresh.Count > 0 && known.Count > 0) Send(fresh, 0);
        }

        readonly List<Net.Message> inbox = new List<Net.Message>();

        static bool Has(IEnumerable<int> list, int x) { foreach (int i in list) if (i == x) return true; return false; }

        List<KeyValuePair<int, string>> Changed(Dictionary<int, string> since)
        {
            List<KeyValuePair<int, string>> list = new List<KeyValuePair<int, string>>();
            foreach (KeyValuePair<int, string> kv in db)
            {
                string old;
                if (!since.TryGetValue(kv.Key, out old) || old != kv.Value) list.Add(kv);
            }
            return list;
        }

        void Send(List<KeyValuePair<int, string>> list, int target)
        {
            for (int start = 0; start < list.Count; start += Chunk)
            {
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    int n = Math.Min(Chunk, list.Count - start);
                    w.Write(n);
                    for (int i = start; i < start + n; i++) { w.Write(list[i].Key); w.Write(list[i].Value ?? ""); }
                    w.Flush();
                    net.SendMessage(Net.MsgWorldState, target, ms.ToArray(), true);
                }
            }
            EntriesSent += list.Count;
        }

        void Apply(Net.Message m)
        {
            Dictionary<int, string> local = Database;
            if (local == null) return;
            Dictionary<int, SaveableItem> loaded = null;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadInt32();
                    string data = r.ReadString();
                    string mine;
                    if (local.TryGetValue(id, out mine) && mine == data) continue;
                    if (loaded == null) loaded = LoadedById();
                    SaveableItem item;
                    bool isLoaded = loaded.TryGetValue(id, out item) && item != null;
                    if (isLoaded && data != "0" && (item.GetComponent<FighterNPCModule>() != null || item.GetComponent<CompanionNPCModule>() != null))
                    {
                        Skipped++; // a living actor: EnemySync moves it
                        // ...but its dialogue switches (open answers) are the host's story: take those.
                        if (!Plugin.NoFix("dialogueswitches")) ApplyDialogue(item, data, local);
                        if (!Plugin.NoFix("storyparts")) ApplyStoryParts(item, data, local);
                        continue;
                    }
                    local[id] = data;
                    // Someone looted an enemy's body ("0" after the loot): bodies keep
                    // their own loot per player, so ours stays until we loot it.
                    NPC body = isLoaded && data == "0" ? item.GetComponent<NPC>() : null;
                    if (body != null && body.isDead)
                    {
                        BodiesKept++;
                        EntriesStored++;
                        continue;
                    }
                    // A world event WorldEventSync runs live (phase, count, phase
                    // objects): its saved state is kept, not applied - applying
                    // it would set the phase without showing that phase's objects.
                    WorldEventBase liveEvent = isLoaded ? item.GetComponent<WorldEventBase>() : null;
                    if (liveEvent != null && WorldEventSync.Instance != null && WorldEventSync.Instance.EventIdOf(liveEvent) != 0)
                    {
                        EntriesStored++;
                        continue;
                    }
                    if (isLoaded)
                    {
                        try { item.AdjustData(data); EntriesApplied++; }
                        catch (Exception e) { Plugin.Log.LogWarning("Applying world entry " + id + " failed: " + e.Message); }
                    }
                    else EntriesStored++;
                }
            }
        }

        public int DialoguesApplied;

        // The Dialogue part of a living NPC's entry ("1|0|1|..." in module order, modules joined
        // by '/'): set its open answers like the host's, keep our own position, health etc.
        void ApplyDialogue(SaveableItem item, string data, Dictionary<int, string> local)
        {
            DialogueNPCModule d = item.GetComponent<DialogueNPCModule>();
            if (d == null || d.allDialogue == null || item.modules == null) return;
            // Story NPCs: the dialogue digest sends the host's live answers; the saved entry's
            // dialogue part can be older, and taking both flipped answers back and forth (escort test).
            if (!Plugin.NoFix("dialoguedigest") && IsStoryNpc(item.instanceID)) return;
            int m = Array.IndexOf(item.modules, SaveableItem.SaveModule.Dialogue);
            string[] parts = data.Split('/');
            if (m < 0 || parts.Length != item.modules.Length + 1) return; // part 0 is the header "1|<active>", module i is part i + 1
            string[] bits = parts[m + 1].Split('|');
            int changed = 0;
            for (int i = 0; i < d.allDialogue.Length && i < bits.Length; i++)
            {
                if (bits[i] != "0" && bits[i] != "1") continue;
                bool on = bits[i] == "1";
                if (d.allDialogue[i].isActiveNow == on) continue;
                d.allDialogue[i].isActiveNow = on;
                changed++;
            }
            if (changed == 0) return;
            // Our saved entry with the host's dialogue part.
            string mine;
            if (local.TryGetValue(item.instanceID, out mine))
            {
                string[] my = mine.Split('/');
                if (my.Length == parts.Length) { my[m + 1] = parts[m + 1]; local[item.instanceID] = string.Join("/", my); }
            }
            DialoguesApplied++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " '" + item.name + "': " + changed + " answer(s) switched like the host's");
        }

        // The module list an entry of this many parts was written with: the object's own, else
        // (story NPCs) GameManager's default list. Null if neither fits.
        static SaveableItem.SaveModule[] Layout(SaveableItem item, int parts)
        {
            if (item.modules != null && item.modules.Length == parts) return item.modules;
            if (GameManager.instance != null && GameManager.instance.defaultStoryNPCData != null)
                foreach (GameManager.storyNpcData n in GameManager.instance.defaultStoryNPCData)
                    if (n.saveableId == item.instanceID && n.modules != null && n.modules.Length == parts) return n.modules;
            return null;
        }

        static SaveableItem.SaveModule[] DefaultModules(int id)
        {
            if (GameManager.instance != null && GameManager.instance.defaultStoryNPCData != null)
                foreach (GameManager.storyNpcData n in GameManager.instance.defaultStoryNPCData) if (n.saveableId == id) return n.modules;
            return null;
        }

        static string Mods(SaveableItem.SaveModule[] m)
        {
            if (m == null) return "-";
            return string.Join(",", Array.ConvertAll(m, x => x.ToString()));
        }

        static bool IsStoryNpc(int id)
        {
            if (GameManager.instance == null || GameManager.instance.defaultStoryNPCData == null) return false;
            foreach (GameManager.storyNpcData n in GameManager.instance.defaultStoryNPCData) if (n.saveableId == id) return true;
            return false;
        }

        public int StoryPartsApplied;
        readonly Dictionary<int, string[]> lastHost = new Dictionary<int, string[]>();
        readonly HashSet<int> noLayout = new HashSet<int>();
        static readonly System.Reflection.MethodInfo fromString = HarmonyLib.AccessTools.Method(typeof(SaveableItem), "fromString");

        // The rest of a living NPC's story state in its entry: quest marks (NPC module fields 3/4),
        // a position the story moved it to (Transform module; not for NPCs EnemySync moves), escort
        // progress and collider. Health, death and loot stay with EnemySync. This is what a friend
        // (or a late joiner) missed when the host's story moved an NPC, marked it or started its walk.
        void ApplyStoryParts(SaveableItem item, string data, Dictionary<int, string> local)
        {
            if (item.modules == null) return;
            string[] parts = data.Split('/');
            // Entry = header "1|<active>" + one part per module. Story NPC entries are also written with
            // GameManager's module list (ActivateQuestEffect, UpdateDatabaseDialogue): take the list that fits.
            SaveableItem.SaveModule[] mods = Layout(item, parts.Length - 1);
            if (mods == null) { if (noLayout.Add(item.instanceID)) Plugin.Log.LogInfo("[storyparts] '" + item.name + "': entry has " + parts.Length + " parts, modules " + Mods(item.modules) + " / default " + Mods(DefaultModules(item.instanceID)) + "; not read"); return; }
            string mine;
            string[] my = local.TryGetValue(item.instanceID, out mine) ? mine.Split('/') : null;
            if (my != null && my.Length != parts.Length) my = null;
            string[] prev;
            if (lastHost.TryGetValue(item.instanceID, out prev) && prev.Length != parts.Length) prev = null;
            lastHost[item.instanceID] = parts;
            NPC npc = item.GetComponent<NPC>();
            int enemyId;
            bool movedByEnemySync = (npc != null && EnemySync.StoryRole(npc, out enemyId) != 0) || item.GetComponent<CompanionNPCModule>() != null; // companions follow their own player
            List<string> what = new List<string>();
            for (int i = 1; i < parts.Length; i++) // part 0: header "1|<active>"; module i - 1 is part i
            {
                if (prev != null && prev[i] == parts[i]) continue; // only what the host changed since its last entry
                try
                {
                    switch (mods[i - 1])
                    {
                        case SaveableItem.SaveModule.NPC:
                        {
                            string[] f = parts[i].Split('|');
                            if (npc == null || f.Length < 5 || GameManager.instance == null) break;
                            string[] mf = my != null ? my[i].Split('|') : null;
                            bool marks = false;
                            if (npc.exclamationQuestMark != null && npc.exclamationQuestMark.activeSelf != (f[3] == "2")) { GameManager.instance.ActivateQuestEffectNPC(npc, true, f[3] == "2"); marks = true; }
                            if (npc.answerQuestmark != null && npc.answerQuestmark.activeSelf != (f[4] == "2")) { GameManager.instance.ActivateQuestEffectNPC(npc, false, f[4] == "2"); marks = true; }
                            if (!marks) break;
                            if (my != null && mf != null && mf.Length >= 5) { mf[3] = f[3]; mf[4] = f[4]; my[i] = string.Join("|", mf); }
                            what.Add("quest marks");
                            break;
                        }
                        case SaveableItem.SaveModule.Transform:
                        {
                            if (movedByEnemySync || fromString == null) break;
                            string[] t = parts[i].Split('|');
                            if (t.Length < 2) break;
                            Vector3 pos = (Vector3)fromString.Invoke(item, new object[] { t[0] });
                            if (Vector3.Distance(pos, item.transform.position) < 3f) break;
                            item.transform.position = pos;
                            item.transform.rotation = Quaternion.Euler((Vector3)fromString.Invoke(item, new object[] { t[1] }));
                            if (my != null) my[i] = parts[i];
                            what.Add("position " + pos.ToString("F0"));
                            break;
                        }
                        case SaveableItem.SaveModule.Escort:
                        {
                            EscortNPCModule e = item.GetComponent<EscortNPCModule>();
                            string[] f = parts[i].Split('|');
                            if (e == null || f.Length < 2) break;
                            bool walking = f[0] == "1";
                            int p = Convert.ToInt32(f[1]);
                            if (e.isWalking == walking && e.currentPathP >= p) break;
                            if (walking && !e.isWalking) e.StartWalk();
                            else if (!walking && e.isWalking) e.isWalking = false;
                            e.currentPathP = Mathf.Max(e.currentPathP, p);
                            if (my != null) my[i] = parts[i];
                            what.Add("escort " + (walking ? "walking" : "stopped") + " at point " + p);
                            break;
                        }
                        case SaveableItem.SaveModule.Collider:
                        {
                            Collider c = item.GetComponent<Collider>();
                            bool on = parts[i] == "1";
                            if (c == null || c.enabled == on) break;
                            c.enabled = on;
                            if (my != null) my[i] = parts[i];
                            what.Add("collider " + (on ? "on" : "off"));
                            break;
                        }
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Story part " + mods[i - 1] + " of '" + item.name + "' failed: " + ex.Message); }
            }
            if (what.Count == 0) return;
            if (my != null) local[item.instanceID] = string.Join("/", my);
            StoryPartsApplied++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " '" + item.name + "': " + string.Join(", ", what.ToArray()) + " like the host's");
        }

        // Test step npclog:<save id>: quest marks, position and saved entry of a (story) NPC.
        public static void TestNpc(string arg)
        {
            int id;
            if (!int.TryParse(arg, out id)) return;
            SaveableItem item;
            string entry = SaveLoadManager.instance != null && SaveLoadManager.instance.WorldDatabase.ContainsKey(id) ? SaveLoadManager.instance.WorldDatabase[id] : "-";
            if (!LoadedById().TryGetValue(id, out item) || item == null) { Plugin.Log.LogInfo(Plugin.Stamp() + " [npclog] " + id + " not loaded; entry=" + entry); return; }
            NPC n = item.GetComponent<NPC>();
            Plugin.Log.LogInfo(Plugin.Stamp() + " [npclog] " + id + " '" + item.name + "' at " + item.transform.position.ToString("F1") +
                " exclam=" + (n != null && n.exclamationQuestMark != null ? n.exclamationQuestMark.activeSelf.ToString() : "-") +
                " answerMark=" + (n != null && n.answerQuestmark != null ? n.answerQuestmark.activeSelf.ToString() : "-") + " modules=" + Mods(item.modules) + " default=" + Mods(DefaultModules(id)) + " entry=" + entry);
        }

        static Dictionary<int, SaveableItem> LoadedById()
        {
            Dictionary<int, SaveableItem> d = new Dictionary<int, SaveableItem>();
            foreach (SaveableItem s in Resources.FindObjectsOfTypeAll<SaveableItem>())
                if (s != null && s.gameObject.scene.IsValid() && s.instanceID != 0) d[s.instanceID] = s;
            return d;
        }

        public string Summary()
        {
            return net.IsHost ? "world baseline=" + (baseline != null ? baseline.Count : 0) + " sent=" + EntriesSent
                : "world applied=" + EntriesApplied + " dialogues=" + DialoguesApplied + " storyParts=" + StoryPartsApplied + " stored=" + EntriesStored + " skipped=" + Skipped + " bodiesKept=" + BodiesKept;
        }
    }
}
