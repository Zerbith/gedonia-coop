using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Text chat between all players. Enter (or the co-op menu) opens an input
    // field; Enter sends, Esc closes. Messages go reliably through the host's
    // relay and show with name and local time: the last few bottom left for a
    // while, the whole history while the field is open.
    //
    // While the field is open the game sees no keyboard at all. The game reads
    // keys three ways, so there are three guards:
    //  - vp_Input (movement, attacks, hotkeys): Harmony prefixes;
    //  - Input.GetKey* / GetAxisRaw (Esc, building keys, gamepad branch):
    //    a transpiler swaps the calls in the classes that use them;
    //  - NGUI (UIKeyBinding, key navigation): its UICamera.GetKey* delegates.
    public class ChatSync
    {
        public const int MaxLength = 200;
        const int MaxHistory = 200, RecentLines = 6;
        const float RecentSeconds = 30f, FadeSeconds = 3f;
        const string FieldName = "GedoniaCoopChat";

        class Line
        {
            public int Sender, Seq;
            public string Text;
            public float At;
        }

        readonly Net net;
        readonly Func<int, string> nameOf;
        readonly Func<IEnumerable<int>> peers;
        public static ChatSync Instance;

        readonly List<Line> history = new List<Line>();
        // Starts high enough that a player who reconnects keeps counting upwards.
        int seq = Environment.TickCount & 0x3fffffff;
        bool inputOpen, openRequested, enterHeld;
        string text = "";
        Vector2 scroll;
        public Rect PanelRect;
        public int Sent, Received;

        public ChatSync(Net net, Func<int, string> nameOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.nameOf = nameOf;
            this.peers = peers;
            Instance = this;
        }

        public static bool Typing { get { return Instance != null && Instance.inputOpen; } }

        // ---------- opening and closing ----------

        // From the co-op menu: the next Update opens the field (the menu's
        // click is an IMGUI event that has no layout pass for the new panel).
        public void RequestOpen() { openRequested = true; }

        void Open()
        {
            if (inputOpen) return;
            inputOpen = true;
            text = "";
            scroll.y = float.MaxValue;
            enterHeld = Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);
            RememberHeld();
            Log("input opened");
        }

        // Keys held at this moment may still report their release, or the game
        // would wait for it forever (aiming, a charged ability).
        void RememberHeld()
        {
            heldAtOpen.Clear();
            try
            {
                foreach (string b in new List<string>(vp_Input.Instance.Buttons.Keys))
                    if (vp_Input.Instance.DoGetButton(b)) heldAtOpen.Add(b);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Chat: could not read the held keys: " + e.Message); }
        }

        public void Close()
        {
            openRequested = false;
            if (!inputOpen) return;
            inputOpen = false;
            text = "";
            GUIUtility.keyboardControl = 0;
            Log("input closed");
        }

        // ---------- main loop ----------

        public void Tick(bool inWorld)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgChat || m.Sender == net.LocalId) continue;
                try { Receive(m); }
                catch (Exception e) { Plugin.Log.LogWarning("Chat message from " + m.Sender + " unreadable: " + e.Message); }
            }
            if (!inWorld) { Close(); return; }
            if (enterHeld && !Input.GetKey(KeyCode.Return) && !Input.GetKey(KeyCode.KeypadEnter)) enterHeld = false;
            if (openRequested) { openRequested = false; Open(); }
            else if (!inputOpen && CanOpenByKey() && (KeyDown(KeyCode.Return) || KeyDown(KeyCode.KeypadEnter))) Open();
        }

        // Not over the game's own windows (options key capture, naming a
        // companion, dialogues) or the mod's.
        static bool CanOpenByKey()
        {
            return !UICamera.inputHasFocus && InterfaceManager.instance != null && !InterfaceManager.instance.AnyPanelIsActive();
        }

        // ---------- sending and receiving ----------

        void Submit()
        {
            string clean = Clean(text);
            Close();
            if (clean.Length > 0) Send(clean, clean);
        }

        // Shown locally as "shown", sent as "wire" (they differ only in a test).
        void Send(string shown, string wire)
        {
            seq++;
            Add(net.LocalId, seq, Plugin.LocalName, shown);
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(seq); w.Write(Plugin.LocalName); w.Write(wire);
                w.Flush();
                net.SendMessage(Net.MsgChat, 0, ms.ToArray(), true);
            }
            Sent++;
            Log("sent #" + seq + ", " + shown.Length + " chars");
            bool anyone = false;
            foreach (int p in peers()) { anyone = true; break; }
            if (!anyone) AddSystem(Lang.T("(Nobody else is connected.)", "(Sonst ist niemand verbunden.)"));
        }

        void Receive(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int number = r.ReadInt32();
                string sentName = Plugin.CleanName(r.ReadString());
                string clean = Clean(r.ReadString());
                foreach (Line l in history) if (l.Sender == m.Sender && l.Seq == number) return;
                if (clean.Length == 0) return;
                string name = nameOf(m.Sender);
                if (string.IsNullOrEmpty(name)) name = sentName.Length > 0 ? sentName : "Player";
                Received++;
                Add(m.Sender, number, name, clean);
                Log("got #" + number + " from " + name + ", " + clean.Length + " chars");
            }
        }

        void Add(int sender, int number, string name, string clean)
        {
            string time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            Insert(new Line { Sender = sender, Seq = number, At = Time.realtimeSinceStartup, Text = "<color=#e8cf8f>[" + time + "] " + name + ":</color> " + clean });
        }

        void AddSystem(string line)
        {
            Insert(new Line { Sender = 0, Seq = 0, At = Time.realtimeSinceStartup, Text = "<color=#a0a0a0>" + line + "</color>" });
        }

        // Reliable packets can overtake each other, so a late one goes before
        // the same player's later messages.
        void Insert(Line line)
        {
            int at = history.Count;
            for (int i = history.Count - 1; i >= 0; i--)
                if (line.Sender != 0 && history[i].Sender == line.Sender && history[i].Seq > line.Seq) at = i;
            history.Insert(at, line);
            if (history.Count > MaxHistory) history.RemoveAt(0);
            scroll.y = float.MaxValue;
        }

        // One line of plain text, whatever came in: no control or invisible
        // characters (text direction, zero width), no rich-text tags (they
        // could recolour or resize the chat), single spaces, limited length.
        public static string Clean(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(Math.Min(s.Length, MaxLength * 2));
            bool space = true;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsSurrogate(c))
                {
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; space = false; }
                    continue;
                }
                UnicodeCategory cat = char.GetUnicodeCategory(c);
                if (c == '\t' || c == '\n' || c == '\r') c = ' ';
                else if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.LineSeparator ||
                    cat == UnicodeCategory.ParagraphSeparator || cat == UnicodeCategory.PrivateUse || cat == UnicodeCategory.OtherNotAssigned) continue;
                if (c == '<') c = '‹';
                else if (c == '>') c = '›';
                if (char.IsWhiteSpace(c))
                {
                    if (space) continue;
                    c = ' ';
                    space = true;
                }
                else space = false;
                sb.Append(c);
            }
            string clean = sb.ToString().TrimEnd();
            if (clean.Length > MaxLength)
            {
                clean = clean.Substring(0, MaxLength);
                if (char.IsHighSurrogate(clean[clean.Length - 1])) clean = clean.Substring(0, clean.Length - 1);
                clean = clean.TrimEnd();
            }
            return clean;
        }

        // ---------- IMGUI ----------

        GUIStyle lineStyle;

        public void Draw(float sw, float sh, GUIStyle panel)
        {
            if (lineStyle == null)
            {
                lineStyle = new GUIStyle(GUI.skin.label);
                lineStyle.fontSize = 14;
                lineStyle.wordWrap = true;
                lineStyle.richText = true;
                lineStyle.normal.textColor = Color.white;
                lineStyle.margin = new RectOffset(0, 0, 1, 1);
                lineStyle.padding = new RectOffset(2, 2, 1, 1);
            }
            GameSkin.Fontify(lineStyle);
            if (inputOpen) DrawInput(sw, sh, panel);
            else DrawRecent(sw, sh, panel);
        }

        // The newest lines, bottom left beside the action bar; they fade out.
        void DrawRecent(float sw, float sh, GUIStyle panel)
        {
            if (Event.current.type != EventType.Repaint || history.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            float w = Mathf.Clamp(sw * 0.28f, 260f, 520f), total = 0f, newest = 0f;
            List<Line> show = new List<Line>();
            for (int i = history.Count - 1; i >= 0 && show.Count < RecentLines; i--)
            {
                if (now - history[i].At > RecentSeconds) continue;
                show.Insert(0, history[i]);
                newest = Mathf.Max(newest, history[i].At);
                total += lineStyle.CalcHeight(new GUIContent(history[i].Text), w - 12f) + 2f;
            }
            if (show.Count == 0) return;
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((RecentSeconds - (now - newest)) / FadeSeconds));
            float y = sh - total - 16f;
            if (!GameSkin.Ready) GUI.Box(new Rect(8f, y - 4f, w, total + 8f), "", panel); // the game's own floating texts have no box, only the outline
            foreach (Line l in show)
            {
                float h = lineStyle.CalcHeight(new GUIContent(l.Text), w - 12f);
                GameSkin.Label(new Rect(14f, y, w - 12f, h), l.Text, lineStyle);
                y += h + 2f;
            }
            GUI.color = old;
        }

        void DrawInput(float sw, float sh, GUIStyle panel)
        {
            // Sending and closing wait for the end of this pass: the layout
            // made before it must keep matching what is drawn.
            bool send = false, close = false;
            Event e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape) { close = true; escGuardFrame = Time.frameCount + 1; e.Use(); }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    // Not the press (or its repeats) that opened the field.
                    if (!enterHeld) send = true;
                    e.Use();
                }
            }
            float w = Mathf.Clamp(sw * 0.28f, 300f, 520f), h = Mathf.Clamp(sh * 0.42f, 200f, 380f);
            Rect area = new Rect(8f, sh - h - 8f, w, h);
            PanelRect = area;
            GameSkin.Window(area, Lang.T("Chat", "Chat"), panel);
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 24f, w - 16f, h - 24f - 56f));
            scroll = GameSkin.BeginScroll(scroll);
            if (history.Count == 0) GameSkin.LLabel(Lang.T("No messages yet.", "Noch keine Nachrichten."), lineStyle);
            foreach (Line l in history) GameSkin.LLabel(l.Text, lineStyle);
            GameSkin.EndScroll();
            GUILayout.EndArea();
            GUI.SetNextControlName(FieldName);
            text = GameSkin.TextField(new Rect(area.x + 8f, area.yMax - 54f, w - 16f - 82f, 28f), text, MaxLength);
            if (GUI.GetNameOfFocusedControl() != FieldName) GUI.FocusControl(FieldName);
            if (GameSkin.Button(new Rect(area.xMax - 8f - 76f, area.yMax - 53f, 76f, 26f), Lang.T("Send", "Senden"))) send = true;
            GameSkin.Label(new Rect(area.x + 8f, area.yMax - 26f, w - 16f, 22f), Lang.T("Enter sends, Esc closes", "Enter sendet, Esc schließt") + "   " + text.Length + "/" + MaxLength, lineStyle);
            if (send) Submit();
            else if (close) Close();
        }

        static void Log(string s) { Plugin.Log.LogInfo(Plugin.Stamp() + " Chat: " + s); }

        // ---------- keeping the keyboard from the game ----------

        static readonly HashSet<string> heldAtOpen = new HashSet<string>();

        [HarmonyPatch(typeof(vp_Input), "GetButton"), HarmonyPriority(Priority.First)]
        static class ButtonPatch
        {
            static bool Prefix(ref bool __result) { if (!Typing) return true; __result = false; return false; }
        }

        [HarmonyPatch(typeof(vp_Input), "GetButtonDown"), HarmonyPriority(Priority.First)]
        static class ButtonDownPatch
        {
            static bool Prefix(ref bool __result) { if (!Typing) return true; __result = false; return false; }
        }

        [HarmonyPatch(typeof(vp_Input), "GetButtonAny"), HarmonyPriority(Priority.First)]
        static class ButtonAnyPatch
        {
            static bool Prefix(ref bool __result) { if (!Typing) return true; __result = false; return false; }
        }

        [HarmonyPatch(typeof(vp_Input), "GetButtonUp"), HarmonyPriority(Priority.First)]
        static class ButtonUpPatch
        {
            static bool Prefix(string button, ref bool __result)
            {
                if (!Typing || heldAtOpen.Contains(button)) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(vp_Input), "GetAxisRaw"), HarmonyPriority(Priority.First)]
        static class AxisPatch
        {
            static bool Prefix(ref float __result) { if (!Typing) return true; __result = 0f; return false; }
        }

        // The Esc that closed the field must not reach the game's pause menu as well.
        static int escGuardFrame = -1;

        public static bool KeyDown(KeyCode key)
        {
            if (Typing || (key == KeyCode.Escape && Time.frameCount <= escGuardFrame)) return false;
            if (TestKey != KeyCode.None && key == TestKey) { TestKey = KeyCode.None; return true; }
            return Input.GetKeyDown(key);
        }

        public static bool Key(KeyCode key) { return !Typing && Input.GetKey(key); }
        public static bool KeyUp(KeyCode key) { return !Typing && Input.GetKeyUp(key); }
        public static float AxisRaw(string axis) { return Typing ? 0f : Input.GetAxisRaw(axis); }

        static int swappedCalls, swappedMethods;

        [HarmonyPatch]
        static class DirectKeysPatch
        {
            static Dictionary<MethodInfo, MethodInfo> Swaps()
            {
                Dictionary<MethodInfo, MethodInfo> d = new Dictionary<MethodInfo, MethodInfo>();
                d[AccessTools.Method(typeof(Input), "GetKeyDown", new Type[] { typeof(KeyCode) })] = AccessTools.Method(typeof(ChatSync), "KeyDown");
                d[AccessTools.Method(typeof(Input), "GetKey", new Type[] { typeof(KeyCode) })] = AccessTools.Method(typeof(ChatSync), "Key");
                d[AccessTools.Method(typeof(Input), "GetKeyUp", new Type[] { typeof(KeyCode) })] = AccessTools.Method(typeof(ChatSync), "KeyUp");
                d[AccessTools.Method(typeof(Input), "GetAxisRaw", new Type[] { typeof(string) })] = AccessTools.Method(typeof(ChatSync), "AxisRaw");
                return d;
            }

            static IEnumerable<MethodBase> TargetMethods()
            {
                Dictionary<MethodInfo, MethodInfo> swaps = Swaps();
                Type[] owners = { typeof(InterfaceManager), typeof(PlayerScript), typeof(PlayerDemonScript), typeof(PlayerWerewolfScript),
                    typeof(BuildingSystemManager), typeof(StartCutscene), typeof(TimelineCutsceneController) };
                foreach (Type t in owners)
                    foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                    {
                        if (m.IsAbstract || m.IsGenericMethod) continue;
                        bool uses = false;
                        foreach (KeyValuePair<OpCode, object> i in PatchProcessor.ReadMethodBody(m))
                        {
                            MethodInfo called = i.Value as MethodInfo;
                            if (called != null && swaps.ContainsKey(called)) { uses = true; break; }
                        }
                        if (uses) yield return m;
                    }
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                Dictionary<MethodInfo, MethodInfo> swaps = Swaps();
                swappedMethods++;
                foreach (CodeInstruction i in instructions)
                {
                    MethodInfo called = i.operand as MethodInfo;
                    if (i.opcode == OpCodes.Call && called != null && swaps.ContainsKey(called))
                    {
                        i.operand = swaps[called];
                        swappedCalls++;
                    }
                    yield return i;
                }
            }
        }

        // Once at startup, after Harmony's patches.
        public static void Install()
        {
            UICamera.GetKeyStateFunc down = UICamera.GetKeyDown, held = UICamera.GetKey, up = UICamera.GetKeyUp;
            UICamera.GetKeyDown = k => (k >= KeyCode.Mouse0 || !Typing) && down(k);
            UICamera.GetKey = k => (k >= KeyCode.Mouse0 || !Typing) && held(k);
            UICamera.GetKeyUp = k => (k >= KeyCode.Mouse0 || !Typing) && up(k);
            if (swappedCalls == 0) Plugin.Log.LogError("Chat: no direct key reads found to guard");
            else Plugin.Log.LogInfo("Chat: guarded " + swappedCalls + " direct key reads in " + swappedMethods + " methods");
        }

        // ---------- test steps ----------
        // Keys go through IMGUI's real event path: a queued key replaces one
        // repaint pass of OnGUI, so the text field and Enter/Esc handling see it
        // like a key press.
        //   chatopen          open the field (as Enter does)
        //   chattype:a_b      type "a b"
        //   chatumlauts       type German letters and the euro sign
        //   chatlong          type more than the field takes
        //   chatenter         press Enter
        //   chatesc           press Esc
        //   chatburst:<n>     send n numbered messages at once
        //   chatbad           send a dirty message past our own cleaning
        //   chatfake:esc      the guarded key reads see Esc once, when they are allowed to
        //   chatfake:enter    the same with Enter
        //   chatfake:off      forget a fake key nobody has read
        //   chatprobe         log what the game sees of the keyboard
        //   chatlog           log the history

        public static KeyCode TestKey = KeyCode.None;
        static readonly Queue<Event> testEvents = new Queue<Event>();

        // At the start of OnGUI; the caller restores the returned event at its end.
        public static Event BeginTestEvent()
        {
            if (testEvents.Count == 0 || Event.current.type != EventType.Repaint) return null;
            Event real = Event.current;
            Event.current = testEvents.Dequeue();
            return real;
        }

        static void QueueChars(string s)
        {
            foreach (char c in s) testEvents.Enqueue(new Event { type = EventType.KeyDown, keyCode = KeyCode.None, character = c });
        }

        static void QueueKey(KeyCode key)
        {
            testEvents.Enqueue(new Event { type = EventType.KeyDown, keyCode = key, character = '\0' });
        }

        public static bool TestStep(string step, Vector3 playerPos)
        {
            ChatSync s = Instance;
            if (s == null || !step.StartsWith("chat")) return false;
            if (step == "chatopen") s.Open();
            else if (step.StartsWith("chattype:")) QueueChars(step.Substring(9).Replace('_', ' '));
            else if (step == "chatumlauts") QueueChars("ÄÖÜ äöüß €");
            else if (step == "chatlong") QueueChars(new string('x', MaxLength + 60));
            else if (step == "chatenter") QueueKey(KeyCode.Return);
            else if (step == "chatesc") QueueKey(KeyCode.Escape);
            else if (step.StartsWith("chatburst:"))
            {
                int n = int.Parse(step.Substring(10), CultureInfo.InvariantCulture);
                for (int i = 1; i <= n; i++) { string m = Plugin.LocalName + " burst " + i + "/" + n; s.Send(m, m); }
            }
            else if (step == "chatbad")
            {
                string bad = "<color=red><size=80>BIG</size></color>\n\tzwei‮gedreht​ \u0007Glocke " + new string('y', 400) + " Ende";
                s.Send("(test: dirty message sent)", bad);
            }
            else if (step == "chatfake:esc") TestKey = KeyCode.Escape;
            else if (step == "chatfake:enter") TestKey = KeyCode.Return;
            else if (step == "chatfake:off") TestKey = KeyCode.None;
            else if (step == "chatprobe")
            {
                int panels = 0;
                foreach (GameObject p in InterfaceManager.instance.panels) if (p != null && p.activeSelf) panels++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " [chatprobe] typing=" + Typing + " pos=" + playerPos.ToString("F2") + " axisH=" + vp_Input.GetAxisRaw("Horizontal") + " axisV=" + vp_Input.GetAxisRaw("Vertical") +
                    " down(Inventory)=" + vp_Input.GetButtonDown("Inventory") + " fakeKeyUnread=" + TestKey + " gamePanels=" + panels + " anyPanel=" + InterfaceManager.instance.AnyPanelIsActive() +
                    " cursor=" + Cursor.visible + "/" + Cursor.lockState + " timeScale=" + Time.timeScale + " field='" + s.text + "' (" + s.text.Length + ")");
            }
            else if (step == "chatlog")
            {
                Plugin.Log.LogInfo(Plugin.Stamp() + " [chatcheck] sent=" + s.Sent + " received=" + s.Received + " lines=" + s.history.Count + " typing=" + Typing);
                foreach (Line l in s.history) Plugin.Log.LogInfo(Plugin.Stamp() + " [chatline] from=" + l.Sender + " #" + l.Seq + " len=" + l.Text.Length + " " + l.Text);
            }
            else return false;
            return true;
        }
    }
}
