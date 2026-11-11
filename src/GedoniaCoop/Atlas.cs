using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Test-only world atlas (player bots T1): who in the world calls which
    // story function, and where.
    //   atlasdump        writes what is loaded now
    //   atlastour[:n]    teleports through the streamed world cell by cell
    //                    (first n stops) and dumps at every stop
    // Output: BepInEx\atlas\*.tsv (npcs, answers, triggers, saveables, quests),
    // rewritten after every stop. tools\atlas\build-atlas.ps1 joins it with the
    // static parse of GameManager.
    public static class Atlas
    {
        static readonly Dictionary<string, string> npcs = new Dictionary<string, string>();
        static readonly Dictionary<string, string> answers = new Dictionary<string, string>();
        static readonly Dictionary<string, string> triggers = new Dictionary<string, string>();
        static readonly Dictionary<string, string> saveables = new Dictionary<string, string>();
        static readonly List<string> quests = new List<string>();
        static List<Vector3> stops;
        static int stop;
        static float nextAt;
        static Vector3 hold;
        public static bool Touring { get { return stops != null && stop <= stops.Count; } }
        const float Wait = 7f;

        static string Dir { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "atlas"); } }

        static string P(Vector3 v) { return v.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ";" + v.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ";" + v.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture); }
        static string T(string s) { return s == null ? "" : s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }

        static int IdOf(Component c)
        {
            SaveableItem s = c.GetComponentInParent<SaveableItem>();
            if (s != null && s.instanceID != 0 && (s.gameObject == c.gameObject || c.GetComponent<SaveableItem>() == s)) return s.instanceID;
            return WorldIds.Of(c.transform, c.gameObject.scene.name);
        }

        static IEnumerable<T2> Loaded<T2>() where T2 : Component
        {
            foreach (T2 x in Resources.FindObjectsOfTypeAll<T2>())
                if (x != null && x.gameObject.scene.IsValid()) yield return x;
        }

        public static void Dump(string where)
        {
            int before = npcs.Count + triggers.Count + saveables.Count;
            string scene = SceneManager.GetActiveScene().name;
            foreach (DialogueNPCModule d in Loaded<DialogueNPCModule>())
            {
                int id = IdOf(d);
                NPC npc = d.GetComponent<NPC>();
                string key = id.ToString();
                bool excl = npc != null && npc.exclamationQuestMark != null && npc.exclamationQuestMark.activeSelf;
                bool ans = npc != null && npc.answerQuestmark != null && npc.answerQuestmark.activeSelf;
                npcs[key] = id + "\t" + T(d.gameObject.name) + "\t" + T(npc != null ? npc.npcName : "") + "\t" + P(d.transform.position) + "\t" + d.gameObject.scene.name + "\t" + scene +
                    "\t" + (d.gameObject.activeInHierarchy ? 1 : 0) + "\t" + (excl ? "!" : "") + (ans ? "?" : "") + "\t" + (d.GetComponent<FighterNPCModule>() != null ? "fighter" : "") + (d.GetComponent<MerchantNPCModule>() != null ? "merchant" : "");
                if (d.allDialogue == null) continue;
                for (int k = 0; k < d.allDialogue.Length; k++)
                {
                    DialogiePoint a = d.allDialogue[k];
                    StringBuilder req = new StringBuilder();
                    if (a.requirements != null)
                        foreach (answerRequirement r in a.requirements)
                            req.Append(r.type + "=" + (r.data != null ? string.Join(",", Array.ConvertAll(r.data, x => x.ToString())) : "") + " ");
                    answers[id + "/" + k] = id + "\t" + k + "\t" + (a.isActiveNow ? 1 : 0) + "\t" + a.answerType + "\t" + (a.isReturn ? 1 : 0) + "\t" + T(a.storyMessage) +
                        "\t" + (a.followingQuestions != null ? string.Join(",", Array.ConvertAll(a.followingQuestions, x => x.ToString())) : "") + "\t" + T(req.ToString().Trim()) +
                        "\t" + T(a.playerQuestionText != null && a.playerQuestionText.Length > 80 ? a.playerQuestionText.Substring(0, 80) : a.playerQuestionText);
                }
            }
            foreach (StoryTrigger x in Loaded<StoryTrigger>()) Trig("StoryTrigger", x, x.message, x.sendGameobject ? "go" : "");
            foreach (UsableObject x in Loaded<UsableObject>()) Trig("UsableObject", x, x.message, "go");
            foreach (PutInSwitch x in Loaded<PutInSwitch>()) Trig("PutInSwitch", x, x.message, "put=" + x.objectToPut + (x.sendToGameManager ? " gm" : ""));
            foreach (DamagableSwitch x in Loaded<DamagableSwitch>()) Trig("DamagableSwitch", x, x.message, x.sendTo != null ? "to=" + x.sendTo.name : "gm");
            foreach (DestructibleThresholdObject x in Loaded<DestructibleThresholdObject>()) if (x.sendMessage) Trig("Destructible", x, x.message, x.sendTo != null ? "to=" + x.sendTo.name : "gm");
            foreach (EscortNPCModule x in Loaded<EscortNPCModule>()) Trig("Escort", x, x.completePathMessage, "");
            foreach (WorldEventBase x in Loaded<WorldEventBase>()) Trig("WorldEvent", x, x.finishMessage, "");
            foreach (TimelineCutsceneController x in Loaded<TimelineCutsceneController>()) Trig("CutsceneExit", x, x.exitMessage, "");
            foreach (FighterNPCModule x in Loaded<FighterNPCModule>())
            {
                if (string.IsNullOrEmpty(x.storyMessage)) continue;
                NPC n = x.GetComponent<NPC>();
                Trig("Death", x, x.storyMessage, (n != null ? T(n.npcName) : "") + (n != null && n.isDead ? " dead" : ""));
            }
            if (ItemsData.instance != null && ItemsData.instance.usableItemsData != null)
                foreach (UsableItem u in ItemsData.instance.usableItemsData)
                    if (u != null && !string.IsNullOrEmpty(u.message))
                        triggers["ItemUse/" + u.databaseId] = "ItemUse\t" + T(u.message) + "\t" + u.databaseId + "\t" + T(u.name) + "\t\t\t\t1\tinventory";
            foreach (SaveableItem s in Loaded<SaveableItem>())
            {
                if (s.instanceID == 0) continue;
                saveables[s.instanceID.ToString()] = s.instanceID + "\t" + T(s.gameObject.name) + "\t" + P(s.transform.position) + "\t" + s.gameObject.scene.name + "\t" + scene +
                    "\t" + (s.gameObject.activeSelf ? 1 : 0) + "\t" + (s.modules != null ? string.Join(",", Array.ConvertAll(s.modules, m => m.ToString())) : "");
            }
            if (quests.Count == 0 && QuestsData.instance != null)
            {
                Quest[] db = QuestsData.instance.questDatabase;
                for (int i = 0; i < db.Length; i++)
                    if (db[i] != null)
                        quests.Add(i + "\t" + T(db[i].questName) + "\t" + (db[i].objectiveText != null ? db[i].objectiveText.Length : 0) + "\t" + db[i].level + "\t" +
                            (db[i].objectiveText != null ? string.Join(" || ", Array.ConvertAll(db[i].objectiveText, T)) : ""));
            }
            // Story NPCs' saved state, also of NPCs that are not loaded.
            if (GameManager.instance != null && GameManager.instance.defaultStoryNPCData != null)
                foreach (GameManager.storyNpcData s in GameManager.instance.defaultStoryNPCData)
                {
                    string key = "story:" + s.saveableId;
                    string cur;
                    if (SaveLoadManager.instance.WorldDatabase.TryGetValue(s.saveableId, out cur)) { }
                    saveables[key] = s.saveableId + "\tstoryNpcData\t\t\t" + scene + "\t\t" + (s.modules != null ? string.Join(",", Array.ConvertAll(s.modules, m => m.ToString())) : "") + "\tdefault=" + T(s.data) + " now=" + T(cur);
                }
            Write();
            Plugin.Log.LogInfo(Plugin.Stamp() + " [atlas] dump at " + where + ": npcs=" + npcs.Count + " answers=" + answers.Count + " triggers=" + triggers.Count +
                " saveables=" + saveables.Count + " (+" + (npcs.Count + triggers.Count + saveables.Count - before) + ") scenes=" + SceneManager.sceneCount);
        }

        static void Trig(string kind, Component c, string message, string extra)
        {
            if (string.IsNullOrEmpty(message)) return;
            int id = IdOf(c);
            triggers[kind + "/" + id] = kind + "\t" + T(message) + "\t" + id + "\t" + T(c.gameObject.name) + "\t" + P(c.transform.position) + "\t" + c.gameObject.scene.name +
                "\t" + SceneManager.GetActiveScene().name + "\t" + (c.gameObject.activeInHierarchy ? 1 : 0) + "\t" + T(extra);
        }

        static void Write()
        {
            Directory.CreateDirectory(Dir);
            Save("npcs.tsv", "id\tobject\tnpcName\tpos\tscene\tworld\tactive\tmarks\tkind", npcs.Values);
            Save("answers.tsv", "npc\tindex\tactive\ttype\treturn\tstoryMessage\tfollowing\trequirements\ttext", answers.Values);
            Save("triggers.tsv", "kind\tmessage\tid\tobject\tpos\tscene\tworld\tactive\textra", triggers.Values);
            Save("saveables.tsv", "id\tobject\tpos\tscene\tworld\tactive\tmodules\tdata", saveables.Values);
            Save("quests.tsv", "index\tname\tobjectives\tlevel\ttexts", quests);
        }

        static void Save(string file, string header, IEnumerable<string> rows)
        {
            List<string> l = new List<string>(rows);
            l.Sort(StringComparer.Ordinal);
            l.Insert(0, header);
            File.WriteAllLines(Path.Combine(Dir, file), l.ToArray());
        }

        // ---------- tour ----------

        public static void StartTour(PlayerScript player, string arg)
        {
            int max = arg.Length > 0 ? int.Parse(arg) : int.MaxValue;
            stops = new List<Vector3>();
            float step = float.MaxValue;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            List<KeyValuePair<Streamer, HashSet<long>>> cells = new List<KeyValuePair<Streamer, HashSet<long>>>();
            // The current world's streamers (after travelling, the old world's can still be around).
            List<Streamer> all = new List<Streamer>();
            foreach (Streamer s in UnityEngine.Object.FindObjectsOfType<Streamer>()) if (s.streamerActive) all.Add(s);
            if (all.Count == 0) all.AddRange(UnityEngine.Object.FindObjectsOfType<Streamer>());
            // After the test's "north" travel the Northern Kingdoms streamers had no player (their
            // PlayerPosForStreamers was in the unloaded main world): they load nothing without one.
            foreach (Streamer s in all)
                if (s.player == null) { s.player = player.transform; Plugin.Log.LogInfo("[atlas] streamer '" + s.name + "' had no player; following ours"); }
            foreach (Streamer s in all)
            {
                SceneCollection sc = s.sceneCollection;
                if (s == null || sc == null || sc.names == null || sc.names.Length == 0 || sc.xSize <= 0 || sc.zSize <= 0) continue;
                HashSet<long> set = new HashSet<long>();
                int cx0 = int.MaxValue, cx1 = int.MinValue, cz0 = int.MaxValue, cz1 = int.MinValue;
                foreach (string n in sc.names)
                {
                    int x, y, z;
                    Streamer.SceneNameToPos(sc, n, out x, out y, out z);
                    set.Add(((long)x << 32) ^ (uint)z);
                    cx0 = Math.Min(cx0, x); cx1 = Math.Max(cx1, x); cz0 = Math.Min(cz0, z); cz1 = Math.Max(cz1, z);
                }
                cells.Add(new KeyValuePair<Streamer, HashSet<long>>(s, set));
                float cover = Mathf.Max(1f, s.loadingRange.x) * 2f * sc.xSize; // stops this far apart leave no gaps
                step = Mathf.Min(step, cover);
                minX = Mathf.Min(minX, cx0 * sc.xSize + s.currentMove.x); maxX = Mathf.Max(maxX, (cx1 + 1) * sc.xSize + s.currentMove.x);
                minZ = Mathf.Min(minZ, cz0 * sc.zSize + s.currentMove.z); maxZ = Mathf.Max(maxZ, (cz1 + 1) * sc.zSize + s.currentMove.z);
                Plugin.Log.LogInfo("[atlas] streamer '" + s.name + "' cells=" + sc.names.Length + " size=" + sc.xSize + "x" + sc.zSize + " range=" + s.loadingRange +
                    " x=" + cx0 + ".." + cx1 + " z=" + cz0 + ".." + cz1 + " move=" + s.currentMove + " active=" + s.streamerActive + " player=" + (s.player != null ? s.player.name : "-") + " scene=" + s.gameObject.scene.name);
            }
            if (cells.Count == 0) { Plugin.Log.LogInfo("[atlas] no streamers"); stops = null; return; }
            // Per streamer (finest first): its cells grouped in squares it loads from one
            // spot; a square already within reach of an earlier stop needs no stop of its own.
            cells.Sort((a, b) => (a.Key.sceneCollection.xSize * a.Key.loadingRange.x).CompareTo(b.Key.sceneCollection.xSize * b.Key.loadingRange.x));
            foreach (KeyValuePair<Streamer, HashSet<long>> c in cells)
            {
                SceneCollection sc = c.Key.sceneCollection;
                if (sc.xSize >= 250) continue; // terrain layers: no story objects
                float reach = Mathf.Max(1f, c.Key.loadingRange.x) * sc.xSize; // loaded around a stop: +-reach
                float sq = reach * 2f;
                HashSet<long> squares = new HashSet<long>();
                int added = 0;
                foreach (long cell in c.Value)
                {
                    int x = (int)(cell >> 32), z = (int)(uint)(cell & 0xffffffff);
                    float wx = (x + 0.5f) * sc.xSize + c.Key.currentMove.x, wz = (z + 0.5f) * sc.zSize + c.Key.currentMove.z;
                    long key = ((long)Mathf.FloorToInt(wx / sq) << 32) ^ (uint)Mathf.FloorToInt(wz / sq);
                    if (!squares.Add(key)) continue;
                    Vector3 centre = new Vector3((Mathf.FloorToInt(wx / sq) + 0.5f) * sq, 1f, (Mathf.FloorToInt(wz / sq) + 0.5f) * sq);
                    bool near = false;
                    foreach (Vector3 s in stops) if (Mathf.Abs(s.x - centre.x) <= reach * 0.5f && Mathf.Abs(s.z - centre.z) <= reach * 0.5f) { near = true; break; }
                    if (!near) { stops.Add(centre); added++; }
                }
                Plugin.Log.LogInfo("[atlas] '" + c.Key.name + "': " + squares.Count + " squares of " + sq + " m, " + added + " new stops");
            }
            // Row by row, alternating direction: neighbouring cells stay loaded.
            stops.Sort((a, b) => { int ra = Mathf.FloorToInt(a.z / 120f), rb = Mathf.FloorToInt(b.z / 120f); if (ra != rb) return ra.CompareTo(rb); return (ra % 2 == 0 ? 1 : -1) * a.x.CompareTo(b.x); });
            if (stops.Count > max) stops.RemoveRange(max, stops.Count - max);
            stop = 0;
            nextAt = 0f;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [atlas] tour: " + stops.Count + " stops, step " + step + " m, area " + minX + ".." + maxX + " x " + minZ + ".." + maxZ + " (~" + (int)(stops.Count * Wait / 60f) + " min)");
            Dump("start");
        }

        // Test step celltour: loads every cell of the active streamers by name (additively, 40 at a
        // time), dumps, unloads. For the Northern Kingdoms, where teleporting the player did not make
        // the streamers load anything after the test's "north" travel.
        public static void StartCellTour()
        {
            List<string> names = new List<string>();
            foreach (Streamer s in UnityEngine.Object.FindObjectsOfType<Streamer>())
            {
                if (!s.streamerActive || s.sceneCollection == null || s.sceneCollection.names == null || s.sceneCollection.xSize >= 250) continue;
                foreach (string n in s.sceneCollection.names) names.Add(n.EndsWith(".unity") ? n.Substring(0, n.Length - 6) : n);
            }
            Plugin.Log.LogInfo(Plugin.Stamp() + " [atlas] cell tour: " + names.Count + " cells");
            GameManager.instance.StartCoroutine(CellTour(names));
        }

        static System.Collections.IEnumerator CellTour(List<string> names)
        {
            const int Batch = 40;
            for (int start = 0; start < names.Count; start += Batch)
            {
                List<string> batch = new List<string>();
                List<AsyncOperation> ops = new List<AsyncOperation>();
                for (int i = start; i < Math.Min(names.Count, start + Batch); i++)
                {
                    if (SceneManager.GetSceneByName(names[i]).isLoaded) continue;
                    AsyncOperation op = null;
                    try { op = SceneManager.LoadSceneAsync(names[i], LoadSceneMode.Additive); } catch (Exception e) { Plugin.Log.LogWarning("[atlas] " + names[i] + ": " + e.Message); }
                    if (op != null) { ops.Add(op); batch.Add(names[i]); }
                }
                float until = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < until && ops.Exists(o => !o.isDone)) yield return null;
                yield return new WaitForSeconds(1.5f); // Start/Awake of the loaded objects
                Dump("cells " + (start + batch.Count) + "/" + names.Count);
                foreach (string n in batch) { try { SceneManager.UnloadSceneAsync(n); } catch { } }
                yield return new WaitForSeconds(0.5f);
            }
            Plugin.Log.LogInfo(Plugin.Stamp() + " [atlas] tour done: npcs=" + npcs.Count + " answers=" + answers.Count + " triggers=" + triggers.Count + " saveables=" + saveables.Count);
        }

        // Test step worldlog: which hub world scenes are loaded, GameManager's world and streamer list,
        // and every streamer's scene/active/player (the Northern Kingdoms load problem on the host).
        public static void WorldLog()
        {
            List<string> hubs = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene sc = SceneManager.GetSceneAt(i);
                if (sc.name == "Gedonia" || sc.name == "NorthernKingdoms" || sc.name == "UnchartedIslands" || sc.name == "Game") hubs.Add(sc.name + (sc.isLoaded ? "" : "(unloading)"));
            }
            GameManager gm = GameManager.instance;
            string list = "-";
            if (gm != null && gm.streamers != null && gm.streamers.Length > 0 && gm.streamers[0] != null)
                list = gm.streamers.Length + " in " + gm.streamers[0].gameObject.scene.name;
            List<string> ss = new List<string>();
            foreach (Streamer s in Resources.FindObjectsOfTypeAll<Streamer>())
            {
                if (s == null || !s.gameObject.scene.IsValid()) continue;
                ss.Add(s.gameObject.scene.name + ":" + s.name.Replace("_Streamer_", "") + (s.streamerActive ? "+" : "-") + (s.player != null ? "@" + s.player.name : "@none"));
            }
            Plugin.Log.LogInfo(Plugin.Stamp() + " [worldlog] world=" + (gm != null ? gm.currentLoadedWorld : "?") + " hubScenes=" + string.Join(",", hubs.ToArray()) +
                " scenes=" + SceneManager.sceneCount + " gm.streamers=" + list + " streamers: " + string.Join(" ", ss.ToArray()));
            GameObject ppos = GameObject.Find("PlayerPosForStreamers");
            PlayerScript pl = PlayerScript.instance;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [worldlog] PlayerPosForStreamers=" + (ppos != null ? P(ppos.transform.position) + " in " + ppos.scene.name : "none") +
                " player=" + (pl != null ? P(pl.transform.position) + " enabled=" + pl.enabled : "none") +
                " loadingPanel=" + (InterfaceManager.instance != null && InterfaceManager.instance.panels[10].activeSelf) + " timeScale=" + Time.timeScale);
            LogStreamers();
        }

        // Diagnostic: what each active streamer is doing (loading queue, tile, player).
        static void LogStreamers()
        {
            System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            foreach (Streamer s in UnityEngine.Object.FindObjectsOfType<Streamer>())
            {
                if (!s.streamerActive) continue;
                Func<string, object> f = n => { var fi = typeof(Streamer).GetField(n, bf); return fi != null ? fi.GetValue(s) : "?"; };
                object q = f("scenesToLoad");
                Plugin.Log.LogInfo("[atlas] '" + s.name + "' player=" + (s.player != null ? s.player.name + "@" + P(s.player.position) : "-") +
                    " tile=" + f("xPos") + "," + f("zPos") + " loading=" + f("currentlySceneLoading") + " queue=" + (q is System.Collections.ICollection ? ((System.Collections.ICollection)q).Count.ToString() : "?") +
                    " tiles=" + f("tilesLoaded") + "/" + f("tilesToLoad") + " loaded=" + (f("loadedScenes") is System.Collections.ICollection ? ((System.Collections.ICollection)f("loadedScenes")).Count.ToString() : "?") +
                    " enabled=" + s.isActiveAndEnabled);
            }
        }

        public static void Tick(PlayerScript player, float now)
        {
            if (stops == null) return;
            if (stop > 0 && stop <= stops.Count)
            {
                // Hold the player in place while the cells load (no ground below yet).
                if (player._char != null) player._char.enabled = false;
                player.transform.position = hold;
            }
            if (now < nextAt) return;
            if (stop > 0) Dump("stop " + stop + "/" + stops.Count + " " + P(hold));
            if (stop > 0 && stop <= 12) LogStreamers();
            if (stop >= stops.Count)
            {
                Plugin.Log.LogInfo(Plugin.Stamp() + " [atlas] tour done: npcs=" + npcs.Count + " answers=" + answers.Count + " triggers=" + triggers.Count + " saveables=" + saveables.Count);
                stops = null;
                if (player._char != null) player._char.enabled = true;
                return;
            }
            hold = stops[stop++];
            player.transform.position = hold;
            nextAt = now + Wait;
        }
    }
}
