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
                : "world applied=" + EntriesApplied + " stored=" + EntriesStored + " skipped=" + Skipped + " bodiesKept=" + BodiesKept;
        }
    }
}
