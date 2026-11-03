using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GedoniaCoop
{
    // Doors. A Door keeps its state on each PC (isOpen + two Animator bools), and
    // it changes from several places: F (Door.Interact), lockpicking, NPCs and
    // companions walking in, and the auto-close 3 s after the player leaves. So
    // here isOpen is watched rather than each caller patched, and a change goes to
    // everyone (reliable).
    //  - Two players changing a door at once: each change counts up the door's
    //    version, ties go to the higher player id, so all PCs end up the same.
    //  - Doors in cells that are not loaded: the state waits and is applied on load.
    //    A new player gets the table of changed doors.
    //  - Auto-close: while another player stands at a door, the door's own "hero
    //    here" flag stays set, so it doesn't close on them; the last one to leave
    //    closes it through the game's own timer.
    // Ids hash scene and position, which also works for generated dungeons (their
    // layouts are the same on every machine).
    public class DoorSync
    {
        class State { public int Version, By; public bool Open, Right; }
        class Entry { public Door D; public int Id; public bool LastOpen, Kept; }

        readonly Net net;
        readonly Func<IEnumerable<Puppet>> puppets;
        readonly Func<IEnumerable<int>> peers;
        readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        readonly Dictionary<Door, Entry> byDoor = new Dictionary<Door, Entry>();
        readonly Dictionary<int, State> states = new Dictionary<int, State>();
        readonly HashSet<int> knownPeers = new HashSet<int>();
        readonly List<Door> fresh = new List<Door>();
        float nextScan, nextFind, nextPeers;
        public int Sent, Applied, Ignored, Waiting, Registered, KeptOpen;
        const float KeepRadius = 3f;
        const int Chunk = 40;

        static readonly FieldInfo heroHere = AccessTools.Field(typeof(Door), "heroHere");
        static readonly MethodInfo collisionOff = AccessTools.Method(typeof(Door), "CollisionDeactivation");
        public static DoorSync Instance;

        public DoorSync(Net net, Func<IEnumerable<Puppet>> puppets, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.puppets = puppets;
            this.peers = peers;
            Instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
            for (int i = 0; i < SceneManager.sceneCount; i++) OnSceneLoaded(SceneManager.GetSceneAt(i), LoadSceneMode.Additive);
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Instance != this || !scene.IsValid()) return;
            // Inactive ones too: doors are switched on and off by distance.
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Door d in root.GetComponentsInChildren<Door>(true)) fresh.Add(d);
        }

        public static int IdOf(Door d)
        {
            Vector3 p = d.transform.position;
            // With the name: two doors can stand on the same spot (a double door).
            return WorldIds.Hash("door/" + d.gameObject.scene.name + "/" + d.name + "/" + Mathf.RoundToInt(p.x * 20f) + "/" + Mathf.RoundToInt(p.y * 20f) + "/" + Mathf.RoundToInt(p.z * 20f));
        }

        void Register(Door d)
        {
            if (d == null || byDoor.ContainsKey(d)) return;
            int id = IdOf(d);
            Entry e = new Entry { D = d, Id = id, LastOpen = d.isOpen };
            byDoor[d] = e;
            entries[id] = e; // a door of an unloaded and reloaded cell replaces the old entry
            Registered++;
            State st;
            if (states.TryGetValue(id, out st)) Apply(e, st, false);
        }

        // ---------- sending ----------

        static bool RightOf(Door d)
        {
            return d.anim != null && d.anim.isActiveAndEnabled && d.anim.runtimeAnimatorController != null && d.anim.GetBool("isRight");
        }

        void Announce(Entry e)
        {
            State st;
            if (!states.TryGetValue(e.Id, out st)) { st = new State(); states[e.Id] = st; }
            st.Version++;
            st.By = net.LocalId;
            st.Open = e.D.isOpen;
            st.Right = RightOf(e.D);
            Send(new List<KeyValuePair<int, State>> { new KeyValuePair<int, State>(e.Id, st) }, 0);
            Sent++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Door '" + e.D.name + "' " + e.Id + " " + (st.Open ? "opened" : "closed") + " here (v" + st.Version + ")");
        }

        void Send(List<KeyValuePair<int, State>> list, int target)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((short)list.Count);
                foreach (KeyValuePair<int, State> kv in list)
                {
                    w.Write(kv.Key); w.Write(kv.Value.Version); w.Write(kv.Value.By);
                    w.Write((byte)((kv.Value.Open ? 1 : 0) | (kv.Value.Right ? 2 : 0)));
                }
                w.Flush();
                net.SendMessage(Net.MsgDoor, target, ms.ToArray(), true);
            }
        }

        // ---------- receiving ----------

        void Receive(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int n = r.ReadInt16();
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadInt32(), version = r.ReadInt32(), by = r.ReadInt32();
                    byte bits = r.ReadByte();
                    State st;
                    bool have = states.TryGetValue(id, out st);
                    if (have && (version < st.Version || (version == st.Version && by <= st.By))) { Ignored++; continue; }
                    if (!have) { st = new State(); states[id] = st; }
                    st.Version = version; st.By = by; st.Open = (bits & 1) != 0; st.Right = (bits & 2) != 0;
                    Entry e;
                    if (entries.TryGetValue(id, out e) && e.D != null) { Apply(e, st, true); Applied++; }
                    else Waiting++;
                }
            }
        }

        void Apply(Entry e, State st, bool show)
        {
            Door d = e.D;
            e.LastOpen = st.Open;
            bool changed = d.isOpen != st.Open;
            d.isOpen = st.Open;
            SetBools(d, st);
            if (!changed || !show) return;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Door '" + d.name + "' " + e.Id + " " + (st.Open ? "opened" : "closed") + " by player " + st.By + " (v" + st.Version + ")");
            if (!d.gameObject.activeInHierarchy) return;
            d.StartCoroutine((System.Collections.IEnumerator)collisionOff.Invoke(d, null));
            PlayerScript player = PlayerScript.instance;
            if (player == null || Vector3.Distance(player.transform.position, d.transform.position) > 25f) return;
            AudioSource own = d.GetComponent<AudioSource>();
            if (own != null) own.Play();
            else if (Camera.main != null && Camera.main.GetComponent<AudioSource>() != null && InterfaceManager.instance != null)
                Camera.main.GetComponent<AudioSource>().PlayOneShot(InterfaceManager.instance.interfacesounds[st.Open ? 12 : 13]);
        }

        static void SetBools(Door d, State st)
        {
            if (d.anim == null || !d.anim.isActiveAndEnabled || d.anim.runtimeAnimatorController == null) return;
            d.anim.SetBool("isRight", st.Right);
            d.anim.SetBool("isOpen", st.Open);
        }

        // ---------- every 0.1 s ----------

        void Scan()
        {
            PlayerScript player = PlayerScript.instance;
            List<Door> gone = null;
            foreach (KeyValuePair<Door, Entry> kv in byDoor)
            {
                Entry e = kv.Value;
                Door d = kv.Key;
                if (d == null) { if (gone == null) gone = new List<Door>(); gone.Add(d); continue; }
                if (d.isOpen != e.LastOpen)
                {
                    e.LastOpen = d.isOpen;
                    if (Plugin.NoFix("doors")) continue;
                    Announce(e);
                }
                if (Plugin.NoFix("doors")) continue;
                State st;
                // An Animator switched off by distance forgets its bools.
                if (states.TryGetValue(e.Id, out st) && d.anim != null && d.anim.isActiveAndEnabled && d.anim.runtimeAnimatorController != null && d.anim.GetBool("isOpen") != st.Open) SetBools(d, st);
                Hold(e, player);
            }
            if (gone != null)
                foreach (Door d in gone)
                {
                    Entry e = byDoor[d];
                    byDoor.Remove(d);
                    Entry cur;
                    if (entries.TryGetValue(e.Id, out cur) && cur == e) entries.Remove(e.Id);
                }
        }

        void Hold(Entry e, PlayerScript player)
        {
            Door d = e.D;
            bool friendNear = false;
            if (d.isOpen)
                foreach (Puppet p in puppets())
                    if (p.Visible && Vector3.Distance(p.Position, d.transform.position) < KeepRadius) { friendNear = true; break; }
            if (friendNear)
            {
                if (!e.Kept) KeptOpen++;
                e.Kept = true;
                heroHere.SetValue(d, true);
            }
            else if (e.Kept)
            {
                e.Kept = false;
                if (player != null && Vector3.Distance(player.transform.position, d.transform.position) >= KeepRadius) heroHere.SetValue(d, false);
            }
        }

        public void Tick(float now, bool inWorld)
        {
            bool off = Plugin.NoFix("doors");
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgDoor || m.Sender == net.LocalId || off) continue;
                try { Receive(m); }
                catch (Exception e) { Plugin.Log.LogWarning("Door message from " + m.Sender + " failed: " + e.Message); }
            }
            if (!inWorld) return;
            if (fresh.Count > 0)
            {
                foreach (Door d in fresh) Register(d);
                fresh.Clear();
            }
            if (now >= nextFind)
            {
                // Doors built at runtime (generated dungeons).
                nextFind = now + 2f;
                foreach (Door d in Object.FindObjectsOfType<Door>()) Register(d);
            }
            if (now >= nextScan)
            {
                nextScan = now + 0.1f;
                Scan();
            }
            if (now >= nextPeers)
            {
                nextPeers = now + 1f;
                knownPeers.RemoveWhere(p => { foreach (int q in peers()) if (q == p) return false; return true; });
                foreach (int p in peers())
                {
                    if (!knownPeers.Add(p) || states.Count == 0) continue;
                    List<KeyValuePair<int, State>> list = new List<KeyValuePair<int, State>>();
                    foreach (KeyValuePair<int, State> kv in states)
                    {
                        list.Add(kv);
                        if (list.Count == Chunk) { Send(list, p); list = new List<KeyValuePair<int, State>>(); }
                    }
                    if (list.Count > 0) Send(list, p);
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Sent " + states.Count + " changed doors to new player " + p);
                }
            }
        }

        public string Summary()
        {
            return "doors known=" + byDoor.Count + " changed=" + states.Count + " sent=" + Sent + " applied=" + Applied + " ignored=" + Ignored + " waited=" + Waiting + " keptOpen=" + KeptOpen;
        }

        // ---------- test helpers ----------

        Door Nearest(Vector3 pos, int skip)
        {
            List<Door> all = new List<Door>();
            foreach (Door d in byDoor.Keys) if (d != null && d.gameObject.activeInHierarchy) all.Add(d);
            all.Sort((a, b) => Vector3.Distance(a.transform.position, pos).CompareTo(Vector3.Distance(b.transform.position, pos)));
            return skip < all.Count ? all[skip] : null;
        }

        static string Show(Door d)
        {
            return "'" + d.name + "' id=" + IdOf(d) + " at " + d.transform.position.ToString("F1") + " open=" + d.isOpen + " animOpen=" +
                (d.anim != null && d.anim.isActiveAndEnabled && d.anim.runtimeAnimatorController != null ? d.anim.GetBool("isOpen").ToString() : "-") +
                " locked=" + d.isLocked + " keyNeeded=" + (d.requiresItemToOpen != null) + " hero=" + heroHere.GetValue(d);
        }

        // doorlist, gotodoor[:n], dooruse[:n] (F on the n-th nearest), doorstate, doorforce:<open|closed>[:n] (changes
        // isOpen like lockpicking does), doorbesides (stand 1 m from the door).
        public static bool TestStep(string step, PlayerScript player)
        {
            DoorSync s = Instance;
            if (step == "nocompanions")
            {
                // Own companions open any door they stand at (Door.Update): out of the way.
                foreach (CompanionNPCModule c in new List<CompanionNPCModule>(player.currentCOmpanions)) if (c != null) Object.Destroy(c.gameObject);
                player.currentCOmpanions.Clear();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: companions dismissed");
                return true;
            }
            if (s == null || !step.StartsWith("door") && !step.StartsWith("gotodoor")) return false;
            Vector3 pos = player.transform.position;
            string[] parts = step.Split(':');
            int n = parts.Length > 1 ? parseN(parts[parts.Length - 1]) : 0;
            // The door of the first gotodoor stays the test's door (walking may bring another one nearer).
            Door d = pinned != null ? pinned : s.Nearest(pos, n);
            if (parts[0] == "gotodoor") pinned = d;
            if (parts[0] == "doorwalk")
            {
                // A real walk, because a teleport never fires the door's OnTriggerExit.
                GameManager.instance.StartCoroutine(Walk(d, player, parseN(parts[1])));
                return true;
            }
            if (step == "doorlist")
            {
                int i = 0;
                int triggers = 0;
                foreach (Door x in s.byDoor.Keys)
                {
                    if (x == null) continue;
                    bool trigger = false;
                    foreach (Collider c in x.GetComponentsInChildren<Collider>(true)) if (c.isTrigger) trigger = true;
                    if (trigger) triggers++;
                    if (i++ < 60) Plugin.Log.LogInfo("[door] " + Show(x) + " trigger=" + trigger + " active=" + x.gameObject.activeInHierarchy + " dist=" + Vector3.Distance(x.transform.position, pos).ToString("F0"));
                }
                Plugin.Log.LogInfo("[door] with a trigger: " + triggers + " of " + s.byDoor.Count + " | " + s.Summary());
            }
            else if (d == null) Plugin.Log.LogInfo("[door] no door near " + pos.ToString("F0") + " (" + s.Summary() + ")");
            else if (parts[0] == "gotodoor" || parts[0] == "doorbesides")
            {
                player._char.enabled = false;
                player.transform.position = d.transform.position + d.transform.TransformDirection(Vector3.forward) * (parts[0] == "gotodoor" ? 2.5f : 0.8f);
                player._char.enabled = true;
                Plugin.Log.LogInfo(Plugin.Stamp() + " [door] at " + player.transform.position.ToString("F1") + " next to " + Show(d));
            }
            else if (parts[0] == "dooruse")
            {
                Plugin.Log.LogInfo(Plugin.Stamp() + " [door] Interact on " + Show(d));
                d.Interact();
            }
            else if (parts[0] == "doorforce")
            {
                bool open = parts[1] == "open";
                d.isOpen = open;
                d.anim.SetBool("isOpen", open);
                Plugin.Log.LogInfo(Plugin.Stamp() + " [door] forced " + parts[1] + " on " + Show(d));
            }
            else if (parts[0] == "doorexit")
            {
                // What leaving the door's trigger does (the test door's trigger didn't fire for the walking player).
                d.OnTriggerExit(player.GetComponent<Collider>());
                Plugin.Log.LogInfo(Plugin.Stamp() + " [door] left the trigger of " + Show(d));
            }
            else if (parts[0] == "doorstate") Plugin.Log.LogInfo(Plugin.Stamp() + " [door] nearest " + Show(d) + " | " + s.Summary());
            else if (parts[0] == "doorrace")
            {
                // Both PCs change the door in the same instant, by the PC's clock (next multiple of 10 s).
                GameManager.instance.StartCoroutine(Race(d, parts[1] == "open"));
            }
            else return false;
            return true;
        }

        static Door pinned;

        [HarmonyPatch(typeof(Door), "OnTriggerEnter")]
        static class EnterLog
        {
            static void Postfix(Door __instance, Collider other)
            {
                if (__instance == pinned) Plugin.Log.LogInfo(Plugin.Stamp() + " [door] trigger enter '" + other.name + "' tag " + other.tag + " hero=" + heroHere.GetValue(__instance));
            }
        }

        [HarmonyPatch(typeof(Door), "OnTriggerExit")]
        static class ExitLog
        {
            static void Postfix(Door __instance, Collider other)
            {
                if (__instance == pinned) Plugin.Log.LogInfo(Plugin.Stamp() + " [door] trigger exit '" + other.name + "' tag " + other.tag + " hero=" + heroHere.GetValue(__instance));
            }
        }

        static System.Collections.IEnumerator Walk(Door d, PlayerScript player, int seconds)
        {
            Vector3 from = player.transform.position;
            float end = Time.time + seconds;
            BotInput.Active = true;
            while (Time.time < end)
            {
                Vector3 away = d != null ? player.transform.position - d.transform.position : Vector3.forward;
                away.y = 0f;
                BotInput.Steer(away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward);
                yield return null;
            }
            BotInput.Stop();
            BotInput.Active = false;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [door] walked " + Vector3.Distance(from, player.transform.position).ToString("F1") + " m away");
        }

        static System.Collections.IEnumerator Race(Door d, bool open)
        {
            int goal = -1;
            while (true)
            {
                int sec = DateTime.Now.Second;
                if (goal < 0) goal = (sec / 10 + 1) * 10 % 60;
                if (sec == goal) break;
                yield return null;
            }
            d.isOpen = open;
            d.anim.SetBool("isOpen", open);
            Plugin.Log.LogInfo(Plugin.Stamp() + " [door] race: " + (open ? "opened" : "closed") + " here");
        }

        static int parseN(string s) { int n; return int.TryParse(s, out n) ? n : 0; }
    }
}
