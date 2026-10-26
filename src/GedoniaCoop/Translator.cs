using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // German on top of the game's English: every text the game puts into an
    // NGUI label passes UILabel.text's setter, where English is swapped for
    // German from the tables next to the plugin (de-*.tsv: English<TAB>German,
    // \n for line breaks). A table entry may have placeholders {0}, {1}... for
    // what the game fills in (numbers, names): "You have {0} gold" matches
    // "You have 25 gold" and the German gets the same pieces, themselves
    // translated when they are known texts (item names...). Texts with no
    // German are logged once (BepInEx/config/gedonia-coop-missing-de.txt).
    public static class Translator
    {
        class Template
        {
            public Regex Re;
            public string Key; // a literal piece the text must contain
            public bool Prefix;
            public string De;
        }

        static Dictionary<string, string> exact;
        static readonly HashSet<string> germanTexts = new HashSet<string>();
        static List<Template> templates;
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        static readonly HashSet<string> missing = new HashSet<string>();
        static readonly HashSet<string> exported = new HashSet<string>();
        static readonly Regex Slot = new Regex(@"\{(\d+)\}");
        static readonly Regex Pieces = new Regex(@"(\[[0-9A-Fa-f]{6}\]|\[-\]|\[/?[bisu]\]|\n)");
        static bool inside;

        // Sets a label's text as it is (already translated, plus our own marks).
        public static void SetRaw(UILabel l, string text)
        {
            inside = true;
            try { l.text = text; }
            finally { inside = false; }
        }
        public static int Hits, Misses;

        static string PluginDir { get { return Path.GetDirectoryName(typeof(Translator).Assembly.Location); } }
        static string MissingFile { get { return Path.Combine(Paths.ConfigPath, "gedonia-coop-missing-de.txt"); } }

        static void Load()
        {
            exact = new Dictionary<string, string>();
            templates = new List<Template>();
            string[] files = Directory.GetFiles(PluginDir, "de-*.tsv");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || line.StartsWith("#")) continue;
                    string en = Unescape(line.Substring(0, tab)), de = Unescape(line.Substring(tab + 1));
                    if (de.Length == 0) continue;
                    exact[en] = de;
                    germanTexts.Add(de);
                    if (Slot.IsMatch(en)) AddTemplate(en, de);
                }
            // The most specific first.
            templates.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            Plugin.Log.LogInfo("German tables: " + files.Length + " files, " + exact.Count + " texts, " + templates.Count + " templates");
        }

        static void AddTemplate(string en, string de)
        {
            string[] parts = Slot.Split(en); // literal, slot no, literal, ...
            StringBuilder re = new StringBuilder("^");
            string longest = "";
            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 0) { re.Append(Regex.Escape(parts[i])); if (parts[i].Length > longest.Length) longest = parts[i]; }
                else re.Append("(?<s" + parts[i] + ">.*?)");
            }
            re.Append("$");
            // Too little fixed text would swallow unrelated texts.
            int letters = 0;
            foreach (char c in Slot.Replace(en, "")) if (char.IsLetter(c)) letters++;
            if (letters < 3 || longest.Trim().Length < 2) return;
            Template t = new Template();
            try { t.Re = new Regex(re.ToString(), RegexOptions.Singleline); } catch { return; }
            t.Prefix = parts[0].Length >= 2;
            t.Key = t.Prefix ? parts[0] : longest;
            t.De = de;
            templates.Add(t);
        }

        static string Unescape(string s) { return s.Replace("\\n", "\n").Replace("\\t", "\t"); }

        // The German for an English text, or null.
        public static string German(string en)
        {
            if (exact == null) Load();
            if (string.IsNullOrEmpty(en)) return null;
            string de;
            if (exact.TryGetValue(en, out de)) return de;
            if (cache.TryGetValue(en, out de)) return de;
            de = Find(en);
            if (cache.Count > 20000) cache.Clear();
            cache[en] = de;
            return de;
        }

        static string Find(string en)
        {
            string de;
            if (en.IndexOf('\r') >= 0) return German(en.Replace("\r", "")); // scene texts keep Windows line ends
            if (en.Contains("\\n")) return German(en.Replace("\\n", "\n")); // or a typed "\n", which NGUI shows as a line break
            string trimmed = en.Trim();
            if (trimmed.Length == 0) return null;
            if (trimmed != en && exact.TryGetValue(trimmed, out de)) return en.Replace(trimmed, de);
            foreach (Template t in templates)
            {
                if (t.Prefix ? !en.StartsWith(t.Key, StringComparison.Ordinal) : en.IndexOf(t.Key, StringComparison.Ordinal) < 0) continue;
                Match m = t.Re.Match(en);
                if (!m.Success) continue;
                return Slot.Replace(t.De, s =>
                {
                    Group g = m.Groups["s" + s.Groups[1].Value];
                    if (!g.Success) return s.Value;
                    string inner;
                    return exact.TryGetValue(g.Value, out inner) ? inner : g.Value;
                });
            }
            // Built texts ("[FFCC00]Quest name[-]\nDescription"): the pieces
            // between colour tags and line breaks one by one.
            if (Pieces.IsMatch(en))
            {
                bool any = false;
                StringBuilder sb = new StringBuilder();
                foreach (string p in Pieces.Split(en))
                {
                    string d = p.Length > 0 && !Pieces.IsMatch(p) ? German(p) : null;
                    if (d != null) any = true;
                    sb.Append(d ?? p);
                }
                if (any) return sb.ToString();
            }
            return null;
        }

        static string Swap(string value)
        {
            if (!Lang.German || inside || string.IsNullOrEmpty(value)) return value;
            string de = German(value);
            if (de != null) { Hits++; germanTexts.Add(de); return de; }
            if (germanTexts.Contains(value) || germanTexts.Contains(value.Trim())) return value;
            if (LooksLikeText(value) && missing.Add(value))
            {
                Misses++;
                try { File.AppendAllText(MissingFile, value.Replace("\n", "\\n") + "\n", Encoding.UTF8); } catch { }
            }
            return value;
        }

        // Test step "textexport": every English text the game holds right now
        // (NGUI dictionary, quests, items, dialogues of the loaded NPCs) into
        // BepInEx/coop-texts-en.tsv as source<TAB>text, without duplicates.
        public static void Export()
        {
            HashSet<string> seen = exported;
            StringBuilder sb = new StringBuilder();
            Action<string, string> add = (src, t) =>
            {
                if (string.IsNullOrEmpty(t) || !LooksLikeText(t) || !seen.Add(t)) return;
                sb.Append(src).Append('\t').Append(t.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t")).Append('\n');
            };
            foreach (KeyValuePair<string, string[]> kv in Localization.dictionary)
                if (kv.Value.Length > 0) add("ui:" + kv.Key, kv.Value[0]);
            foreach (Quest q in QuestsData.instance.questDatabase)
            {
                if (q == null) continue;
                add("quest", q.questName);
                add("quest", q.descriptionShort);
                if (q.objectiveText != null) foreach (string o in q.objectiveText) add("quest", o);
            }
            foreach (Item[] arr in new Item[][] { ItemsData.instance.basicItemsData, ItemsData.instance.weaponsDatabase, ItemsData.instance.armorDatabase, ItemsData.instance.consumablesData })
                foreach (Item it in arr) if (it != null) { add("item", it.name); add("item", it.basicDescription); }
            int modules = 0;
            foreach (DialogueNPCModule d in Resources.FindObjectsOfTypeAll<DialogueNPCModule>())
            {
                if (d.allDialogue == null) continue;
                modules++;
                NPC npc = d.GetComponent<NPC>();
                string src = "dialogue:" + (npc != null ? npc.npcName : d.name);
                foreach (DialogiePoint p in d.allDialogue) { add(src, p.playerQuestionText); add(src, p.npcAnswerText); }
            }
            foreach (NPC npc in Resources.FindObjectsOfTypeAll<NPC>()) add("npc", npc.npcName);
            foreach (UILabel l in Resources.FindObjectsOfTypeAll<UILabel>()) if (l.gameObject.scene.IsValid() && l.GetComponent<UILocalize>() == null) { string t = l.text; string o; if (!string.IsNullOrEmpty(t) && original.TryGetValue(l, out o)) t = o; add("label", t == null ? null : t.Replace("\r", "")); }
            // Everything else the game keeps in English/Russian field pairs
            // (zone names, boards, companion lines, events, tips...).
            foreach (KeyValuePair<Type, FieldInfo[]> kv in PairedTypes())
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(kv.Key))
                    AddPaired(o, kv.Value, kv.Key.Name, add, 0);
            string file = Path.Combine(Paths.BepInExRootPath, "coop-texts-en.tsv");
            File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            Plugin.Log.LogInfo("[textexport] " + seen.Count + " texts (" + modules + " dialogue modules) -> " + file);
        }

        static List<KeyValuePair<Type, FieldInfo[]>> paired;
        static readonly Dictionary<Type, FieldInfo[]> pairedFields = new Dictionary<Type, FieldInfo[]>();

        // The English fields that have a "...Rus" twin, per type (also inside
        // serializable classes held in arrays and lists).
        static FieldInfo[] EnglishFields(Type t)
        {
            FieldInfo[] r;
            if (pairedFields.TryGetValue(t, out r)) return r;
            List<FieldInfo> list = new List<FieldInfo>();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (FieldInfo f in t.GetFields(all))
            {
                if (f.Name.EndsWith("Rus") || f.Name.EndsWith("Russian"))
                {
                    string en = f.Name.EndsWith("Rus") ? f.Name.Substring(0, f.Name.Length - 3) : f.Name.Substring(0, f.Name.Length - 7);
                    FieldInfo e = t.GetField(en, all) ?? t.GetField(en + "Eng", all);
                    if (e != null && (e.FieldType == typeof(string) || e.FieldType == typeof(string[]) || e.FieldType == typeof(List<string>))) list.Add(e);
                }
                else
                {
                    Type el = f.FieldType.IsArray ? f.FieldType.GetElementType() : f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>) ? f.FieldType.GetGenericArguments()[0] : f.FieldType;
                    if (el != null && el != t && !el.IsPrimitive && el != typeof(string) && !typeof(UnityEngine.Object).IsAssignableFrom(el) && el.Assembly == t.Assembly && el.IsSerializable)
                    {
                        pairedFields[t] = new FieldInfo[0]; // guards recursion
                        if (EnglishFields(el).Length > 0) list.Add(f);
                    }
                }
            }
            r = list.ToArray();
            pairedFields[t] = r;
            return r;
        }

        static List<KeyValuePair<Type, FieldInfo[]>> PairedTypes()
        {
            if (paired != null) return paired;
            paired = new List<KeyValuePair<Type, FieldInfo[]>>();
            foreach (Type t in typeof(NPC).Assembly.GetTypes())
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(t) && !typeof(ScriptableObject).IsAssignableFrom(t)) continue;
                try { FieldInfo[] f = EnglishFields(t); if (f.Length > 0) paired.Add(new KeyValuePair<Type, FieldInfo[]>(t, f)); }
                catch { }
            }
            Plugin.Log.LogInfo("[textexport] types with English/Russian fields: " + paired.Count);
            return paired;
        }

        static void AddPaired(object o, FieldInfo[] fields, string src, Action<string, string> add, int depth)
        {
            if (o == null || depth > 3) return;
            foreach (FieldInfo f in fields)
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                if (v == null) continue;
                if (v is string) { add(src, (string)v); continue; }
                System.Collections.IEnumerable seq = v as System.Collections.IEnumerable;
                if (seq == null)
                {
                    FieldInfo[] inner = EnglishFields(v.GetType());
                    if (inner.Length > 0) AddPaired(v, inner, src, add, depth + 1);
                    continue;
                }
                foreach (object e in seq)
                {
                    if (e == null) continue;
                    if (e is string) add(src, (string)e);
                    else { FieldInfo[] inner = EnglishFields(e.GetType()); if (inner.Length > 0) AddPaired(e, inner, src, add, depth + 1); }
                }
            }
        }

        // Test step "textexportall": NPC dialogues live in the world's streamed
        // cells, so load every build scene once (additive, then unload again)
        // and export after each. Only for the test copies: it fills memory and
        // the world with everything for a moment.
        public static System.Collections.IEnumerator ExportAll()
        {
            string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            int count = UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings;
            Plugin.Log.LogInfo("[textexport] build scenes: " + count);
            Export();
            const int Batch = 8; // cells loaded at the same time
            for (int i = 0; i < count; i += Batch)
            {
                List<string> names = new List<string>();
                List<AsyncOperation> loads = new List<AsyncOperation>();
                for (int k = i; k < count && k < i + Batch; k++)
                {
                    string name = Path.GetFileNameWithoutExtension(UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(k));
                    if (name == active || name == "MainMenu" || UnityEngine.SceneManagement.SceneManager.GetSceneByName(name).isLoaded) continue;
                    try
                    {
                        AsyncOperation op = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(k, UnityEngine.SceneManagement.LoadSceneMode.Additive);
                        if (op != null) { loads.Add(op); names.Add(name); }
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("[textexport] " + name + ": " + e.Message); }
                }
                foreach (AsyncOperation op in loads) while (!op.isDone) yield return null;
                yield return null;
                if (i % 400 == 0) Plugin.Log.LogInfo("[textexport] scene " + i + " " + (names.Count > 0 ? names[0] : ""));
                try { Export(); } catch (Exception e) { Plugin.Log.LogWarning("[textexport] " + i + ": " + e.Message); }
                List<AsyncOperation> unloads = new List<AsyncOperation>();
                foreach (string name in names)
                {
                    AsyncOperation u = UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(name);
                    if (u != null) unloads.Add(u);
                }
                foreach (AsyncOperation u in unloads) while (!u.isDone) yield return null;
            }
            Plugin.Log.LogInfo("[textexport] all scenes done");
        }

        // Skip numbers, single characters, key names.
        static bool LooksLikeText(string s)
        {
            int letters = 0;
            foreach (char c in s)
            {
                if (c >= 'Ѐ' && c <= 'ӿ') return false; // Russian
                if (char.IsLetter(c)) letters++;
            }
            if (letters < 3) return false;
            // key names and identifiers: Alpha3, LeftControl, savefile
            if (s.IndexOf(' ') < 0 && s.IndexOf('\n') < 0)
                for (int i = 1; i < s.Length; i++)
                    if (char.IsDigit(s[i]) || char.IsUpper(s[i])) return false;
            return true;
        }

        // Language switched in the main menu: the labels already on screen.
        public static void Refresh()
        {
            foreach (UILabel l in Resources.FindObjectsOfTypeAll<UILabel>())
            {
                if (l == null || !l.gameObject.scene.IsValid()) continue;
                UILocalize loc = l.GetComponent<UILocalize>();
                if (loc != null) { loc.value = Localization.Get(loc.key); continue; } // English again, swapped on the way in
                string en;
                if (!Lang.German && original.TryGetValue(l, out en)) { inside = true; l.text = en; inside = false; }
                else if (Lang.German) l.text = l.text;
            }
        }

        // German labels remember their English, so switching back works.
        static readonly Dictionary<UILabel, string> original = new Dictionary<UILabel, string>();

        [HarmonyPatch(typeof(UILabel), "text", MethodType.Setter)]
        static class TextPatch
        {
            static void Prefix(UILabel __instance, ref string value)
            {
                string de = Swap(value);
                if (!ReferenceEquals(de, value)) { original[__instance] = value; value = de; }
            }
        }

        // Texts typed into the scene never pass the setter; catch them when
        // the label starts.
        [HarmonyPatch(typeof(UILabel), "OnStart")]
        static class StartPatch
        {
            static void Postfix(UILabel __instance)
            {
                if (Lang.German && !string.IsNullOrEmpty(__instance.text)) __instance.text = __instance.text;
            }
        }
    }
}
