using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Shared conversations: several players talk to the same NPC together.
    //
    // Gedonia's dialogue: DialogueNPCModule.Talk opens the window (panels[6]),
    // InterfaceManager.RefreshDialogue builds one button per active answer of
    // currentQuestions (grey when the LOCAL player misses a requirement), and
    // OnDialogueButton(go) checks the requirements again and runs the answer
    // (story function, next questions, shop, aggro, or close).
    //  - Whoever opens a conversation announces it; players near the NPC are
    //    told and join by talking to the same NPC (F). Members see the same
    //    NPC line and answers (the leader's state).
    //  - With two or more members a click is a vote. All agree -> it is taken;
    //    with a vote timer (config Dialogue.VoteSeconds, 0 = none) the leader's
    //    vote (or the most common one) wins when it runs out.
    //  - Every member runs the chosen answer with the game's own code; only the
    //    leader dispatches its story function (StorySync brings it to everyone).
    //  - An answer is open if ANY member meets its stat/skill/level/speech/
    //    reputation requirements and ANY member has the money/items. One member
    //    pays (the leader if they can, else the first who can): story functions
    //    run on every machine, StorySync skips the payment everywhere else.
    public class DialogueSync
    {
        const byte KOpen = 0, KJoin = 1, KLeave = 2, KState = 3, KVote = 4, KDecide = 5, KEligible = 6;
        const float NearRange = 30f;

        class Conv
        {
            public int Npc, Leader;
            public int Payer; // who paid for an answer here: later answers' story functions take it (Sidmir: price asked, gold taken on "Let's go")
            public string NpcName = "";
            public Vector3 Pos;
            public List<int> Members = new List<int>();
            public Dictionary<int, int> Votes = new Dictionary<int, int>();
            public float FirstVoteAt = -1f, LastHeard;
            // member -> answer index -> bit 1 stats ok, bit 2 money/items ok
            public Dictionary<int, Dictionary<int, byte>> Elig = new Dictionary<int, Dictionary<int, byte>>();
        }

        readonly Net net;
        readonly Func<int, string> nameOf;
        readonly Func<IEnumerable<int>> peers;
        public static DialogueSync Instance;
        public static float VoteSeconds = 10f;
        static bool deciding;
        public static bool SuppressStory; // a member who isn't the leader runs a chosen answer
        // Who pays for the answer running right now (0 = it costs nothing).
        public static int AnswerPayer;
        static int decidedPayer;
        // Answering alone: a paid answer makes the rest of this conversation ours to pay.
        static GameObject soloTarget;
        static bool soloPaid;

        readonly Dictionary<int, Conv> convs = new Dictionary<int, Conv>();
        Conv mine;
        DialogueNPCModule myNpc;
        float nextState, nextNote, nextExpire;
        readonly Dictionary<UILabel, string> baseText = new Dictionary<UILabel, string>();
        public int Opened, Joined, VotesSent, Decisions, DecisionsApplied, Refused;

        public DialogueSync(Net net, Func<int, string> nameOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.nameOf = nameOf;
            this.peers = peers;
            Instance = this;
        }

        int Me { get { return net.LocalId; } }
        bool Connected { get { foreach (int p in peers()) return true; return false; } }
        string NameOf(int id) { return id == Me ? Plugin.LocalName : (nameOf(id) ?? "?"); }

        // ---------- ids ----------

        // Scene NPCs without a save id: the hierarchy path as it was when their
        // scene loaded (the game destroys and moves things later, which shifts
        // sibling indices differently on each machine).
        static readonly Dictionary<GameObject, int> sceneIds = new Dictionary<GameObject, int>();
        static bool indexing;

        public static void StartIndexing()
        {
            if (indexing) return;
            indexing = true;
            SceneManager.sceneLoaded += (s, mode) => IndexScene(s);
            for (int i = 0; i < SceneManager.sceneCount; i++) IndexScene(SceneManager.GetSceneAt(i));
        }

        static void IndexScene(Scene scene)
        {
            if (!scene.IsValid()) return;
            List<GameObject> stale = new List<GameObject>();
            foreach (GameObject g in sceneIds.Keys) if (g == null) stale.Add(g);
            foreach (GameObject g in stale) sceneIds.Remove(g);
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (DialogueNPCModule d in root.GetComponentsInChildren<DialogueNPCModule>(true))
                    sceneIds[d.gameObject] = WorldIds.Of(d.transform, scene.name);
        }

        public static int NpcId(GameObject go)
        {
            SaveableItem si = go.GetComponent<SaveableItem>();
            if (si != null && si.instanceID != 0) return si.instanceID;
            int id;
            NPC npc = go.GetComponent<NPC>();
            if (npc != null && TravellerSync.IdFor(npc, out id)) return id;
            if (sceneIds.TryGetValue(go, out id)) return id;
            return WorldIds.Of(go.transform, go.scene.name);
        }

        static DialogueNPCModule FindNpc(int id)
        {
            foreach (DialogueNPCModule d in UnityEngine.Object.FindObjectsOfType<DialogueNPCModule>())
                if (NpcId(d.gameObject) == id) return d;
            return null;
        }

        // ---------- messages ----------

        void Send(byte kind, int npc, Action<BinaryWriter> body)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(kind); w.Write(npc);
                if (body != null) body(w);
                w.Flush();
                net.SendMessage(Net.MsgDialogue, 0, ms.ToArray(), true);
            }
        }

        void SendState(Conv c)
        {
            DialogueNPCModule d = myNpc;
            Send(KState, c.Npc, w =>
            {
                w.Write(c.Leader);
                w.Write(c.NpcName);
                w.Write(c.Pos.x); w.Write(c.Pos.y); w.Write(c.Pos.z);
                w.Write((short)c.Members.Count);
                foreach (int m in c.Members) w.Write(m);
                w.Write((short)c.Votes.Count);
                foreach (KeyValuePair<int, int> v in c.Votes) { w.Write(v.Key); w.Write(v.Value); }
                w.Write(d != null ? d.currentAnswer ?? "" : "");
                int[] q = d != null && d.currentQuestions != null ? d.currentQuestions : new int[0];
                w.Write((short)q.Length);
                foreach (int i in q) w.Write(i);
            });
        }

        Conv Get(int npc)
        {
            Conv c;
            if (!convs.TryGetValue(npc, out c)) { c = new Conv { Npc = npc }; convs[npc] = c; }
            c.LastHeard = Time.realtimeSinceStartup;
            return c;
        }

        void Handle(Net.Message m)
        {
            if (m.Sender == Me) return;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                byte kind = r.ReadByte();
                int npc = r.ReadInt32();
                Conv c = Get(npc);
                switch (kind)
                {
                    case KOpen:
                    {
                        c.Leader = m.Sender;
                        c.Payer = 0;
                        c.NpcName = r.ReadString();
                        c.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (!c.Members.Contains(m.Sender)) c.Members.Add(m.Sender);
                        // Both opened the same NPC at once: the lower id leads.
                        if (mine != null && mine.Npc == npc && mine.Leader == Me && mine.Members.Count == 1 && m.Sender < Me)
                        {
                            Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: " + NameOf(m.Sender) + " opened '" + c.NpcName + "' at the same time; joining theirs");
                            mine = c;
                            if (!c.Members.Contains(Me)) c.Members.Add(Me);
                            Send(KJoin, npc, null);
                        }
                        else if (Plugin.SameWorld(m.Sender) && PlayerScript.instance != null && Vector3.Distance(PlayerScript.instance.transform.position, c.Pos) < NearRange && mine == null)
                            Note(NameOf(m.Sender) + " is talking to " + c.NpcName + " - go there and press F to join.",
                                 NameOf(m.Sender) + " spricht mit " + Translated(c.NpcName) + " - geh hin und drück F, um mitzureden.", 8f);
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: " + NameOf(m.Sender) + " opened '" + c.NpcName + "' (" + npc + ")");
                        break;
                    }
                    case KJoin:
                        if (!c.Members.Contains(m.Sender)) c.Members.Add(m.Sender);
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: " + NameOf(m.Sender) + " joined '" + c.NpcName + "'");
                        if (mine == c && c.Leader == Me) { nextState = 0f; Note(NameOf(m.Sender) + " joined the conversation.", NameOf(m.Sender) + " redet jetzt mit.", 4f); }
                        break;
                    case KLeave:
                        RemoveMember(c, m.Sender);
                        break;
                    case KState:
                        ApplyState(c, m.Sender, r);
                        break;
                    case KVote:
                    {
                        int option = r.ReadInt32();
                        if (option < 0) c.Votes.Remove(m.Sender); else c.Votes[m.Sender] = option;
                        if (c.FirstVoteAt < 0f && c.Votes.Count > 0) c.FirstVoteAt = Time.realtimeSinceStartup;
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: " + NameOf(m.Sender) + " votes " + option + " at '" + c.NpcName + "'");
                        if (mine == c) { Redraw(); if (c.Leader == Me) Tally(c); }
                        break;
                    }
                    case KDecide:
                    {
                        int option = r.ReadInt32();
                        int payer = r.ReadInt32();
                        if (payer != 0) c.Payer = payer;
                        c.Votes.Clear(); c.FirstVoteAt = -1f;
                        if (mine == c) Execute(option, false, m.Sender, payer);
                        break;
                    }
                    case KEligible:
                    {
                        Dictionary<int, byte> e = new Dictionary<int, byte>();
                        int n = r.ReadInt16();
                        for (int i = 0; i < n; i++) { int opt = r.ReadInt32(); e[opt] = r.ReadByte(); }
                        c.Elig[m.Sender] = e;
                        if (mine == c) Redraw();
                        break;
                    }
                }
            }
        }

        void ApplyState(Conv c, int sender, BinaryReader r)
        {
            c.Leader = r.ReadInt32();
            c.NpcName = r.ReadString();
            c.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            List<int> members = new List<int>();
            int n = r.ReadInt16();
            for (int i = 0; i < n; i++) members.Add(r.ReadInt32());
            Dictionary<int, int> votes = new Dictionary<int, int>();
            n = r.ReadInt16();
            for (int i = 0; i < n; i++) { int k = r.ReadInt32(); votes[k] = r.ReadInt32(); }
            string answer = r.ReadString();
            n = r.ReadInt16();
            int[] questions = new int[n];
            for (int i = 0; i < n; i++) questions[i] = r.ReadInt32();

            // Our own membership counts from what we know (a Join may cross a State).
            if (mine == c && !members.Contains(Me)) members.Add(Me);
            c.Members = members;
            c.Votes = votes;
            if (votes.Count == 0) c.FirstVoteAt = -1f;
            else if (c.FirstVoteAt < 0f) c.FirstVoteAt = Time.realtimeSinceStartup;
            if (mine != c || myNpc == null) return;
            // Follow the leader's place in the conversation (when we joined, or missed a step).
            if (!SameQuestions(myNpc.currentQuestions, questions) || myNpc.currentAnswer != answer)
            {
                myNpc.currentAnswer = answer;
                myNpc.currentQuestions = questions;
                InterfaceManager.instance.RefreshDialogue();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: took " + NameOf(sender) + "'s place in '" + c.NpcName + "' (" + questions.Length + " answers)");
            }
            else Redraw();
        }

        static bool SameQuestions(int[] a, int[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        void RemoveMember(Conv c, int who)
        {
            c.Members.Remove(who);
            c.Votes.Remove(who);
            c.Elig.Remove(who);
            if (c.Leader == who && c.Members.Count > 0) c.Leader = c.Members[0];
            Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: " + NameOf(who) + " left '" + c.NpcName + "'" + (c.Members.Count > 0 ? ", leader now " + NameOf(c.Leader) : ""));
            if (c.Members.Count == 0) convs.Remove(c.Npc);
            if (mine == c) { Redraw(); if (c.Leader == Me) { nextState = 0f; Tally(c); } }
        }

        // ---------- local hooks ----------

        void OnTalk(DialogueNPCModule d)
        {
            if (!Connected || StorySync.Applying) return;
            int id = NpcId(d.gameObject);
            NPC npc = d.GetComponent<NPC>();
            myNpc = d;
            baseText.Clear();
            Conv c;
            if (convs.TryGetValue(id, out c) && c.Members.Count > 0 && !c.Members.Contains(Me) && Time.realtimeSinceStartup - c.LastHeard < 5f)
            {
                mine = c;
                c.Members.Add(Me);
                Joined++;
                Send(KJoin, id, null);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: joined " + NameOf(c.Leader) + "'s conversation with '" + c.NpcName + "' (" + id + ")");
                OnRefreshed(); // Talk built the answers before we knew the conversation
                return;
            }
            c = new Conv { Npc = id, Leader = Me, NpcName = npc != null ? npc.npcName : d.name, Pos = d.transform.position, LastHeard = Time.realtimeSinceStartup };
            c.Members.Add(Me);
            convs[id] = c;
            mine = c;
            Opened++;
            Send(KOpen, id, w => { w.Write(c.NpcName); w.Write(c.Pos.x); w.Write(c.Pos.y); w.Write(c.Pos.z); });
            Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: opened '" + c.NpcName + "' (" + id + ")");
            nextState = 0f;
            OnRefreshed();
        }

        void OnExit()
        {
            if (mine == null) return;
            Conv c = mine;
            mine = null;
            myNpc = null;
            baseText.Clear();
            Send(KLeave, c.Npc, null);
            RemoveMember(c, Me);
        }

        // Returns false when the click was taken as a vote (or refused).
        bool OnAnswer(GameObject go)
        {
            if (deciding || mine == null || mine.Members.Count < 2) return true;
            int option;
            if (!int.TryParse(go.name, out option)) return true;
            if (!GroupEligible(mine, option))
            {
                Refused++;
                Note("Nobody here meets the requirements of this answer.", "Niemand von euch erfüllt die Bedingungen dieser Antwort.", 3f);
                return false;
            }
            mine.Votes[Me] = option;
            if (mine.FirstVoteAt < 0f) mine.FirstVoteAt = Time.realtimeSinceStartup;
            VotesSent++;
            Send(KVote, mine.Npc, w => w.Write(option));
            Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: my vote " + option + " at '" + mine.NpcName + "'");
            Redraw();
            if (mine.Leader == Me) Tally(mine);
            return false;
        }

        void Tally(Conv c)
        {
            if (c.Votes.Count == 0) return;
            int first = -1;
            bool same = true;
            foreach (int m in c.Members)
            {
                int v;
                if (!c.Votes.TryGetValue(m, out v)) { same = false; break; }
                if (first == -1) first = v; else if (v != first) same = false;
            }
            int chosen = -1;
            string why = "";
            if (same && first >= 0) { chosen = first; why = "everyone agreed"; }
            else if (VoteSeconds > 0f && c.FirstVoteAt >= 0f && Time.realtimeSinceStartup - c.FirstVoteAt >= VoteSeconds)
            {
                int lv;
                if (c.Votes.TryGetValue(c.Leader, out lv)) { chosen = lv; why = "time up, leader's vote"; }
                else
                {
                    Dictionary<int, int> count = new Dictionary<int, int>();
                    foreach (int v in c.Votes.Values) { int k; count.TryGetValue(v, out k); count[v] = k + 1; }
                    int best = 0;
                    foreach (KeyValuePair<int, int> kv in count) if (kv.Value > best) { best = kv.Value; chosen = kv.Key; }
                    why = "time up, most votes";
                }
            }
            if (chosen < 0) return;
            c.Votes.Clear(); c.FirstVoteAt = -1f;
            Decisions++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: answer " + chosen + " chosen at '" + c.NpcName + "' (" + why + ")");
            int opt = chosen;
            int payer = Payer(c, chosen);
            if (payer != 0) Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: answer " + chosen + (Costs(myNpc, chosen) ? " costs money/items; " : " follows a paid answer; ") + NameOf(payer) + " pays");
            Send(KDecide, c.Npc, w => { w.Write(opt); w.Write(payer); });
            Execute(chosen, true, Me, payer);
        }

        // The member who pays for an answer with money/item requirements: the
        // leader if they have it, else the first member who has. 0 = no cost.
        int Payer(Conv c, int option)
        {
            if (myNpc == null || option >= myNpc.allDialogue.Length || !Costs(myNpc, option)) return c.Payer;
            c.Payer = c.Leader;
            if ((MemberEligibility(c, c.Leader, option) & 2) == 0)
                foreach (int m in c.Members) if ((MemberEligibility(c, m, option) & 2) != 0) { c.Payer = m; break; }
            return c.Payer;
        }

        static bool Costs(DialogueNPCModule d, int option)
        {
            if (d == null || d.allDialogue == null || option < 0 || option >= d.allDialogue.Length) return false;
            foreach (answerRequirement q in d.allDialogue[option].requirements ?? new answerRequirement[0])
                if (q.type == answerReqType.Money || q.type == answerReqType.Item) return true;
            return false;
        }

        // Runs the chosen answer here with the game's own code. Requirements were
        // checked for the group, so the local check is lifted for this one call.
        void Execute(int option, bool leader, int from, int payer)
        {
            InterfaceManager im = InterfaceManager.instance;
            DialogueNPCModule d = myNpc;
            if (im == null || d == null || option < 0 || option >= d.allDialogue.Length) return;
            DecisionsApplied++;
            GameObject button = null;
            foreach (GameObject g in Questions()) if (g != null && g.name == option.ToString()) button = g;
            if (button == null)
            {
                // Not shown here (out of step): a stand-in with the same name.
                button = new GameObject(option.ToString());
                UnityEngine.Object.Destroy(button, 1f);
            }
            answerRequirement[] reqs = d.allDialogue[option].requirements;
            deciding = true;
            decidedPayer = payer;
            SuppressStory = !leader;
            try
            {
                d.allDialogue[option].requirements = new answerRequirement[0];
                im.OnDialogueButton(button);
            }
            catch (Exception e) { Plugin.Log.LogError("Dialogue answer " + option + " failed: " + e); }
            finally
            {
                d.allDialogue[option].requirements = reqs;
                deciding = false;
                decidedPayer = 0;
                SuppressStory = false;
            }
            Plugin.Log.LogInfo(Plugin.Stamp() + " Dialogue: ran answer " + option + (leader ? " (leader)" : " (chosen by " + NameOf(from) + ")"));
        }

        static List<GameObject> Questions()
        {
            List<GameObject> l = InterfaceManager.instance != null ? Traverse.Create(InterfaceManager.instance).Field("createdQuestions").GetValue<List<GameObject>>() : null;
            return l ?? new List<GameObject>();
        }

        // ---------- requirements ----------

        // bit 1: stat/skill/level/speech/reputation requirements met here; bit 2: money/items.
        static byte LocalEligibility(DialogueNPCModule d, int option)
        {
            PlayerScript p = PlayerScript.instance;
            NPC npc = d.GetComponent<NPC>();
            bool stats = true, res = true;
            answerRequirement[] reqs = d.allDialogue[option].requirements ?? new answerRequirement[0];
            foreach (answerRequirement q in reqs)
            {
                int[] a = q.data ?? new int[0];
                switch (q.type)
                {
                    case answerReqType.Reputation:
                        if (npc != null && a.Length > 0 && (int)GameManager.instance.factionsRelationsDefault[(int)npc.faction][0] < a[0]) stats = false; break;
                    case answerReqType.SpeechLevel:
                        if (a.Length > 0 && p.skills[12] < a[0]) stats = false; break;
                    case answerReqType.BaseStatLevel:
                        if (a.Length > 1)
                        {
                            int have = a[0] == 0 ? p.strengthBasic : a[0] == 1 ? p.agilityBasic : a[0] == 2 ? p.intellectBasic : a[0] == 3 ? p.charismaBasic : int.MaxValue;
                            if (have < a[1]) stats = false;
                        }
                        break;
                    case answerReqType.PlayerLevel:
                        if (a.Length > 0 && p.level < a[0]) stats = false; break;
                    case answerReqType.SkillLevel:
                        if (a.Length > 1 && p.skills[a[0]] < a[1]) stats = false; break;
                    case answerReqType.Money:
                        if (a.Length > 0 && p.gold < a[0]) res = false; break;
                    case answerReqType.Item:
                        if (a.Length > 2 && !HasItem(p, (ItemType)a[0], a[1], a[2])) res = false; break;
                }
            }
            return (byte)((stats ? 1 : 0) | (res ? 2 : 0));
        }

        static bool HasItem(PlayerScript p, ItemType type, int index, int amount)
        {
            Item item = null;
            ItemsData db = ItemsData.instance;
            if (type == ItemType.Consumable) item = db.consumablesData[index];
            else if (type == ItemType.Simple) item = db.basicItemsData[index];
            else if (type == ItemType.UsableItem) item = db.usableItemsData[index];
            else if (type == ItemType.Weapon) item = db.weaponsDatabase[index];
            else if (type == ItemType.Armor) item = db.armorDatabase[index];
            foreach (InventoryItem i in p.inventory) if (i.item == item && i.amount >= amount) return true;
            return false;
        }

        byte MemberEligibility(Conv c, int m, int option)
        {
            byte e;
            Dictionary<int, byte> per;
            if (m == Me) return myNpc != null && option < myNpc.allDialogue.Length ? LocalEligibility(myNpc, option) : (byte)3;
            if (c.Elig.TryGetValue(m, out per) && per.TryGetValue(option, out e)) return e;
            return 0; // not reported yet: counts for nothing
        }

        bool GroupEligible(Conv c, int option)
        {
            bool stats = false, res = false;
            foreach (int m in c.Members)
            {
                byte e = MemberEligibility(c, m, option);
                if ((e & 1) != 0) stats = true;
                if ((e & 2) != 0) res = true;
            }
            return stats && res;
        }

        void OnRefreshed()
        {
            if (mine == null || myNpc == null) return;
            baseText.Clear();
            List<int> shown = new List<int>();
            foreach (GameObject g in Questions())
            {
                int o;
                if (g == null || !int.TryParse(g.name, out o)) continue;
                shown.Add(o);
                Transform t = g.transform.Find("text");
                UILabel l = t != null ? t.GetComponent<UILabel>() : null;
                if (l != null) baseText[l] = l.text;
            }
            Conv c = mine;
            Send(KEligible, c.Npc, w =>
            {
                w.Write((short)shown.Count);
                foreach (int o in shown) { w.Write(o); w.Write(LocalEligibility(myNpc, o)); }
            });
            Redraw();
        }

        // Answer colours by the group and the members' votes next to the answers.
        void Redraw()
        {
            if (mine == null || myNpc == null || mine.Members.Count < 2) return;
            foreach (GameObject g in Questions())
            {
                int o;
                if (g == null || !int.TryParse(g.name, out o)) continue;
                Transform t = g.transform.Find("text");
                UILabel l = t != null ? t.GetComponent<UILabel>() : null;
                string b;
                if (l == null || !baseText.TryGetValue(l, out b)) continue;
                List<string> who = new List<string>();
                foreach (KeyValuePair<int, int> v in mine.Votes) if (v.Value == o) who.Add(NameOf(v.Key));
                Translator.SetRaw(l, who.Count == 0 ? b : b + "  [80C0FF](" + string.Join(", ", who.ToArray()) + ")[-]");
                l.color = GroupEligible(mine, o) ? Color.white : Color.grey;
            }
        }

        // ---------- notes ----------

        static string Translated(string s) { string de = Lang.German ? Translator.German(s) : null; return de ?? s; }

        void Note(string en, string de, float seconds)
        {
            if (Plugin.Ui != null) Plugin.Ui.ShowNote(Lang.T(en, de), seconds);
        }

        void StatusNote(float now)
        {
            if (mine == null || now < nextNote) return;
            nextNote = now + 0.5f;
            if (mine.Members.Count < 2)
            {
                // Alone in it: tell who could join.
                List<string> near = new List<string>();
                foreach (int p in peers())
                {
                    Vector3 at;
                    if (Plugin.SameWorld(p) && Plugin.PlayerPosition(p, out at) && Vector3.Distance(at, mine.Pos) < NearRange) near.Add(NameOf(p));
                }
                if (near.Count > 0)
                    Note(string.Join(", ", near.ToArray()) + " can join with F at " + mine.NpcName + " (or answer alone).",
                         string.Join(", ", near.ToArray()) + " kann bei " + Translated(mine.NpcName) + " mit F mitreden (oder du antwortest allein).", 1f);
                return;
            }
            List<string> others = new List<string>();
            foreach (int m in mine.Members) if (m != Me) others.Add(NameOf(m));
            string en = "Conversation with " + string.Join(", ", others.ToArray()) + ": click an answer to vote", de = "Gemeinsames Gespräch mit " + string.Join(", ", others.ToArray()) + ": Antwort anklicken = abstimmen";
            if (mine.Votes.Count > 0)
            {
                if (VoteSeconds > 0f && mine.FirstVoteAt >= 0f)
                {
                    int left = Mathf.Max(0, Mathf.CeilToInt(VoteSeconds - (now - mine.FirstVoteAt)));
                    en += " - decided in " + left + " s (" + NameOf(mine.Leader) + "'s vote counts then)";
                    de += " - Entscheidung in " + left + " s (dann zählt die Stimme von " + NameOf(mine.Leader) + ")";
                }
                else { en += " - waiting until you agree"; de += " - wartet, bis ihr euch einig seid"; }
            }
            Note(en, de, 1f);
        }

        // ---------- loop ----------

        public void Tick(float now)
        {
            bool inWorld = PlayerScript.instance != null && SceneManager.GetActiveScene().name != "MainMenu";
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgDialogue || !inWorld) continue;
                try { Handle(m); }
                catch (Exception e) { Plugin.Log.LogError("Dialogue message from " + m.Sender + " failed: " + e); }
            }
            if (!inWorld) return;
            // The window closed some other way (death, loading, story).
            if (mine != null && (InterfaceManager.instance == null || !InterfaceManager.instance.panels[6].activeSelf) && !deciding) OnExit();
            if (mine != null)
            {
                // Players who left the game leave the conversation.
                foreach (int m in new List<int>(mine.Members))
                {
                    if (m == Me) continue;
                    bool present = false;
                    foreach (int p in peers()) if (p == m) present = true;
                    if (!present) RemoveMember(mine, m);
                }
                if (mine != null && mine.Leader == Me)
                {
                    Tally(mine);
                    if (now >= nextState) { nextState = now + 1f; SendState(mine); }
                }
                StatusNote(now);
            }
            if (now >= nextExpire)
            {
                nextExpire = now + 2f;
                List<int> old = new List<int>();
                foreach (KeyValuePair<int, Conv> kv in convs) if (kv.Value != mine && now - kv.Value.LastHeard > 5f) old.Add(kv.Key);
                foreach (int k in old) convs.Remove(k);
            }
        }

        public string Summary()
        {
            return "dialogue opened=" + Opened + " joined=" + Joined + " votes=" + VotesSent + " decisions=" + Decisions + " applied=" + DecisionsApplied + " refused=" + Refused +
                (mine != null ? " in='" + mine.NpcName + "' members=" + mine.Members.Count + " leader=" + NameOf(mine.Leader) : "");
        }

        public string Describe()
        {
            InterfaceManager im = InterfaceManager.instance;
            if (mine == null || myNpc == null) return "not in a conversation (window " + (im != null && im.panels[6].activeSelf) + ")";
            string s = "'" + mine.NpcName + "' leader=" + NameOf(mine.Leader) + " members=";
            foreach (int m in mine.Members) s += NameOf(m) + ",";
            s += " answer='" + (im.answerText.text ?? "").Replace("\n", " ") + "' options:";
            foreach (GameObject g in Questions())
            {
                if (g == null) continue;
                Transform t = g.transform.Find("text");
                UILabel l = t != null ? t.GetComponent<UILabel>() : null;
                int o; int.TryParse(g.name, out o);
                s += " [" + g.name + (l != null && l.color == Color.grey ? " grey" : "") + " group=" + GroupEligible(mine, o) + " local=" + LocalEligibility(myNpc, o) + "] " + (l != null ? l.text : "");
            }
            s += " votes:";
            foreach (KeyValuePair<int, int> v in mine.Votes) s += " " + NameOf(v.Key) + "=" + v.Value;
            return s;
        }

        // ---------- Harmony ----------

        [HarmonyPatch(typeof(DialogueNPCModule), "Talk")]
        static class TalkPatch
        {
            static void Postfix(DialogueNPCModule __instance)
            {
                if (Instance == null) return;
                try { Instance.OnTalk(__instance); }
                catch (Exception e) { Plugin.Log.LogError("Dialogue open failed: " + e); }
            }
        }

        [HarmonyPatch(typeof(InterfaceManager), "RefreshDialogue")]
        static class RefreshPatch
        {
            static void Postfix()
            {
                if (Instance == null) return;
                try { Instance.OnRefreshed(); }
                catch (Exception e) { Plugin.Log.LogError("Dialogue refresh failed: " + e); }
            }
        }

        [HarmonyPatch(typeof(InterfaceManager), "OnDialogueButton")]
        static class AnswerPatch
        {
            static bool Prefix(GameObject go)
            {
                AnswerPayer = 0;
                if (Instance == null || go == null) return true;
                bool run = true;
                try { run = Instance.OnAnswer(go); }
                catch (Exception e) { Plugin.Log.LogError("Dialogue vote failed: " + e); }
                if (!run) return false;
                // Answered by one player: they pay. Chosen by a conversation: its payer.
                int option;
                GameObject target = InterfaceManager.instance != null ? InterfaceManager.instance.currentTalkTarget : null;
                if (target != soloTarget) { soloTarget = target; soloPaid = false; }
                if (deciding) AnswerPayer = decidedPayer;
                else if (soloPaid || (int.TryParse(go.name, out option) && target != null && Costs(target.GetComponent<DialogueNPCModule>(), option)))
                {
                    AnswerPayer = Instance.Me;
                    soloPaid = true;
                }
                return true;
            }

            static void Postfix() { AnswerPayer = 0; }
        }

        [HarmonyPatch(typeof(InterfaceManager), "ExitDialogue")]
        static class ExitPatch
        {
            static void Postfix()
            {
                if (Instance == null || deciding) return;
                soloTarget = null;
                soloPaid = false;
                try { Instance.OnExit(); }
                catch (Exception e) { Plugin.Log.LogError("Dialogue exit failed: " + e); }
            }
        }

        // ---------- test helpers ----------

        public static string Survey()
        {
            Vector3 me = PlayerScript.instance.transform.position;
            List<string> lines = new List<string>();
            foreach (DialogueNPCModule d in UnityEngine.Object.FindObjectsOfType<DialogueNPCModule>())
            {
                float dist = Vector3.Distance(me, d.transform.position);
                if (dist > 600f || d.allDialogue == null || d.defaultQuestions == null || d.defaultQuestions.Length == 0) continue;
                NPC n = d.GetComponent<NPC>();
                string reqs = "";
                int chains = 0;
                for (int i = 0; i < d.allDialogue.Length; i++)
                {
                    DialogiePoint p = d.allDialogue[i];
                    if (!p.isReturn && p.followingQuestions != null && p.followingQuestions.Length > 0) chains++;
                    if (p.requirements == null) continue;
                    foreach (answerRequirement q in p.requirements)
                        reqs += " " + i + ":" + q.type + "(" + string.Join(",", Array.ConvertAll(q.data ?? new int[0], x => x.ToString())) + ")" + (p.isActiveNow ? "" : "-off");
                }
                string first = "";
                foreach (int q in d.defaultQuestions)
                    if (q >= 0 && q < d.allDialogue.Length && d.allDialogue[q].isActiveNow) first += " " + q + "'" + d.allDialogue[q].playerQuestionText + "'";
                lines.Add(dist.ToString("F0") + "m id=" + NpcId(d.gameObject) + " '" + (n != null ? n.npcName : d.name) + "' at " + d.transform.position.ToString("F1") +
                    " answers=" + d.allDialogue.Length + " chains=" + chains + (reqs.Length > 0 ? " reqs:" + reqs : "") + " | first:" + first);
            }
            lines.Sort((a, b) => float.Parse(a.Substring(0, a.IndexOf('m')), System.Globalization.CultureInfo.InvariantCulture).CompareTo(float.Parse(b.Substring(0, b.IndexOf('m')), System.Globalization.CultureInfo.InvariantCulture)));
            return lines.Count + " dialogue NPCs within 600 m:\n  " + string.Join("\n  ", lines.ToArray());
        }

        public static void TestTalk(PlayerScript player, int id)
        {
            DialogueNPCModule d = FindNpc(id);
            if (d == null)
            {
                string near = "";
                foreach (DialogueNPCModule x in UnityEngine.Object.FindObjectsOfType<DialogueNPCModule>())
                    if (Vector3.Distance(x.transform.position, player.transform.position) < 60f)
                    {
                        SaveableItem si = x.GetComponent<SaveableItem>();
                        near += " '" + x.name + "'=" + NpcId(x.gameObject) + "(save " + (si != null ? si.instanceID.ToString() : "-") + ", scene " + x.gameObject.scene.name + ")";
                    }
                Plugin.Log.LogWarning("Test: dialogue NPC " + id + " not loaded; near:" + near);
                return;
            }
            Vector3 to = d.transform.position + d.transform.forward * 1.6f + Vector3.up * 0.3f;
            if (player._char != null) player._char.enabled = false;
            player.transform.position = to;
            if (player._char != null) player._char.enabled = true;
            player.transform.rotation = Quaternion.LookRotation(new Vector3(d.transform.position.x - to.x, 0f, d.transform.position.z - to.z));
            player.lockedInDialogue = false;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: talking to " + id + " '" + d.name + "' from " + to.ToString("F1"));
            d.StartTalk();
            if (!InterfaceManager.instance.panels[6].activeSelf) { Plugin.Log.LogInfo("Test: StartTalk refused; Talk()"); d.Talk(); }
        }

        // Clicks the button of answer index i (as the game names them).
        public static void TestVoteAnswer(int i)
        {
            foreach (GameObject g in Questions())
                if (g != null && g.name == i.ToString())
                {
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Test: clicking answer " + i);
                    InterfaceManager.instance.OnDialogueButton(g);
                    return;
                }
            Plugin.Log.LogWarning("Test: answer " + i + " not shown");
        }

        public static void TestVote(int k)
        {
            List<GameObject> q = Questions();
            if (k < 0 || k >= q.Count) { Plugin.Log.LogWarning("Test: no answer #" + k + " (" + q.Count + " shown)"); return; }
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: clicking answer #" + k + " (" + q[k].name + ")");
            InterfaceManager.instance.OnDialogueButton(q[k]);
        }
    }
}
