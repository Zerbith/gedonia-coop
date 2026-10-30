using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GedoniaCoop
{
    // Groups of players (owner's choice: what a group changes):
    //  - combat XP is shared only between members of one group (XpSync),
    //  - the maps show only the members of your group (MapMarkers),
    //  - enemy scaling counts the members of one group near the enemy (ScaleSync).
    //
    // The host keeps the groups. Everyone asks the host (invite, answer, leave,
    // kick); the host checks the request, changes its table and sends the
    // whole table to everyone (on change and every 3 s). A group's first member
    // leads it; only the leader invites and kicks. A group left with one member
    // ends. Players who drop out are removed by the host.
    public class PartySync
    {
        const byte KInvite = 1, KInvited = 2, KAnswer = 3, KLeave = 4, KKick = 5, KTable = 6, KRefused = 7;
        const float InviteSeconds = 60f;

        public class Group
        {
            public int Id;
            public List<int> Members = new List<int>(); // [0] leads
        }

        readonly Net net;
        readonly Func<int, Puppet> puppetOf;
        readonly Func<IEnumerable<int>> peers;
        public static PartySync Instance;

        // Everyone: the host's table.
        readonly List<Group> groups = new List<Group>();
        // Invitations for us: inviter -> when it arrived.
        readonly Dictionary<int, float> invitesForMe = new Dictionary<int, float>();
        // Host only: (inviter, invitee) -> when.
        readonly Dictionary<long, float> pending = new Dictionary<long, float>();
        int nextGroupId = 1;
        float nextTable;
        public int TablesSent, TablesGot, InvitesSent, InvitesGot, Refusals;

        public PartySync(Net net, Func<int, Puppet> puppetOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.puppetOf = puppetOf;
            this.peers = peers;
            Instance = this;
        }

        int Me { get { return net.LocalId; } }

        // ---------- queries (used by XpSync, MapMarkers, ScaleSync) ----------

        public Group GroupOf(int player)
        {
            foreach (Group g in groups) if (g.Members.Contains(player)) return g;
            return null;
        }

        public static Group MyGroup { get { return Instance != null ? Instance.GroupOf(Instance.Me) : null; } }

        // Whether another player is in our group.
        public static bool Mate(int player)
        {
            if (Instance == null) return false;
            Group g = Instance.GroupOf(Instance.Me);
            return g != null && player != Instance.Me && g.Members.Contains(player);
        }

        public static bool SameGroup(int a, int b)
        {
            if (Instance == null || a == b) return a == b;
            Group g = Instance.GroupOf(a);
            return g != null && g.Members.Contains(b);
        }

        public int InvitationCount { get { return invitesForMe.Count; } }

        string NameOf(int id)
        {
            if (id == Me) return Plugin.LocalName;
            Puppet p = puppetOf(id);
            return p != null && p.Latest != null ? p.Latest.Name : "#" + id;
        }

        // ---------- local actions (UI and test steps) ----------

        public void Invite(int player) { InvitesSent++; Request(KInvite, player, false); Log("invite " + NameOf(player)); }
        public void Answer(int inviter, bool accept) { invitesForMe.Remove(inviter); Request(KAnswer, inviter, accept); Log((accept ? "accept " : "decline ") + NameOf(inviter)); }
        public void Leave() { Request(KLeave, 0, false); Log("leave"); }
        public void Kick(int player) { Request(KKick, player, false); Log("kick " + NameOf(player)); }

        void Request(byte kind, int other, bool flag)
        {
            if (net.IsHost) { HostHandle(Me, kind, other, flag); return; }
            net.SendMessage(Net.MsgParty, 0, Pack(kind, other, flag), true);
        }

        static byte[] Pack(byte kind, int other, bool flag)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(kind); w.Write(other); w.Write(flag);
                w.Flush();
                return ms.ToArray();
            }
        }

        // ---------- main loop ----------

        public void Tick(float now)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgParty || m.Sender == Me) continue;
                try { Handle(m, now); }
                catch (Exception e) { Plugin.Log.LogError("Party message from " + m.Sender + " failed: " + e); }
            }
            List<int> old = null;
            foreach (KeyValuePair<int, float> kv in invitesForMe)
                if (now - kv.Value > InviteSeconds || puppetOf(kv.Key) == null) { if (old == null) old = new List<int>(); old.Add(kv.Key); }
            if (old != null) foreach (int k in old) invitesForMe.Remove(k);
            if (!net.IsHost) return;

            // Players who are gone leave their groups.
            HashSet<int> here = new HashSet<int>(peers());
            here.Add(Me);
            bool changed = false;
            foreach (Group g in groups)
                for (int i = g.Members.Count - 1; i >= 0; i--)
                    if (!here.Contains(g.Members[i])) { Log("player " + g.Members[i] + " gone, removed from group " + g.Id); g.Members.RemoveAt(i); changed = true; }
            if (Tidy()) changed = true;
            List<long> stale = null;
            foreach (KeyValuePair<long, float> kv in pending) if (now - kv.Value > InviteSeconds) { if (stale == null) stale = new List<long>(); stale.Add(kv.Key); }
            if (stale != null) foreach (long k in stale) pending.Remove(k);
            if (changed || now >= nextTable) { nextTable = now + 3f; SendTable(); }
        }

        void Handle(Net.Message m, float now)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                byte kind = r.ReadByte();
                if (kind == KTable)
                {
                    if (net.IsHost) return;
                    ReadTable(r);
                    return;
                }
                int other = r.ReadInt32();
                bool flag = r.ReadBoolean();
                if (kind == KInvited)
                {
                    if (m.Target != Me) return;
                    InvitesGot++;
                    invitesForMe[other] = now;
                    Log("invited by " + NameOf(other));
                    if (Plugin.Ui != null) Plugin.Ui.ShowNote(Lang.T(NameOf(other) + " invites you to a group. Answer in the co-op menu (F8).",
                        NameOf(other) + " lädt dich in eine Gruppe ein. Antworte im Co-op-Menü (F8)."), 8f);
                    return;
                }
                if (kind == KRefused)
                {
                    if (m.Target != Me) return;
                    Refusals++;
                    Log("refused: " + Reason(other));
                    if (Plugin.Ui != null) Plugin.Ui.ShowNote(Reason(other), 5f);
                    return;
                }
                if (net.IsHost) HostHandle(m.Sender, kind, other, flag);
            }
        }

        static string Reason(int code)
        {
            switch (code)
            {
                case 1: return Lang.T("Only the group's leader can invite.", "Nur der Gruppenleiter kann einladen.");
                case 2: return Lang.T("That player is already in a group.", "Dieser Spieler ist schon in einer Gruppe.");
                case 3: return Lang.T("The invitation is no longer valid.", "Die Einladung gilt nicht mehr.");
                case 4: return Lang.T("Only the group's leader can remove players.", "Nur der Gruppenleiter kann Spieler entfernen.");
                default: return Lang.T("Not possible.", "Nicht möglich.");
            }
        }

        // ---------- host ----------

        void HostHandle(int from, byte kind, int other, bool flag)
        {
            Group mine = GroupOf(from);
            bool changed = false;
            if (kind == KInvite)
            {
                if (other == from || (other != Me && puppetOf(other) == null)) { Refuse(from, 0); return; }
                if (mine != null && mine.Members[0] != from) { Refuse(from, 1); return; }
                if (GroupOf(other) != null) { Refuse(from, 2); return; }
                pending[Key(from, other)] = Time.realtimeSinceStartup;
                Log("host: " + NameOf(from) + " invites " + NameOf(other));
                if (other == Me) Handle(new Net.Message { Type = Net.MsgParty, Sender = from, Target = Me, Payload = Pack(KInvited, from, false) }, Time.realtimeSinceStartup);
                else net.SendMessage(Net.MsgParty, other, Pack(KInvited, from, false), true);
                return;
            }
            if (kind == KAnswer)
            {
                // from = invitee, other = inviter
                long key = Key(other, from);
                if (!pending.ContainsKey(key)) { if (flag) Refuse(from, 3); return; }
                pending.Remove(key);
                if (!flag) { Log("host: " + NameOf(from) + " declined " + NameOf(other)); return; }
                Group g = GroupOf(other);
                if (mine != null || (g != null && g.Members[0] != other) || (other != Me && puppetOf(other) == null)) { Refuse(from, 3); return; }
                if (g == null)
                {
                    g = new Group { Id = nextGroupId++ };
                    g.Members.Add(other);
                    groups.Add(g);
                }
                g.Members.Add(from);
                Log("host: " + NameOf(from) + " joined group " + g.Id + " (" + Describe(g) + ")");
                changed = true;
            }
            else if (kind == KLeave)
            {
                if (mine == null) return;
                mine.Members.Remove(from);
                Log("host: " + NameOf(from) + " left group " + mine.Id);
                changed = true;
            }
            else if (kind == KKick)
            {
                if (mine == null || mine.Members[0] != from) { Refuse(from, 4); return; }
                if (!mine.Members.Remove(other)) return;
                Log("host: " + NameOf(from) + " removed " + NameOf(other) + " from group " + mine.Id);
                changed = true;
            }
            if (changed) { Tidy(); SendTable(); }
        }

        static long Key(int inviter, int invitee) { return ((long)inviter << 32) | (uint)invitee; }

        void Refuse(int to, int code)
        {
            if (to == Me) { Refusals++; Log("refused: " + Reason(code)); if (Plugin.Ui != null) Plugin.Ui.ShowNote(Reason(code), 5f); return; }
            net.SendMessage(Net.MsgParty, to, Pack(KRefused, code, false), true);
        }

        // Groups of one end.
        bool Tidy()
        {
            bool changed = false;
            for (int i = groups.Count - 1; i >= 0; i--)
                if (groups[i].Members.Count < 2) { Log("host: group " + groups[i].Id + " ended"); groups.RemoveAt(i); changed = true; }
            return changed;
        }

        void SendTable()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(KTable);
                w.Write(groups.Count);
                foreach (Group g in groups)
                {
                    w.Write(g.Id);
                    w.Write(g.Members.Count);
                    foreach (int p in g.Members) w.Write(p);
                }
                w.Flush();
                net.SendMessage(Net.MsgParty, 0, ms.ToArray(), true);
            }
            TablesSent++;
        }

        void ReadTable(BinaryReader r)
        {
            string before = Describe();
            groups.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                Group g = new Group { Id = r.ReadInt32() };
                int c = r.ReadInt32();
                for (int k = 0; k < c; k++) g.Members.Add(r.ReadInt32());
                groups.Add(g);
            }
            TablesGot++;
            string after = Describe();
            if (after != before) Log("groups now: " + after);
        }

        // ---------- text ----------

        string Describe(Group g)
        {
            List<string> names = new List<string>();
            foreach (int p in g.Members) names.Add(NameOf(p) + (p == g.Members[0] ? "*" : ""));
            return string.Join(", ", names.ToArray());
        }

        public string Describe()
        {
            if (groups.Count == 0) return "none";
            List<string> s = new List<string>();
            foreach (Group g in groups) s.Add(g.Id + "[" + Describe(g) + "]");
            return string.Join(" ", s.ToArray());
        }

        public string Summary()
        {
            Group g = GroupOf(Me);
            return "party mine=" + (g != null ? g.Id + "[" + Describe(g) + "]" : "-") + " all=" + Describe() + " invites=" + invitesForMe.Count +
                " tables=" + TablesSent + "/" + TablesGot + " refused=" + Refusals;
        }

        static void Log(string s) { Plugin.Log.LogInfo(Plugin.Stamp() + " Party: " + s); }

        // ---------- IMGUI: group window and member frames ----------

        public bool Open;
        public Rect PanelRect;

        public void DrawPanel(float sw, GUIStyle panel)
        {
            List<int> others = new List<int>(peers());
            Group mine = GroupOf(Me);
            bool lead = mine == null || mine.Members[0] == Me;
            float w = 420f, h = 150f + Mathf.Max(1, others.Count) * 30f + invitesForMe.Count * 30f;
            Rect area = new Rect(sw - w - 30f, 200f, w, h);
            PanelRect = area;
            GUI.Box(area, "", panel);
            GUI.Box(area, Lang.T("Group", "Gruppe"));
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 28f, area.width - 20f, area.height - 34f));
            GUILayout.Label(mine == null ? Lang.T("You are not in a group.", "Du bist in keiner Gruppe.")
                : Lang.T("Your group: ", "Deine Gruppe: ") + Describe(mine) + Lang.T("  (* leads)", "  (* leitet)"));
            foreach (int inviter in new List<int>(invitesForMe.Keys))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Lang.T("Invitation from ", "Einladung von ") + NameOf(inviter), GUILayout.Width(200f));
                if (GUILayout.Button(Lang.T("Accept", "Annehmen"), GUILayout.Width(90f))) Answer(inviter, true);
                if (GUILayout.Button(Lang.T("Decline", "Ablehnen"), GUILayout.Width(90f))) Answer(inviter, false);
                GUILayout.EndHorizontal();
            }
            if (others.Count == 0) GUILayout.Label(Lang.T("No other players.", "Keine anderen Spieler."));
            foreach (int p in others)
            {
                GUILayout.BeginHorizontal();
                Group theirs = GroupOf(p);
                string where = theirs == null ? "" : theirs == mine ? Lang.T(" (in your group)", " (in deiner Gruppe)") : Lang.T(" (other group)", " (andere Gruppe)");
                GUILayout.Label(NameOf(p) + where, GUILayout.Width(250f));
                if (theirs == null && lead && GUILayout.Button(Lang.T("Invite", "Einladen"), GUILayout.Width(130f))) Invite(p);
                else if (theirs != null && theirs == mine && mine.Members[0] == Me && GUILayout.Button(Lang.T("Remove", "Entfernen"), GUILayout.Width(130f))) Kick(p);
                GUILayout.EndHorizontal();
            }
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (mine != null && GUILayout.Button(Lang.T("Leave group", "Gruppe verlassen"))) Leave();
            if (GUILayout.Button(Lang.T("Close", "Schließen"))) Open = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        Texture2D white;

        // Small frames for the other members (name, health, where), left side.
        public void DrawFrames(GUIStyle panel)
        {
            Group mine = GroupOf(Me);
            if (mine == null) return;
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            float y = 380f; // below the companion list at the top left
            foreach (int p in mine.Members)
            {
                if (p == Me) continue;
                Puppet pp = puppetOf(p);
                PlayerState s = pp != null ? pp.Latest : null;
                Rect r = new Rect(10f, y, 200f, 42f);
                GUI.Box(r, "", panel);
                string name = NameOf(p) + (p == mine.Members[0] ? " *" : "");
                if (s == null || !Plugin.SameWorld(p)) name += Lang.T("  (elsewhere)", "  (anderswo)");
                else if (s.InDungeon) name += Lang.T("  (dungeon)", "  (Dungeon)");
                GUI.Label(new Rect(r.x + 6f, r.y + 2f, r.width - 12f, 20f), name);
                float frac = s != null && s.MaxHealth > 0 ? Mathf.Clamp01((float)s.Health / s.MaxHealth) : 0f;
                Color old = GUI.color;
                GUI.color = new Color(0.25f, 0.05f, 0.05f);
                GUI.DrawTexture(new Rect(r.x + 6f, r.y + 24f, r.width - 12f, 12f), white);
                GUI.color = s != null && s.Dead ? Color.gray : new Color(0.85f, 0.15f, 0.15f);
                GUI.DrawTexture(new Rect(r.x + 6f, r.y + 24f, (r.width - 12f) * frac, 12f), white);
                GUI.color = old;
                y += 46f;
            }
        }

        // ---------- test steps ----------
        //   partyinvite       invite the first other player
        //   partyaccept       accept the first invitation
        //   partydecline      decline the first invitation
        //   partyleave        leave our group
        //   partykick         remove the first other member
        //   partylog          log the table
        //   partyui           open the group window (screenshot)
        public static bool TestStep(string step)
        {
            PartySync s = Instance;
            if (s == null || !step.StartsWith("party")) return false;
            int first = 0;
            foreach (int p in s.peers()) { first = p; break; }
            if (step == "partyinvite") { if (first != 0) s.Invite(first); }
            else if (step == "partyaccept" || step == "partydecline")
            {
                foreach (int inviter in new List<int>(s.invitesForMe.Keys)) { s.Answer(inviter, step == "partyaccept"); break; }
            }
            else if (step == "partyleave") s.Leave();
            else if (step == "partykick") { if (first != 0) s.Kick(first); }
            else if (step == "partylog") Plugin.Log.LogInfo(Plugin.Stamp() + " [partycheck] " + s.Summary());
            else if (step == "partyui") s.Open = !s.Open;
            else return false;
            return true;
        }
    }
}
