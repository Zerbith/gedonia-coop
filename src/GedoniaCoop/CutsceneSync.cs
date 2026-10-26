using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Cutscenes watched together (owner's wish).
    //
    // Gedonia's cutscenes are TimelineCutsceneController prefabs that story
    // functions instantiate and start (StartCutscene: player hidden, camera
    // and UI off, director plays; ExitCutscene runs exitMessage). Story
    // functions run on every machine (StorySync), so everyone gets the same
    // cutscene - but each at its own moment, and while one watched, the other
    // played on.
    //  - In co-op StartCutscene is held. Everyone is told and confirms by holding
    //    the interaction key (F) for 1.5 s - a ring fills up (owner's wish);
    //    (also those whose own copy has not started yet); the host starts it
    //    for all once every player has confirmed. No timeout: it waits for
    //    everyone (owner's choice).
    //  - While a cutscene plays here, other players' figures are hidden: in
    //    the cutscene only your own character is seen.
    public class CutsceneSync
    {
        const byte KPending = 0, KReady = 1, KGo = 2;

        class Wait
        {
            public string Key, Name;
            public TimelineCutsceneController Held; // ours, waiting
            public readonly HashSet<int> Ready = new HashSet<int>();
            public bool Go;
            public float GoAt, SeenAt;
        }

        readonly Net net;
        readonly Func<int, string> nameOf;
        readonly Func<IEnumerable<int>> peers;
        public static CutsceneSync Instance;
        static bool releasing;
        readonly Dictionary<string, Wait> waits = new Dictionary<string, Wait>();
        float nextNote, nextAnnounce, nextPlayingCheck;
        public static bool PlayingNow; // Playing, checked 4 times a second (puppets hide meanwhile)
        public int Held, Started, ReadySent;

        public CutsceneSync(Net net, Func<int, string> nameOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.nameOf = nameOf;
            this.peers = peers;
            Instance = this;
        }

        int Me { get { return net.LocalId; } }
        bool Connected { get { foreach (int p in peers()) return true; return false; } }
        string NameOf(int id) { return id == Me ? Plugin.LocalName : (nameOf(id) ?? "?"); }

        static string KeyOf(TimelineCutsceneController c) { return c.gameObject.name.Replace("(Clone)", "").Trim(); }

        // A cutscene of ours plays right now (its controller runs while it plays).
        public static bool Playing
        {
            get
            {
                foreach (TimelineCutsceneController c in UnityEngine.Object.FindObjectsOfType<TimelineCutsceneController>())
                    if (c.enabled) return true;
                return false;
            }
        }

        Wait Get(string key)
        {
            Wait w;
            if (!waits.TryGetValue(key, out w)) { w = new Wait { Key = key, Name = key }; waits[key] = w; }
            w.SeenAt = Time.realtimeSinceStartup;
            return w;
        }

        void Send(byte kind, string key)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter wr = new BinaryWriter(ms))
            {
                wr.Write(kind); wr.Write(key);
                wr.Flush();
                net.SendMessage(Net.MsgCutscene, 0, ms.ToArray(), true);
            }
        }

        // false: held until everyone is ready.
        bool OnStart(TimelineCutsceneController c)
        {
            if (releasing || !Connected) return true;
            string key = KeyOf(c);
            Wait w = Get(key);
            if (w.Go)
            {
                // Everyone already confirmed (our story event came late): play it now.
                waits.Remove(key);
                Started++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Cutscene '" + key + "' starts at once (everyone confirmed before it began here)");
                return true;
            }
            w.Held = c;
            c.gameObject.SetActive(false); // its actors stand in the world until it plays
            Held++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Cutscene '" + key + "' held until every player confirms (hold F)");
            Send(KPending, key);
            nextNote = 0f;
            return false;
        }

        void Confirm()
        {
            foreach (Wait w in waits.Values)
            {
                if (w.Go || w.Ready.Contains(Me)) continue;
                w.Ready.Add(Me);
                ReadySent++;
                Send(KReady, w.Key);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Cutscene '" + w.Key + "': ready");
                if (net.IsHost) CheckAll(w);
                nextNote = 0f;
                return; // one at a time
            }
        }

        // Host: everyone in the session ready -> go.
        void CheckAll(Wait w)
        {
            if (!net.IsHost || w.Go) return;
            if (!w.Ready.Contains(Me)) return;
            foreach (int p in peers()) if (!w.Ready.Contains(p)) return;
            Send(KGo, w.Key);
            Begin(w);
        }

        void Begin(Wait w)
        {
            w.Go = true;
            w.GoAt = Time.realtimeSinceStartup;
            if (w.Held == null) { Plugin.Log.LogInfo(Plugin.Stamp() + " Cutscene '" + w.Key + "': everyone ready; it is not here yet (plays when it starts)"); return; }
            TimelineCutsceneController c = w.Held;
            if (c == null) { waits.Remove(w.Key); return; }
            c.gameObject.SetActive(true);
            waits.Remove(w.Key);
            releasing = true;
            try { c.StartCutscene(); Started++; }
            catch (Exception e) { Plugin.Log.LogError("Cutscene '" + w.Key + "' failed to start: " + e); }
            finally { releasing = false; }
            Plugin.Log.LogInfo(Plugin.Stamp() + " Cutscene '" + w.Key + "' started for everyone");
        }

        void Handle(Net.Message m)
        {
            if (m.Sender == Me) return;
            byte kind; string key;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload))) { kind = r.ReadByte(); key = r.ReadString(); }
            Wait w = Get(key);
            if (kind == KPending)
            {
                Plugin.Log.LogInfo(Plugin.Stamp() + " Cutscene '" + key + "' waits at " + NameOf(m.Sender));
                nextNote = 0f;
            }
            else if (kind == KReady)
            {
                w.Ready.Add(m.Sender);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Cutscene '" + key + "': " + NameOf(m.Sender) + " is ready");
                CheckAll(w);
                nextNote = 0f;
            }
            else if (kind == KGo) Begin(w);
        }

        public void Tick(float now)
        {
            bool inWorld = PlayerScript.instance != null && SceneManager.GetActiveScene().name != "MainMenu";
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgCutscene || !inWorld) continue;
                try { Handle(m); }
                catch (Exception e) { Plugin.Log.LogError("Cutscene message from " + m.Sender + " failed: " + e); }
            }
            if (now >= nextPlayingCheck) { nextPlayingCheck = now + 0.25f; PlayingNow = inWorld && Playing; }
            if (!inWorld) return;
            // Alone again: what we hold plays now.
            if (!Connected)
            {
                foreach (Wait w in new List<Wait>(waits.Values)) if (w.Held != null) Begin(w);
                waits.Clear();
                showRing = false;
                return;
            }
            // Someone who left can't confirm any more.
            if (net.IsHost) foreach (Wait w in new List<Wait>(waits.Values)) CheckAll(w);
            // Forget finished or stale entries.
            List<string> old = new List<string>();
            foreach (Wait w in waits.Values)
                if ((w.Go && now - w.GoAt > 120f) || (w.Held == null && !w.Go && now - w.SeenAt > 600f)) old.Add(w.Key);
            foreach (string k in old) waits.Remove(k);
            // Remind the others now and then (a late joiner, a lost message).
            if (now >= nextAnnounce)
            {
                nextAnnounce = now + 5f;
                foreach (Wait w in waits.Values) if (w.Held != null) Send(KPending, w.Key);
            }
            HoldKey(Time.unscaledDeltaTime);
            if (now >= nextNote) { nextNote = now + 0.5f; Note(); }
        }

        void Note()
        {
            foreach (Wait w in waits.Values)
            {
                if (w.Go) continue;
                List<string> waiting = new List<string>();
                if (!w.Ready.Contains(Me)) waiting.Add(NameOf(Me));
                foreach (int p in peers()) if (!w.Ready.Contains(p)) waiting.Add(NameOf(p));
                string who = string.Join(", ", waiting.ToArray());
                string en = w.Ready.Contains(Me) ? "Cutscene: you are ready - waiting for " + who
                                                 : "A cutscene is about to start. Hold F when you are ready to watch it together" + (waiting.Count > 1 ? " (waiting for " + who + ")" : "");
                string de = w.Ready.Contains(Me) ? "Zwischensequenz: du bist bereit - es fehlt noch " + who
                                                 : "Eine Zwischensequenz beginnt gleich. Halte F gedrückt, wenn du bereit bist, sie gemeinsam anzusehen" + (waiting.Count > 1 ? " (es fehlen: " + who + ")" : "");
                if (Plugin.Ui != null) Plugin.Ui.ShowNote(Lang.T(en, de), 1f);
                return;
            }
        }

        public string Summary()
        {
            return "cutscenes held=" + Held + " started=" + Started + " ready=" + ReadySent + " waiting=" + waits.Count;
        }

        public string Describe()
        {
            List<string> s = new List<string>();
            foreach (Wait w in waits.Values)
            {
                List<string> r = new List<string>();
                foreach (int p in w.Ready) r.Add(NameOf(p));
                s.Add("'" + w.Key + "' held=" + (w.Held != null) + " go=" + w.Go + " ready=[" + string.Join(",", r.ToArray()) + "]");
            }
            return Summary() + " playing=" + Playing + " " + string.Join("; ", s.ToArray());
        }

        // ---------- hold F ----------

        const float HoldSeconds = 1.5f;
        static float hold;       // 0..1 while F is held
        static bool showRing;    // a cutscene waits for us (or we are ready and wait for others)
        static bool readyShown;
        public static float TestHoldUntil = -1f; // test: F counts as held until then
        static Texture2D[] rings;

        // Our confirmation still open: F belongs to the ring, not to talking/looting.
        static bool WaitingForMe
        {
            get
            {
                if (Instance == null) return false;
                foreach (Wait w in Instance.waits.Values) if (!w.Go && !w.Ready.Contains(Instance.Me)) return true;
                return false;
            }
        }

        void HoldKey(float dt)
        {
            bool waiting = false, ready = false;
            foreach (Wait w in waits.Values) { if (w.Go) continue; if (w.Ready.Contains(Me)) ready = true; else waiting = true; }
            showRing = waiting || ready;
            readyShown = ready && !waiting;
            if (!waiting) { hold = ready ? 1f : 0f; return; }
            bool down = Time.realtimeSinceStartup < TestHoldUntil;
            try { if (!down && vp_Input.Instance != null) down = vp_Input.Instance.DoGetButton("Interact"); } catch (Exception) { }
            if (!down) down = Input.GetKey(KeyCode.F);
            // Let go too early: the ring empties again.
            hold = down ? hold + dt / HoldSeconds : Mathf.Max(0f, hold - dt * 3f);
            if (hold >= 1f) { hold = 1f; Confirm(); }
        }

        static Texture2D Ring(int step, bool done)
        {
            const int Steps = 32, Size = 96;
            if (rings == null) rings = new Texture2D[(Steps + 1) * 2];
            int k = step + (done ? Steps + 1 : 0);
            if (rings[k] != null) return rings[k];
            Texture2D t = new Texture2D(Size, Size, TextureFormat.ARGB32, false);
            Color fill = done ? new Color(0.45f, 0.95f, 0.45f, 1f) : new Color(1f, 1f, 1f, 0.95f);
            Color empty = new Color(0f, 0f, 0f, 0.45f), none = new Color(0f, 0f, 0f, 0f);
            float c = (Size - 1) / 2f;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float dx = x - c, dy = y - c, r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (r > 46f || r < 34f) { t.SetPixel(x, y, r < 34f ? new Color(0f, 0f, 0f, 0.35f) : none); continue; }
                    // Clockwise from the top (texture y goes up).
                    float a = Mathf.Atan2(dx, dy) / (2f * Mathf.PI);
                    if (a < 0f) a += 1f;
                    t.SetPixel(x, y, a <= step / (float)Steps ? fill : empty);
                }
            t.Apply();
            rings[k] = t;
            return t;
        }

        // Called from Plugin.OnGUI: the ring under the note at the top.
        public static void DrawRing()
        {
            if (!showRing || Event.current.type != EventType.Repaint) return;
            int step = Mathf.Clamp(Mathf.RoundToInt(hold * 32f), 0, 32);
            float size = Mathf.Round(Screen.height * 0.08f);
            Rect r = new Rect((Screen.width - size) / 2f, Screen.height * 0.22f, size, size);
            GUI.DrawTexture(r, Ring(step, readyShown));
            GUIStyle s = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(size * 0.42f), fontStyle = FontStyle.Bold };
            s.normal.textColor = Color.white;
            GUI.Label(r, readyShown ? "✓" : "F", s);
        }

        // While our confirmation is open, F doesn't also talk, loot or bind.
        [HarmonyPatch(typeof(vp_Input), "GetButtonDown")]
        static class InteractPatch
        {
            static bool Prefix(string button, ref bool __result)
            {
                if (button != "Interact" || !WaitingForMe) return true;
                __result = false;
                return false;
            }
        }

        public static void TestConfirm() { if (Instance != null) Instance.Confirm(); }
        public static float Hold { get { return hold; } }

        [HarmonyPatch(typeof(TimelineCutsceneController), "StartCutscene")]
        static class StartPatch
        {
            static bool Prefix(TimelineCutsceneController __instance)
            {
                if (Instance == null) return true;
                try { return Instance.OnStart(__instance); }
                catch (Exception e) { Plugin.Log.LogError("Cutscene hold failed: " + e); return true; }
            }
        }
    }
}
