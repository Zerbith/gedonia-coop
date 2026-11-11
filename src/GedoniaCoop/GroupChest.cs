using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace GedoniaCoop
{
    // A group chest at every angel statue (statue menu). The idea was "in the
    // host's house", but each player plays in their own copy of the world, so
    // the host's house does not exist on a friend's PC; the statues stand in
    // every village for everyone. The host keeps the contents (saved per host
    // character in BepInEx/config/gedonia-coop-groupchest.txt) and sends them
    // to everyone after each change:
    //  - put: the player removes the stack from the inventory and sends it to
    //    the host, which adds it;
    //  - take: the player asks the host, the host checks the chest still has
    //    it, removes it and grants it; only then the item goes to the inventory.
    // Items: the trade's line format (type, database id, name, extra data).
    public class GroupChest
    {
        const byte KindContents = 0, KindPut = 1, KindTake = 2, KindGrant = 3, KindAsk = 4;
        readonly Net net;
        public static GroupChest Instance;
        public readonly List<TradeSync.Line> Items = new List<TradeSync.Line>();
        string loadedFor = "";
        public int Puts, Takes, Denied;
        public static string Last = "";

        public GroupChest(Net net)
        {
            this.net = net;
            Instance = this;
            if (!net.IsHost) Send(KindAsk, 0, w => { }); // a client asks for the contents once
        }
        public Net Net { get { return net; } }

        static string File { get { return Path.Combine(Paths.ConfigPath, "gedonia-coop-groupchest.txt"); } }
        static string Key { get { return PlayerScript.instance != null && !string.IsNullOrEmpty(PlayerScript.instance.characterName) ? "character:" + PlayerScript.instance.characterName : ""; } }

        public void Tick()
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgChest || m.Sender == net.LocalId) continue;
                try { Receive(m); } catch (Exception e) { Plugin.Log.LogWarning("Group chest message unreadable: " + e.Message); }
            }
            if (net.IsHost && Key.Length > 0 && Key != loadedFor) { loadedFor = Key; Load(); Broadcast(); }
        }

        delegate void Body(BinaryWriter w);
        void Send(byte kind, int target, Body body)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(kind);
                body(w);
                w.Flush();
                net.SendMessage(Net.MsgChest, target, ms.ToArray(), true);
            }
        }

        static void WriteLine(BinaryWriter w, TradeSync.Line l) { w.Write(l.Type); w.Write(l.DbId); w.Write(l.Name ?? ""); w.Write(l.Data ?? ""); w.Write(l.Amount); }
        static TradeSync.Line ReadLine(BinaryReader r) { return new TradeSync.Line { Type = r.ReadInt32(), DbId = r.ReadInt32(), Name = r.ReadString(), Data = r.ReadString(), Amount = r.ReadInt32() }; }

        void Broadcast()
        {
            Send(KindContents, 0, w => { w.Write(Items.Count); foreach (TradeSync.Line l in Items) WriteLine(w, l); });
        }

        void Receive(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                byte kind = r.ReadByte();
                if (kind == KindContents && !net.IsHost)
                {
                    Items.Clear();
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++) Items.Add(ReadLine(r));
                    Plugin.Log.LogInfo(Plugin.Stamp() + " [chest] contents from the host: " + Describe());
                }
                else if (kind == KindAsk && net.IsHost) Broadcast();
                else if (kind == KindPut && net.IsHost)
                {
                    TradeSync.Line l = ReadLine(r);
                    Add(l);
                    Plugin.Log.LogInfo(Plugin.Stamp() + " [chest] " + m.Sender + " put " + l.Amount + "x " + l.Name);
                    Save(); Broadcast();
                }
                else if (kind == KindTake && net.IsHost)
                {
                    TradeSync.Line l = ReadLine(r);
                    if (Remove(l.Key, l.Amount))
                    {
                        Plugin.Log.LogInfo(Plugin.Stamp() + " [chest] " + m.Sender + " took " + l.Amount + "x " + l.Name);
                        Send(KindGrant, m.Sender, w => WriteLine(w, l));
                        Save(); Broadcast();
                    }
                    else { Denied++; Plugin.Log.LogInfo(Plugin.Stamp() + " [chest] take by " + m.Sender + " refused (gone): " + l.Name); Broadcast(); }
                }
                else if (kind == KindGrant && m.Target == net.LocalId)
                {
                    TradeSync.Line l = ReadLine(r);
                    Give(l);
                }
            }
        }

        void Add(TradeSync.Line l)
        {
            foreach (TradeSync.Line x in Items) if (x.Key == l.Key) { x.Amount += l.Amount; return; }
            Items.Add(new TradeSync.Line { Type = l.Type, DbId = l.DbId, Name = l.Name, Data = l.Data, Amount = l.Amount });
        }

        bool Remove(string key, int amount)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Key != key || Items[i].Amount < amount) continue;
                Items[i].Amount -= amount;
                if (Items[i].Amount <= 0) Items.RemoveAt(i);
                return true;
            }
            return false;
        }

        // ---------- the local player's actions (statue menu, tests) ----------

        public bool Put(InventoryItem ii)
        {
            PlayerScript p = PlayerScript.instance;
            if (p == null || ii == null || ii.item == null || !TradeSync.Tradable(ii.item) || !p.inventory.Contains(ii)) return false;
            TradeSync.Line l = TradeSync.LineOf(ii, ii.amount);
            p.RemoveItemFromInventory(ii, ii.amount);
            TradeSync.Refresh();
            Puts++;
            Last = "put " + l.Amount + "x " + l.Name;
            TradeSync.SaveNow("chest put");
            Plugin.Log.LogInfo(Plugin.Stamp() + " [chest] " + Last);
            if (net.IsHost) { Add(l); Save(); Broadcast(); }
            else Send(KindPut, 0, w => WriteLine(w, l));
            return true;
        }

        public bool Take(int index)
        {
            if (index < 0 || index >= Items.Count) return false;
            TradeSync.Line l = Items[index];
            TradeSync.Line want = new TradeSync.Line { Type = l.Type, DbId = l.DbId, Name = l.Name, Data = l.Data, Amount = l.Amount };
            Last = "take " + want.Amount + "x " + want.Name;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [chest] " + Last + (net.IsHost ? "" : " (asking the host)"));
            if (net.IsHost)
            {
                if (!Remove(want.Key, want.Amount)) return false;
                Give(want); Save(); Broadcast();
            }
            else Send(KindTake, 0, w => WriteLine(w, want));
            return true;
        }

        void Give(TradeSync.Line l)
        {
            PlayerScript p = PlayerScript.instance;
            Item it = TradeSync.Resolve(l);
            if (p == null || it == null) { Plugin.Log.LogError("Group chest: unknown item " + l.Key + " x" + l.Amount); return; }
            if (it.stackable) p.AddItemToInventory(new InventoryItem { item = it, amount = l.Amount, additionalData = l.Data });
            else for (int i = 0; i < l.Amount; i++) p.AddItemToInventory(new InventoryItem { item = it, amount = 1, additionalData = l.Data });
            TradeSync.Refresh();
            Takes++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [chest] got " + l.Amount + "x " + l.Name);
            TradeSync.SaveNow("chest take");
        }

        public string Describe()
        {
            List<string> s = new List<string>();
            foreach (TradeSync.Line l in Items) s.Add(l.Amount + "x " + l.Name);
            return s.Count == 0 ? "empty" : string.Join(", ", s.ToArray());
        }

        public string Summary() { return "chest items=" + Items.Count + " puts=" + Puts + " takes=" + Takes + " denied=" + Denied; }

        // ---------- saving (host) ----------

        void Load()
        {
            Items.Clear();
            try
            {
                if (!System.IO.File.Exists(File)) return;
                foreach (string line in System.IO.File.ReadAllLines(File))
                {
                    if (!line.StartsWith(Key + "\t")) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 6) continue;
                    Items.Add(new TradeSync.Line { Type = int.Parse(f[1]), DbId = int.Parse(f[2]), Name = f[3], Data = f[4], Amount = int.Parse(f[5]) });
                }
                Plugin.Log.LogInfo("Group chest loaded for " + Key + ": " + Describe());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Reading the group chest failed: " + e.Message); }
        }

        void Save()
        {
            if (Key.Length == 0) return;
            try
            {
                List<string> lines = new List<string>();
                if (System.IO.File.Exists(File))
                    foreach (string line in System.IO.File.ReadAllLines(File)) if (!line.StartsWith(Key + "\t")) lines.Add(line);
                foreach (TradeSync.Line l in Items)
                    lines.Add(Key + "\t" + l.Type + "\t" + l.DbId + "\t" + Clean(l.Name) + "\t" + Clean(l.Data) + "\t" + l.Amount);
                System.IO.File.WriteAllLines(File, lines.ToArray());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Saving the group chest failed: " + e.Message); }
        }

        static string Clean(string s) { return (s ?? "").Replace("\t", " ").Replace("\n", " ").Replace("\r", " "); }
    }
}
