using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GedoniaCoop
{
    // Test-only quest runner (player bots T2). Walks quests step by step on two
    // copies with the game's own verbs and checks after each step that both PCs
    // ran the step's story function and have the same quest and world state.
    //
    // Plan: BepInEx\quest-plan.tsv (tools\atlas\build-atlas.ps1 writes it from the
    // atlas): quest, step, verb, argument, position, function, save ids it touches.
    // Verbs: answer <npc>:<k> (talk, click answer k), use <object> (UsableObject.Interact),
    // zone <object> (StoryTrigger.OnTriggerEnter with the player), kill <npc> (NPC.Damage),
    // item <databaseId> (PlayerScript.UseItem), smash <object> (destructible damage), skip.
    //
    // The host drives (-coop-questrun "<quest>;<quest>" or "all", optional -coop-questrun-from <n>):
    // both players go to the step's spot, one of them acts (host on even steps, the
    // friend on odd ones; -coop-questrun-actor host|client fixes it), and 10 s later
    // both write a [qrun] probe line. scripts\questrun-report.ps1 pairs the lines.
    public class QuestRunner
    {
        class Step { public int Index; public string Quest, Name, Verb, Arg, Fn; public Vector3 Pos; public bool HasPos; public int[] Ids = new int[0]; }

        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        readonly List<Step> plan = new List<Step>();
        readonly List<Step> todo = new List<Step>();
        int cursor = -1, phase;
        float nextAt;
        string actorMode;
        readonly HashSet<int> seenOps = new HashSet<int>();
        public const byte OpGoto = 1, OpAct = 2, OpProbe = 3, OpPrep = 4, OpDone = 5;
        string world;
        float worldSince = -1f;

        public QuestRunner(Net net, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.peers = peers;
            Load();
            string[] args = Environment.GetCommandLineArgs();
            string which = Plugin.Arg(args, "-coop-questrun");
            actorMode = Plugin.Arg(args, "-coop-questrun-actor") ?? "alternate";
            world = Plugin.Arg(args, "-coop-questrun-world"); // e.g. NorthernKingdoms: start once both are there
            if (which == null || !net.IsHost) return;
            HashSet<string> wanted = new HashSet<string>(which.Replace('_', ' ').Split(';'));
            int from = 0; int.TryParse(Plugin.Arg(args, "-coop-questrun-from") ?? "0", out from);
            int max = int.MaxValue; int.TryParse(Plugin.Arg(args, "-coop-questrun-max") ?? "", out max); if (max <= 0) max = int.MaxValue;
            List<string> quests = new List<string>();
            foreach (Step s in plan) if ((which == "all" || wanted.Contains(s.Quest)) && !quests.Contains(s.Quest)) quests.Add(s.Quest);
            if (from > 0) quests.RemoveRange(0, Math.Min(from, quests.Count));
            if (quests.Count > max) quests.RemoveRange(max, quests.Count - max);
            foreach (Step s in plan) if (quests.Contains(s.Quest)) todo.Add(s);
            Plugin.Log.LogInfo("[qrun] plan: " + plan.Count + " steps; running " + todo.Count + " steps of " + quests.Count + " quests: " + string.Join("; ", quests.ToArray()));
        }

        void Load()
        {
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "quest-plan.tsv");
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] c = lines[i].Split('\t');
                if (c.Length < 7) continue;
                Step s = new Step { Index = plan.Count, Quest = c[0], Name = c[1], Verb = c[2], Arg = c[3], Fn = c[5] };
                string[] p = c[4].Split(';');
                if (p.Length == 3)
                {
                    s.Pos = new Vector3(F(p[0]), F(p[1]), F(p[2]));
                    s.HasPos = true;
                }
                if (c.Length > 6 && c[6].Length > 0) s.Ids = Array.ConvertAll(c[6].Split(','), x => int.Parse(x));
                plan.Add(s);
            }
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        void Send(byte op, int step)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms)) { w.Write(op); w.Write(step); w.Flush(); net.SendMessage(Net.MsgQuestRun, 0, ms.ToArray(), true); }
        }

        public void Tick(PlayerScript player, float now, bool inWorld)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgQuestRun || net.IsHost) continue;
                byte op; int step;
                using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload))) { op = r.ReadByte(); step = r.ReadInt32(); }
                if (!inWorld || player == null) continue;
                // Each op once (the friend saw some messages twice, see notes).
                if (!seenOps.Add(op * 100000 + step) && op != OpGoto) continue; // kept as a guard; the cause (a second peer entry on the host) is fixed in Net.Poll
                if (step < 0 || step >= plan.Count) { if (op == OpDone) Plugin.Log.LogInfo("[qrun] done (host)"); continue; }
                try { Do(op, plan[step], player); }
                catch (Exception e) { Plugin.Log.LogError("[qrun] step " + step + " op " + op + " failed: " + e); }
            }
            if (!net.IsHost || todo.Count == 0 || !inWorld || player == null || now < nextAt) return;
            if (cursor < 0)
            {
                if (!Connected) { nextAt = now + 2f; return; }
                if (world != null && (GameManager.instance == null || GameManager.instance.currentLoadedWorld != world || !Plugin.FriendsInWorld(world))) { nextAt = now + 2f; return; }
                if (world != null && worldSince < 0f) { worldSince = now; nextAt = now + 20f; return; } // let the new world settle
                cursor = 0; phase = 0;
            }
            if (cursor >= todo.Count)
            {
                Send(OpDone, -1);
                Plugin.Log.LogInfo(Plugin.Stamp() + " [qrun] done: " + todo.Count + " steps");
                todo.Clear();
                return;
            }
            Step s = todo[cursor];
            bool hostActs = actorMode == "host" || (actorMode == "alternate" && cursor % 2 == 0);
            try
            {
                switch (phase)
                {
                    case 0:
                        StorySync.Ran.Clear();
                        Plugin.Log.LogInfo(Plugin.Stamp() + " [qrun] step " + s.Index + " '" + s.Quest + "' " + s.Name + ": " + s.Verb + " " + s.Arg + " (" + s.Fn + ") by " + (hostActs ? "host" : "client"));
                        if (s.Verb == "skip") { Probe(s, "SKIP " + s.Arg); Send(OpProbe, s.Index); cursor++; nextAt = now + 1f; return; }
                        Do(OpGoto, s, player); Send(OpGoto, s.Index);
                        phase = 1; nextAt = now + 7f; // cells load around both
                        return;
                    case 1:
                        Do(OpPrep, s, player); Send(OpPrep, s.Index); // once the NPC is loaded on both
                        phase = 2; nextAt = now + 1.5f;
                        return;
                    case 2:
                        if (hostActs) Do(OpAct, s, player); else Send(OpAct, s.Index);
                        phase = 3; nextAt = now + 14f; // a dialogue digest (10 s) lies in between
                        return;
                    case 3:
                        Probe(s, null); Send(OpProbe, s.Index);
                        if (InterfaceManager.instance != null && InterfaceManager.instance.panels[6].activeSelf) InterfaceManager.instance.ExitDialogue();
                        cursor++; phase = 0; nextAt = now + 2f;
                        return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[qrun] step " + s.Index + " failed: " + e);
                cursor++; phase = 0; nextAt = now + 2f;
            }
        }

        void Do(byte op, Step s, PlayerScript player)
        {
            switch (op)
            {
                case OpGoto:
                    if (!s.HasPos) return;
                    Vector3 to = s.Pos + (net.IsHost ? new Vector3(1.5f, 0.5f, 0f) : new Vector3(-1.5f, 0.5f, 0f));
                    if (player._char != null) player._char.enabled = false;
                    player.transform.position = to;
                    if (player._char != null) player._char.enabled = true;
                    break;
                case OpPrep:
                    // Test setup: the answer the step needs is open (the save may be at another point of the story).
                    if (s.Verb != "answer") return;
                    // Stat, skill and money gates (one run sees one branch; the runner takes the gated one).
                    if (player.strengthBasic < 10) player.strengthBasic = 10;
                    if (player.agilityBasic < 10) player.agilityBasic = 10;
                    if (player.intellectBasic < 10) player.intellectBasic = 10;
                    if (player.charismaBasic < 10) player.charismaBasic = 10;
                    for (int i = 0; i < player.skills.Length; i++) if (player.skills[i] < 10) player.skills[i] = 10;
                    if (player.gold < 5000) player.gold = 5000;
                    int k;
                    DialogueNPCModule d = NpcOf(s, out k);
                    if (d != null && k < d.allDialogue.Length && !d.allDialogue[k].isActiveNow)
                    {
                        d.allDialogue[k].isActiveNow = true;
                        d.SendMessage("UpdateDatabase", false, SendMessageOptions.DontRequireReceiver);
                        Plugin.Log.LogInfo("[qrun] prep: answer " + k + " of '" + d.name + "' opened for the test");
                    }
                    break;
                case OpAct: Act(s, player); break;
                case OpProbe: Probe(s, s.Verb == "skip" ? "SKIP " + s.Arg : null); break;
            }
        }

        // answer argument: <npc id>:<answer>[|<object name>]; the name helps when the id (a scene path) differs here.
        static DialogueNPCModule NpcOf(Step s, out int k)
        {
            string[] parts = s.Arg.Split('|');
            string[] a = parts[0].Split(':');
            k = int.Parse(a[1]);
            DialogueNPCModule d = FindNpc(int.Parse(a[0]));
            if (d == null && parts.Length > 1) { GameObject g = FindNamed(parts[1], s.Pos, s.HasPos); d = g != null ? g.GetComponent<DialogueNPCModule>() : null; }
            return d;
        }

        static DialogueNPCModule FindNpc(int id)
        {
            foreach (DialogueNPCModule d in Resources.FindObjectsOfTypeAll<DialogueNPCModule>())
                if (d != null && d.gameObject.scene.IsValid() && DialogueSync.NpcId(d.gameObject) == id) return d;
            return null;
        }

        static GameObject FindNamed(string name, Vector3 near, bool hasPos, float radius = 6f)
        {
            GameObject best = null; float bestD = hasPos ? radius : float.MaxValue;
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.name != name || !t.gameObject.scene.IsValid()) continue;
                float dd = hasPos ? Vector3.Distance(t.position, near) : 0f;
                if (dd < bestD) { bestD = dd; best = t.gameObject; }
            }
            return best;
        }

        // Test step qclick:<npc id>:<answer>|<object name>[;y;z]: talk to the NPC and click the answer (same as a runner step).
        public static void TestClick(PlayerScript player, string arg)
        {
            Step s = new Step { Index = -1, Quest = "-", Name = "-", Verb = "answer", Arg = arg, Fn = "" };
            Act(s, player);
        }

        static void Act(Step s, PlayerScript player)
        {
            string what;
            switch (s.Verb)
            {
                case "answer":
                {
                    int k;
                    DialogueNPCModule d = NpcOf(s, out k);
                    if (d == null) { what = "NPC " + s.Arg + " not loaded"; break; }
                    player.lockedInDialogue = false;
                    try { d.StartTalk(); } catch (Exception e) { Plugin.Log.LogWarning("[qrun] StartTalk of '" + d.name + "' failed: " + e.Message); }
                    if (!InterfaceManager.instance.panels[6].activeSelf) { try { d.Talk(); } catch (Exception e) { Plugin.Log.LogWarning("[qrun] Talk failed: " + e.Message); } }
                    InterfaceManager.instance.currentTalkTarget = d.gameObject;
                    // The answer's button (the game reads its index from the name).
                    GameObject button = new GameObject(k.ToString());
                    try { InterfaceManager.instance.OnDialogueButton(button); }
                    finally { UnityEngine.Object.Destroy(button); }
                    what = "clicked answer " + k + " of '" + d.name + "'";
                    break;
                }
                case "use":
                {
                    GameObject g = FindNamed(s.Arg, s.Pos, s.HasPos);
                    UsableObject u = g != null ? g.GetComponent<UsableObject>() : null;
                    if (u == null) { what = "usable '" + s.Arg + "' not loaded"; break; }
                    u.Interact();
                    what = "used '" + u.name + "'";
                    break;
                }
                case "zone":
                {
                    GameObject g = FindNamed(s.Arg, s.Pos, s.HasPos);
                    StoryTrigger t = g != null ? g.GetComponent<StoryTrigger>() : null;
                    if (t == null) { what = "trigger '" + s.Arg + "' not loaded"; break; }
                    Collider pc = player.GetComponent<Collider>();
                    if (player._char != null) player._char.enabled = false;
                    player.transform.position = t.transform.position;
                    if (player._char != null) player._char.enabled = true;
                    t.OnTriggerEnter(pc != null ? pc : player._char);
                    what = "entered '" + t.name + "'";
                    break;
                }
                case "kill":
                {
                    NPC n = null;
                    foreach (NPC x in Resources.FindObjectsOfTypeAll<NPC>())
                        if (x != null && x.gameObject.scene.IsValid() && DialogueSync.NpcId(x.gameObject).ToString() == s.Arg.Split('|')[0]) { n = x; break; }
                    if (n == null) { string[] kp = s.Arg.Split('|'); GameObject g = kp.Length > 1 ? FindNamed(kp[1], s.Pos, s.HasPos, 60f) : null; n = g != null ? g.GetComponent<NPC>() : null; }
                    if (n == null || n.isDead) { what = "enemy '" + s.Arg + "' " + (n == null ? "not loaded" : "already dead"); break; }
                    n.Damage(n.health + 100000, NPC.DamageType.Melee, player.gameObject, false, KnockPower.None, false, false);
                    what = "killed '" + n.npcName + "'";
                    break;
                }
                case "item":
                {
                    UsableItem item = null;
                    foreach (UsableItem u in ItemsData.instance.usableItemsData) if (u != null && u.databaseId.ToString() == s.Arg) item = u;
                    if (item == null) { what = "item " + s.Arg + " unknown"; break; }
                    player.UseItem(new InventoryItem { item = item, amount = 1, additionalData = "" });
                    what = "used item '" + item.name + "'";
                    break;
                }
                case "smash":
                {
                    GameObject g = FindNamed(s.Arg, s.Pos, s.HasPos);
                    DestructibleThresholdObject o = g != null ? g.GetComponent<DestructibleThresholdObject>() : null;
                    if (o == null) { what = "destructible '" + s.Arg + "' not loaded"; break; }
                    o.Damage(o.health + 1000, NPC.DamageType.Melee, player.gameObject, false);
                    what = "smashed '" + o.name + "'";
                    break;
                }
                default: what = "verb '" + s.Verb + "' not supported"; break;
            }
            Plugin.Log.LogInfo(Plugin.Stamp() + " [qrun] act " + s.Index + ": " + what);
        }

        // [qrun] probe <step> fn=<ran?> quest=<state> wdb=<hash> ran=<functions run here since the step began>
        void Probe(Step s, string note)
        {
            string state = "none";
            Quest q = QuestsData.instance.GetCurrentQuestByName(s.Quest);
            if (q != null)
            {
                char[] bits = new char[q.completed.Length];
                for (int i = 0; i < bits.Length; i++) bits[i] = q.completed[i] ? '1' : '0';
                state = "active:" + new string(bits);
            }
            else
            {
                Quest[] db = QuestsData.instance.questDatabase;
                foreach (int i in GameManager.instance.journalledQuests) if (i >= 0 && i < db.Length && db[i] != null && db[i].questName == s.Quest) state = "journal";
            }
            uint h = 2166136261;
            StringBuilder vals = new StringBuilder();
            foreach (int id in s.Ids)
            {
                string v;
                string text = SaveLoadManager.instance.WorldDatabase.TryGetValue(id, out v) ? DialogueBits(id, v) : "-";
                foreach (char c in id + "=" + text + ";") { h ^= c; h *= 16777619; }
                vals.Append(id + "=" + text + ";");
            }
            bool ran = StorySync.Ran.Contains(s.Fn);
            string ranList = string.Join(",", StorySync.Ran.ToArray());
            if (ranList.Length > 300) ranList = ranList.Substring(0, 300) + "...";
            Plugin.Log.LogInfo(Plugin.Stamp() + " [qrun] probe " + s.Index + " quest='" + s.Quest + "' step='" + s.Name + "' fn=" + s.Fn + " fnRan=" + (ran ? 1 : 0) +
                " state=" + state + " wdb=" + h.ToString("x8") + " ran=" + ranList + (note != null ? " note=" + note : ""));
            Plugin.Log.LogInfo("[qrunv] " + s.Index + " " + (vals.Length > 600 ? vals.ToString(0, 600) + "..." : vals.ToString()));
            StorySync.Ran.Clear();
        }

        // World entries compared without positions and timers (an NPC that walks differs on each PC).
        static string DialogueBits(int id, string v)
        {
            // Story NPCs: only their open answers (health and the like may differ).
            if (GameManager.instance != null && GameManager.instance.defaultStoryNPCData != null && v != "0")
                foreach (GameManager.storyNpcData n in GameManager.instance.defaultStoryNPCData)
                {
                    if (n.saveableId != id || n.modules == null) continue;
                    string[] ps = v.Split('/');
                    // The entry is written in the scene object's module order, which can differ
                    // from GameManager's default list: prefer the loaded object's modules.
                    SaveableItem.SaveModule[] mods = n.modules;
                    foreach (SaveableItem si in Resources.FindObjectsOfTypeAll<SaveableItem>())
                        if (si != null && si.instanceID == id && si.gameObject.scene.IsValid() && si.modules != null) { mods = si.modules; break; }
                    if (mods.Length == 0) break; // an object only switched on/off (header only): compare the raw entry
                    if (mods.Length + 1 != ps.Length) return "answers:? (layout " + ps.Length + "/" + mods.Length + ")";
                    int m = Array.IndexOf(mods, SaveableItem.SaveModule.Dialogue);
                    if (m < 0) break;
                    List<string> b = new List<string>();
                    foreach (string x in ps[m + 1].Split('|')) if (x == "0" || x == "1") b.Add(x);
                    return "answers:" + string.Join("", b.ToArray());
                }
            string[] parts = v.Split('/');
            List<string> keep = new List<string>();
            foreach (string p in parts) if (p.IndexOf('(') < 0) keep.Add(p);
            return string.Join("/", keep.ToArray());
        }
    }
}
