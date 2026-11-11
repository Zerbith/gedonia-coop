using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        // What started a story function, so every PC can run it the same way
        // and the host can tell two players' identical actions from two different ones.
        public class Ctx
        {
            public const byte None = 0, Answer = 1, Object = 2, Death = 3, Plain = 4, Escort = 5;
            public byte Kind;
            public int A, B;          // Answer: NPC save id, answer index. Object: save id (0 = none). Death: shared enemy id.
            public string Obj = "";   // Object: name
            public Vector3 Pos;       // Object: position

            public void Write(BinaryWriter w) { w.Write(Kind); w.Write(A); w.Write(B); w.Write(Obj ?? ""); w.Write(Pos.x); w.Write(Pos.y); w.Write(Pos.z); }
            public static Ctx Read(BinaryReader r) { return new Ctx { Kind = r.ReadByte(), A = r.ReadInt32(), B = r.ReadInt32(), Obj = r.ReadString(), Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) }; }

            // Same key within 3 s = the same action by two players: run once.
            public string Key(string name)
            {
                if (Kind == Death) return name + "#enemy" + A;
                if (Kind == Escort) return name + "#escort" + A;
                if (Kind == Object) return name + "#" + (A != 0 ? A.ToString() : Obj + "@" + Mathf.RoundToInt(Pos.x) + "," + Mathf.RoundToInt(Pos.z));
                return name;
            }

            public static Ctx ForObject(GameObject go)
            {
                SaveableItem si = go.GetComponent<SaveableItem>();
                return new Ctx { Kind = Object, A = si != null ? si.instanceID : 0, Obj = go.name, Pos = go.transform.position };
            }

            public override string ToString()
            {
                switch (Kind)
                {
                    case Answer: return "answer " + B + " of NPC " + A;
                    case Object: return "object '" + Obj + "'" + (A != 0 ? " (" + A + ")" : "") + " at " + Pos.ToString("F0");
                    case Death: return "death of enemy " + A;
                    case Plain: return "trigger";
                    case Escort: return "escort " + A + " at its end";
                }
                return "-";
            }
        }

        class Pending { public int Id; public string Name; public int Payer; public float NextSend; public Ctx Ctx; public GameObject Direct; public bool RanHere; public Purse Purse; }
        class Entry { public int Seq; public string Name; public Ctx Ctx; public List<bool> Checks = new List<bool>(); }

        // What a paying friend carries when it sends an intent: its gold and inventory (item name ->
        // amount of the game's matching entry). The host answers the function's item checks from it
        // (and records them for everyone else), so a friend can't finish a step it lacks the items for.
        public class Purse
        {
            public int Gold = -1;
            public readonly Dictionary<string, int> Items = new Dictionary<string, int>();

            public static Purse Mine()
            {
                PlayerScript p = PlayerScript.instance;
                if (p == null) return null;
                Purse r = new Purse { Gold = p.gold };
                foreach (InventoryItem i in p.inventory) if (i != null && i.item != null) r.Items[i.item.name] = i.amount; // ContainsItem uses the last match
                return r;
            }

            public bool Has(Item item, int amount)
            {
                int n;
                return item != null && Items.TryGetValue(item.name, out n) && n >= amount;
            }

            public static void Write(BinaryWriter w, Purse p)
            {
                w.Write(p != null);
                if (p == null) return;
                w.Write(p.Gold); w.Write(p.Items.Count);
                foreach (KeyValuePair<string, int> kv in p.Items) { w.Write(kv.Key); w.Write(kv.Value); }
            }

            public static Purse Read(BinaryReader r)
            {
                if (r.BaseStream.Position >= r.BaseStream.Length || !r.ReadBoolean()) return null;
                Purse p = new Purse { Gold = r.ReadInt32() };
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) { string k = r.ReadString(); p.Items[k] = r.ReadInt32(); }
                return p;
            }
        }

        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        public static StorySync Instance;
        static bool applying;
        // True while a story function or quest repair runs (its effects are
        // already happening on every machine).
        public static bool Applying { get { return applying; } }

        // Host
        int seq;
        readonly List<Entry> log = new List<Entry>();
        readonly Dictionary<int, int> acked = new Dictionary<int, int>();       // peer -> highest event applied
        readonly Dictionary<int, int> intentDone = new Dictionary<int, int>();  // peer -> highest intent run
        readonly Dictionary<string, float> lastRun = new Dictionary<string, float>();
        float nextResend, nextQuestState;

        // Client
        int applied;
        readonly SortedDictionary<int, Entry> buffered = new SortedDictionary<int, Entry>();
        readonly List<Pending> pending = new List<Pending>();
        int nextIntent;
        float lastStoryAt = -100f, nextIntentResend;
        string lastQuestDiff = "";

        public int EventsRun, EventsApplied, IntentsSent, IntentsRun, IntentsDeduped, Repairs;
        public int StaleRefused, RunHereFallbacks, ObjectMissing, PathsRouted;

        public StorySync(Net net, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.peers = peers;
            Instance = this;
        }

        public bool Connected { get { foreach (int p in peers()) return true; return false; } }

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
                if (target is GameManager) Ran.Add(name);
                target.SendMessage(name, options);
                return;
            }
            s.Local(name, DialogueSync.AnswerPayer, answerCtx ?? new Ctx(), null);
        }

        // The answer being clicked (OnDialogueButton): its NPC and index go with the
        // intent, so the host can refuse an answer that is no longer open there.
        static Ctx answerCtx;

        [HarmonyPatch(typeof(InterfaceManager), "OnDialogueButton")]
        static class AnswerCtxPatch
        {
            static void Prefix(InterfaceManager __instance, GameObject go)
            {
                answerCtx = null;
                try
                {
                    GameObject t = __instance.currentTalkTarget;
                    SaveableItem si = t != null ? t.GetComponent<SaveableItem>() : null;
                    answerCtx = new Ctx { Kind = Ctx.Answer, A = si != null ? si.instanceID : 0, B = Convert.ToInt32(go.name) };
                }
                catch (Exception) { answerCtx = null; }
            }
            static void Postfix() { answerCtx = null; }
        }

        // ---------- other paths (A2) ----------
        //
        // Zone triggers, usable objects, inventory items, damage switches, destructible
        // objects and enemy deaths also call story functions by name. Before mod 0.9.10
        // they ran only on the PC where they happened, so a friend's zone entry or
        // pickup never reached the host's story. Now they go through the story log too.
        // Kept local: what happens inside a world event or a generated dungeon (their
        // own sync counts it at the event's owner), enemies each PC has its own copy of,
        // and functions that only move the player who triggered them.
        // Generated by tools\atlas (op "move" without quest or world ops): doors, cave entrances, the homing stone.
        static readonly HashSet<string> MoveOnly = new HashSet<string> { "EndCutscene24", "EndCutscene24B", "StoryChapter28", "MoveAndStart31", "WaitAndStartTorture", "StoryChapter32", "EnterKingCastle", "MoveToHalluc", "WaitAndEscape", "EnterMaestroTomb", "WarriorsEpicCaravanCutscene", "EpicMageExitArcane", "EpicHolyUseObelisk", "NecroQ32", "UseNecroAccess", "EpicNecroTalk2", "EpicMerchEnter", "EnterPast", "ExitDungeonRandom", "ExitArcaneRift", "HomingStone" };
        // Every story function this PC ran (test probe of the quest runner reads and clears it).
        public static readonly List<string> Ran = new List<string>();
        static GameObject currentSource;   // the trigger/switch whose method is running
        static NPC dyingNpc;               // FighterNPCModule.Death running for this NPC
        static bool suppressDeathStory;    // killing our copy of an enemy whose death already ran here
        static bool runHereNext;           // the next intent already ran here (destructible objects)

        [HarmonyPatch]
        static class PathPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(StoryTrigger), "OnTriggerEnter");
                yield return AccessTools.Method(typeof(UsableObject), "Interact");
                yield return AccessTools.Method(typeof(PlayerScript), "UseItem");
                yield return AccessTools.Method(typeof(DestructibleThresholdObject), "Damage", new Type[] { typeof(int), typeof(NPC.DamageType), typeof(GameObject), typeof(bool) });
                yield return AccessTools.Method(typeof(DamagableSwitch), "Damage", new Type[] { typeof(int), typeof(NPC.DamageType), typeof(GameObject), typeof(bool) });
                yield return AccessTools.Method(typeof(DamagableSwitch), "Interact");
                yield return AccessTools.Method(typeof(FighterNPCModule), "Death");
            }

            static void Prefix(object __instance)
            {
                Component c = __instance as Component;
                currentSource = c != null ? c.gameObject : null;
                FighterNPCModule f = __instance as FighterNPCModule;
                dyingNpc = f != null ? f.GetComponent<NPC>() : null;
            }

            static void Postfix() { currentSource = null; dyingNpc = null; }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                MethodInfo send2 = AccessTools.Method(typeof(GameObject), "SendMessage", new Type[] { typeof(string), typeof(SendMessageOptions) });
                MethodInfo send3 = AccessTools.Method(typeof(GameObject), "SendMessage", new Type[] { typeof(string), typeof(object), typeof(SendMessageOptions) });
                MethodInfo hook2 = AccessTools.Method(typeof(StorySync), "DispatchGo");
                MethodInfo hook3 = AccessTools.Method(typeof(StorySync), "DispatchGoArg");
                int replaced = 0;
                foreach (CodeInstruction i in instructions)
                {
                    if (i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Call)
                    {
                        if (Equals(i.operand, send2)) { i.opcode = OpCodes.Call; i.operand = hook2; replaced++; }
                        else if (Equals(i.operand, send3)) { i.opcode = OpCodes.Call; i.operand = hook3; replaced++; }
                    }
                    yield return i;
                }
                if (replaced == 0) Plugin.Log.LogError("Story path hook found no SendMessage call in " + original.DeclaringType.Name + "." + original.Name);
                else Plugin.Log.LogInfo("Story path hook: " + replaced + " call(s) in " + original.DeclaringType.Name + "." + original.Name);
            }
        }

        public static void DispatchGo(GameObject target, string name, SendMessageOptions options) { Route(target, name, null, false, options); }
        public static void DispatchGoArg(GameObject target, string name, object arg, SendMessageOptions options) { Route(target, name, arg, true, options); }

        static void Pass(GameObject target, string name, object arg, bool hasArg, SendMessageOptions o)
        {
            if (GameManager.instance != null && target == GameManager.instance.gameObject) Ran.Add(name);
            if (hasArg) target.SendMessage(name, arg, o); else target.SendMessage(name, o);
        }

        static bool InLocalArea(GameObject go)
        {
            return go != null && (go.GetComponentInParent<WorldEventBase>() != null || go.GetComponentInParent<StandaloneGeneratedDungeon>() != null);
        }

        static void Route(GameObject target, string name, object arg, bool hasArg, SendMessageOptions o)
        {
            StorySync s = Instance;
            GameObject gm = GameManager.instance != null ? GameManager.instance.gameObject : null;
            if (target == null || target != gm || s == null || applying || !s.Connected || string.IsNullOrEmpty(name) || LocalOnly.Contains(name) || MoveOnly.Contains(name) || Plugin.NoFix("storypaths"))
            {
                Pass(target, name, arg, hasArg, o);
                return;
            }
            Ctx ctx;
            GameObject direct = null;
            if (dyingNpc != null)
            {
                int id;
                int role = EnemySync.StoryRole(dyingNpc, out id);
                if (suppressDeathStory) { Plugin.Log.LogInfo(Plugin.Stamp() + " Story function '" + name + "' of '" + dyingNpc.npcName + "' not run again (its death came through the story log)"); return; }
                if (InLocalArea(dyingNpc.gameObject) || !dyingNpc.gameObject.scene.IsValid()) { Pass(target, name, arg, hasArg, o); return; }
                // Not shared by EnemySync: each PC has its own copy. Its death goes through the story
                // log by its scene id; the others' copies die with it (without running it again).
                if (role == 0 && !Plugin.NoFix("storydeaths")) { id = DialogueSync.NpcId(dyingNpc.gameObject); ctx = new Ctx { Kind = Ctx.Death, A = id, B = 1 }; }
                else if (role == 0) { Pass(target, name, arg, hasArg, o); return; }
                else if (role == 2)
                {
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Story function '" + name + "' (death of enemy " + id + ") left to the enemy's owner");
                    return;
                }
                else ctx = new Ctx { Kind = Ctx.Death, A = id };
            }
            else if (hasArg)
            {
                direct = arg as GameObject;
                if (direct == null || InLocalArea(direct)) { Pass(target, name, arg, hasArg, o); return; }
                ctx = Ctx.ForObject(direct);
            }
            else
            {
                if (InLocalArea(currentSource)) { Pass(target, name, arg, hasArg, o); return; }
                ctx = new Ctx { Kind = Ctx.Plain };
            }
            s.PathsRouted++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Story function '" + name + "' from " + (currentSource != null ? currentSource.GetType().Name + " '" + currentSource.name + "'" : "-") + " (" + ctx + ") goes through the story log");
            // A destructible object is destroyed at the end of this frame: the friend who broke it
            // runs the function now (with the object), the others when the host's event comes.
            if (!s.net.IsHost && currentSource != null && currentSource.GetComponent<DestructibleThresholdObject>() != null)
            {
                Pass(target, name, arg, hasArg, o);
                runHereNext = true;
            }
            s.Local(name, s.net.LocalId, ctx, direct); // only whoever triggered it pays (items, gold)
        }

        // Most functions called by usable objects take no parameter: they run without the object.
        static readonly Dictionary<string, bool> needsObject = new Dictionary<string, bool>();
        static bool NeedsObject(string name)
        {
            bool n;
            if (needsObject.TryGetValue(name, out n)) return n;
            n = false;
            foreach (MethodInfo m in typeof(GameManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == name && m.GetParameters().Length > 0) n = true;
            needsObject[name] = n;
            return n;
        }

        // Another player killed an enemy that every PC simulates on its own: ours dies too,
        // without running its death story function a second time.
        static void KillCopy(int id, string name)
        {
            foreach (NPC n in Resources.FindObjectsOfTypeAll<NPC>())
            {
                if (n == null || n.isDead || !n.gameObject.scene.IsValid() || DialogueSync.NpcId(n.gameObject) != id) continue;
                suppressDeathStory = true;
                try { n.Death(PlayerScript.instance != null ? PlayerScript.instance.gameObject : null); Plugin.Log.LogInfo(Plugin.Stamp() + " '" + n.npcName + "' (" + id + ") died here too ('" + name + "' came through the story log)"); }
                catch (Exception e) { Plugin.Log.LogWarning("Killing our copy of " + id + " failed: " + e.Message); }
                finally { suppressDeathStory = false; }
                return;
            }
        }

        // The object a story function gets, found on this PC (save id, else name + position).
        static GameObject Resolve(Ctx ctx, GameObject direct)
        {
            if (direct != null) return direct;
            if (ctx.A != 0)
                foreach (SaveableItem si in Resources.FindObjectsOfTypeAll<SaveableItem>())
                    if (si != null && si.instanceID == ctx.A && si.gameObject.scene.IsValid()) return si.gameObject;
            GameObject best = null; float bestD = 3f;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.name != ctx.Obj || !t.gameObject.scene.IsValid()) continue;
                float d = Vector3.Distance(t.position, ctx.Pos);
                if (d < bestD) { bestD = d; best = t.gameObject; }
            }
            return best;
        }

        // A friend's answer that is no longer open on the host (another player's choice
        // closed it meanwhile): refused, the friend's dialogue follows the host's.
        static bool IsStale(Ctx ctx)
        {
            if (ctx == null || ctx.Kind != Ctx.Answer || ctx.A == 0) return false;
            foreach (DialogueNPCModule d in Resources.FindObjectsOfTypeAll<DialogueNPCModule>())
            {
                if (d == null || !d.gameObject.scene.IsValid()) continue;
                SaveableItem si = d.GetComponent<SaveableItem>();
                if (si == null || si.instanceID != ctx.A) continue;
                return d.allDialogue == null || ctx.B >= d.allDialogue.Length || !d.allDialogue[ctx.B].isActiveNow;
            }
            // Not loaded here: its saved answers (story NPCs know their module order).
            string data;
            if (SaveLoadManager.instance == null || !SaveLoadManager.instance.WorldDatabase.TryGetValue(ctx.A, out data) || GameManager.instance == null) return false;
            foreach (GameManager.storyNpcData n in GameManager.instance.defaultStoryNPCData)
            {
                if (n.saveableId != ctx.A || n.modules == null) continue;
                int m = Array.IndexOf(n.modules, SaveableItem.SaveModule.Dialogue);
                string[] parts = data.Split('/');
                if (m < 0 || parts.Length != n.modules.Length + 1) return false; // entry layout unknown (see DialogueBits)
                string[] bits = parts[m + 1].Split('|');
                return ctx.B < bits.Length && bits[ctx.B] == "0";
            }
            return false;
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

        // Escort completions that ran this session (host): a later copy arriving is dropped.
        readonly HashSet<string> escortDone = new HashSet<string>();

        // EscortSync: an escort reached the end of its path here.
        public void Escort(string name, int escortId)
        {
            Local(name, net.LocalId, new Ctx { Kind = Ctx.Escort, A = escortId }, null);
        }

        void Local(string name, int payer, Ctx ctx, GameObject direct)
        {
            // Our own click on an answer a story event (someone else's choice) closed while our
            // dialogue window still showed it.
            if (ctx.Kind == Ctx.Answer && !Plugin.NoFix("storystale") && IsStale(ctx))
            {
                StaleRefused++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Story answer '" + name + "' not run: " + ctx + " was closed by another player's choice");
                if (InterfaceManager.instance != null && InterfaceManager.instance.panels[6].activeSelf) InterfaceManager.instance.ExitDialogue();
                return;
            }
            if (net.IsHost)
            {
                float at;
                if ((lastRun.TryGetValue(ctx.Key(name), out at) && Time.realtimeSinceStartup - at < 3f) || (ctx.Kind == Ctx.Escort && escortDone.Contains(ctx.Key(name))))
                {
                    IntentsDeduped++;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Story function '" + name + "' ignored on the host: a friend's identical action just ran");
                    return;
                }
                RunAndLog(name, payer != 0 ? payer : net.LocalId, payer, ctx, direct); // a recruit goes to whoever paid
                return;
            }
            Pending p = new Pending { Id = ++nextIntent, Name = name, Payer = payer, NextSend = 0f, Ctx = ctx, Direct = direct, RanHere = runHereNext,
                Purse = payer == net.LocalId && !Plugin.NoFix("friendpurse") ? Purse.Mine() : null };
            runHereNext = false;
            pending.Add(p);
            IntentsSent++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Story intent #" + p.Id + " '" + name + "' (" + ctx + ") sent to the host");
            SendIntent(p);
        }

        void SendIntent(Pending p)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(p.Id); w.Write(p.Name); w.Write(p.Payer); p.Ctx.Write(w); Purse.Write(w, p.Purse); w.Flush(); net.SendMessage(Net.MsgStoryIntent, 0, ms.ToArray(), true); }
            p.NextSend = Time.realtimeSinceStartup + 1f;
        }

        // Intent answers: the host ran it (its event follows), someone just did the same,
        // the answer was no longer open on the host, or the host could not run it (the
        // object is not loaded there) and the friend runs it alone as before.
        const byte AckDeduped = 0, AckRan = 1, AckStale = 2, AckRunHere = 3;

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
        // Policy for answers that cost money or items: only the payer (the
        // member chosen by the conversation, or whoever answered alone) pays.
        // Everyone else runs the function as if they had it: item checks pass,
        // removals are skipped and any gold it takes is given back.
        public static bool SkipPayment;
        // Item checks of a story function: the payer's results (recorded on the host when it pays) go
        // with the event, and everyone else gets the same answers - not a blanket "yes", which let a
        // friend finish a step the payer lacked the items for (runner, BarbF42).
        static List<bool> checkRecord, checkReplay;
        static Purse checkPurse; // a paying friend's inventory: the host answers from it
        static int checkAt;
        public int PurseChecks;
        static void ItemCheck(Item item, int amount, ref bool result)
        {
            if (checkPurse != null)
            {
                result = checkPurse.Has(item, amount);
                if (Instance != null) Instance.PurseChecks++;
                if (checkRecord != null) checkRecord.Add(result);
                return;
            }
            if (checkRecord != null) { checkRecord.Add(result); return; }
            if (!SkipPayment) return;
            result = checkReplay != null && checkAt < checkReplay.Count ? checkReplay[checkAt++] : true;
        }
        public int PaymentsSkipped;

        // Returns false if the function needs an object that this PC does not have loaded.
        bool Run(string name, int number, int origin, int payer, Ctx ctx, GameObject direct, List<bool> replayChecks, List<bool> recordChecks, Purse purse = null)
        {
            GameObject arg = null;
            if (ctx != null && ctx.Kind == Ctx.Object && NeedsObject(name))
            {
                arg = Resolve(ctx, direct);
                if (arg == null)
                {
                    ObjectMissing++;
                    Plugin.Log.LogWarning(Plugin.Stamp() + " [storygap] '" + name + "' needs " + ctx + ", not loaded here; not run");
                    return false;
                }
            }
            PlayerScript player = PlayerScript.instance;
            List<CompanionNPCModule> before = player != null ? new List<CompanionNPCModule>(player.currentCOmpanions) : null;
            bool skip = payer != 0 && payer != net.LocalId && player != null;
            const int Float = 10000000;
            int gold = player != null ? player.gold : 0;
            applying = true;
            CurrentEvent = number;
            spawnIndex = 0;
            // A paying friend's purse (host only): its gold and items answer the checks, recorded for the others.
            int baseGold = skip && purse != null && purse.Gold >= 0 ? purse.Gold : gold + Float;
            if (skip) { SkipPayment = true; player.gold = baseGold; checkReplay = Plugin.NoFix("storychecks") ? null : replayChecks; checkAt = 0; if (purse != null) { checkPurse = purse; checkRecord = recordChecks; } }
            else if (recordChecks != null) checkRecord = recordChecks;
            Ran.Add(name);
            // Same random rolls on every PC: seeded by the event (name + number).
            UnityEngine.Random.State rnd = UnityEngine.Random.state;
            bool seeded = number > 0 && !Plugin.NoFix("storyrandom");
            if (seeded) UnityEngine.Random.InitState(WorldIds.Hash(name + "#" + number));
            try
            {
                if (arg != null) GameManager.instance.gameObject.SendMessage(name, arg, SendMessageOptions.DontRequireReceiver);
                else GameManager.instance.SendMessage(name, SendMessageOptions.DontRequireReceiver);
            }
            catch (Exception e) { Plugin.Log.LogError("Story function '" + name + "' failed: " + e); }
            finally
            {
                // The next roll after the function: equal on every PC if it rolled the same way.
                checkRecord = null; checkReplay = null; checkPurse = null;
                if (seeded) { Plugin.Log.LogInfo(Plugin.Stamp() + " [storyrnd] '" + name + "' #" + number + " next=" + UnityEngine.Random.Range(0, 1000000)); UnityEngine.Random.state = rnd; }
                applying = false; CurrentEvent = 0;
                if (skip)
                {
                    SkipPayment = false;
                    int change = player.gold - baseGold;
                    player.gold = gold + Mathf.Max(0, change);
                    PaymentsSkipped++;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " '" + name + "' is paid by player " + payer + "; not by us (gold change " + change + " ignored if negative)");
                }
            }
            if (ctx != null && ctx.Kind == Ctx.Death && ctx.B == 1 && origin != 0 && origin != net.LocalId) KillCopy(ctx.A, name);
            if (before == null || origin == 0 || origin == net.LocalId) return true;
            List<CompanionNPCModule> added = player.currentCOmpanions.FindAll(c => c != null && !before.Contains(c));
            foreach (CompanionNPCModule c in added)
            {
                player.currentCOmpanions.Remove(c);
                Plugin.Log.LogInfo(Plugin.Stamp() + " '" + name + "' recruited '" + (c.npc != null ? c.npc.npcName : c.name) + "' for player " + origin + "; not for us");
                UnityEngine.Object.Destroy(c.gameObject);
            }
            if (added.Count > 0 && InterfaceManager.instance != null) InterfaceManager.instance.RefreshCompanionsPanels();
            return true;
        }

        readonly Dictionary<int, int> originOf = new Dictionary<int, int>(); // event number -> player who caused it

        // ---------- host ----------

        readonly Dictionary<int, int> payerOf = new Dictionary<int, int>(); // event number -> who pays (0 = everyone)

        bool RunAndLog(string name, int origin, int payer, Ctx ctx, GameObject direct, Purse purse = null)
        {
            // An object the host does not have loaded: it can't run here.
            if (ctx.Kind == Ctx.Object && NeedsObject(name) && Resolve(ctx, direct) == null)
            {
                ObjectMissing++;
                Plugin.Log.LogWarning(Plugin.Stamp() + " [storygap] '" + name + "' needs " + ctx + ", not loaded on the host");
                return false;
            }
            seq++;
            originOf[seq] = origin;
            payerOf[seq] = payer;
            Entry e = new Entry { Seq = seq, Name = name, Ctx = ctx };
            log.Add(e);
            lastRun[ctx.Key(name)] = Time.realtimeSinceStartup;
            if (ctx.Kind == Ctx.Escort) escortDone.Add(ctx.Key(name));
            Run(name, seq, origin, payer, ctx, direct, null, payer == net.LocalId || purse != null ? e.Checks : null, purse);
            EventsRun++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Story event #" + seq + " '" + name + "' (from " + (origin == net.LocalId ? "host" : origin.ToString()) + (ctx.Kind != Ctx.None ? ", " + ctx : "") + ")");
            SendEvent(e, 0);
            return true;
        }

        void SendEvent(Entry e, int target)
        {
            int origin, payer;
            originOf.TryGetValue(e.Seq, out origin);
            payerOf.TryGetValue(e.Seq, out payer);
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(e.Seq); w.Write(e.Name); w.Write(origin); w.Write(payer); e.Ctx.Write(w); w.Write(e.Checks.Count); foreach (bool c in e.Checks) w.Write(c); w.Flush(); net.SendMessage(Net.MsgStoryEvent, target, ms.ToArray(), true); }
        }

        void HostIntent(Net.Message m)
        {
            int id, payer; string name; Ctx ctx;
            Purse purse;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload))) { id = r.ReadInt32(); name = r.ReadString(); payer = r.ReadInt32(); ctx = Ctx.Read(r); purse = Purse.Read(r); }
            if (payer != m.Sender) purse = null; // only the payer's own purse counts
            int done;
            intentDone.TryGetValue(m.Sender, out done);
            byte code = AckRan;
            if (id > done)
            {
                intentDone[m.Sender] = id;
                float at;
                // Two players doing the same thing at once: run it once.
                if ((lastRun.TryGetValue(ctx.Key(name), out at) && Time.realtimeSinceStartup - at < 3f) || (ctx.Kind == Ctx.Escort && escortDone.Contains(ctx.Key(name))))
                {
                    IntentsDeduped++;
                    code = AckDeduped;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Story intent '" + name + "' from " + m.Sender + " ignored: it just ran");
                }
                else if (!Plugin.NoFix("storystale") && IsStale(ctx))
                {
                    StaleRefused++;
                    code = AckStale;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Story intent '" + name + "' from " + m.Sender + " refused: " + ctx + " is no longer open here (another choice was made)");
                }
                else if (RunAndLog(name, payer != 0 ? payer : m.Sender, payer, ctx, null, purse)) IntentsRun++;
                else { code = AckRunHere; RunHereFallbacks++; }
            }
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(id); w.Write(code); w.Flush(); net.SendMessage(Net.MsgIntentAck, m.Sender, ms.ToArray(), true); }
        }

        void HostResend(float now)
        {
            foreach (int p in peers())
            {
                // A player seen for the first time starts at the current
                // number; the quest-state check brings them up to date.
                if (!acked.ContainsKey(p)) { acked[p] = seq; continue; }
                int sent = 0;
                foreach (Entry e in log)
                {
                    if (e.Seq <= acked[p]) continue;
                    if (sent++ >= 20) break;
                    SendEvent(e, p);
                }
            }
        }

        // ---------- client ----------

        void ClientEvent(Net.Message m, float now)
        {
            Entry e;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                e = new Entry { Seq = r.ReadInt32(), Name = r.ReadString() };
                originOf[e.Seq] = r.ReadInt32(); payerOf[e.Seq] = r.ReadInt32();
                e.Ctx = Ctx.Read(r);
                int nc = r.ReadInt32();
                for (int i = 0; i < nc; i++) e.Checks.Add(r.ReadBoolean());
            }
            if (applied == 0 && e.Seq > 1 && buffered.Count == 0) applied = e.Seq - 1; // joined mid-session
            if (e.Seq > applied) buffered[e.Seq] = e;
            Drain(now);
        }

        void Drain(float now)
        {
            if (!InWorld) return;
            while (buffered.ContainsKey(applied + 1))
            {
                Entry e = buffered[applied + 1];
                buffered.Remove(applied + 1);
                applied++;
                // Our own intent coming back: done waiting for it (and its object is at hand).
                GameObject direct = null; bool ranHere = false;
                foreach (Pending p in pending) if (p.Name == e.Name && p.Ctx.Key(p.Name) == e.Ctx.Key(e.Name)) { if (p.Direct != null) direct = p.Direct; ranHere |= p.RanHere; }
                pending.RemoveAll(p => p.Name == e.Name && p.Ctx.Key(p.Name) == e.Ctx.Key(e.Name));
                int origin, payer;
                originOf.TryGetValue(applied, out origin);
                payerOf.TryGetValue(applied, out payer);
                if (ranHere) Plugin.Log.LogInfo(Plugin.Stamp() + " Story event #" + applied + " '" + e.Name + "' already ran here when we broke the object");
                else Run(e.Name, applied, origin, payer, e.Ctx, direct, e.Checks, null);
                EventsApplied++;
                lastStoryAt = now;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Story event #" + applied + " '" + e.Name + "' applied" + (e.Ctx.Kind != Ctx.None ? " (" + e.Ctx + ")" : ""));
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
                // One digit per objective: 1 = completed, 2 = shown in the journal.
                for (int i = 0; i < bits.Length; i++) bits[i] = (char)('0' + (q.completed[i] ? 1 : 0) + (q.shownObjective != null && i < q.shownObjective.Length && q.shownObjective[i] ? 2 : 0));
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
                    int hb = h.Value[i] - '0', mb = mine[i] - '0';
                    string qn = h.Key; int obj = i;
                    if ((hb & 1) != 0 && (mb & 1) == 0)
                    {
                        diff.Add("'" + h.Key + "' objective " + i + " open");
                        Repair(() => CompleteObjective(qn, obj), "complete objective " + obj + " of '" + qn + "'");
                    }
                    else if ((hb & 1) == 0 && (mb & 1) != 0) diff.Add("'" + h.Key + "' objective " + i + " done here, open on host");
                    // The next step's line, which the game's story function shows along with it.
                    if ((hb & 2) != 0 && (mb & 2) == 0 && !Plugin.NoFix("questloop")) Repair(() => ShowObjective(qn, obj), "show objective " + obj + " of '" + qn + "'");
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

        // QuestsData.ObjectiveCompleted is only the show (tick, sparks, sound): the game's story
        // functions set completed[] themselves before calling it. Calling just that left the
        // difference in place, so the "objective done" sound came again with every state (5 s).
        static void CompleteObjective(string qn, int obj)
        {
            ObjectiveShows++;
            if (!Plugin.NoFix("questloop"))
            {
                Quest q = QuestsData.instance.GetCurrentQuestByName(qn);
                if (q == null || obj >= q.completed.Length) return;
                q.completed[obj] = true;
                InterfaceManager.instance.RefreshQuestList();
            }
            QuestsData.instance.ObjectiveCompleted(qn, obj);
        }

        static void ShowObjective(string qn, int obj)
        {
            Quest q = QuestsData.instance.GetCurrentQuestByName(qn);
            if (q == null || q.shownObjective == null || obj >= q.shownObjective.Length) return;
            q.shownObjective[obj] = true;
            InterfaceManager.instance.RefreshQuestList();
        }

        public static int ObjectiveShows;
        // A repair that didn't take must not repeat its sound and message for ever.
        readonly Dictionary<string, float> repairedAt = new Dictionary<string, float>();
        public int RepairsHeldBack;

        void Repair(Action fix, string what)
        {
            float last;
            if (repairedAt.TryGetValue(what, out last) && Time.realtimeSinceStartup - last < 60f && !Plugin.NoFix("questloop")) { RepairsHeldBack++; return; }
            repairedAt[what] = Time.realtimeSinceStartup;
            applying = true;
            try { fix(); Repairs++; Plugin.Log.LogInfo(Plugin.Stamp() + " [quests] repaired: " + what); }
            catch (Exception e) { Plugin.Log.LogError("Quest repair '" + what + "' failed: " + e); }
            finally { applying = false; }
        }

        // ---------- dialogue digest ----------
        //
        // Which answers every story NPC has open is story state too. The host's world-state
        // sync only sends entries the host changed, so an answer switched differently on a
        // friend's PC (a story function that took another branch there, a path that ran only
        // there, a late join) stayed that way (quest runner: 13 DIFF steps). The host sends the
        // open answers of all story NPCs every 10 s; the friend takes them when no story event
        // is in flight.
        float nextDialogues;
        public int DialogueRepairs;

        static Dictionary<int, DialogueNPCModule> LoadedTalkers()
        {
            Dictionary<int, DialogueNPCModule> d = new Dictionary<int, DialogueNPCModule>();
            foreach (DialogueNPCModule x in Resources.FindObjectsOfTypeAll<DialogueNPCModule>())
            {
                if (x == null || !x.gameObject.scene.IsValid()) continue;
                SaveableItem si = x.GetComponent<SaveableItem>();
                if (si != null && si.instanceID != 0) d[si.instanceID] = x;
            }
            return d;
        }

        // "1|0|1|" for an NPC: from the loaded NPC, else its saved entry, else the game's default.
        static string DialogueBits(GameManager.storyNpcData n, Dictionary<int, DialogueNPCModule> loaded)
        {
            DialogueNPCModule d;
            if (loaded.TryGetValue(n.saveableId, out d) && d.allDialogue != null)
            {
                StringBuilder b = new StringBuilder();
                foreach (DialogiePoint p in d.allDialogue) b.Append(p.isActiveNow ? "1|" : "0|");
                return b.ToString();
            }
            int m = n.modules != null ? Array.IndexOf(n.modules, SaveableItem.SaveModule.Dialogue) : -1;
            if (m < 0) return null;
            string data;
            if (!SaveLoadManager.instance.WorldDatabase.TryGetValue(n.saveableId, out data)) data = n.data;
            if (data == null || data == "0") return null;
            string[] parts = data.Split('/');
            // Entry = header "1|<active>" then one part per module. An entry of another length is not
            // read (or patched); such NPCs are compared once loaded (a repair that never sticks repeats).
            return parts.Length == n.modules.Length + 1 ? parts[m + 1] : null; // part 0 is the header "1|<active>"
        }

        // "1|0||" and "1|0|" mean the same answers.
        static string Norm(string bits)
        {
            List<string> l = new List<string>();
            foreach (string x in bits.Split('|')) if (x == "0" || x == "1") l.Add(x);
            return string.Join("|", l.ToArray());
        }

        void SendDialogues()
        {
            if (GameManager.instance == null || GameManager.instance.defaultStoryNPCData == null) return;
            Dictionary<int, DialogueNPCModule> loaded = LoadedTalkers();
            List<KeyValuePair<int, string>> all = new List<KeyValuePair<int, string>>();
            foreach (GameManager.storyNpcData n in GameManager.instance.defaultStoryNPCData)
            {
                string bits = DialogueBits(n, loaded);
                if (bits != null) all.Add(new KeyValuePair<int, string>(n.saveableId, bits));
            }
            for (int start = 0; start < all.Count; start += 80)
            {
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    int n = Math.Min(80, all.Count - start);
                    w.Write(seq); w.Write(n);
                    for (int i = start; i < start + n; i++) { w.Write(all[i].Key); w.Write(all[i].Value); }
                    w.Flush();
                    net.SendMessage(Net.MsgDialogueDigest, 0, ms.ToArray(), false);
                }
            }
        }

        void ClientDialogues(Net.Message m, float now)
        {
            if (!InWorld || GameManager.instance == null || GameManager.instance.defaultStoryNPCData == null) return;
            int hostSeq;
            List<KeyValuePair<int, string>> host = new List<KeyValuePair<int, string>>();
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                hostSeq = r.ReadInt32();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) host.Add(new KeyValuePair<int, string>(r.ReadInt32(), r.ReadString()));
            }
            if (hostSeq != applied || buffered.Count > 0 || pending.Count > 0 || now - lastStoryAt < 3f) return;
            Dictionary<int, GameManager.storyNpcData> byId = new Dictionary<int, GameManager.storyNpcData>();
            foreach (GameManager.storyNpcData n in GameManager.instance.defaultStoryNPCData) byId[n.saveableId] = n;
            Dictionary<int, DialogueNPCModule> loaded = LoadedTalkers();
            List<string> fixedNames = new List<string>();
            foreach (KeyValuePair<int, string> h in host)
            {
                GameManager.storyNpcData n;
                if (!byId.TryGetValue(h.Key, out n)) continue;
                string mine = DialogueBits(n, loaded);
                if (mine == null || Norm(mine) == Norm(h.Value)) continue;
                string[] bits = h.Value.Split('|');
                DialogueNPCModule d;
                if (loaded.TryGetValue(h.Key, out d) && d.allDialogue != null)
                    for (int i = 0; i < d.allDialogue.Length && i < bits.Length; i++)
                        if (bits[i] == "0" || bits[i] == "1") d.allDialogue[i].isActiveNow = bits[i] == "1";
                // The saved entry with the host's dialogue part (our position, health etc. stay).
                int mi = Array.IndexOf(n.modules, SaveableItem.SaveModule.Dialogue);
                string data;
                if (!SaveLoadManager.instance.WorldDatabase.TryGetValue(h.Key, out data) || data == "0") data = n.data;
                if (data != null && mi >= 0)
                {
                    string[] parts = data.Split('/');
                    if (parts.Length == n.modules.Length + 1) { parts[mi + 1] = h.Value; SaveLoadManager.instance.WorldDatabase[h.Key] = string.Join("/", parts); }
                }
                DialogueRepairs++;
                fixedNames.Add(d != null ? d.name : h.Key.ToString());
            }
            if (fixedNames.Count > 0)
                Plugin.Log.LogInfo(Plugin.Stamp() + " [dialogues] " + fixedNames.Count + " story NPC(s) had other open answers than the host; taken: " + string.Join(", ", fixedNames.ToArray()));
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
                                    byte code = r.ReadByte();
                                    // Ran: its numbered event is on the way (or here); stop resending
                                    // and wait for it by name. Deduped (someone else just did it): that
                                    // event already reached us. Stale: the host's choice stands.
                                    // Run here: the host lacks the object; we run it alone as before.
                                    if (code == AckRan) { foreach (Pending p in pending) if (p.Id <= id) p.NextSend = float.MaxValue; }
                                    else
                                    {
                                        Pending mine = pending.Find(p => p.Id == id);
                                        pending.RemoveAll(p => p.Id == id);
                                        if (mine != null && code == AckStale)
                                        {
                                            StaleRefused++;
                                            Plugin.Log.LogInfo(Plugin.Stamp() + " Story intent #" + id + " '" + mine.Name + "' refused by the host: " + mine.Ctx + " is closed there");
                                            if (InterfaceManager.instance != null && InterfaceManager.instance.panels[6].activeSelf) InterfaceManager.instance.ExitDialogue();
                                        }
                                        else if (mine != null && code == AckRunHere && !mine.RanHere)
                                        {
                                            RunHereFallbacks++;
                                            Plugin.Log.LogInfo(Plugin.Stamp() + " Story intent #" + id + " '" + mine.Name + "': the host can't run it; running it here only");
                                            Run(mine.Name, 0, net.LocalId, mine.Payer, mine.Ctx, mine.Direct, null, null);
                                        }
                                    }
                                }
                            break;
                        case Net.MsgQuestState: if (!net.IsHost) ClientQuestState(m, now); break;
                        case Net.MsgDialogueDigest: if (!net.IsHost) ClientDialogues(m, now); break;
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
                if (InWorld && Connected && now >= nextDialogues && !Plugin.NoFix("dialoguedigest")) { nextDialogues = now + 10f; SendDialogues(); }
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
            string paths = " paths=" + PathsRouted + " stale=" + StaleRefused + " runHere=" + RunHereFallbacks + " objMissing=" + ObjectMissing + " purseChecks=" + PurseChecks + " escorts=" + EscortSync.Completions;
            return net.IsHost
                ? "story log=" + seq + " run=" + EventsRun + " fromFriends=" + IntentsRun + " deduped=" + IntentsDeduped + paths
                : "story applied=" + applied + " waiting=" + buffered.Count + " intents=" + IntentsSent + " pending=" + pending.Count + " repairs=" + Repairs + " dialogueRepairs=" + DialogueRepairs + paths;
        }

        // Test helpers: the same path a dialogue answer takes, and a change
        // made behind the sync's back (to exercise the repair).
        [HarmonyPatch(typeof(PlayerScript), "ContainsItem", new Type[] { typeof(Item) })]
        static class ContainsPatch
        {
            static void Postfix(Item item, ref bool __result) { ItemCheck(item, 0, ref __result); }
        }

        [HarmonyPatch(typeof(PlayerScript), "ContainsItem", new Type[] { typeof(Item), typeof(int) })]
        static class ContainsAmountPatch
        {
            static void Postfix(Item item, int amount, ref bool __result) { ItemCheck(item, amount, ref __result); }
        }

        [HarmonyPatch]
        static class RemovePatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (MethodInfo m in typeof(PlayerScript).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    if (m.Name == "RemoveItemFromInventory") yield return m;
            }

            static bool Prefix() { return !SkipPayment; }
        }

        public static void TestAnswer(string name)
        {
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: dialogue answer '" + name + "'");
            Dispatch(GameManager.instance, name, SendMessageOptions.DontRequireReceiver);
        }

        // Test: an objective done behind the sync's back, as a story function reached by a path
        // that runs on one PC only (zone entry, item pickup): questobj:<quest>:<objective>.
        public static void TestUnsyncedObjective(string step)
        {
            string[] a = step.Split(':');
            int obj = int.Parse(a[2]);
            Quest q = QuestsData.instance.GetCurrentQuestByName(a[1]);
            if (q == null) { Plugin.Log.LogInfo(Plugin.Stamp() + " Test: no quest '" + a[1] + "'"); return; }
            q.completed[obj] = true;
            if (obj + 1 < q.shownObjective.Length) q.shownObjective[obj + 1] = true;
            InterfaceManager.instance.RefreshQuestList();
            QuestsData.instance.ObjectiveCompleted(a[1], obj);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: objective " + obj + " of '" + a[1] + "' done without sync");
        }

        // Test: the quest as this PC has it, and how often the objective show (with its sound) ran from repairs.
        public string TestQuestState(string quest)
        {
            Quest q = QuestsData.instance.GetCurrentQuestByName(quest);
            string s = q == null ? "no quest" : "completed=" + string.Join(",", Array.ConvertAll(q.completed, b => b ? "1" : "0")) + " shown=" + string.Join(",", Array.ConvertAll(q.shownObjective, b => b ? "1" : "0"));
            return "'" + quest + "' " + s + " objectiveShowsFromRepairs=" + ObjectiveShows + " repairs=" + Repairs + " heldBack=" + RepairsHeldBack;
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
