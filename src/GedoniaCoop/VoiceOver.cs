using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace GedoniaCoop
{
    // German voice-over (private, like the tables): pre-rendered OGG files in
    // plugins/voice-de/, named sha1("voice|English text")[:16], plus speakers.tsv
    // (NPC name -> voice). Only while the mod shows German.
    //  - Dialogue: after InterfaceManager.RefreshDialogue the NPC's current answer
    //    (English, as the game holds it) is spoken in that NPC's voice; a new
    //    answer or leaving the dialogue stops the old one.
    //  - Journal: picking a quest speaks its description and the newest open
    //    objective in the narrator's voice; closing the journal stops it.
    // Keys are built from the text exactly as the speakers export wrote it
    // (no \r, line breaks as a typed "\n").
    public class VoiceOver : MonoBehaviour
    {
        public static float Volume = 1f;
        public static bool Enabled = true;

        static VoiceOver instance;
        static Dictionary<string, string> voices;
        static string lastJournal;

        AudioSource source;
        readonly Queue<string> queue = new Queue<string>();
        int generation;

        static string Dir { get { return Path.Combine(Path.GetDirectoryName(typeof(VoiceOver).Assembly.Location), "voice-de"); } }

        static bool Active
        {
            get
            {
                if (!Enabled || !Lang.German) return false;
                if (voices == null) LoadVoices();
                return voices.Count > 0;
            }
        }

        static void LoadVoices()
        {
            voices = new Dictionary<string, string>();
            try
            {
                string file = Path.Combine(Dir, "speakers.tsv");
                if (!File.Exists(file)) return;
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string[] p = line.Split('\t');
                    if (p.Length >= 2 && p[0] != "speaker") voices[p[0]] = p[1];
                }
                Plugin.Log.LogInfo("[voice] " + voices.Count + " speakers, folder " + Dir);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[voice] speakers.tsv: " + e.Message); }
        }

        static VoiceOver Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject go = new GameObject("GedoniaCoopVoiceOver");
                    DontDestroyOnLoad(go);
                    instance = go.AddComponent<VoiceOver>();
                    instance.source = go.AddComponent<AudioSource>();
                    instance.source.spatialBlend = 0f;
                    instance.source.playOnAwake = false;
                }
                return instance;
            }
        }

        static string Key(string voice, string english)
        {
            string text = english.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(voice + "|" + text));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        static string FileFor(string voice, string english)
        {
            if (string.IsNullOrEmpty(english)) return null;
            string f = Path.Combine(Dir, Key(voice, english) + ".ogg");
            return File.Exists(f) ? f : null;
        }

        public static void Stop()
        {
            if (instance == null) return;
            instance.generation++;
            instance.queue.Clear();
            instance.source.Stop();
        }

        static void Say(params string[] files)
        {
            VoiceOver vo = Instance;
            Stop();
            foreach (string f in files) if (f != null) vo.queue.Enqueue(f);
            if (vo.queue.Count > 0) vo.StartCoroutine(vo.PlayQueue(vo.generation));
        }

        IEnumerator PlayQueue(int gen)
        {
            while (queue.Count > 0 && gen == generation)
            {
                string file = queue.Dequeue();
                using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, AudioType.OGGVORBIS))
                {
                    yield return req.SendWebRequest();
                    if (gen != generation) yield break;
                    if (req.isNetworkError || req.isHttpError) { Plugin.Log.LogWarning("[voice] " + file + ": " + req.error); continue; }
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                    source.clip = clip;
                    source.volume = Volume;
                    source.Play();
                    Plugin.Log.LogInfo("[voice] playing " + Path.GetFileName(file) + " " + clip.length.ToString("F1") + " s");
                    while (source.isPlaying && gen == generation) yield return null;
                    if (source.clip == clip) source.clip = null;
                    Destroy(clip);
                }
                if (queue.Count > 0) yield return new WaitForSeconds(0.4f);
            }
        }

        // Dialogue line shown (also for answers a co-op friend picked).
        [HarmonyPatch(typeof(InterfaceManager), "RefreshDialogue")]
        static class DialoguePatch
        {
            static void Postfix(InterfaceManager __instance)
            {
                try
                {
                    if (!Active || __instance.currentTalkTarget == null) return;
                    DialogueNPCModule d = __instance.currentTalkTarget.GetComponent<DialogueNPCModule>();
                    if (d == null) return;
                    NPC npc = d.GetComponent<NPC>();
                    string name = npc != null ? npc.npcName : d.name;
                    string voice;
                    if (!voices.TryGetValue(name ?? "", out voice)) { Stop(); return; }
                    string f = FileFor(voice, d.currentAnswer);
                    if (f == null) { Stop(); Plugin.Log.LogInfo("[voice] no file: " + name + " / " + voice); return; }
                    Say(f);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[voice] dialogue: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(InterfaceManager), "ExitDialogue")]
        static class ExitPatch
        {
            static void Postfix() { Stop(); }
        }

        // Journal page of the picked quest (RefreshJournal also runs for other
        // reasons, so only a newly picked quest or objective is spoken).
        [HarmonyPatch(typeof(InterfaceManager), "RefreshJournal")]
        static class JournalPatch
        {
            static void Postfix(InterfaceManager __instance)
            {
                try
                {
                    if (!Active) return;
                    if (__instance.pickedEntry < 0) { lastJournal = null; return; }
                    Quest picked = QuestsData.instance.questDatabase[GameManager.instance.journalledQuests[__instance.pickedEntry]];
                    Quest quest = null;
                    foreach (Quest q in QuestsData.instance.currentQuests) if (q.questName == picked.questName) quest = q;
                    if (quest == null) return;
                    string objective = null;
                    for (int i = 0; i < quest.objectiveText.Length; i++)
                        if (quest.shownObjective[i] && !quest.completed[i]) objective = quest.objectiveText[i];
                    string id = quest.questName + "|" + objective;
                    if (id == lastJournal) return;
                    lastJournal = id;
                    Say(FileFor("narrator", quest.descriptionShort), FileFor("narrator", objective));
                }
                catch (Exception e) { Plugin.Log.LogWarning("[voice] journal: " + e.Message); }
            }
        }

        // The journal window (panels[14]) closed -> its quest text stops.
        void Update()
        {
            if (lastJournal == null) return;
            InterfaceManager im = InterfaceManager.instance;
            if (im == null || im.panels == null || im.panels.Length <= 14 || !im.panels[14].activeSelf) { lastJournal = null; Stop(); }
        }
    }
}
