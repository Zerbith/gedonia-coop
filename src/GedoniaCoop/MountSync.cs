using System;
using System.Collections.Generic;
using System.IO;
using MalbersAnimations;
using MalbersAnimations.HAP;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GedoniaCoop
{
    // Mounts for the eyes of the other players.
    //
    // The ability (Abilities/SummonHorse) instantiates Resources/Mounts/<name> on
    // the summoner's PC only (PlayerScript.createdHorse); riding moves the player
    // along with the animal's mount point. So the others saw a player floating at
    // saddle height, and running: the riding layer's weight (Rider3rdPerson sets
    // it by script) never reached the puppet - that part is PlayerState.LayerWeights.
    //  - While a mount exists, its owner sends 15 times a second: which mount, its
    //    pose, whether it is ridden, where the rider sits on it, its Animator.
    //  - Others build a copy that only looks (no scripts, colliders, rigidbody);
    //    a ridden one is placed below the puppet, so the two can't drift apart.
    //  - Mount gone (or nothing heard for 3 s): the copy goes.
    public class MountSync
    {
        const float SendInterval = 1f / 15f, Timeout = 3f;

        class Copy
        {
            public GameObject Go;
            public string Name;
            public Animator Anim;
            public readonly AnimMirror Mirror = new AnimMirror();
            public Vector3 Target, Offset;
            public Quaternion Rot = Quaternion.identity, RiderRot = Quaternion.identity;
            public bool Ridden;
            public float LastHeard;
            public PlayerState Pose = new PlayerState();
        }

        readonly Net net;
        readonly Func<int, Puppet> puppetOf;
        readonly Func<IEnumerable<int>> peers;
        static readonly Dictionary<int, Copy> copies = new Dictionary<int, Copy>();
        static readonly HashSet<string> unknown = new HashSet<string>();

        GameObject mine;
        Animator mineAnim;
        List<AnimatorControllerParameter> mineParams;
        readonly PlayerState minePose = new PlayerState();
        float nextSend;
        bool sentEnd = true;
        public int Sent, Built, Ended, Unknown;

        public MountSync(Net net, Func<int, Puppet> puppetOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.puppetOf = puppetOf;
            this.peers = peers;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        // ---------- sending ----------

        static GameObject MyMount(PlayerScript player)
        {
            if (player.createdHorse != null) return player.createdHorse;
            if (!player.isMounted) return null;
            Rider r = player.GetComponent<Rider>();
            return r != null && r.Montura != null ? r.Montura.gameObject : null; // an animal placed in the world
        }

        void SendState(PlayerScript player)
        {
            if (mine.GetComponentInChildren<Animator>(true) != mineAnim || (mineAnim != null && (mineParams == null || mineParams.Count == 0)))
            {
                mineAnim = mine.GetComponentInChildren<Animator>(true);
                mineParams = mineAnim != null ? LocalPlayer.SyncableParams(mineAnim) : null;
            }
            if (mineAnim != null && mineAnim.isActiveAndEnabled && mineParams != null) LocalPlayer.ReadAnimator(mineAnim, minePose, mineParams);
            Transform t = mine.transform, p = player.transform;
            Vector3 offset = Quaternion.Inverse(t.rotation) * (p.position - t.position);
            Quaternion riderRot = Quaternion.Inverse(t.rotation) * p.rotation;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(mine.name.Replace("(Clone)", "").Trim());
                w.Write(player.isMounted);
                w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
                w.Write(t.rotation.x); w.Write(t.rotation.y); w.Write(t.rotation.z); w.Write(t.rotation.w);
                w.Write(offset.x); w.Write(offset.y); w.Write(offset.z);
                w.Write(riderRot.x); w.Write(riderRot.y); w.Write(riderRot.z); w.Write(riderRot.w);
                w.Write((byte)minePose.LayerStates.Length);
                for (int l = 0; l < minePose.LayerStates.Length; l++) { w.Write(minePose.LayerStates[l]); w.Write(minePose.LayerTimes[l]); w.Write(minePose.LayerWeights[l]); }
                w.Write((byte)minePose.Params.Length);
                foreach (float f in minePose.Params) w.Write(f);
                w.Flush();
                net.SendMessage(Net.MsgMount, 0, ms.ToArray(), false);
            }
            Sent++;
        }

        // ---------- receiving ----------

        void Apply(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                string name = r.ReadString();
                if (name.Length == 0) { Remove(m.Sender, "mount gone"); return; }
                if (!Plugin.SameWorld(m.Sender)) return;
                Copy c;
                if (!copies.TryGetValue(m.Sender, out c) || c.Go == null || c.Name != name)
                {
                    Remove(m.Sender, null);
                    c = Build(m.Sender, name);
                    if (c == null) return;
                }
                c.LastHeard = Time.realtimeSinceStartup;
                c.Ridden = r.ReadBoolean();
                c.Target = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                c.Rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                c.Offset = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                c.RiderRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int layers = r.ReadByte();
                if (c.Pose.LayerStates.Length != layers) { c.Pose.LayerStates = new int[layers]; c.Pose.LayerTimes = new float[layers]; c.Pose.LayerWeights = new float[layers]; }
                for (int l = 0; l < layers; l++) { c.Pose.LayerStates[l] = r.ReadInt32(); c.Pose.LayerTimes[l] = r.ReadSingle(); c.Pose.LayerWeights[l] = r.ReadSingle(); }
                int n = r.ReadByte();
                if (c.Pose.Params.Length != n) c.Pose.Params = new float[n];
                for (int i = 0; i < n; i++) c.Pose.Params[i] = r.ReadSingle();
            }
        }

        Copy Build(int sender, string name)
        {
            GameObject prefab = Resources.Load<GameObject>("Mounts/" + name);
            if (prefab == null)
            {
                Unknown++;
                if (unknown.Add(name)) Plugin.Log.LogInfo(Plugin.Stamp() + " Player " + sender + " has mount '" + name + "', which is not a summoned mount here; not shown");
                return null;
            }
            Puppet p = puppetOf(sender);
            GameObject holder = new GameObject("CoopMountHolder");
            holder.SetActive(false);
            try
            {
                EffectSync.Replaying = true;
                GameObject go = Object.Instantiate(prefab, holder.transform);
                for (int pass = 0; pass < 3; pass++)
                    foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                        if (mb != null && !EffectSync.IsVisual(mb)) Object.DestroyImmediate(mb);
                foreach (Joint j in go.GetComponentsInChildren<Joint>(true)) Object.DestroyImmediate(j);
                foreach (Collider col in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
                foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(rb);
                foreach (AudioListener al in go.GetComponentsInChildren<AudioListener>(true)) Object.DestroyImmediate(al);
                foreach (Camera cam in go.GetComponentsInChildren<Camera>(true)) Object.DestroyImmediate(cam);
                go.transform.SetParent(null, false);
                SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());
                go.transform.position = p != null ? p.Position : PlayerScript.instance.transform.position;
                go.name = "CoopMount " + name + " (" + sender + ")";
                Copy c = new Copy { Go = go, Name = name, Target = go.transform.position };
                c.Anim = go.GetComponentInChildren<Animator>(true);
                if (c.Anim != null) { c.Anim.applyRootMotion = false; c.Anim.fireEvents = false; c.Anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; QuietAnimators.Add(c.Anim); }
                copies[sender] = c;
                Built++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Player " + sender + " has mount '" + name + "': showing it");
                return c;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Mount copy '" + name + "' for player " + sender + " failed: " + e.Message); return null; }
            finally { EffectSync.Replaying = false; Object.Destroy(holder); }
        }

        static void Remove(int sender, string why)
        {
            Copy c;
            if (!copies.TryGetValue(sender, out c)) return;
            copies.Remove(sender);
            if (c.Go != null) Object.Destroy(c.Go);
            if (why != null) Plugin.Log.LogInfo(Plugin.Stamp() + " Player " + sender + " mount copy removed (" + why + ")");
        }

        void Drive(int sender, Copy c, float dt, float now)
        {
            Puppet p = puppetOf(sender);
            bool show = p == null || p.Visible; // hidden with its rider (cutscenes)
            if (c.Go.activeSelf != show) c.Go.SetActive(show);
            if (!show) return;
            Transform t = c.Go.transform;
            float k = 1f - Mathf.Exp(-15f * dt);
            t.rotation = Quaternion.Slerp(t.rotation, c.Rot, k);
            if (c.Ridden && p != null)
            {
                t.position = p.Position - t.rotation * c.Offset;
                p.Root.transform.rotation = t.rotation * c.RiderRot;
            }
            else t.position = Vector3.Distance(t.position, c.Target) > 15f ? c.Target : Vector3.Lerp(t.position, c.Target, k);
            bool inTarget;
            if (c.Anim != null) c.Mirror.Apply(c.Anim, c.Pose.LayerStates, c.Pose.LayerTimes, c.Pose.Params, now, out inTarget, null, c.Pose.LayerWeights);
        }

        public void Tick(float now)
        {
            PlayerScript player = PlayerScript.instance;
            bool inWorld = player != null && SceneManager.GetActiveScene().name != "MainMenu";
            bool off = Plugin.NoFix("mount");
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgMount || m.Sender == net.LocalId || !inWorld || off) continue;
                try { Apply(m); }
                catch (Exception e) { Plugin.Log.LogWarning("Mount message from " + m.Sender + " failed: " + e.Message); }
            }
            if (!inWorld) return;

            GameObject now_ = off ? null : MyMount(player);
            if (now_ != mine)
            {
                mine = now_;
                mineAnim = null;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: " + (mine != null ? "'" + mine.name + "' shown to the others" : "none"));
            }
            if (mine != null && Connected)
            {
                sentEnd = false;
                if (now >= nextSend) { nextSend = now + SendInterval; SendState(player); }
            }
            else if (!sentEnd)
            {
                sentEnd = true;
                Ended++;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write("");
                    w.Flush();
                    if (Connected) net.SendMessage(Net.MsgMount, 0, ms.ToArray(), true);
                }
            }

            List<int> stale = null;
            foreach (KeyValuePair<int, Copy> kv in copies)
            {
                if (kv.Value.Go == null || now - kv.Value.LastHeard > Timeout || !Plugin.SameWorld(kv.Key)) { if (stale == null) stale = new List<int>(); stale.Add(kv.Key); continue; }
                Drive(kv.Key, kv.Value, Time.deltaTime, now);
            }
            if (stale != null) foreach (int id in stale) Remove(id, "not heard");
        }

        public string Summary()
        {
            return "mounts sent=" + Sent + " built=" + Built + " ended=" + Ended + " unknown=" + Unknown + " shown=" + copies.Count;
        }

        // ---------- test helpers ----------

        // Test step: what the sender has or the receiver shows, with the numbers to compare.
        public string Describe()
        {
            PlayerScript player = PlayerScript.instance;
            string s = Summary() + " mounted=" + player.isMounted + " mine=" + (mine != null ? mine.name + "@" + mine.transform.position.ToString("F1") : "-");
            Animator a = player.anim;
            if (a != null)
            {
                s += " myWeights=";
                for (int l = 0; l < a.layerCount; l++) s += (l > 0 ? "/" : "") + a.GetLayerWeight(l).ToString("F2");
                s += " myLayer0=" + a.GetCurrentAnimatorStateInfo(0).fullPathHash + " pos=" + player.transform.position.ToString("F2");
            }
            foreach (KeyValuePair<int, Copy> kv in copies)
            {
                if (kv.Value.Go == null) continue;
                Copy c = kv.Value;
                Puppet p = puppetOf(kv.Key);
                int rOn = 0;
                foreach (Renderer r in c.Go.GetComponentsInChildren<Renderer>(false)) if (r.enabled) rOn++;
                s += " [copy of " + kv.Key + ": " + c.Name + " at " + c.Go.transform.position.ToString("F2") + " sentAt " + c.Target.ToString("F2") + " ridden=" + c.Ridden + " renderers=" + rOn +
                    " state=" + (c.Anim != null && c.Anim.isActiveAndEnabled ? c.Anim.GetCurrentAnimatorStateInfo(0).fullPathHash.ToString() : "-") +
                    " want=" + (c.Pose.LayerStates.Length > 0 ? c.Pose.LayerStates[0].ToString() : "-");
                if (p != null) s += " puppet=" + p.Position.ToString("F2") + " " + p.WeightsText();
                s += "]";
            }
            foreach (KeyValuePair<int, Copy> kv in copies) if (kv.Value.Go != null) return s;
            return s + " [no copy]";
        }

        // Test steps mountsummon:<pickedMount>, mountnear, mountride, mountgo:<v>, mountoff, mountaway.
        public static float DriveForward;
        public static bool TestStep(string step, PlayerScript player)
        {
            if (step.StartsWith("mountsummon:") || step == "mountaway")
            {
                if (step != "mountaway") GameManager.instance.pickedMount = int.Parse(step.Substring(12));
                Abilities.SummonHorse a = player.GetComponent<Abilities.SummonHorse>() ?? player.gameObject.AddComponent<Abilities.SummonHorse>();
                a.lastUse = 0f;
                a.UseDown();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: SummonHorse used, createdHorse=" + (player.createdHorse != null ? player.createdHorse.name + "@" + player.createdHorse.transform.position.ToString("F1") : "none"));
                return true;
            }
            if (step == "mountnear")
            {
                if (player.createdHorse == null) { Plugin.Log.LogWarning("Test: no mount"); return true; }
                MountTriggers mt = player.createdHorse.GetComponentInChildren<MountTriggers>(true);
                player._char.enabled = false;
                player.transform.position = mt != null ? mt.transform.position : player.createdHorse.transform.position;
                player._char.enabled = true;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: next to the mount at " + player.transform.position.ToString("F1") + " trigger=" + (mt != null ? mt.name : "none"));
                return true;
            }
            if (step == "mountride")
            {
                Rider3rdPerson r = player.GetComponent<Rider3rdPerson>();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: mounting, canMount=" + r.CanMount);
                r.MountAnimal();
                return true;
            }
            if (step.StartsWith("mountgo:")) { DriveForward = float.Parse(step.Substring(8), System.Globalization.CultureInfo.InvariantCulture); return true; }
            if (step == "mountoff")
            {
                DriveForward = 0f;
                Rider3rdPerson r = player.GetComponent<Rider3rdPerson>();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: dismounting, canDismount=" + r.CanDismount);
                r.DismountAnimal();
                return true;
            }
            return false;
        }

        // Test: a stick pushed forward while riding (MalbersInput reads real keys).
        [HarmonyLib.HarmonyPatch(typeof(MalbersInput), "SetInput")]
        static class DrivePatch
        {
            static bool Prefix(MalbersInput __instance)
            {
                if (DriveForward == 0f) return true;
                ICharacterMove m = __instance.GetComponent<ICharacterMove>();
                if (m == null) return true;
                m.Move(new Vector3(0f, 0f, DriveForward), false);
                return false;
            }
        }
    }
}
