using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace GedoniaCoop
{
    // Trading items and gold between two players.
    //
    // Asking, offers and confirmations go between the two traders. Each offer
    // change gets a new version; a confirmation names both versions it saw,
    // so changing anything after the other confirmed takes that back.
    // Moving the goods is a two-phase commit with the host as coordinator:
    //  1. both confirmations name the same versions -> host: Commit
    //  2. each trader checks its offer is still there, writes it to its
    //     escrow file (BepInEx/config/gedonia-coop-trade.txt), takes it out
    //     of its inventory and reports Removed (or Failed)
    //  3. both Removed -> host writes "deliver" to its outcome file and sends
    //     Deliver (each adds the other's offer); a failure, a trader gone or
    //     20 s without an answer -> "abort", each gives itself back what it took.
    // A trader that missed the outcome (left, crashed) still has the trade in
    // its escrow file and asks the host again when it is in a co-op game; the
    // host answers from its outcome file (unknown trade = abort). So nothing
    // is given twice and nothing is lost, as far as the players' own saves go
    // (a game closed without saving goes back to its save, as in single player).
    // Items: whatever is in the inventory except gold (offered as a number),
    // mod items and quest items (cantbesold, except weapons and armour).
    public class TradeSync
    {
        const byte KAsk = 1, KReply = 2, KOffer = 3, KConfirm = 4, KCancel = 5,
            KCommit = 10, KRemoved = 11, KDeliver = 12, KAbort = 13, KQuery = 14;
        const float Reach = 15f, CommitTimeout = 20f;

        public class Line
        {
            public int Type, DbId, Amount;
            public string Name = "", Data = "";
            public string Key { get { return Type + "|" + DbId + "|" + Name + "|" + Data; } }
        }

        public class Offer
        {
            public int Version, Gold;
            public List<Line> Lines = new List<Line>();
        }

        class Session
        {
            public int Id, Partner;
            public bool Mine;              // we asked
            public bool Open;              // the other accepted
            public Offer Ours = new Offer(), Theirs = new Offer();
            public int OurConfirm = -1, OurConfirmSeen = -1;     // versions in our confirmation
            public int TheirConfirm = -1, TheirConfirmSeen = -1; // versions in theirs
            public bool Committing;
        }

        class Coord
        {
            public int Id, A, B;
            public int[] Own = { -1, -1 }, Seen = { -1, -1 };
            public bool Committed;
            public float CommitAt;
            public int[] Removed = { 0, 0 }; // 0 waiting, 1 ok, 2 failed
        }

        class Escrow
        {
            public int Id;
            public Offer Ours, Theirs;
            public float NextQuery;
        }

        readonly Net net;
        readonly Func<int, Puppet> puppetOf;
        readonly Func<IEnumerable<int>> peers;
        public static TradeSync Instance;

        Session cur;
        readonly Dictionary<int, KeyValuePair<int, float>> asks = new Dictionary<int, KeyValuePair<int, float>>(); // trade id -> (asker, when)
        readonly Dictionary<int, Escrow> escrow = new Dictionary<int, Escrow>();
        // Host only
        readonly Dictionary<int, Coord> coords = new Dictionary<int, Coord>();
        readonly Dictionary<int, bool> outcomes = new Dictionary<int, bool>(); // trade id -> delivered
        public int Delivered, Aborted, Refunded, Received, Cancelled;
        // Test switches: act as if this message got lost.
        public static bool TestDropOutcome, TestStall, TestAuto;

        static string EscrowFile { get { return Path.Combine(Paths.ConfigPath, "gedonia-coop-trade.txt"); } }
        static string OutcomeFile { get { return Path.Combine(Paths.ConfigPath, "gedonia-coop-trades-host.txt"); } }

        public TradeSync(Net net, Func<int, Puppet> puppetOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.puppetOf = puppetOf;
            this.peers = peers;
            Instance = this;
            LoadEscrow();
            LoadOutcomes();
        }

        int Me { get { return net.LocalId; } }

        string NameOf(int id)
        {
            if (id == Me) return Plugin.LocalName;
            Puppet p = puppetOf(id);
            return p != null && p.Latest != null ? p.Latest.Name : "#" + id;
        }

        static void Log(string s) { Plugin.Log.LogInfo(Plugin.Stamp() + " Trade: " + s); }

        void Note(string en, string de) { if (Plugin.Ui != null) Plugin.Ui.ShowNote(Lang.T(en, de), 6f); }

        // ---------- items ----------

        static Item[] Table(ItemType t)
        {
            ItemsData db = ItemsData.instance;
            if (db == null) return null;
            switch (t)
            {
                case ItemType.Weapon: return db.weaponsDatabase;
                case ItemType.Armor: return db.armorDatabase;
                case ItemType.Consumable: return db.consumablesData;
                case ItemType.Simple: return db.basicItemsData;
                case ItemType.Book: return db.booksData;
                case ItemType.Trophy: return db.trophies;
                case ItemType.UsableItem: return db.usableItemsData;
                case ItemType.Note: return db.notesDatabase;
                case ItemType.Map: return db.mapsDatabase;
            }
            return null;
        }

        static Item Resolve(Line l)
        {
            Item[] t = Table((ItemType)l.Type);
            if (t == null) return null;
            Item byId = null;
            foreach (Item it in t)
            {
                if (it == null || it.databaseId != l.DbId) continue;
                if (it.name == l.Name) return it;
                if (byId == null) byId = it;
            }
            return byId;
        }

        public static bool Tradable(Item it)
        {
            if (it == null || it.itemType == ItemType.Money || it.itemType == ItemType.Modded) return false;
            if (it.cantbesold && it.itemType != ItemType.Weapon && it.itemType != ItemType.Armor) return false; // quest items
            return Table(it.itemType) != null;
        }

        static string Show(string english)
        {
            if (!Lang.German) return english;
            string de = Translator.German(english);
            return string.IsNullOrEmpty(de) ? english : de;
        }

        static Line LineOf(InventoryItem ii, int amount)
        {
            return new Line { Type = (int)ii.item.itemType, DbId = ii.item.databaseId, Name = ii.item.name ?? "", Data = ii.additionalData ?? "", Amount = amount };
        }

        static Dictionary<string, int> Needs(Offer o)
        {
            Dictionary<string, int> need = new Dictionary<string, int>();
            foreach (Line l in o.Lines) { int n; need.TryGetValue(l.Key, out n); need[l.Key] = n + l.Amount; }
            return need;
        }

        static Dictionary<string, int> Holdings(PlayerScript p)
        {
            Dictionary<string, int> have = new Dictionary<string, int>();
            foreach (InventoryItem ii in p.inventory)
            {
                if (ii == null || ii.item == null) continue;
                string k = LineOf(ii, 0).Key;
                int n; have.TryGetValue(k, out n); have[k] = n + ii.amount;
            }
            return have;
        }

        // Whether the player still has everything in the offer.
        static bool Has(PlayerScript p, Offer o, out string missing)
        {
            missing = null;
            if (p == null) { missing = "no player"; return false; }
            if (o.Gold < 0 || p.gold < o.Gold) { missing = "gold " + p.gold + "/" + o.Gold; return false; }
            Dictionary<string, int> have = Holdings(p);
            foreach (KeyValuePair<string, int> kv in Needs(o))
            {
                int n;
                have.TryGetValue(kv.Key, out n);
                if (n < kv.Value) { missing = kv.Key + " " + n + "/" + kv.Value; return false; }
            }
            return true;
        }

        static void Take(PlayerScript p, Offer o)
        {
            p.gold -= o.Gold;
            foreach (Line l in o.Lines)
            {
                int left = l.Amount;
                for (int i = p.inventory.Count - 1; i >= 0 && left > 0; i--)
                {
                    InventoryItem ii = p.inventory[i];
                    if (ii == null || ii.item == null || LineOf(ii, 0).Key != l.Key) continue;
                    int n = Math.Min(left, ii.amount);
                    p.RemoveItemFromInventory(ii, n);
                    left -= n;
                }
                if (left > 0) Plugin.Log.LogError("Trade: " + left + " of " + l.Name + " were not there to take");
            }
            Refresh();
        }

        static void Give(PlayerScript p, Offer o)
        {
            p.gold += o.Gold;
            foreach (Line l in o.Lines)
            {
                Item it = Resolve(l);
                if (it == null) { Plugin.Log.LogError("Trade: unknown item " + l.Key + " x" + l.Amount + " (other game version?)"); continue; }
                if (it.stackable) p.AddItemToInventory(new InventoryItem { item = it, amount = l.Amount, additionalData = l.Data });
                else for (int i = 0; i < l.Amount; i++) p.AddItemToInventory(new InventoryItem { item = it, amount = 1, additionalData = l.Data });
            }
            if (o.Gold > 0 && InterfaceManager.instance != null) InterfaceManager.instance.screenMessages.Add("+ " + o.Gold + " gold", Color.yellow, 3f);
            Refresh();
        }

        static void Refresh()
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null) return;
            try { im.RefreshInventory(); } catch (Exception) { }
            if (im.moneyLabel != null && PlayerScript.instance != null) im.moneyLabel.text = PlayerScript.instance.gold.ToString();
        }

        // Test: gold and every stack, to compare before and after.
        public static string Inventory(PlayerScript p)
        {
            if (p == null) return "-";
            List<string> s = new List<string>();
            int count = 0;
            foreach (KeyValuePair<string, int> kv in Holdings(p)) { count += kv.Value; }
            s.Add("gold=" + p.gold);
            s.Add("items=" + count);
            return string.Join(" ", s.ToArray());
        }

        static void WriteOffer(BinaryWriter w, Offer o)
        {
            w.Write(o.Version); w.Write(o.Gold); w.Write(o.Lines.Count);
            foreach (Line l in o.Lines) { w.Write(l.Type); w.Write(l.DbId); w.Write(l.Name ?? ""); w.Write(l.Data ?? ""); w.Write(l.Amount); }
        }

        static Offer ReadOffer(BinaryReader r)
        {
            Offer o = new Offer { Version = r.ReadInt32(), Gold = r.ReadInt32() };
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) o.Lines.Add(new Line { Type = r.ReadInt32(), DbId = r.ReadInt32(), Name = r.ReadString(), Data = r.ReadString(), Amount = r.ReadInt32() });
            return o;
        }

        static string Describe(Offer o)
        {
            List<string> s = new List<string>();
            if (o.Gold > 0) s.Add(o.Gold + " gold");
            foreach (Line l in o.Lines) s.Add(l.Amount + "x " + l.Name + (l.Data.Length > 0 ? "{" + l.Data + "}" : ""));
            return s.Count == 0 ? "nothing" : string.Join(", ", s.ToArray());
        }

        // ---------- sending ----------

        delegate void Body(BinaryWriter w);

        // Everything goes to everyone (target 0): the host coordinates, the
        // partner follows. Our own message is handled here as well.
        void Send(byte kind, int trade, Body body)
        {
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(kind); w.Write(trade);
                if (body != null) body(w);
                w.Flush();
                data = ms.ToArray();
            }
            net.SendMessage(Net.MsgTrade, 0, data, true);
            Process(Me, data, Time.realtimeSinceStartup);
        }

        // ---------- actions (UI and test steps) ----------

        public bool Busy { get { return cur != null; } }

        public void Ask(int partner)
        {
            if (cur != null) return;
            Vector3 at;
            if (!Near(partner, out at)) { Note("Too far away to trade.", "Zu weit weg zum Handeln."); return; }
            cur = new Session { Id = new System.Random().Next(1, int.MaxValue), Partner = partner, Mine = true };
            Log("asking " + NameOf(partner) + " (trade " + cur.Id + ")");
            int p = partner;
            Send(KAsk, cur.Id, w => w.Write(p));
        }

        public void Reply(int trade, bool accept)
        {
            KeyValuePair<int, float> a;
            if (!asks.TryGetValue(trade, out a)) return;
            asks.Remove(trade);
            if (accept && cur != null) accept = false; // already trading
            if (accept) cur = new Session { Id = trade, Partner = a.Key, Open = true };
            Log((accept ? "accepted " : "declined ") + NameOf(a.Key) + " (trade " + trade + ")");
            int asker = a.Key;
            Send(KReply, trade, w => { w.Write(asker); w.Write(accept); });
            if (accept) Open = true;
        }

        public void Cancel(string why)
        {
            if (cur == null || cur.Committing) return;
            Log("cancelled trade " + cur.Id + ": " + why);
            Cancelled++;
            int partner = cur.Partner;
            Send(KCancel, cur.Id, w => w.Write(partner));
            cur = null;
        }

        void Changed()
        {
            cur.Ours.Version++;
            cur.OurConfirm = cur.OurConfirmSeen = -1;
            Offer o = cur.Ours;
            int partner = cur.Partner;
            Send(KOffer, cur.Id, w => { w.Write(partner); WriteOffer(w, o); });
        }

        public void AddItem(InventoryItem ii, int amount)
        {
            if (cur == null || !cur.Open || cur.Committing || ii == null || !Tradable(ii.item)) return;
            Line add = LineOf(ii, 0);
            int offered = 0;
            foreach (Line l in cur.Ours.Lines) if (l.Key == add.Key) offered += l.Amount;
            int have;
            Holdings(PlayerScript.instance).TryGetValue(add.Key, out have);
            amount = Math.Min(amount, have - offered);
            if (amount <= 0) return;
            Line same = cur.Ours.Lines.Find(l => l.Key == add.Key);
            if (same != null) same.Amount += amount;
            else { add.Amount = amount; cur.Ours.Lines.Add(add); }
            Changed();
        }

        public void RemoveLine(int index)
        {
            if (cur == null || cur.Committing || index < 0 || index >= cur.Ours.Lines.Count) return;
            cur.Ours.Lines.RemoveAt(index);
            Changed();
        }

        public void SetGold(int gold)
        {
            if (cur == null || !cur.Open || cur.Committing || PlayerScript.instance == null) return;
            gold = Mathf.Clamp(gold, 0, PlayerScript.instance.gold);
            if (gold == cur.Ours.Gold) return;
            cur.Ours.Gold = gold;
            Changed();
        }

        public void Confirm()
        {
            if (cur == null || !cur.Open || cur.Committing) return;
            string missing;
            if (!Has(PlayerScript.instance, cur.Ours, out missing)) { Cancel("our offer is gone (" + missing + ")"); return; }
            Vector3 at;
            if (!Near(cur.Partner, out at)) { Note("Too far away to trade.", "Zu weit weg zum Handeln."); return; }
            cur.OurConfirm = cur.Ours.Version;
            cur.OurConfirmSeen = cur.Theirs.Version;
            Log("confirmed trade " + cur.Id + ": we give " + Describe(cur.Ours) + " for " + Describe(cur.Theirs));
            int partner = cur.Partner, own = cur.OurConfirm, seen = cur.OurConfirmSeen;
            Send(KConfirm, cur.Id, w => { w.Write(partner); w.Write(own); w.Write(seen); });
        }

        bool Near(int partner, out Vector3 at)
        {
            at = Vector3.zero;
            PlayerScript p = PlayerScript.instance;
            return p != null && Plugin.SameWorld(partner) && Plugin.PlayerPosition(partner, out at) && Vector3.Distance(at, p.transform.position) <= Reach;
        }

        // ---------- main loop ----------

        public void Tick(float now, bool inWorld)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgTrade || m.Sender == Me) continue;
                try { Process(m.Sender, m.Payload, now); }
                catch (Exception e) { Plugin.Log.LogError("Trade message from " + m.Sender + " failed: " + e); }
            }
            HashSet<int> here = new HashSet<int>(peers());
            // Our trade: partner gone, or our offer no longer in the inventory.
            if (cur != null && !cur.Committing)
            {
                string missing;
                if (!here.Contains(cur.Partner)) { Log("partner left, trade " + cur.Id + " ended"); cur = null; Note("The other player left; trade ended.", "Der andere Spieler ist weg; Handel beendet."); }
                else if (inWorld && cur.Open && !Has(PlayerScript.instance, cur.Ours, out missing))
                {
                    Note("Your inventory changed; trade cancelled.", "Dein Inventar hat sich geändert; Handel abgebrochen.");
                    Cancel("inventory changed (" + missing + ")");
                }
            }
            List<int> old = null;
            foreach (KeyValuePair<int, KeyValuePair<int, float>> kv in asks)
                if (now - kv.Value.Value > 60f || !here.Contains(kv.Value.Key)) { if (old == null) old = new List<int>(); old.Add(kv.Key); }
            if (old != null) foreach (int k in old) asks.Remove(k);
            // Trades whose outcome we missed: ask the host.
            if (inWorld && here.Count > 0)
                foreach (Escrow e in escrow.Values)
                    if (now >= e.NextQuery)
                    {
                        e.NextQuery = now + 5f;
                        Log("asking the host about unfinished trade " + e.Id);
                        Send(KQuery, e.Id, null);
                    }
            if (net.IsHost) HostTick(now, here);
        }

        void Process(int from, byte[] payload, float now)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
            {
                byte kind = r.ReadByte();
                int trade = r.ReadInt32();
                if (kind >= KCommit)
                {
                    // Coordinator messages: from the host (handled by the host itself too).
                    if (kind == KRemoved || kind == KQuery) { if (net.IsHost) HostProcess(from, 0, kind, trade, r); return; }
                    if (net.IsHost ? from != Me : from == Me) return;
                    Outcome(kind, trade, r);
                    return;
                }
                // Between the traders: the other trader comes first.
                int target = r.ReadInt32();
                long pos = r.BaseStream.Position;
                if (net.IsHost) { HostProcess(from, target, kind, trade, r); r.BaseStream.Position = pos; }
                if (from == Me || target != Me) return;
                switch (kind)
                {
                    case KAsk:
                        if (cur != null) { int asker = from; Send(KReply, trade, w => { w.Write(asker); w.Write(false); }); return; }
                        asks[trade] = new KeyValuePair<int, float>(from, now);
                        Log(NameOf(from) + " asks to trade (trade " + trade + ")");
                        if (TestAuto) { AutoAnswer(trade); break; }
                        Note(NameOf(from) + " wants to trade. Answer in the co-op menu (F8).", NameOf(from) + " möchte handeln. Antworte im Co-op-Menü (F8).");
                        break;
                    case KReply:
                        if (cur == null || cur.Id != trade) return;
                        if (r.ReadBoolean()) { cur.Open = true; Open = true; Log(NameOf(from) + " accepted trade " + trade); }
                        else { Log(NameOf(from) + " declined trade " + trade); cur = null; Note("Trade declined.", "Handel abgelehnt."); }
                        break;
                    case KOffer:
                        if (cur == null || cur.Id != trade) return;
                        cur.Theirs = ReadOffer(r);
                        cur.OurConfirm = cur.OurConfirmSeen = -1; // the other side changed: confirm again
                        cur.TheirConfirm = cur.TheirConfirmSeen = -1;
                        Log(NameOf(from) + " offers v" + cur.Theirs.Version + ": " + Describe(cur.Theirs));
                        break;
                    case KConfirm:
                        if (cur == null || cur.Id != trade) return;
                        cur.TheirConfirm = r.ReadInt32(); cur.TheirConfirmSeen = r.ReadInt32();
                        Log(NameOf(from) + " confirmed (their v" + cur.TheirConfirm + ", our v" + cur.TheirConfirmSeen + ")");
                        if (TestAuto && cur.TheirConfirm == cur.Theirs.Version && cur.TheirConfirmSeen == cur.Ours.Version) Confirm();
                        break;
                    case KCancel:
                        if (cur == null || cur.Id != trade || cur.Committing) return;
                        Log(NameOf(from) + " cancelled trade " + trade);
                        cur = null;
                        Note("The other player cancelled the trade.", "Der andere Spieler hat den Handel abgebrochen.");
                        break;
                }
            }
        }

        // Commit / Deliver / Abort for us.
        void Outcome(byte kind, int trade, BinaryReader r)
        {
            PlayerScript p = PlayerScript.instance;
            if (kind == KCommit)
            {
                int a = r.ReadInt32(), b = r.ReadInt32(), va = r.ReadInt32(), vb = r.ReadInt32();
                if (cur == null || cur.Id != trade || (Me != a && Me != b)) return;
                int ours = Me == a ? va : vb, theirs = Me == a ? vb : va;
                bool ok = cur.Ours.Version == ours && cur.Theirs.Version == theirs;
                string missing = "versions " + cur.Ours.Version + "/" + cur.Theirs.Version + " vs " + ours + "/" + theirs;
                if (ok) ok = Has(p, cur.Ours, out missing);
                cur.Committing = true;
                if (ok)
                {
                    Escrow e = new Escrow { Id = trade, Ours = cur.Ours, Theirs = cur.Theirs, NextQuery = Time.realtimeSinceStartup + 25f };
                    escrow[trade] = e;
                    SaveEscrow();
                    Take(p, cur.Ours);
                    Log("commit " + trade + ": took " + Describe(cur.Ours) + " out (in escrow) -> " + Inventory(p));
                }
                else Log("commit " + trade + " failed: " + missing);
                if (TestStall) { Log("test: not answering the commit"); return; }
                bool okc = ok;
                Send(KRemoved, trade, w => w.Write(okc));
                return;
            }
            if (TestDropOutcome) { TestDropOutcome = false; Log("test: outcome " + kind + " for " + trade + " 'lost'"); return; }
            Escrow held;
            bool mine = escrow.TryGetValue(trade, out held);
            if (kind == KDeliver)
            {
                if (mine)
                {
                    Give(p, held.Theirs);
                    escrow.Remove(trade);
                    SaveEscrow();
                    Delivered++;
                    Received++;
                    Log("delivered " + trade + ": got " + Describe(held.Theirs) + " -> " + Inventory(p));
                    Note("Trade done.", "Handel abgeschlossen.");
                }
                if (cur != null && cur.Id == trade) { cur = null; Open = false; }
            }
            else if (kind == KAbort)
            {
                if (mine)
                {
                    Give(p, held.Ours);
                    escrow.Remove(trade);
                    SaveEscrow();
                    Refunded++;
                    Log("aborted " + trade + ": gave back " + Describe(held.Ours) + " -> " + Inventory(p));
                }
                else Log("aborted " + trade + " (nothing taken here)");
                if (cur != null && cur.Id == trade) { cur = null; Note("The trade did not go through; nothing changed.", "Der Handel hat nicht geklappt; nichts wurde verändert."); }
                Aborted++;
            }
        }

        // ---------- host: coordinator ----------

        void HostProcess(int from, int other, byte kind, int trade, BinaryReader r)
        {
            Coord c;
            coords.TryGetValue(trade, out c);
            switch (kind)
            {
                case KReply:
                    if (r.ReadBoolean()) coords[trade] = new Coord { Id = trade, A = other, B = from };
                    break;
                case KCancel:
                    if (c != null && !c.Committed) coords.Remove(trade);
                    break;
                case KConfirm:
                {
                    if (c == null || c.Committed) return;
                    int side = from == c.A ? 0 : from == c.B ? 1 : -1;
                    if (side < 0) return;
                    c.Own[side] = r.ReadInt32(); c.Seen[side] = r.ReadInt32();
                    if (c.Own[0] >= 0 && c.Own[1] >= 0 && c.Own[0] == c.Seen[1] && c.Own[1] == c.Seen[0])
                    {
                        c.Committed = true;
                        c.CommitAt = Time.realtimeSinceStartup;
                        Log("host: both confirmed trade " + trade + " (" + NameOf(c.A) + " v" + c.Own[0] + ", " + NameOf(c.B) + " v" + c.Own[1] + ") -> commit");
                        int a = c.A, b = c.B, va = c.Own[0], vb = c.Own[1];
                        Send(KCommit, trade, w => { w.Write(a); w.Write(b); w.Write(va); w.Write(vb); });
                    }
                    break;
                }
                case KRemoved:
                {
                    bool ok = r.ReadBoolean();
                    if (c == null || !c.Committed) return;
                    int side = from == c.A ? 0 : from == c.B ? 1 : -1;
                    if (side < 0) return;
                    c.Removed[side] = ok ? 1 : 2;
                    Log("host: " + NameOf(from) + (ok ? " took its offer out" : " could not give its offer") + " (trade " + trade + ")");
                    if (c.Removed[0] == 2 || c.Removed[1] == 2) Decide(c, false, "a trader could not give");
                    else if (c.Removed[0] == 1 && c.Removed[1] == 1) Decide(c, true, "both took their offers out");
                    break;
                }
                case KQuery:
                {
                    bool delivered;
                    if (coords.ContainsKey(trade)) return; // still being decided
                    if (!outcomes.TryGetValue(trade, out delivered)) { delivered = false; Record(trade, false); }
                    Log("host: " + NameOf(from) + " asks about trade " + trade + " -> " + (delivered ? "deliver" : "abort"));
                    Send(delivered ? KDeliver : KAbort, trade, null);
                    break;
                }
            }
        }

        void HostTick(float now, HashSet<int> here)
        {
            here = new HashSet<int>(here);
            here.Add(Me);
            List<Coord> todo = new List<Coord>(coords.Values);
            foreach (Coord c in todo)
            {
                bool gone = !here.Contains(c.A) || !here.Contains(c.B);
                if (!c.Committed) { if (gone) coords.Remove(c.Id); continue; }
                if (gone) Decide(c, false, "a trader left before both took their offers out");
                else if (now - c.CommitAt > CommitTimeout) Decide(c, false, "no answer in " + CommitTimeout + " s");
            }
        }

        // The decision is written down before anyone hears it.
        void Decide(Coord c, bool deliver, string why)
        {
            coords.Remove(c.Id);
            Record(c.Id, deliver);
            Log("host: trade " + c.Id + " -> " + (deliver ? "deliver" : "abort") + " (" + why + ")");
            Send(deliver ? KDeliver : KAbort, c.Id, null);
        }

        void Record(int trade, bool delivered)
        {
            outcomes[trade] = delivered;
            try { File.AppendAllText(OutcomeFile, trade + " " + (delivered ? "deliver" : "abort") + "\n"); }
            catch (Exception e) { Plugin.Log.LogError("Trade outcome file: " + e.Message); }
        }

        void LoadOutcomes()
        {
            try
            {
                if (!File.Exists(OutcomeFile)) return;
                foreach (string line in File.ReadAllLines(OutcomeFile))
                {
                    string[] f = line.Split(' ');
                    int id;
                    if (f.Length == 2 && int.TryParse(f[0], out id)) outcomes[id] = f[1] == "deliver";
                }
            }
            catch (Exception e) { Plugin.Log.LogError("Trade outcome file: " + e.Message); }
        }

        // ---------- escrow file ----------

        void SaveEscrow()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (Escrow e in escrow.Values)
                    using (MemoryStream ms = new MemoryStream())
                    using (BinaryWriter w = new BinaryWriter(ms))
                    {
                        w.Write(e.Id); WriteOffer(w, e.Ours); WriteOffer(w, e.Theirs);
                        w.Flush();
                        lines.Add(Convert.ToBase64String(ms.ToArray()));
                    }
                string tmp = EscrowFile + ".tmp";
                File.WriteAllText(tmp, string.Join("\n", lines.ToArray()));
                if (File.Exists(EscrowFile)) File.Delete(EscrowFile);
                File.Move(tmp, EscrowFile);
            }
            catch (Exception e) { Plugin.Log.LogError("Trade escrow file: " + e.Message); }
        }

        void LoadEscrow()
        {
            try
            {
                if (!File.Exists(EscrowFile)) return;
                foreach (string line in File.ReadAllLines(EscrowFile))
                {
                    if (line.Trim().Length == 0) continue;
                    using (BinaryReader r = new BinaryReader(new MemoryStream(Convert.FromBase64String(line.Trim()))))
                    {
                        Escrow e = new Escrow { Id = r.ReadInt32() };
                        e.Ours = ReadOffer(r); e.Theirs = ReadOffer(r);
                        escrow[e.Id] = e;
                        Log("unfinished trade " + e.Id + " in the escrow file: gave " + Describe(e.Ours) + ", gets " + Describe(e.Theirs));
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("Trade escrow file: " + e.Message); }
        }

        public string Summary()
        {
            return "trade cur=" + (cur != null ? cur.Id + (cur.Open ? "open" : "asking") + (cur.Committing ? ",committing" : "") : "-") +
                " delivered=" + Delivered + " aborted=" + Aborted + " refunded=" + Refunded + " cancelled=" + Cancelled + " escrow=" + escrow.Count;
        }

        // ---------- IMGUI ----------

        public bool Open;
        public Rect PanelRect;
        Vector2 invScroll;
        string goldText = "0";

        public int AskCount { get { return asks.Count; } }

        public void DrawPanel(float sw, GUIStyle panel)
        {
            PlayerScript p = PlayerScript.instance;
            float w = cur != null && cur.Open ? 720f : 420f, h = cur != null && cur.Open ? 470f : 160f + 30f * (asks.Count + 4);
            Rect area = new Rect(sw - w - 30f, 160f, w, h);
            PanelRect = area;
            GUI.Box(area, "", panel);
            GUI.Box(area, Lang.T("Trade", "Handel"));
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 28f, area.width - 20f, area.height - 34f));
            if (cur == null)
            {
                foreach (KeyValuePair<int, KeyValuePair<int, float>> a in new List<KeyValuePair<int, KeyValuePair<int, float>>>(asks))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Lang.T(NameOf(a.Value.Key) + " wants to trade", NameOf(a.Value.Key) + " möchte handeln"), GUILayout.Width(200f));
                    if (GUILayout.Button(Lang.T("Accept", "Annehmen"), GUILayout.Width(90f))) Reply(a.Key, true);
                    if (GUILayout.Button(Lang.T("Decline", "Ablehnen"), GUILayout.Width(90f))) Reply(a.Key, false);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Label(Lang.T("Trade with (within " + Reach + " m):", "Handeln mit (bis " + Reach + " m):"));
                int shown = 0;
                foreach (int other in peers())
                {
                    Vector3 at;
                    if (!Near(other, out at)) continue;
                    shown++;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(NameOf(other), GUILayout.Width(250f));
                    if (GUILayout.Button(Lang.T("Trade", "Handeln"), GUILayout.Width(130f))) Ask(other);
                    GUILayout.EndHorizontal();
                }
                if (shown == 0) GUILayout.Label(Lang.T("Nobody close enough.", "Niemand nah genug."));
            }
            else if (!cur.Open)
            {
                GUILayout.Label(Lang.T("Waiting for " + NameOf(cur.Partner) + " ...", "Warte auf " + NameOf(cur.Partner) + " ..."));
                if (GUILayout.Button(Lang.T("Cancel", "Abbrechen"))) Cancel("asker cancelled");
            }
            else DrawTrade(p);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("Close", "Schließen"))) Open = false;
            GUILayout.EndArea();
        }

        void DrawTrade(PlayerScript p)
        {
            bool frozen = cur.Committing;
            GUILayout.BeginHorizontal();
            // Inventory
            GUILayout.BeginVertical(GUILayout.Width(240f));
            GUILayout.Label("<b>" + Lang.T("Your inventory", "Dein Inventar") + "</b>");
            invScroll = GUILayout.BeginScrollView(invScroll, GUILayout.Height(300f));
            if (p != null)
                foreach (InventoryItem ii in new List<InventoryItem>(p.inventory))
                {
                    if (ii == null || !Tradable(ii.item)) continue;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Show(ii.item.name) + (ii.amount > 1 ? " x" + ii.amount : ""), GUILayout.Width(140f));
                    GUI.enabled = !frozen;
                    if (GUILayout.Button("+1", GUILayout.Width(30f))) AddItem(ii, 1);
                    if (ii.amount > 1 && GUILayout.Button(Lang.T("all", "alle"), GUILayout.Width(36f))) AddItem(ii, ii.amount);
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            // Our offer
            GUILayout.BeginVertical(GUILayout.Width(210f));
            GUILayout.Label("<b>" + Lang.T("You give", "Du gibst") + "</b>");
            for (int i = 0; i < cur.Ours.Lines.Count; i++)
            {
                Line l = cur.Ours.Lines[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(l.Amount + "x " + Show(l.Name), GUILayout.Width(160f));
                GUI.enabled = !frozen;
                if (GUILayout.Button("-", GUILayout.Width(26f))) { RemoveLine(i); GUI.enabled = true; GUILayout.EndHorizontal(); break; }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Gold:", "Gold:"), GUILayout.Width(45f));
            GUI.enabled = !frozen;
            goldText = GUILayout.TextField(goldText, 9, GUILayout.Width(80f));
            int g;
            if (GUILayout.Button("OK", GUILayout.Width(40f)) && int.TryParse(goldText, out g)) SetGold(g);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label(Lang.T("Offered: ", "Angeboten: ") + cur.Ours.Gold + Lang.T(" gold (you have ", " Gold (du hast ") + (p != null ? p.gold : 0) + ")");
            GUILayout.EndVertical();
            // Their offer
            GUILayout.BeginVertical(GUILayout.Width(210f));
            GUILayout.Label("<b>" + Lang.T(NameOf(cur.Partner) + " gives", NameOf(cur.Partner) + " gibt") + "</b>");
            foreach (Line l in cur.Theirs.Lines) GUILayout.Label(l.Amount + "x " + Show(l.Name));
            if (cur.Theirs.Gold > 0) GUILayout.Label(cur.Theirs.Gold + Lang.T(" gold", " Gold"));
            if (cur.Theirs.Lines.Count == 0 && cur.Theirs.Gold == 0) GUILayout.Label(Lang.T("(nothing yet)", "(noch nichts)"));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            bool weOk = cur.OurConfirm == cur.Ours.Version && cur.OurConfirmSeen == cur.Theirs.Version;
            bool theyOk = cur.TheirConfirm == cur.Theirs.Version && cur.TheirConfirmSeen == cur.Ours.Version;
            GUILayout.Label(frozen ? Lang.T("Exchanging ...", "Wird getauscht ...")
                : (weOk ? Lang.T("You confirmed. ", "Du hast bestätigt. ") : "") + (theyOk ? Lang.T(NameOf(cur.Partner) + " confirmed.", NameOf(cur.Partner) + " hat bestätigt.") : Lang.T(NameOf(cur.Partner) + " has not confirmed.", NameOf(cur.Partner) + " hat noch nicht bestätigt.")));
            GUILayout.BeginHorizontal();
            GUI.enabled = !frozen && !weOk;
            if (GUILayout.Button(Lang.T("Confirm", "Bestätigen"), GUILayout.Height(30f))) Confirm();
            GUI.enabled = !frozen;
            if (GUILayout.Button(Lang.T("Cancel trade", "Handel abbrechen"), GUILayout.Height(30f))) Cancel("cancel button");
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // ---------- test steps ----------
        //   tradeask            ask the first other player
        //   tradeaccept         accept the first request
        //   tradeitem:<n>       offer one of the n-th tradable stack (0 = first)
        //   tradestack:<n>      offer the whole n-th tradable stack
        //   tradegold:<g>       offer g gold
        //   tradeconfirm        confirm
        //   tradecancel         cancel
        //   tradeuse            drop one of the first offered stack from the inventory (inventory change)
        //   tradestall          do not answer the next commit (like a trader gone mid-commit)
        //   tradedrop           ignore the next outcome (like missing it); the escrow file asks again
        //   tradeui, tradeui:off open / close the trade window
        //   tradelog            log the inventory summary and the state
        public static bool TestStep(string step)
        {
            TradeSync s = Instance;
            if (s == null || !step.StartsWith("trade")) return false;
            PlayerScript p = PlayerScript.instance;
            int first = 0;
            foreach (int x in s.peers()) { first = x; break; }
            if (step == "tradeask") { if (first != 0) s.Ask(first); }
            else if (step == "tradeaccept") { foreach (int t in new List<int>(s.asks.Keys)) { s.Reply(t, true); break; } }
            else if (step.StartsWith("tradeitem:") || step.StartsWith("tradestack:"))
            {
                int n = int.Parse(step.Substring(step.IndexOf(':') + 1)), k = 0;
                foreach (InventoryItem ii in p.inventory)
                {
                    if (ii == null || !Tradable(ii.item)) continue;
                    if (step.StartsWith("tradestack:") && ii.amount < 2) continue;
                    if (k++ < n) continue;
                    s.AddItem(ii, step.StartsWith("tradestack:") ? ii.amount : 1);
                    break;
                }
            }
            else if (step.StartsWith("tradegold:")) s.SetGold(int.Parse(step.Substring(10)));
            else if (step == "tradeconfirm") s.Confirm();
            else if (step == "tradecancel") s.Cancel("test");
            else if (step == "tradeuse")
            {
                if (s.cur != null && s.cur.Ours.Lines.Count > 0)
                {
                    string key = s.cur.Ours.Lines[0].Key;
                    foreach (InventoryItem ii in p.inventory)
                        if (ii != null && ii.item != null && LineOf(ii, 0).Key == key) { Log("test: removing one " + ii.item.name + " from the inventory"); p.RemoveItemFromInventory(ii, ii.amount); break; }
                }
            }
            else if (step == "tradestall") TestStall = true;
            else if (step == "tradedrop") TestDropOutcome = true;
            else if (step == "tradeunstall") TestStall = false;
            else if (step == "tradeui") s.Open = true;
            else if (step == "tradeui:off") s.Open = false;
            else if (step == "tradeauto") TestAuto = true;
            else if (step == "tradelog")
            {
                string offers = s.cur != null ? " ours=" + Describe(s.cur.Ours) + " theirs=" + Describe(s.cur.Theirs) : "";
                Plugin.Log.LogInfo(Plugin.Stamp() + " [tradecheck] " + Inventory(p) + " " + s.Summary() + offers + " stacks=" + Stacks(p));
            }
            else return false;
            return true;
        }

        // Test auto mode (the other copy drives): accept, offer one of the first
        // tradable stack and 10 gold, confirm when the other confirms.
        void AutoAnswer(int trade)
        {
            Reply(trade, true);
            if (cur == null || cur.Id != trade) return;
            foreach (InventoryItem ii in PlayerScript.instance.inventory)
                if (ii != null && Tradable(ii.item)) { AddItem(ii, 1); break; }
            SetGold(10);
        }

        // Test: every stack as key=amount, sorted (to diff before/after).
        static string Stacks(PlayerScript p)
        {
            List<string> s = new List<string>();
            foreach (KeyValuePair<string, int> kv in Holdings(p)) s.Add(kv.Key.Replace(' ', '_') + "=" + kv.Value);
            s.Sort(StringComparer.Ordinal);
            return string.Join(";", s.ToArray());
        }
    }
}
