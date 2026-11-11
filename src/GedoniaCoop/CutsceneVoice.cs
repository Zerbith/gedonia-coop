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
            static void Postfix(VoiceOverClip __instance, Playable __result, object[] __args)
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
                    if (m != null || f != null || instance.IsGerman(b.voice) || instance.IsGerman(b.voiceFemale)) AddGain(__args[1] as GameObject, __instance);
                }
                catch (Exception e) { Plugin.Log.LogWarning("[voice] cutscene clip: " + e.Message); }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(AudioPlayableAsset), "CreatePlayable")]
        static class AudioAssetPatch
        {
            static void Prefix(AudioPlayableAsset __instance, object[] __args)
            {
                if (instance == null || !instance.loaded || !VoiceOver.Enabled || !Lang.German) return;
                if (instance.IsGerman(__instance.clip)) AddGain(__args[1] as GameObject, __instance);
                AudioClip de = instance.For(__instance.clip);
                if (de == null) return;
                AddGain(__args[1] as GameObject, __instance);
                if (!instance.originals.ContainsKey(__instance)) instance.originals[__instance] = new AudioClip[] { __instance.clip };
                __instance.clip = de;
                Plugin.Log.LogInfo("[voice] cutscene audio German: " + de.name);
            }
        }

        // VoiceOverBehaviour skips every clip while the game's "voices" option is off, and the German
        // takes sit in those clips. A German take always plays (it is switched off with [VoiceOver]
        // Enabled); originals without one (laughs, screams, groans) keep following the option.
        // AudioPlayableAsset clips never looked at the option.
        [HarmonyLib.HarmonyPatch(typeof(VoiceOverBehaviour), "ProcessFrame")]
        static class OptionPatch
        {
            static void Prefix(VoiceOverBehaviour __instance, ref int ___voiceInteger)
            {
                if (___voiceInteger == 1 || instance == null || !instance.loaded) return;
                if (instance.IsGerman(__instance.voice) || instance.IsGerman(__instance.voiceFemale)) ___voiceInteger = 1;
            }
        }

        // The voice-over slider also reaches the source a German clip plays on. A track that shares its
        // source with music or sounds gets those louder too.
        // Known issue A10 (music louder too above 100 %):
        //  - the voice track has a source of its own -> gain there as before;
        //  - the source is shared with other tracks (music, sounds) -> the voice track
        //    gets its own copy of the source and only that copy is boosted;
        //  - voice and other sounds sit on the SAME track -> that source is capped at
        //    100 % (VoiceGain.Cap): the music never gets louder, that voice not above 100 %.
        public static int OwnSources, CappedSources;
        static void AddGain(GameObject owner, PlayableAsset asset)
        {
            PlayableDirector dir = owner != null ? owner.GetComponent<PlayableDirector>() : null;
            TimelineAsset t = dir != null ? dir.playableAsset as TimelineAsset : null;
            if (t == null) return;
            foreach (TrackAsset track in t.GetOutputTracks())
                foreach (TimelineClip tc in track.GetClips())
                {
                    if (tc.asset != asset) continue;
                    AudioSource s = dir.GetGenericBinding(track) as AudioSource;
                    if (s == null) return;
                    if (Plugin.NoFix("voicegain")) { if (s.GetComponent<VoiceGain>() == null) s.gameObject.AddComponent<VoiceGain>(); return; }
                    bool mixed = false;
                    foreach (TimelineClip o in track.GetClips()) if (!IsVoiceAsset(o.asset as PlayableAsset)) mixed = true;
                    bool shared = false;
                    foreach (TrackAsset other in t.GetOutputTracks())
                        if (other != track && dir.GetGenericBinding(other) as AudioSource == s) shared = true;
                    if (mixed)
                    {
                        VoiceGain g = s.GetComponent<VoiceGain>() ?? s.gameObject.AddComponent<VoiceGain>();
                        if (!g.Cap) { g.Cap = true; CappedSources++; Plugin.Log.LogInfo("[voice] source '" + s.name + "' capped at 100 % (voice and other sounds on track '" + track.name + "')"); }
                        return;
                    }
                    if (shared)
                    {
                        // A copy of the source next to it, for this track only.
                        GameObject go = new GameObject("CoopVoiceSource (" + track.name + ")");
                        go.transform.SetParent(s.transform, false);
                        AudioSource v = go.AddComponent<AudioSource>();
                        v.outputAudioMixerGroup = s.outputAudioMixerGroup; v.volume = s.volume; v.pitch = s.pitch;
                        v.spatialBlend = s.spatialBlend; v.priority = s.priority; v.minDistance = s.minDistance; v.maxDistance = s.maxDistance;
                        v.rolloffMode = s.rolloffMode; v.dopplerLevel = s.dopplerLevel; v.spread = s.spread; v.reverbZoneMix = s.reverbZoneMix;
                        v.bypassEffects = s.bypassEffects; v.bypassListenerEffects = s.bypassListenerEffects; v.ignoreListenerPause = s.ignoreListenerPause;
                        v.playOnAwake = false;
                        go.AddComponent<VoiceGain>();
                        dir.SetGenericBinding(track, v);
                        OwnSources++;
                        Plugin.Log.LogInfo("[voice] track '" + track.name + "' gets its own source (shared '" + s.name + "' with other tracks)");
                        return;
                    }
                    if (s.GetComponent<VoiceGain>() != null) return;
                    s.gameObject.AddComponent<VoiceGain>();
                    Plugin.Log.LogInfo("[voice] gain on source '" + s.name + "'");
                    return;
                }
        }

        // Test step voicesurvey: every cutscene prefab (Resources/Cutscenes) and loaded timeline -
        // which audio sources carry voice clips, and whether they share the source with other
        // tracks or the track with other sounds (what A10 is about).
        public static IEnumerator SurveyScenes(string[] scenes)
        {
            foreach (string sc in scenes)
            {
                if (string.IsNullOrEmpty(sc) || SceneManager.GetSceneByName(sc).isLoaded) continue;
                AsyncOperation op = SceneManager.LoadSceneAsync(sc, LoadSceneMode.Additive);
                if (op == null) { Plugin.Log.LogWarning("[voicesurvey] scene " + sc + " cannot be loaded"); continue; }
                while (!op.isDone) yield return null;
                Plugin.Log.LogInfo("[voicesurvey] scene " + sc + " loaded");
            }
            yield return null;
            Survey();
        }

        public static void Survey()
        {
            List<PlayableDirector> dirs = new List<PlayableDirector>();
            foreach (GameObject g in Resources.LoadAll<GameObject>("Cutscenes")) foreach (PlayableDirector d in g.GetComponentsInChildren<PlayableDirector>(true)) dirs.Add(d);
            foreach (PlayableDirector d in Resources.FindObjectsOfTypeAll<PlayableDirector>()) if (d.gameObject.scene.IsValid() && !dirs.Contains(d)) dirs.Add(d);
            int own = 0, shared = 0, mixed = 0;
            foreach (PlayableDirector dir in dirs)
            {
                TimelineAsset t = dir.playableAsset as TimelineAsset;
                if (t == null) continue;
                foreach (TrackAsset track in t.GetOutputTracks())
                {
                    bool voice = false, other = false;
                    foreach (TimelineClip c in track.GetClips()) { if (c.asset is VoiceOverClip || (c.asset is AudioPlayableAsset && IsVoiceAsset(c.asset as PlayableAsset))) voice = true; else if (c.asset is AudioPlayableAsset) other = true; }
                    if (!voice) continue;
                    AudioSource s = dir.GetGenericBinding(track) as AudioSource;
                    bool sh = false;
                    foreach (TrackAsset o in t.GetOutputTracks()) if (o != track && s != null && dir.GetGenericBinding(o) as AudioSource == s) sh = true;
                    string kind = other ? "MIXED" : sh ? "SHARED" : "own";
                    if (other) mixed++; else if (sh) shared++; else own++;
                    if (kind != "own") Plugin.Log.LogInfo("[voicesurvey] " + t.name + " track '" + track.name + "' source '" + (s != null ? s.name : "-") + "': " + kind);
                }
            }
            Plugin.Log.LogInfo("[voicesurvey] directors=" + dirs.Count + " voice tracks: own=" + own + " shared=" + shared + " mixed=" + mixed);
        }

        // A voice clip: a VoiceOverClip, or audio that is a German take or a game voice line.
        static bool IsVoiceAsset(PlayableAsset a)
        {
            if (a is VoiceOverClip) return true;
            AudioPlayableAsset ap = a as AudioPlayableAsset;
            if (ap == null) return true; // not audio: no sound of its own
            if (ap.clip == null) return true;
            if (instance != null && (instance.IsGerman(ap.clip) || instance.For(ap.clip) != null)) return true;
            return false;
        }

        bool IsGerman(AudioClip c) { return c != null && c.name.StartsWith("de_", StringComparison.Ordinal); }

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
