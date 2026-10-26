using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Keeps quests and story decisions the same for everyone.
    //
    // Gedonia runs its story through named GameManager functions
    // (GameManager.instance.SendMessage("TakeHorseQuest")). Player actions
    // that do this - a dialogue answer, a solved mystery, an item put into a
    // switch - are routed here instead:
    //  - on the host the function runs, gets the next number in the story
    //    log and goes to every client;
    //  - on a client it becomes an intent to the host and runs only when the
    //    host's numbered event comes back, so both run it once, in the same
    //    order. Whoever talks decides; everyone gets the result.
    // Each machine runs the game's own function, so world side effects
    // (dialogues, quest markers) happen everywhere and every player gets the
    // full reward on their own character (the owner's chosen policy).
    // Cutscene markers, cutscene exits and world-event completions follow
    // from events that already run everywhere, so they stay local.
    //
    // Safety net (policy "the host's story counts"): the host sends its quest
    // state every 5 s; a client that is missing a quest or objective the host
    // has, or still has a quest open that the host finished, is repaired.
    public class StorySync
    {
        class Pending { public int Id; public string Name; public float NextSend; }

        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        public static StorySync Instance;
        static bool applying;
        // True while a story function or quest repair runs (its effects are
        // already happening on every machine).
        public static bool Applying { get { return applying; } }

        // Host
        int seq;
        readonly List<KeyValuePair<int, string>> log = new List<KeyValuePair<int, string>>();
        readonly Dictionary<int, int> acked = new Dictionary<int, int>();       // peer -> highest event applied
        readonly Dictionary<int, int> intentDone = new Dictionary<int, int>();  // peer -> highest intent run
        readonly Dictionary<string, float> lastRun = new Dictionary<string, float>();
        float nextResend, nextQuestState;

        // Client
        int applied;
        readonly SortedDictionary<int, string> buffered = new SortedDictionary<int, string>();
        readonly List<Pending> pending = new List<Pending>();
        int nextIntent;
        float lastStoryAt = -100f, nextIntentResend;
        string lastQuestDiff = "";

        public int EventsRun, EventsApplied, IntentsSent, IntentsRun, IntentsDeduped, Repairs;

        public StorySync(Net net, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.peers = peers;
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        static bool InWorld
        {
            get { return GameManager.instance != null && QuestsData.instance != null && PlayerScript.instance != null && SceneManager.GetActiveScene().name != "MainMenu"; }
        }

        // ---------- the hook ----------

        // Replaces GameManager.instance.SendMessage(name, options) in the
        // methods below. Anything not aimed at the GameManager is untouched.
        // Dialogue functions that act on the speaker's own conversation target
        // (InterfaceManager.currentTalkTarget) and only advance a world event:
        // run where spoken; the event objective reaches the event's owner.
        static readonly HashSet<string> LocalOnly = new HashSet<string> { "TalkOutGhostSoul", "BeginRevealRitual" };

        public static void Dispatch(Component target, string name, SendMessageOptions options)
        {
            StorySync s = Instance;
            // A shared conversation's answer: its leader dispatches the story
            // function, the other members get it from the story log.
            if (DialogueSync.SuppressStory && target is GameManager && !applying)
            {
                Plugin.Log.LogInfo(Plugin.Stamp() + " Story function '" + name + "' left to the conversation's leader");
                return;
            }
            if (s == null || applying || !(target is GameManager) || !s.Connected || LocalOnly.Contains(name))
            {
                target.SendMessage(name, options);
                return;
            }
            s.Local(name);
        }

        [HarmonyPatch]
        static class DispatchPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(InterfaceManager), "OnDialogueButton");
                yield return AccessTools.Method(typeof(InterfaceManager), "ConfirmMystery");
                yield return AccessTools.Method(typeof(PutInSwitch), "OnTriggerEnter");
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                MethodInfo send = AccessTools.Method(typeof(Component), "SendMessage", new Type[] { typeof(string), typeof(SendMessageOptions) });
                MethodInfo hook = AccessTools.Method(typeof(StorySync), "Dispatch");
                int replaced = 0;
                foreach (CodeInstruction i in instructions)
                {
                    if ((i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Call) && Equals(i.operand, send))
                    {
                        i.opcode = OpCodes.Call;
                        i.operand = hook;
                        replaced++;
                    }
                    yield return i;
                }
                if (replaced == 0) Plugin.Log.LogError("Story hook found no SendMessage call in " + original.DeclaringType.Name + "." + original.Name);
                else Plugin.Log.LogInfo("Story hook: " + replaced + " call(s) in " + original.DeclaringType.Name + "." + original.Name);
            }
        }

        void Local(string name)
        {
            if (net.IsHost)
            {
                float at;
                if (lastRun.TryGetValue(name, out at) && Time.realtimeSinceStartup - at < 3f)
                {
                    IntentsDeduped++;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Story answer '" + name + "' ignored on the host: a friend's identical answer just ran");
                    return;
                }
                RunAndLog(name, net.LocalId);
                return;
            }
            Pending p = new Pending { Id = ++nextIntent, Name = name, NextSend = 0f };
            pending.Add(p);
            IntentsSent++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Story intent #" + p.Id + " '" + name + "' sent to the host");
            SendIntent(p);
        }

        void SendIntent(Pending p)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(p.Id); w.Write(p.Name); w.Flush(); net.SendMessage(Net.MsgStoryIntent, 0, ms.ToArray(), true); }
            p.NextSend = Time.realtimeSinceStartup + 1f;
        }

        // Number of the story event running right now (0 = none) and how many
        // actors it has spawned so far: EnemySync pairs enemies spawned by the
        // same event on every machine by (event, n-th spawn).
        public static int CurrentEvent;
        static int spawnIndex;
        public static int NextSpawnIndex() { return ++spawnIndex; }

        // Runs a story function. Policy: a companion it recruits belongs to
        // the player who caused the event, so on everyone else's machine the
        // freshly added follower is removed again (the recruiter's companion
        // shows up there as a mirror instead).
        void Run(string name, int number, int origin)
        {
            PlayerScript player = PlayerScript.instance;
            List<CompanionNPCModule> before = player != null ? new List<CompanionNPCModule>(player.currentCOmpanions) : null;
            applying = true;
            CurrentEvent = number;
            spawnIndex = 0;
            try { GameManager.instance.SendMessage(name, SendMessageOptions.DontRequireReceiver); }
            catch (Exception e) { Plugin.Log.LogError("Story function '" + name + "' failed: " + e); }
            finally { applying = false; CurrentEvent = 0; }
            if (before == null || origin == 0 || origin == net.LocalId) return;
            List<CompanionNPCModule> added = player.currentCOmpanions.FindAll(c => c != null && !before.Contains(c));
            foreach (CompanionNPCModule c in added)
            {
                player.currentCOmpanions.Remove(c);
                Plugin.Log.LogInfo(Plugin.Stamp() + " '" + name + "' recruited '" + (c.npc != null ? c.npc.npcName : c.name) + "' for player " + origin + "; not for us");
                UnityEngine.Object.Destroy(c.gameObject);
            }
            if (added.Count > 0 && InterfaceManager.instance != null) InterfaceManager.instance.RefreshCompanionsPanels();
        }

        readonly Dictionary<int, int> originOf = new Dictionary<int, int>(); // event number -> player who caused it

        // ---------- host ----------

        void RunAndLog(string name, int origin)
        {
            seq++;
            originOf[seq] = origin;
            Run(name, seq, origin);
            log.Add(new KeyValuePair<int, string>(seq, name));
            lastRun[name] = Time.realtimeSinceStartup;
            EventsRun++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Story event #" + seq + " '" + name + "' (from " + (origin == net.LocalId ? "host" : origin.ToString()) + ")");
            SendEvent(seq, name, 0);
        }

        void SendEvent(int number, string name, int target)
        {
            int origin;
            originOf.TryGetValue(number, out origin);
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(number); w.Write(name); w.Write(origin); w.Flush(); net.SendMessage(Net.MsgStoryEvent, target, ms.ToArray(), true); }
        }

        void HostIntent(Net.Message m)
        {
            int id; string name;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload))) { id = r.ReadInt32(); name = r.ReadString(); }
            int done;
            intentDone.TryGetValue(m.Sender, out done);
            bool ran = true;
            if (id > done)
            {
                intentDone[m.Sender] = id;
                float at;
                // Two players answering the same NPC at once: run it once.
                if (lastRun.TryGetValue(name, out at) && Time.realtimeSinceStartup - at < 3f)
                {
                    IntentsDeduped++;
                    ran = false;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Story intent '" + name + "' from " + m.Sender + " ignored: it just ran");
                }
                else
                {
                    IntentsRun++;
                    RunAndLog(name, m.Sender);
                }
            }
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(id); w.Write(ran); w.Flush(); net.SendMessage(Net.MsgIntentAck, m.Sender, ms.ToArray(), true); }
        }

        void HostResend(float now)
        {
            foreach (int p in peers())
            {
                // A player seen for the first time starts at the current
                // number; the quest-state check brings them up to date.
                if (!acked.ContainsKey(p)) { acked[p] = seq; continue; }
                int sent = 0;
                foreach (KeyValuePair<int, string> e in log)
                {
                    if (e.Key <= acked[p]) continue;
                    if (sent++ >= 20) break;
                    SendEvent(e.Key, e.Value, p);
                }
            }
        }

        // ---------- client ----------

        void ClientEvent(Net.Message m, float now)
        {
            int number; string name;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload))) { number = r.ReadInt32(); name = r.ReadString(); originOf[number] = r.ReadInt32(); }
            if (applied == 0 && number > 1 && buffered.Count == 0) applied = number - 1; // joined mid-session
            if (number > applied) buffered[number] = name;
            Drain(now);
        }

        void Drain(float now)
        {
            if (!InWorld) return;
            while (buffered.ContainsKey(applied + 1))
            {
                string name = buffered[applied + 1];
                buffered.Remove(applied + 1);
                applied++;
                // Our own intent coming back: done waiting for it.
                pending.RemoveAll(p => p.Name == name);
                int origin;
                originOf.TryGetValue(applied, out origin);
                Run(name, applied, origin);
                EventsApplied++;
                lastStoryAt = now;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Story event #" + applied + " '" + name + "' applied");
            }
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(applied); w.Flush(); net.SendMessage(Net.MsgStoryAck, 0, ms.ToArray(), false); }
        }

        // ---------- quest state ----------

        // Active quests with their objectives, and every quest in the journal.
        static void Snapshot(out Dictionary<string, string> active, out HashSet<string> journal)
        {
            active = new Dictionary<string, string>();
            journal = new HashSet<string>();
            foreach (Quest q in QuestsData.instance.currentQuests)
            {
                if (q == null) continue;
                char[] bits = new char[q.completed.Length];
                for (int i = 0; i < bits.Length; i++) bits[i] = q.completed[i] ? '1' : '0';
                active[q.questName] = new string(bits);
            }
            Quest[] db = QuestsData.instance.questDatabase;
            foreach (int i in GameManager.instance.journalledQuests)
                if (i >= 0 && i < db.Length && db[i] != null) journal.Add(db[i].questName);
        }

        void SendQuestState()
        {
            Dictionary<string, string> active; HashSet<string> journal;
            Snapshot(out active, out journal);
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(seq);
                w.Write(active.Count);
                foreach (KeyValuePair<string, string> kv in active) { w.Write(kv.Key); w.Write(kv.Value); }
                w.Write(journal.Count);
                foreach (string j in journal) w.Write(j);
                w.Flush();
                net.SendMessage(Net.MsgQuestState, 0, ms.ToArray(), false);
            }
        }

        void ClientQuestState(Net.Message m, float now)
        {
            if (!InWorld) return;
            int hostSeq;
            Dictionary<string, string> hostActive = new Dictionary<string, string>();
            HashSet<string> hostJournal = new HashSet<string>();
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                hostSeq = r.ReadInt32();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) hostActive[r.ReadString()] = r.ReadString();
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) hostJournal.Add(r.ReadString());
            }
            // Compare only when nothing is in flight, or we'd "repair" what an
            // event is about to do anyway.
            if (hostSeq != applied || buffered.Count > 0 || pending.Count > 0 || now - lastStoryAt < 3f) return;

            Dictionary<string, string> active; HashSet<string> journal;
            Snapshot(out active, out journal);
            List<string> diff = new List<string>();
            foreach (KeyValuePair<string, string> h in hostActive)
            {
                string mine;
                if (!active.TryGetValue(h.Key, out mine))
                {
                    if (!journal.Contains(h.Key)) { diff.Add("missing quest '" + h.Key + "'"); Repair(() => QuestsData.instance.AddQuest(h.Key), "add quest '" + h.Key + "'"); }
                    else diff.Add("'" + h.Key + "' finished here, open on host");
                    continue;
                }
                for (int i = 0; i < h.Value.Length && i < mine.Length; i++)
                {
                    if (h.Value[i] == '1' && mine[i] == '0')
                    {
                        diff.Add("'" + h.Key + "' objective " + i + " open");
                        string qn = h.Key; int obj = i;
                        Repair(() => QuestsData.instance.ObjectiveCompleted(qn, obj), "complete objective " + obj + " of '" + qn + "'");
                    }
                    else if (h.Value[i] != mine[i]) diff.Add("'" + h.Key + "' objective " + i + " done here, open on host");
                }
            }
            foreach (KeyValuePair<string, string> a in active)
            {
                if (hostActive.ContainsKey(a.Key)) continue;
                if (hostJournal.Contains(a.Key))
                {
                    diff.Add("'" + a.Key + "' open here, finished on host");
                    string qn = a.Key;
                    Repair(() => QuestsData.instance.ReturnQuest(qn), "finish quest '" + qn + "' (full reward, per policy)");
                }
                else diff.Add("'" + a.Key + "' only here (kept)");
            }
            string text = diff.Count == 0 ? "" : string.Join("; ", diff.ToArray());
            if (text != lastQuestDiff)
            {
                lastQuestDiff = text;
                Plugin.Log.LogInfo(Plugin.Stamp() + " [quests] " + (diff.Count == 0 ? "same as host (" + active.Count + " active, " + journal.Count + " in journal)" : "differs from host: " + text));
            }
        }

        void Repair(Action fix, string what)
        {
            applying = true;
            try { fix(); Repairs++; Plugin.Log.LogInfo(Plugin.Stamp() + " [quests] repaired: " + what); }
            catch (Exception e) { Plugin.Log.LogError("Quest repair '" + what + "' failed: " + e); }
            finally { applying = false; }
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
                        case Net.MsgStoryIntent: if (net.IsHost && InWorld) HostIntent(m); break;
                        case Net.MsgStoryEvent: if (!net.IsHost) ClientEvent(m, now); break;
                        case Net.MsgStoryAck:
                            if (net.IsHost)
                                using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
                                {
                                    int a = r.ReadInt32(), cur;
                                    if (!acked.TryGetValue(m.Sender, out cur) || a > cur) acked[m.Sender] = a;
                                }
                            break;
                        case Net.MsgIntentAck:
                            if (!net.IsHost)
                                using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
                                {
                                    int id = r.ReadInt32();
                                    bool ran = r.ReadBoolean();
                                    // Ran: its numbered event is on the way (or here); stop resending
                                    // and wait for it by name. Not run (someone else just did it):
                                    // that event already reached us, nothing to wait for.
                                    if (ran) { foreach (Pending p in pending) if (p.Id <= id) p.NextSend = float.MaxValue; }
                                    else pending.RemoveAll(p => p.Id == id);
                                }
                            break;
                        case Net.MsgQuestState: if (!net.IsHost) ClientQuestState(m, now); break;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Story message " + m.Type + " from " + m.Sender + " failed: " + e);
                }
            }

            if (net.IsHost)
            {
                if (now >= nextResend) { nextResend = now + 1f; HostResend(now); }
                if (InWorld && Connected && now >= nextQuestState) { nextQuestState = now + 5f; SendQuestState(); }
            }
            else
            {
                Drain(now);
                if (now >= nextIntentResend)
                {
                    nextIntentResend = now + 0.5f;
                    foreach (Pending p in pending) if (now >= p.NextSend) SendIntent(p);
                }
            }
        }

        public string Summary()
        {
            return net.IsHost
                ? "story log=" + seq + " run=" + EventsRun + " fromFriends=" + IntentsRun + " deduped=" + IntentsDeduped
                : "story applied=" + applied + " waiting=" + buffered.Count + " intents=" + IntentsSent + " pending=" + pending.Count + " repairs=" + Repairs;
        }

        // Test helpers: the same path a dialogue answer takes, and a change
        // made behind the sync's back (to exercise the repair).
        public static void TestAnswer(string name)
        {
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: dialogue answer '" + name + "'");
            Dispatch(GameManager.instance, name, SendMessageOptions.DontRequireReceiver);
        }

        public static void TestUnsyncedQuest(string quest)
        {
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: adding quest '" + quest + "' without sync");
            applying = true;
            try { QuestsData.instance.AddQuest(quest); }
            finally { applying = false; }
        }

        public static string QuestReport(params string[] names)
        {
            if (!InWorld) return "not in world";
            Dictionary<string, string> active; HashSet<string> journal;
            Snapshot(out active, out journal);
            string s = "";
            foreach (string n in names)
            {
                int copies = 0;
                foreach (Quest q in QuestsData.instance.currentQuests) if (q != null && q.questName == n) copies++;
                s += " '" + n + "'=" + (copies > 0 ? "active x" + copies : journal.Contains(n) ? "journal" : "no");
            }
            return s.Trim();
        }
    }
}
