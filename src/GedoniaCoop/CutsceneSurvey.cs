using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace GedoniaCoop
{
    // Test-only survey for a possible German cutscene voice-over: every timeline the
    // loaded scenes hold, with its subtitle clips (SubtitleClip text, jaw), the game's
    // own voice clips (VoiceOverClip male/female) and plain audio clips, each with its
    // start and duration. Runs with every text export (textexport / textexportall),
    // writes BepInEx/coop-cutscenes.tsv:
    //   timeline, director object, scene, track type, start, duration, kind, text or clip, clip length
    public static class CutsceneSurvey
    {
        static readonly HashSet<string> seen = new HashSet<string>();
        static bool prefabsLoaded;

        // Most story cutscenes are prefabs under Resources/Cutscenes that the game only
        // instantiates when the story gets there; load them (not instantiate) so their
        // timelines and subtitle texts are in memory for the export.
        public static void LoadPrefabs()
        {
            if (prefabsLoaded) return;
            prefabsLoaded = true;
            UnityEngine.Object[] all = Resources.LoadAll("Cutscenes", typeof(GameObject));
            Plugin.Log.LogInfo("[cutscenes] loaded " + all.Length + " cutscene prefabs from Resources/Cutscenes");
        }

        public static void Collect()
        {
            StringBuilder sb = new StringBuilder();
            int rows = 0;
            Dictionary<PlayableAsset, string> where = new Dictionary<PlayableAsset, string>();
            foreach (PlayableDirector d in Resources.FindObjectsOfTypeAll<PlayableDirector>())
                if (d.playableAsset != null && !where.ContainsKey(d.playableAsset))
                    where[d.playableAsset] = d.gameObject.name + "\t" + (d.gameObject.scene.IsValid() ? d.gameObject.scene.name : "(asset)");
            foreach (TimelineAsset t in Resources.FindObjectsOfTypeAll<TimelineAsset>())
            {
                string loc;
                if (!where.TryGetValue(t, out loc)) loc = "-\t-";
                foreach (TrackAsset track in t.GetOutputTracks())
                {
                    foreach (TimelineClip c in track.GetClips())
                    {
                        string kind = null, what = null;
                        float len = 0f;
                        SubtitleClip sub = c.asset as SubtitleClip;
                        VoiceOverClip vo = c.asset as VoiceOverClip;
                        AudioPlayableAsset au = c.asset as AudioPlayableAsset;
                        if (sub != null) { kind = "subtitle"; what = (sub.subtitleText ?? "") + " [jaw " + sub.jaw + "]"; }
                        else if (vo != null)
                        {
                            kind = vo.isPlayer ? "voice-player" : "voice";
                            what = (vo.voiceClip != null ? vo.voiceClip.name : "-") + (vo.voiceClipFemale != null ? " / " + vo.voiceClipFemale.name : "");
                            len = vo.voiceClip != null ? vo.voiceClip.length : 0f;
                        }
                        else if (au != null) { kind = "audio"; what = au.clip != null ? au.clip.name : "-"; len = au.clip != null ? au.clip.length : 0f; }
                        else continue;
                        string key = t.name + "|" + track.name + "|" + c.start.ToString("F2") + "|" + kind;
                        if (!seen.Add(key)) continue;
                        sb.Append(t.name).Append('\t').Append(loc).Append('\t').Append(track.GetType().Name).Append('\t')
                          .Append(c.start.ToString("F2")).Append('\t').Append(c.duration.ToString("F2")).Append('\t')
                          .Append(kind).Append('\t').Append(what.Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ")).Append('\t')
                          .Append(len.ToString("F2")).Append('\n');
                        rows++;
                    }
                }
            }
            // Who speaks: a subtitle's jaw index points into the bound SubtitlesContainer's jaws
            // (the speaking character's jaw bone) -> BepInEx/coop-cutscene-jaws.tsv: timeline, jaw, path.
            StringBuilder jaws = new StringBuilder();
            foreach (PlayableDirector d in Resources.FindObjectsOfTypeAll<PlayableDirector>())
            {
                TimelineAsset t = d.playableAsset as TimelineAsset;
                if (t == null) continue;
                foreach (TrackAsset track in t.GetOutputTracks())
                {
                    if (!(track is SubtitleTrack)) continue;
                    SubtitlesContainer sc = d.GetGenericBinding(track) as SubtitlesContainer;
                    if (sc == null) sc = d.GetComponentInChildren<SubtitlesContainer>(true);
                    if (sc == null || sc.jaws == null) continue;
                    for (int i = 0; i < sc.jaws.Length; i++)
                    {
                        if (!seen.Add("jaw|" + t.name + "|" + i)) continue;
                        string path = "-";
                        if (sc.jaws[i] != null)
                        {
                            path = sc.jaws[i].name;
                            for (Transform p = sc.jaws[i].transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
                        }
                        jaws.Append(t.name).Append('\t').Append(i).Append('\t').Append(path).Append('\n');
                    }
                }
            }
            if (jaws.Length > 0) File.AppendAllText(Path.Combine(Paths.BepInExRootPath, "coop-cutscene-jaws.tsv"), jaws.ToString(), Encoding.UTF8);
            // The intro (StartCutscene) shows its lines from arrays, not from a subtitle track.
            foreach (StartCutscene s in Resources.FindObjectsOfTypeAll<StartCutscene>())
                if (s.lines != null)
                    for (int i = 0; i < s.lines.Length; i++)
                        if (seen.Add("intro|" + i))
                        {
                            sb.Append("StartCutscene\t").Append(s.gameObject.name).Append("\tintro\tlines\t-\t")
                              .Append(s.durations != null && i < s.durations.Length ? s.durations[i].ToString("F2") : "-")
                              .Append("\tsubtitle\t").Append((s.lines[i] ?? "").Replace("\n", "\\n")).Append("\t0\n");
                            rows++;
                        }
            File.AppendAllText(Path.Combine(Paths.BepInExRootPath, "coop-cutscenes.tsv"), sb.ToString(), Encoding.UTF8);
            if (rows > 0) Plugin.Log.LogInfo("[cutscenes] +" + rows + " clips");
        }
    }
}
