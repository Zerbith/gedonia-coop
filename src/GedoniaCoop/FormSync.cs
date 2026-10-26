using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GedoniaCoop
{
    // Transformations for the eyes of the other players (werewolf, blood demon,
    // elemental forms).
    //
    // The abilities (Abilities/WerewolfForm, BloodDemon, InvokeElemental) switch
    // the player off and instantiate a separate body from PlayerScript.effectsGos
    // (145 werewolf, 160 blood demon, 164/249/250/251 elemental forms) with its
    // own controller script (PlayerWerewolfScript / PlayerDemonScript /
    // PlayerElementalScript); the player's transform follows it. The puppet of
    // that player kept showing the human body standing still.
    //  - While transformed, the player sends 15 times a second: which body,
    //    where, and every Animator of it (state per layer, parameters).
    //  - Others build a copy that only looks (under an inactive holder: every
    //    script except visual ones and every collider removed before it wakes),
    //    move it smoothly, play the same states, and hide the puppet meanwhile.
    //  - Back to human (or nothing heard for 3 s): the copy goes.
    public class FormSync
    {
        const float SendInterval = 1f / 15f, Timeout = 3f;

        class Copy
        {
            public GameObject Go;
            public int Index;
            public Animator[] Anims;
            public Vector3 Target;
            public float Yaw, LastHeard;
            public int[][] States;
            public float[][] Times;
            public float[][] Params;
        }

        readonly Net net;
        readonly Func<int, Puppet> puppetOf;
        readonly Func<IEnumerable<int>> peers;
        public static FormSync Instance;
        static readonly Dictionary<int, Copy> copies = new Dictionary<int, Copy>();

        GameObject myForm;
        int myIndex = -1;
        Animator[] myAnims;
        float nextSend, nextLook;
        bool sentEnd = true;
        public int Sent, Built, Ended;

        public FormSync(Net net, Func<int, Puppet> puppetOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.puppetOf = puppetOf;
            this.peers = peers;
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        // This player shows as a transformed body here (its puppet stays hidden).
        public static bool Transformed(int player) { Copy c; return copies.TryGetValue(player, out c) && c.Go != null; }

        // ---------- sending ----------

        void FindMyForm(PlayerScript player)
        {
            if (player.gameObject.activeSelf) { myForm = null; myIndex = -1; return; }
            if (myForm != null) return;
            MonoBehaviour body = (MonoBehaviour)Object.FindObjectOfType<PlayerWerewolfScript>() ?? (MonoBehaviour)Object.FindObjectOfType<PlayerDemonScript>();
            if (body == null) body = Object.FindObjectOfType<PlayerElementalScript>();
            if (body == null) return;
            GameObject root = body.transform.root.gameObject; // elemental forms keep their script on a child
            string name = root.name.Replace("(Clone)", "").Trim();
            for (int i = 0; i < player.effectsGos.Length; i++)
                if (player.effectsGos[i] != null && player.effectsGos[i].name == name) { myIndex = i; break; }
            if (myIndex < 0) { Plugin.Log.LogWarning("Form '" + name + "' is not one of the effect prefabs; not shown to others"); return; }
            myForm = root;
            myAnims = myForm.GetComponentsInChildren<Animator>(true);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Transformed into '" + name + "' (effect " + myIndex + ", " + myAnims.Length + " animators)");
        }

        void SendState()
        {
            Transform t = myForm.transform;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((short)myIndex);
                w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z); w.Write(t.eulerAngles.y);
                w.Write((byte)myAnims.Length);
                foreach (Animator a in myAnims)
                {
                    bool ok = a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null;
                    int layers = ok ? a.layerCount : 0;
                    w.Write((byte)layers);
                    for (int l = 0; l < layers; l++)
                    {
                        AnimatorStateInfo info = a.IsInTransition(l) ? a.GetNextAnimatorStateInfo(l) : a.GetCurrentAnimatorStateInfo(l);
                        w.Write(info.fullPathHash); w.Write(info.normalizedTime);
                    }
                    AnimatorControllerParameter[] ps = ok ? a.parameters : new AnimatorControllerParameter[0];
                    int n = 0;
                    foreach (AnimatorControllerParameter p in ps) if (p.type != AnimatorControllerParameterType.Trigger) n++;
                    w.Write((byte)n);
                    foreach (AnimatorControllerParameter p in ps)
                    {
                        if (p.type == AnimatorControllerParameterType.Float) w.Write(a.GetFloat(p.nameHash));
                        else if (p.type == AnimatorControllerParameterType.Int) w.Write((float)a.GetInteger(p.nameHash));
                        else if (p.type == AnimatorControllerParameterType.Bool) w.Write(a.GetBool(p.nameHash) ? 1f : 0f);
                    }
                }
                w.Flush();
                net.SendMessage(Net.MsgForm, 0, ms.ToArray(), false);
            }
            Sent++;
        }

        void SendEnd()
        {
            net.SendMessage(Net.MsgForm, 0, BitConverter.GetBytes((short)-1), true);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Back to human form");
        }

        // ---------- receiving ----------

        void Apply(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int index = r.ReadInt16();
                if (index < 0) { Remove(m.Sender, "back to human"); return; }
                PlayerScript player = PlayerScript.instance;
                if (player == null || !Plugin.SameWorld(m.Sender) || index >= player.effectsGos.Length || player.effectsGos[index] == null) return;
                Copy c;
                if (!copies.TryGetValue(m.Sender, out c) || c.Go == null || c.Index != index)
                {
                    Remove(m.Sender, null);
                    c = Build(m.Sender, index);
                    if (c == null) return;
                }
                c.LastHeard = Time.realtimeSinceStartup;
                c.Target = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                c.Yaw = r.ReadSingle();
                int anims = r.ReadByte();
                for (int k = 0; k < anims; k++)
                {
                    int layers = r.ReadByte();
                    int[] states = new int[layers]; float[] times = new float[layers];
                    for (int l = 0; l < layers; l++) { states[l] = r.ReadInt32(); times[l] = r.ReadSingle(); }
                    int n = r.ReadByte();
                    float[] ps = new float[n];
                    for (int i = 0; i < n; i++) ps[i] = r.ReadSingle();
                    if (k < c.Anims.Length) { c.States[k] = states; c.Times[k] = times; c.Params[k] = ps; }
                }
            }
        }

        Copy Build(int sender, int index)
        {
            PlayerScript player = PlayerScript.instance;
            Puppet p = puppetOf(sender);
            Vector3 at = p != null ? p.Position : player.transform.position;
            GameObject holder = new GameObject("CoopFormHolder");
            holder.SetActive(false);
            try
            {
                EffectSync.Replaying = true;
                GameObject go = Object.Instantiate(player.effectsGos[index], holder.transform);
                for (int pass = 0; pass < 3; pass++)
                    foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                        if (mb != null && !EffectSync.IsVisual(mb)) Object.DestroyImmediate(mb);
                foreach (Collider col in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col); // also the CharacterController
                foreach (AudioListener al in go.GetComponentsInChildren<AudioListener>(true)) Object.DestroyImmediate(al);
                foreach (Camera cam in go.GetComponentsInChildren<Camera>(true)) Object.DestroyImmediate(cam);
                go.transform.SetParent(null, false);
                SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());
                go.transform.position = at;
                go.name = "CoopForm " + player.effectsGos[index].name + " (" + sender + ")";
                Copy c = new Copy { Go = go, Index = index, Target = at };
                c.Anims = go.GetComponentsInChildren<Animator>(true);
                foreach (Animator a in c.Anims) { a.applyRootMotion = false; a.fireEvents = false; a.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
                c.States = new int[c.Anims.Length][]; c.Times = new float[c.Anims.Length][]; c.Params = new float[c.Anims.Length][];
                copies[sender] = c;
                Built++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Player " + sender + " transformed: showing '" + player.effectsGos[index].name + "' (" + c.Anims.Length + " animators)");
                return c;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Form copy " + index + " for player " + sender + " failed: " + e.Message); return null; }
            finally { EffectSync.Replaying = false; Object.Destroy(holder); }
        }

        static void Remove(int sender, string why)
        {
            Copy c;
            if (!copies.TryGetValue(sender, out c)) return;
            copies.Remove(sender);
            if (c.Go != null) Object.Destroy(c.Go);
            if (why != null) Plugin.Log.LogInfo(Plugin.Stamp() + " Player " + sender + " form copy removed (" + why + ")");
        }

        static void Drive(Copy c, float dt)
        {
            Transform t = c.Go.transform;
            t.position = Vector3.Distance(t.position, c.Target) > 15f ? c.Target : Vector3.Lerp(t.position, c.Target, Mathf.Clamp01(dt * 12f));
            t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, c.Yaw, 0f), Mathf.Clamp01(dt * 12f));
            for (int k = 0; k < c.Anims.Length; k++)
            {
                Animator a = c.Anims[k];
                if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null || c.States[k] == null) continue;
                AnimatorControllerParameter[] ps = a.parameters;
                int i = 0;
                foreach (AnimatorControllerParameter p in ps)
                {
                    if (p.type == AnimatorControllerParameterType.Trigger) continue;
                    if (c.Params[k] == null || i >= c.Params[k].Length) break;
                    float v = c.Params[k][i++];
                    if (p.type == AnimatorControllerParameterType.Float) a.SetFloat(p.nameHash, v);
                    else if (p.type == AnimatorControllerParameterType.Int) a.SetInteger(p.nameHash, (int)v);
                    else a.SetBool(p.nameHash, v > 0.5f);
                }
                for (int l = 0; l < c.States[k].Length && l < a.layerCount; l++)
                {
                    int want = c.States[k][l];
                    AnimatorStateInfo now = a.IsInTransition(l) ? a.GetNextAnimatorStateInfo(l) : a.GetCurrentAnimatorStateInfo(l);
                    if (want != 0 && now.fullPathHash != want && a.HasState(l, want)) a.CrossFade(want, 0.1f, l, c.Times[k][l] % 1f);
                }
            }
        }

        public void Tick(float now)
        {
            PlayerScript player = PlayerScript.instance;
            bool inWorld = player != null && SceneManager.GetActiveScene().name != "MainMenu";
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgForm || m.Sender == net.LocalId || !inWorld) continue;
                try { Apply(m); }
                catch (Exception e) { Plugin.Log.LogWarning("Form message from " + m.Sender + " failed: " + e.Message); }
            }
            if (!inWorld) return;

            if (now >= nextLook) { nextLook = now + 0.25f; FindMyForm(player); }
            if (myForm == null && myIndex >= 0) { myIndex = -1; }
            if (myForm != null && Connected)
            {
                sentEnd = false;
                if (now >= nextSend) { nextSend = now + SendInterval; SendState(); }
            }
            else if (!sentEnd)
            {
                sentEnd = true;
                Ended++;
                if (Connected) SendEnd();
            }

            List<int> stale = null;
            foreach (KeyValuePair<int, Copy> kv in copies)
            {
                if (kv.Value.Go == null || now - kv.Value.LastHeard > Timeout || !Plugin.SameWorld(kv.Key)) { if (stale == null) stale = new List<int>(); stale.Add(kv.Key); continue; }
                Drive(kv.Value, Time.deltaTime);
            }
            if (stale != null) foreach (int id in stale) Remove(id, "not heard");
        }

        public string Summary()
        {
            return "forms sent=" + Sent + " built=" + Built + " ended=" + Ended + " mine=" + (myIndex >= 0 ? myIndex.ToString() : "-") + " shown=" + copies.Count;
        }

        public string Describe()
        {
            string s = Summary();
            foreach (KeyValuePair<int, Copy> kv in copies)
                if (kv.Value.Go != null)
                {
                    Animator a = kv.Value.Anims.Length > 0 ? kv.Value.Anims[0] : null;
                    int rOn = 0, rAll = 0;
                    Bounds b = new Bounds(kv.Value.Go.transform.position, Vector3.zero);
                    foreach (Renderer rr in kv.Value.Go.GetComponentsInChildren<Renderer>(true))
                    {
                        rAll++;
                        if (rr.enabled && rr.gameObject.activeInHierarchy) { rOn++; b.Encapsulate(rr.bounds); }
                    }
                    s += " [renderers " + rOn + "/" + rAll + " size " + b.size.ToString("F1") + " active=" + kv.Value.Go.activeInHierarchy + "]";
                    s += " [" + kv.Key + ": " + kv.Value.Go.name + " at " + kv.Value.Go.transform.position.ToString("F1") + " target " + kv.Value.Target.ToString("F1") +
                        (a != null && a.isActiveAndEnabled ? " state " + a.GetCurrentAnimatorStateInfo(0).fullPathHash + " want " + (kv.Value.States[0] != null && kv.Value.States[0].Length > 0 ? kv.Value.States[0][0] : 0) : "") + "]";
                }
            return s;
        }

        // Test-only: what the abilities do (Abilities/WerewolfForm etc.).
        public static void TestTransform(PlayerScript player, int index)
        {
            if (index < 0)
            {
                foreach (PlayerWerewolfScript w in Object.FindObjectsOfType<PlayerWerewolfScript>()) w.GetBack();
                foreach (MonoBehaviour b in Object.FindObjectsOfType<PlayerDemonScript>()) b.SendMessage("GetBack", SendMessageOptions.DontRequireReceiver);
                foreach (MonoBehaviour b in Object.FindObjectsOfType<PlayerElementalScript>()) b.SendMessage("GetBack", SendMessageOptions.DontRequireReceiver);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: back from the form (player active=" + player.gameObject.activeSelf + ")");
                return;
            }
            // The elemental form's GetBack hands buffs back through the learned ability.
            Abilities.InvokeElemental inv = player.GetComponent<Abilities.InvokeElemental>() ?? player.gameObject.AddComponent<Abilities.InvokeElemental>();
            HarmonyLib.Traverse.Create(inv).Field("bufferEffects").SetValue(player.currentEffects);
            player.gameObject.SetActive(false);
            GameObject g =Object.Instantiate(player.effectsGos[index], player.tr.position, player.tr.rotation);
            CameraMovement.instance.PlayerTr = g.transform;
            PlayerElementalScript el = g.GetComponentInChildren<PlayerElementalScript>(true);
            if (el != null) el.lifeTime = 10f; // InvokeElemental sets 6-10 s
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: transformed into effect " + index + " '" + player.effectsGos[index].name + "'");
        }
    }
}
