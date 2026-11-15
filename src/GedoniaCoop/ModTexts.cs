using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace GedoniaCoop
{
    // German for content of Gedonia's own mods (Steam Workshop / Mods folder).
    //
    // Mod authors write English and Russian only, and every player has other
    // mods, so there can't be a table shipped with the co-op mod. Instead the
    // texts of the installed mods (items, item descriptions, effects, NPC
    // names, quests, dialogue) are written to
    //   BepInEx/config/gedonia-coop-de-mods.tsv   (English<TAB>German)
    // with an empty German column; whatever the player fills in is used like
    // the shipped tables (also {0} templates). Lines already there are kept, the
    // file is re-read when it changes (no restart needed).
    public static partial class Translator
    {
        static string ModFile { get { return Path.Combine(Paths.ConfigPath, "gedonia-coop-de-mods.tsv"); } }
        static DateTime modFileTime;
        static int modsSeen = -1;
        static float nextModCheck;
        public static int ModTexts, ModTranslated, ModAdded;

        static void LoadModTable()
        {
            ModTexts = ModTranslated = 0;
            string file = ModFile;
            if (!File.Exists(file)) return;
            try
            {
                modFileTime = File.GetLastWriteTimeUtc(file);
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || line.StartsWith("#")) continue;
                    ModTexts++;
                    string en = Unescape(line.Substring(0, tab)), de = Unescape(line.Substring(tab + 1)).Trim();
                    if (de.Length == 0) continue;
                    ModTranslated++;
                    exact[en] = de;
                    germanTexts.Add(de);
                    if (Slot.IsMatch(en)) AddTemplate(en, de);
                }
                Plugin.Log.LogInfo("German mod table: " + ModTranslated + " of " + ModTexts + " texts translated");
            }
            catch (Exception e) { Plugin.Log.LogWarning("German mod table not read: " + e.Message); }
        }

        // Every few seconds: new mods loaded -> export their texts; table edited -> reload.
        public static void ModTick(float now)
        {
            if (now < nextModCheck || !Available) return;
            nextModCheck = now + 5f;
            try
            {
                if (exact != null && File.Exists(ModFile) && File.GetLastWriteTimeUtc(ModFile) != modFileTime)
                {
                    exact = null; cache.Clear();
                    Load();
                    if (Lang.German) Refresh();
                }
                GameModIntegrationManager g = GameModIntegrationManager.instance;
                int count = g != null && g.mods != null ? g.mods.Count : 0;
                if (count == modsSeen) return;
                modsSeen = count;
                if (count > 0) ExportModTexts(g.mods);
            }
            catch (Exception e) { Plugin.Log.LogWarning("German mod texts: " + e.Message); }
        }

        static void Collect(List<string> into, HashSet<string> seen, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            s = s.Replace("\r", "");
            if (s.Trim().Length == 0 || !seen.Add(s)) return;
            int letters = 0;
            foreach (char c in s) if (c < 128 && char.IsLetter(c)) letters++;
            if (letters < 2) return; // numbers, Russian only
            into.Add(s);
        }

        public static void ExportModTexts(List<ModData> mods)
        {
            if (exact == null) Load();
            List<string> texts = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (ModData m in mods)
            {
                if (m == null) continue;
                if (m.playerItems != null)
                    foreach (Item it in m.playerItems) if (it != null) { Collect(texts, seen, it.name); Collect(texts, seen, it.basicDescription); }
                if (m.playerEffects != null)
                    foreach (Effect e in m.playerEffects) Collect(texts, seen, e.name);
                if (m.playerNPCs != null)
                    foreach (ModData.NPCData n in m.playerNPCs)
                    {
                        Collect(texts, seen, n.name);
                        Collect(texts, seen, n.faction);
                        if (n.dialogiePoints != null)
                            foreach (DialogiePoint d in n.dialogiePoints) { Collect(texts, seen, d.playerQuestionText); Collect(texts, seen, d.npcAnswerText); }
                    }
                if (m.modQuests != null)
                    foreach (SteamWorkshopManager.ModQuest q in m.modQuests)
                    {
                        if (q == null || q.quest == null) continue;
                        Collect(texts, seen, q.quest.questName);
                        Collect(texts, seen, q.quest.descriptionShort);
                        if (q.quest.objectiveText != null) foreach (string o in q.quest.objectiveText) Collect(texts, seen, o);
                    }
            }
            // Lines already in the file stay as they are (with the player's German).
            HashSet<string> inFile = new HashSet<string>();
            string file = ModFile;
            bool exists = File.Exists(file);
            if (exists)
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && !line.StartsWith("#")) inFile.Add(Unescape(line.Substring(0, tab)));
                }
            StringBuilder add = new StringBuilder();
            int added = 0, shipped = 0;
            foreach (string s in texts)
            {
                if (inFile.Contains(s)) continue;
                if (German(s) != null) { shipped++; continue; } // a vanilla text the shipped tables know
                add.Append(Escape(s)).Append('\t').Append('\n');
                added++;
            }
            ModAdded += added;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [modtexts] " + mods.Count + " mods, " + texts.Count + " texts: " + added + " new in " + Path.GetFileName(file) + ", " + inFile.Count + " already there, " + shipped + " known");
            if (added == 0) return;
            if (!exists)
                add.Insert(0, "# German for texts of your Gedonia mods. Write the German after the TAB of a line; lines without German stay English.\n" +
                              "# Line breaks as \\n. {0}, {1} ... in the English text match any value, use them in the German too.\n" +
                              "# The co-op mod adds new texts at the end when you install more mods; the game reads changes at once.\n");
            File.AppendAllText(file, add.ToString(), new UTF8Encoding(false));
            modFileTime = File.GetLastWriteTimeUtc(file);
        }

        // Test-only: what the mod items are called now.
        public static void TestModTexts()
        {
            GameModIntegrationManager g = GameModIntegrationManager.instance;
            List<string> names = new List<string>();
            if (g != null && g.mods != null) foreach (ModData m in g.mods) if (m != null && m.playerItems != null) foreach (Item it in m.playerItems) if (it != null) names.Add(it.name + " -> " + (German(it.name) ?? "(none)"));
            Plugin.Log.LogInfo(Plugin.Stamp() + " [modtexts] table " + ModTranslated + "/" + ModTexts + " file=" + File.Exists(ModFile) + " items: " + string.Join("; ", names.ToArray()));
        }

        // Test-only: "English=German" written into the table the way a player would (the tick picks it up).
        public static void TestSetModText(string pair)
        {
            int eq = pair.IndexOf('=');
            string en = pair.Substring(0, eq), de = pair.Substring(eq + 1);
            string[] lines = File.ReadAllLines(ModFile, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++) if (lines[i] == en + "\t") lines[i] = en + "\t" + de;
            File.WriteAllLines(ModFile, lines, new UTF8Encoding(false));
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: mod table line " + en + " -> " + de);
        }

        static string Escape(string s) { return s.Replace("\t", "\\t").Replace("\n", "\\n"); }
    }
}
