using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace GedoniaCoop
{
    // German voices for the cutscenes that have English speech (intro, premonition,
    // origin story, demon lord, Fleshwarper's monologue). plugins/voice-de/cutscene/
    // holds one OGG per English clip and cutscenes.tsv (English clip name, file).
    // The timelines play their speech from VoiceOverClip (PlayOneShot of voiceClip /
    // voiceClipFemale) or from plain AudioPlayableAsset clips, so after every scene
    // load the clips are swapped in those assets: the game then plays German at the
    // same moment. An audio clip is cut at its timeline clip's end, so that end is
    // moved out when the German take is longer. Back to English -> originals again.
    public class CutsceneVoice : MonoBehaviour
    {
        static CutsceneVoice instance;
        readonly Dictionary<string, AudioClip> german = new Dictionary<string, AudioClip>();
        readonly Dictionary<UnityEngine.Object, AudioClip[]> originals = new Dictionary<UnityEngine.Object, AudioClip[]>();
        readonly Dictionary<TimelineClip, double> originalDurations = new Dictionary<TimelineClip, double>();
        bool loaded, loading;

        static string Dir { get { return Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(CutsceneVoice).Assembly.Location), "voice-de"), "cutscene"); } }

        public static void Init()
        {
            if (instance != null || !File.Exists(Path.Combine(Dir, "cutscenes.tsv"))) return;
            GameObject go = new GameObject("GedoniaCoopCutsceneVoice");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<CutsceneVoice>();
            SceneManager.sceneLoaded += (s, m) => instance.Apply();
        }

        void Start() { Apply(); }

        // The timeline graph copies the clips when it is built (VoiceOverClip / AudioPlayableAsset
        // CreatePlayable), which can happen before any scan above: swap right there as well.
        [HarmonyLib.HarmonyPatch(typeof(VoiceOverClip), "CreatePlayable")]
        static class VoiceOverPatch
        {
            static void Postfix(VoiceOverClip __instance, Playable __result)
            {
                if (instance == null || !instance.loaded || !VoiceOver.Enabled || !Lang.German) return;
                try
                {
                    VoiceOverBehaviour b = ((ScriptPlayable<VoiceOverBehaviour>)__result).GetBehaviour();
                    AudioClip m = instance.For(b.voice);
                    AudioClip f = __instance.isPlayer ? instance.ForFemale(b.voiceFemale) : instance.For(b.voiceFemale);
                    if (m != null) b.voice = m;
                    if (f != null) b.voiceFemale = f;
                    if (m != null || f != null) Plugin.Log.LogInfo("[voice] cutscene line German: " + (m != null ? m.name : "-") + " / " + (f != null ? f.name : "-"));
                }
                catch (Exception e) { Plugin.Log.LogWarning("[voice] cutscene clip: " + e.Message); }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(AudioPlayableAsset), "CreatePlayable")]
        static class AudioAssetPatch
        {
            static void Prefix(AudioPlayableAsset __instance)
            {
                if (instance == null || !instance.loaded || !VoiceOver.Enabled || !Lang.German) return;
                AudioClip de = instance.For(__instance.clip);
                if (de == null) return;
                if (!instance.originals.ContainsKey(__instance)) instance.originals[__instance] = new AudioClip[] { __instance.clip };
                __instance.clip = de;
                Plugin.Log.LogInfo("[voice] cutscene audio German: " + de.name);
            }
        }

        // Cutscenes made from prefabs come after the scene load: swap once more when one starts.
        [HarmonyLib.HarmonyPatch(typeof(TimelineCutsceneController), "StartCutscene")]
        static class StartPatch
        {
            static void Prefix() { if (instance != null && instance.loaded) instance.Apply(); }
        }

        // Test step "timeline:<timeline>|<scene>": loads the scene additively if needed, then starts
        // the cutscene whose director plays that timeline and logs the clips it will play.
        public static IEnumerator TestStart(string want, string scene)
        {
            if (scene != null) scene = scene.Replace(' ', '_'); // the story step parser turns "_" into " "
            if (!string.IsNullOrEmpty(scene) && !SceneManager.GetSceneByName(scene).isLoaded)
            {
                AsyncOperation op = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Additive);
                if (op == null) { Plugin.Log.LogWarning("Test: scene " + scene + " cannot be loaded"); yield break; }
                while (!op.isDone) yield return null;
                yield return null;
                Plugin.Log.LogInfo("Test: scene " + scene + " loaded=" + SceneManager.GetSceneByName(scene).isLoaded);
            }
            foreach (PlayableDirector d in Resources.FindObjectsOfTypeAll<PlayableDirector>())
            {
                if (d.playableAsset == null || d.playableAsset.name != want || !d.gameObject.scene.IsValid()) continue;
                TimelineCutsceneController tc = d.GetComponentInParent<TimelineCutsceneController>();
                if (tc == null) tc = d.GetComponentInChildren<TimelineCutsceneController>(true);
                if (tc != null) { tc.gameObject.SetActive(true); tc.StartCutscene(); } else d.Play();
                Plugin.Log.LogInfo("Test: timeline " + want + " started (" + (tc != null ? "controller" : "director") + "), clips:" + Describe(d));
                yield break;
            }
            int n = 0;
            foreach (PlayableDirector d in Resources.FindObjectsOfTypeAll<PlayableDirector>()) n++;
            Plugin.Log.LogWarning("Test: no loaded timeline " + want + " (" + n + " directors)");
        }

        // Test: the clips a timeline will play now, for the log.
        public static string Describe(PlayableDirector d)
        {
            TimelineAsset t = d.playableAsset as TimelineAsset;
            if (t == null) return "-";
            StringBuilder sb = new StringBuilder();
            foreach (TrackAsset track in t.GetOutputTracks())
                foreach (TimelineClip tc in track.GetClips())
                {
                    VoiceOverClip vo = tc.asset as VoiceOverClip;
                    AudioPlayableAsset au = tc.asset as AudioPlayableAsset;
                    if (vo != null) sb.Append(" vo:").Append(vo.voiceClip != null ? vo.voiceClip.name : "-").Append('/').Append(vo.voiceClipFemale != null ? vo.voiceClipFemale.name : "-");
                    else if (au != null) sb.Append(" au:").Append(au.clip != null ? au.clip.name : "-").Append('(').Append(tc.duration.ToString("F1")).Append(')');
                }
            return sb.ToString();
        }

        void Apply()
        {
            bool want = VoiceOver.Enabled && Lang.German;
            if (want && !loaded) { if (!loading) StartCoroutine(Load()); return; }
            try { Swap(want); } catch (Exception e) { Plugin.Log.LogWarning("[voice] cutscenes: " + e.Message); }
        }

        IEnumerator Load()
        {
            loading = true;
            foreach (string line in File.ReadAllLines(Path.Combine(Dir, "cutscenes.tsv"), Encoding.UTF8))
            {
                string[] p = line.Split('\t');
                if (p.Length < 2 || german.ContainsKey(p[0])) continue;
                using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(Path.Combine(Dir, p[1])).AbsoluteUri, AudioType.OGGVORBIS))
                {
                    // ~700 lines: keep them compressed in memory (decoded they would take a few hundred MB)
                    ((DownloadHandlerAudioClip)req.downloadHandler).compressed = true;
                    yield return req.SendWebRequest();
                    if (req.isNetworkError || req.isHttpError) { Plugin.Log.LogWarning("[voice] " + p[1] + ": " + req.error); continue; }
                    AudioClip c = DownloadHandlerAudioClip.GetContent(req);
                    c.name = "de_" + p[0];
                    DontDestroyOnLoad(c);
                    german[p[0]] = c;
                }
            }
            loaded = true;
            loading = false;
            Plugin.Log.LogInfo("[voice] cutscenes: " + german.Count + " German clips loaded");
            Apply();
        }

        AudioClip For(AudioClip original)
        {
            AudioClip c;
            return original != null && german.TryGetValue(original.name, out c) ? c : null;
        }

        // The player's female takes are listed as "f:<clip>" (one has the same name as the male take).
        AudioClip ForFemale(AudioClip original)
        {
            AudioClip c;
            return original != null && german.TryGetValue("f:" + original.name, out c) ? c : For(original);
        }

        void Swap(bool toGerman)
        {
            int swapped = 0;
            foreach (VoiceOverClip vo in Resources.FindObjectsOfTypeAll<VoiceOverClip>())
            {
                AudioClip[] orig;
                if (!originals.TryGetValue(vo, out orig))
                {
                    if (!toGerman || (For(vo.voiceClip) == null && ForFemale(vo.voiceClipFemale) == null)) continue;
                    orig = new AudioClip[] { vo.voiceClip, vo.voiceClipFemale };
                    originals[vo] = orig;
                }
                AudioClip m = toGerman ? (For(orig[0]) ?? orig[0]) : orig[0];
                AudioClip f = toGerman ? (vo.isPlayer ? ForFemale(orig[1]) : For(orig[1])) ?? orig[1] : orig[1];
                if (vo.voiceClip != m || vo.voiceClipFemale != f) { vo.voiceClip = m; vo.voiceClipFemale = f; swapped++; }
            }
            foreach (TimelineAsset t in Resources.FindObjectsOfTypeAll<TimelineAsset>())
                foreach (TrackAsset track in t.GetOutputTracks())
                    foreach (TimelineClip tc in track.GetClips())
                    {
                        AudioPlayableAsset au = tc.asset as AudioPlayableAsset;
                        if (au == null) continue;
                        AudioClip[] orig;
                        if (!originals.TryGetValue(au, out orig))
                        {
                            if (!toGerman || For(au.clip) == null) continue;
                            orig = new AudioClip[] { au.clip };
                            originals[au] = orig;
                            originalDurations[tc] = tc.duration;
                        }
                        AudioClip want = toGerman ? (For(orig[0]) ?? orig[0]) : orig[0];
                        if (au.clip == want) continue;
                        au.clip = want;
                        double d;
                        if (originalDurations.TryGetValue(tc, out d)) tc.duration = toGerman ? Math.Max(d, want.length) : d;
                        swapped++;
                    }
            if (swapped == 0) return;
            // Graphs built before the swap still hold the old clips.
            foreach (PlayableDirector dir in Resources.FindObjectsOfTypeAll<PlayableDirector>())
                if (dir.playableGraph.IsValid() && dir.state != PlayState.Playing) dir.RebuildGraph();
            Plugin.Log.LogInfo("[voice] cutscenes: " + swapped + " clips " + (toGerman ? "German" : "English"));
        }
    }
}
