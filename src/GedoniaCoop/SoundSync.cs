using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Sounds of other players and of enemies simulated elsewhere.
    //
    // Most player sounds come from animation events (Footsteps.FootR/Whoosh/
    // ClothFoley...) and code (jump, damage, bow, sheath) on the local player;
    // the puppet clone has no Footsteps and fires no events, so it was silent.
    // A mirrored enemy skips its AI (attack, aggro, idle sounds) and its local
    // hit (hurt, impact sounds), which all happen on the owner.
    //  - Every AudioSource.PlayOneShot/Play on our own player, or on an enemy or
    //    follower we simulate, is sent: actor (0 = our player, else the shared
    //    id), the source's path below the actor, clip name, volume, pitch.
    //  - Receivers play the clip on the puppet (a 3D source made like ours) or
    //    on the mirror's own source at the same path.
    //  - A clip the mirror already played here within a second (e.g. its death,
    //    which runs on every machine) is not played twice.
    public class SoundSync
    {
        struct Entry { public int Actor; public string Path, Clip; public float Volume, Pitch; }

        readonly Net net;
        readonly Func<int, Puppet> puppetOf;
        readonly Func<IEnumerable<int>> peers;
        public static SoundSync Instance;
        static bool playing; // playing a remote sound: don't send it back

        readonly List<Entry> outbox = new List<Entry>();
        float nextFlush, windowStart;
        int sentInWindow;
        const int MaxPerSecond = 40;

        // Clip lookup by name (all loaded clips), rebuilt on a miss at most every 10 s.
        Dictionary<string, AudioClip> clips;
        float clipsBuiltAt = -100f;
        readonly HashSet<string> missingClips = new HashSet<string>();
        // Local plays per (source, clip), for the double-play check.
        readonly Dictionary<long, float> localPlays = new Dictionary<long, float>();
        // Our 3D sources on puppets, per (player, path).
        readonly Dictionary<string, AudioSource> puppetSources = new Dictionary<string, AudioSource>();

        public int Sent, Dropped, Played, Unknown, NoTarget, Deduped;
        readonly Dictionary<string, int> playedByClip = new Dictionary<string, int>();

        public SoundSync(Net net, Func<int, Puppet> puppetOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.puppetOf = puppetOf;
            this.peers = peers;
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        static long Key(AudioSource s, AudioClip c) { return ((long)s.GetInstanceID() << 32) ^ (uint)c.GetInstanceID(); }

        // ---------- sending ----------

        static string PathBelow(Transform t, Transform root)
        {
            if (t == root) return "";
            List<string> parts = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        void OnLocalPlay(AudioSource src, AudioClip clip, float volume)
        {
            if (playing) return;
            float now = Time.realtimeSinceStartup;
            localPlays[Key(src, clip)] = now;
            if (localPlays.Count > 2000) localPlays.Clear();
            if (!Connected) return;

            PlayerScript player = PlayerScript.instance;
            Transform t = src.transform;
            int actor;
            Transform root;
            if (player != null && t.IsChildOf(player.transform)) { actor = 0; root = player.transform; }
            else if (EnemySync.Instance == null || !EnemySync.Instance.SimulatedActor(t, out actor, out root)) return;

            if (now - windowStart >= 1f) { windowStart = now; sentInWindow = 0; }
            if (sentInWindow >= MaxPerSecond) { Dropped++; return; }
            sentInWindow++;
            outbox.Add(new Entry { Actor = actor, Path = PathBelow(t, root), Clip = clip.name, Volume = volume, Pitch = src.pitch });
        }

        void Flush()
        {
            if (outbox.Count == 0) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((short)outbox.Count);
                foreach (Entry e in outbox)
                {
                    w.Write(e.Actor); w.Write(e.Path); w.Write(e.Clip); w.Write(e.Volume); w.Write(e.Pitch);
                }
                w.Flush();
                net.SendMessage(Net.MsgSounds, 0, ms.ToArray(), false);
            }
            Sent += outbox.Count;
            outbox.Clear();
        }

        // Harmony: every one-shot and every non-looping Play.
        [HarmonyPatch(typeof(AudioSource), "PlayOneShot", new Type[] { typeof(AudioClip), typeof(float) })]
        static class OneShotPatch
        {
            static void Postfix(AudioSource __instance, AudioClip clip, float volumeScale)
            {
                SoundSync s = Instance;
                if (s == null || clip == null || __instance == null) return;
                try { s.OnLocalPlay(__instance, clip, volumeScale); }
                catch (Exception e) { Plugin.Log.LogWarning("Sound capture failed: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(AudioSource), "Play", new Type[0])]
        static class PlayPatch
        {
            static void Postfix(AudioSource __instance)
            {
                SoundSync s = Instance;
                if (s == null || __instance == null || __instance.clip == null || __instance.loop) return;
                try { s.OnLocalPlay(__instance, __instance.clip, 1f); }
                catch (Exception e) { Plugin.Log.LogWarning("Sound capture failed: " + e.Message); }
            }
        }

        // ---------- receiving ----------

        AudioClip FindClip(string name)
        {
            AudioClip c;
            if (clips != null && clips.TryGetValue(name, out c) && c != null) return c;
            float now = Time.realtimeSinceStartup;
            if (clips == null || now - clipsBuiltAt > 10f)
            {
                clipsBuiltAt = now;
                clips = new Dictionary<string, AudioClip>();
                foreach (AudioClip a in Resources.FindObjectsOfTypeAll<AudioClip>())
                    if (a != null && !clips.ContainsKey(a.name)) clips[a.name] = a;
                if (clips.TryGetValue(name, out c)) return c;
            }
            return null;
        }

        // A 3D source on the puppet, set up like our own player's source at that path.
        AudioSource PuppetSource(int player, Puppet p, string path)
        {
            string key = player + "|" + path;
            AudioSource src;
            if (puppetSources.TryGetValue(key, out src) && src != null) return src;
            GameObject go = new GameObject("CoopSound " + path);
            go.transform.SetParent(p.Root.transform, false);
            go.transform.localPosition = new Vector3(0f, 1f, 0f);
            src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            Transform mine = PlayerScript.instance != null ? (path.Length == 0 ? PlayerScript.instance.transform : PlayerScript.instance.transform.Find(path)) : null;
            AudioSource template = mine != null ? mine.GetComponent<AudioSource>() : null;
            if (template != null)
            {
                src.outputAudioMixerGroup = template.outputAudioMixerGroup;
                src.volume = template.volume;
                src.priority = template.priority;
                src.rolloffMode = template.rolloffMode;
                src.minDistance = template.minDistance;
                src.maxDistance = template.maxDistance;
                src.spatialBlend = template.spatialBlend;
                go.transform.localPosition = mine.position - PlayerScript.instance.transform.position;
            }
            // Our own player's sounds may be 2D (heard at the camera); someone
            // else's must come from where they stand.
            if (src.spatialBlend < 0.9f)
            {
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = 3f;
                src.maxDistance = 50f;
            }
            src.dopplerLevel = 0f;
            puppetSources[key] = src;
            return src;
        }

        void Apply(Net.Message m)
        {
            if (m.Sender == net.LocalId || PlayerScript.instance == null) return;
            float now = Time.realtimeSinceStartup;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int n = r.ReadInt16();
                for (int k = 0; k < n; k++)
                {
                    int actor = r.ReadInt32();
                    string path = r.ReadString(), clipName = r.ReadString();
                    float volume = r.ReadSingle(), pitch = r.ReadSingle();

                    AudioSource src = null;
                    if (actor == 0)
                    {
                        Puppet p = puppetOf(m.Sender);
                        if (p != null && Plugin.SameWorld(m.Sender) && p.Root.activeInHierarchy) src = PuppetSource(m.Sender, p, path);
                    }
                    else if (EnemySync.Instance != null)
                    {
                        Transform root = EnemySync.Instance.MirrorRoot(actor);
                        if (root != null)
                        {
                            Transform at = path.Length == 0 ? root : root.Find(path);
                            src = at != null ? at.GetComponent<AudioSource>() : null;
                            if (src == null) src = root.GetComponentInChildren<AudioSource>();
                        }
                    }
                    if (src == null || !src.isActiveAndEnabled) { NoTarget++; continue; }

                    AudioClip clip = FindClip(clipName);
                    if (clip == null)
                    {
                        Unknown++;
                        if (missingClips.Add(clipName)) Plugin.Log.LogInfo("Sound clip '" + clipName + "' from player " + m.Sender + " is not loaded here");
                        continue;
                    }
                    float at2;
                    if (localPlays.TryGetValue(Key(src, clip), out at2) && now - at2 < 1f) { Deduped++; continue; }

                    playing = true;
                    try
                    {
                        src.pitch = Mathf.Clamp(pitch, 0.1f, 3f);
                        src.PlayOneShot(clip, Mathf.Clamp(volume, 0f, 2f));
                    }
                    finally { playing = false; }
                    Played++;
                    int c;
                    playedByClip.TryGetValue(clipName, out c);
                    playedByClip[clipName] = c + 1;
                }
            }
        }

        public void Tick(float now)
        {
            bool inWorld = PlayerScript.instance != null && SceneManager.GetActiveScene().name != "MainMenu";
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgSounds || !inWorld) continue;
                try { Apply(m); }
                catch (Exception e) { Plugin.Log.LogError("Sound message from " + m.Sender + " failed: " + e); }
            }
            if (now >= nextFlush)
            {
                nextFlush = now + 0.05f;
                if (inWorld && Connected) Flush(); else outbox.Clear();
            }
        }

        public string Summary()
        {
            List<KeyValuePair<string, int>> byCount = new List<KeyValuePair<string, int>>(playedByClip);
            byCount.Sort((a, b) => b.Value.CompareTo(a.Value));
            List<string> top = new List<string>();
            foreach (KeyValuePair<string, int> kv in byCount) if (top.Count < 25) top.Add(kv.Key + "x" + kv.Value);
            return "sounds sent=" + Sent + " capped=" + Dropped + " played=" + Played + " deduped=" + Deduped + " unknown=" + Unknown + " noTarget=" + NoTarget +
                (top.Count > 0 ? " [" + string.Join(",", top.ToArray()) + "]" : "");
        }
    }
}
