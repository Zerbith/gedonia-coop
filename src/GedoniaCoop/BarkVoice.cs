using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace GedoniaCoop
{
    // German voice for the short barks: RandomTalkNPCModule.RandomSay shows one of its phrases as a
    // bubble (the text is swapped by the Translator, table de-16-barks.tsv) and plays the matching
    // English greeting clip, which GreetingMute silences in German. Here the German take of that
    // phrase plays instead (voice-de/<sha1("voice|phrase")[:16]>.ogg, rendered by tts/render_all.py).
    // Voice (speakers.tsv rows from tts/scripts/assign_barks.py):
    //  - random citizen (RandomCItizenNPCModule gave it a random name): "clip:<family of greetings[0]>",
    //    else "citizen:f" / "citizen:m";
    //  - other NPCs: their npcName, else "clip:<family>".
    // A row may list several voices; each NPC keeps one of them (by instance id).
    // Phrases paired with a wordless clip (GreetingMute.IsSpeech false: seers' jargon...) stay unvoiced.
    // Barks have their own AudioSource, so they never cut a dialogue or journal voice.
    public class BarkVoice : MonoBehaviour
    {
        static BarkVoice instance;
        AudioSource source;
        int generation;

        // Set by Pick while RandomSay runs: the phrase index the game chose.
        static int picked = -1;

        static BarkVoice Instance
        {
            get
            {
                if (instance == null)
                {
                    GameObject go = new GameObject("GedoniaCoopBarkVoice");
                    DontDestroyOnLoad(go);
                    instance = go.AddComponent<BarkVoice>();
                    instance.source = go.AddComponent<AudioSource>();
                    instance.source.spatialBlend = 0f;
                    instance.source.playOnAwake = false;
                    go.AddComponent<VoiceGain>();
                }
                return instance;
            }
        }

        // Stands in for the first Random.Range(int, int) in RandomSay (the phrase index).
        public static int Pick(int min, int max)
        {
            int n = UnityEngine.Random.Range(min, max);
            picked = n;
            return n;
        }

        internal static string Family(string clip)
        {
            string[] p = clip.Split('_');
            return p.Length >= 3 ? p[0] + "_" + p[1] : clip;
        }

        internal static string VoiceFor(RandomTalkNPCModule r)
        {
            RandomCItizenNPCModule citizen = r.GetComponent<RandomCItizenNPCModule>();
            bool random = citizen != null && citizen.addRandomDialogue;
            NPC npc = r.GetComponent<NPC>();
            string list = null;
            if (!random && npc != null && !string.IsNullOrEmpty(npc.npcName) && npc.npcName.Trim().Length > 0)
                list = VoiceOver.VoiceList(npc.npcName) ?? VoiceOver.VoiceList(npc.npcName.Trim());
            if (list == null && r.greetings != null && r.greetings.Length > 0 && r.greetings[0] != null)
                list = VoiceOver.VoiceList("clip:" + Family(r.greetings[0].name));
            if (list == null && random)
                list = VoiceOver.VoiceList(r.phrases == citizen.femaleDialogie ? "citizen:f" : "citizen:m");
            if (string.IsNullOrEmpty(list) || list == "narrator") return null;
            string[] all = list.Split(',');
            return all[Math.Abs(r.gameObject.GetInstanceID()) % all.Length];
        }

        void Play(string file)
        {
            generation++;
            source.Stop();
            StartCoroutine(Load(file, generation));
        }

        IEnumerator Load(string file, int gen)
        {
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, AudioType.OGGVORBIS))
            {
                yield return req.SendWebRequest();
                if (gen != generation) yield break;
                if (req.isNetworkError || req.isHttpError) { Plugin.Log.LogWarning("[voice] " + file + ": " + req.error); yield break; }
                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (source.clip != null) Destroy(source.clip);
                source.clip = clip;
                source.volume = 1f;
                source.Play();
                Plugin.Log.LogInfo("[voice] bark " + Path.GetFileName(file) + " " + clip.length.ToString("F1") + " s");
            }
        }

        [HarmonyPatch(typeof(RandomTalkNPCModule), "RandomSay")]
        static class SayPatch
        {
            static void Prefix() { picked = -1; }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo range = AccessTools.Method(typeof(UnityEngine.Random), "Range", new Type[] { typeof(int), typeof(int) });
                bool done = false;
                foreach (CodeInstruction i in instructions)
                {
                    if (!done && i.Calls(range))
                    {
                        i.opcode = OpCodes.Call;
                        i.operand = AccessTools.Method(typeof(BarkVoice), "Pick");
                        done = true;
                    }
                    yield return i;
                }
                if (!done) Plugin.Log.LogWarning("[voice] bark patch: no Random.Range in RandomSay");
            }

            static void Postfix(RandomTalkNPCModule __instance)
            {
                try
                {
                    int n = picked;
                    picked = -1;
                    if (n < 0 || __instance.phrases == null || n >= __instance.phrases.Length) return;
                    // A wordless clip (seers' jargon, creature sounds) keeps playing as in the original: no German on top.
                    AudioClip[] g = __instance.greetings;
                    if (g != null && n < g.Length && g[n] != null && !GreetingMute.IsSpeech(g[n].name)) return;
                    string voice = VoiceFor(__instance);
                    if (voice == null) return;
                    string f = VoiceOver.FileFor(voice, __instance.phrases[n]);
                    if (f == null) { Plugin.Log.LogInfo("[voice] no bark file: " + voice + " / " + __instance.phrases[n]); return; }
                    Instance.Play(f);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[voice] bark: " + e.Message); }
            }
        }
    }
}
