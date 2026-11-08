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
            public int BorrowedFrom; // the sender rides this player's mount (0: its own)
            public float LastHeard;
            public PlayerState Pose = new PlayerState();
            public Transform[] Ik; // left foot, right foot, left knee, right knee
            public GameObject Original; // a world animal: ours, hidden while the copy shows it
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
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        // ---------- sending ----------

        GameObject MyMount(PlayerScript player)
        {
            if (seatDriver != 0) return null; // a passenger has no mount of its own to show
            if (player.isMounted)
            {
                Rider r = player.GetComponent<Rider>();
                if (r != null && r.Montura != null) return r.Montura.transform.root.gameObject; // ours, a borrowed one or one placed in the world
            }
            if (lentTo != 0) return null; // someone else rides it and shows it
            return player.createdHorse;
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
                w.Write(mine == cand && cand != null ? candName : mine.name.Replace("(Clone)", "").Trim());
                w.Write(player.isMounted);
                w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
                w.Write(t.rotation.x); w.Write(t.rotation.y); w.Write(t.rotation.z); w.Write(t.rotation.w);
                w.Write(offset.x); w.Write(offset.y); w.Write(offset.z);
                w.Write(riderRot.x); w.Write(riderRot.y); w.Write(riderRot.z); w.Write(riderRot.w);
                w.Write((byte)minePose.LayerStates.Length);
                for (int l = 0; l < minePose.LayerStates.Length; l++) { w.Write(minePose.LayerStates[l]); w.Write(minePose.LayerTimes[l]); w.Write(minePose.LayerWeights[l]); }
                w.Write((byte)minePose.Params.Length);
                foreach (float f in minePose.Params) w.Write(f);
                w.Write(borrowing && cand != null && mine == cand ? candOwner : 0);
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
                if (name.Length == 0)
                {
                    Remove(m.Sender, "mount gone");
                    // A borrower got off: our mount is back, where it was left.
                    if (r.BaseStream.Position < r.BaseStream.Length)
                    {
                        int returnTo = r.ReadInt32();
                        Vector3 at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        Quaternion rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (returnTo == net.LocalId && lentTo == m.Sender) TakeBack(at, rot, "returned by " + m.Sender);
                    }
                    return;
                }
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
                c.BorrowedFrom = r.BaseStream.Position < r.BaseStream.Length ? r.ReadInt32() : 0;
            }
            // Someone rides our summoned mount: ours stands aside (hidden) until it comes back.
            Copy b = copies[m.Sender];
            if (b.BorrowedFrom == net.LocalId && lentTo != m.Sender)
            {
                PlayerScript me = PlayerScript.instance;
                if (me.createdHorse == null || (me.isMounted && MyMount(me) == me.createdHorse))
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: player " + m.Sender + " says it rides ours, but " + (me.createdHorse == null ? "we have none" : "we ride it") + "; it will get off");
                else
                {
                    lentTo = m.Sender;
                    me.createdHorse.SetActive(false);
                    Lent++;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: lent to player " + m.Sender + " (ours hidden while it rides)");
                }
            }
            if (lentTo == m.Sender) lentLast = b.Target;
        }

        // ---------- lending: anyone may ride a mount another player summoned ----------
        //
        // The summoner keeps the real mount. A player who comes near someone else's
        // parked mount gets a real one of the same kind in its place (the copy is
        // hidden), so the game's own trigger and F mount it as usual. While it is
        // ridden the summoner hides its own and shows the rider's; getting off sends
        // the place back and the summoner's mount stands there again.
        int lentTo, candOwner;
        string candName;
        Vector3 lentLast;
        GameObject cand;
        bool borrowing;
        float candMissing;
        public int Lent, Borrowed, Returned;

        void TakeBack(Vector3 at, Quaternion rot, string why)
        {
            lentTo = 0;
            PlayerScript me = PlayerScript.instance;
            if (me == null || me.createdHorse == null) return;
            me.createdHorse.transform.position = at;
            me.createdHorse.transform.rotation = rot;
            me.createdHorse.SetActive(true);
            Returned++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: ours is back at " + at.ToString("F1") + " (" + why + ")");
        }

        void TickLending(PlayerScript player, float now)
        {
            // A borrower that went quiet: ours comes back where it was seen last.
            if (lentTo != 0 && !copies.ContainsKey(lentTo)) TakeBack(lentLast, player.createdHorse != null ? player.createdHorse.transform.rotation : Quaternion.identity, "rider gone");

            Rider rider = player.GetComponent<Rider>();
            Copy c;
            copies.TryGetValue(candOwner, out c);
            if (cand != null)
            {
                bool ridingCand = player.isMounted && rider != null && rider.Montura != null && rider.Montura.gameObject == cand;
                if (ridingCand && !borrowing) { borrowing = true; Borrowed++; Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: riding player " + candOwner + "'s mount"); }
                else if (!ridingCand && borrowing) { borrowing = false; SendReturn(); }
                if (borrowing)
                {
                    // Its owner got on it at the same moment: theirs wins.
                    if (c != null && c.Ridden && c.BorrowedFrom == 0 && now - c.LastHeard < 0.5f)
                    {
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: player " + candOwner + " rides it already; getting off");
                        Rider3rdPerson r3 = player.GetComponent<Rider3rdPerson>();
                        if (r3 != null) r3.DismountAnimal();
                    }
                    return;
                }
                // Parked: stays where the owner's mount is, until we walk off or it goes.
                if (c == null || c.Go == null) { if (candMissing == 0f) candMissing = now; if (now - candMissing > Timeout) DropCand("owner's mount gone"); return; }
                candMissing = 0f;
                if (c.Ridden || Vector3.Distance(player.transform.position, cand.transform.position) > 12f) { DropCand(c.Ridden ? "ridden by its owner" : "walked away"); return; }

                if (Vector3.Distance(cand.transform.position, c.Target) > 0.5f) { cand.transform.position = c.Target; cand.transform.rotation = c.Rot; }
                return;
            }
            if (player.isMounted || seatDriver != 0) return;
            // Near someone else's parked mount: a real one in its place.
            foreach (KeyValuePair<int, Copy> kv in copies)
            {
                Copy k = kv.Value;
                if (k.Go == null || k.Ridden || k.BorrowedFrom != 0 || !k.Go.activeInHierarchy) continue;
                if (Vector3.Distance(player.transform.position, k.Go.transform.position) > 6f) continue;
                GameObject prefab = Resources.Load<GameObject>("Mounts/" + k.Name);
                if (prefab == null) continue;
                cand = Object.Instantiate(prefab, k.Target, k.Rot);
                cand.name = "CoopBorrowed " + k.Name + " (" + kv.Key + ")";
                candOwner = kv.Key;
                candName = k.Name;
                candMissing = 0f;

                Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: player " + kv.Key + "'s '" + k.Name + "' can be ridden here (real one placed)");
                return;
            }
        }


        void DropCand(string why)
        {
            if (cand != null) Object.Destroy(cand);
            cand = null;
            borrowing = false;

            Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: borrow place dropped (" + why + ")");
            candOwner = 0;
        }

        void SendReturn()
        {
            Returned++;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                Transform t = cand.transform;
                w.Write("");
                w.Write(candOwner);
                w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
                w.Write(t.rotation.x); w.Write(t.rotation.y); w.Write(t.rotation.z); w.Write(t.rotation.w);
                w.Flush();
                if (Connected) net.SendMessage(Net.MsgMount, 0, ms.ToArray(), true);
            }
            sentEnd = true;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: got off player " + candOwner + "'s mount at " + cand.transform.position.ToString("F1") + ", given back");
        }

        // ---------- the second seat: ride along behind the rider ----------
        //
        // Next to a mount someone rides, F seats the player behind the rider: the
        // player is held at a seat point of the mount (in the mount's space, sent
        // to everyone so all show it there) with the game's riding pose on the
        // mount layer, and counts as mounted (no walking, sprinting, attacking).
        // F again, the rider getting off, the mount going or dying: off again.
        public static string Hint = "";
        int seatDriver;
        Vector3 seatLocal;
        int seatLayer = -1;
        float seatChanged;
        static readonly Dictionary<int, KeyValuePair<int, Vector3>> seats = new Dictionary<int, KeyValuePair<int, Vector3>>();
        public int Seated, Unseated;

        Transform MountOf(int driver)
        {
            if (driver == net.LocalId) return mine != null ? mine.transform : null;
            Copy c;
            if (copies.TryGetValue(driver, out c) && c.Go != null && c.Ridden) return c.Go.transform;
            return null;
        }

        void TickSeat(PlayerScript player, float now)
        {
            Hint = "";
            if (seatDriver != 0)
            {
                Copy c;
                Transform t = copies.TryGetValue(seatDriver, out c) && c.Go != null && c.Ridden && c.Go.activeInHierarchy ? c.Go.transform : null;
                if (t == null) { Unseat(player, "the rider got off"); return; }
                player.transform.position = t.TransformPoint(seatLocal);
                player.transform.rotation = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
                if (c.Anim != null && player.anim != null) CopyFloat(c.Anim, player.anim, "Vertical");
                if (now - seatChanged > 0.5f && vp_Input.GetButtonDown("Interact")) Unseat(player, "F");
                return;
            }
            if (player.isMounted || player.isDead || InterfaceManager.instance == null || InterfaceManager.instance.AnyPanelIsActive()) return;
            foreach (KeyValuePair<int, Copy> kv in copies)
            {
                Copy k = kv.Value;
                if (k.Go == null || !k.Ridden || !k.Go.activeInHierarchy || Taken(kv.Key)) continue;
                if (Vector3.Distance(player.transform.position, k.Go.transform.position) > 3.5f) continue;
                Hint = Lang.T("F: ride along behind " + NameOf(kv.Key), "F: hinten bei " + NameOf(kv.Key) + " aufsitzen");
                if (now - seatChanged > 0.5f && vp_Input.GetButtonDown("Interact")) Seat(player, kv.Key);
                return;
            }
        }

        string NameOf(int id) { Puppet p = puppetOf(id); return p != null && p.Latest != null ? p.Latest.Name : "#" + id; }

        bool Taken(int driver)
        {
            foreach (KeyValuePair<int, KeyValuePair<int, Vector3>> s in seats) if (s.Value.Key == driver) return true;
            return false;
        }

        static void CopyFloat(Animator from, Animator to, string name)
        {
            foreach (AnimatorControllerParameter p in from.parameters)
                if (p.name == name && p.type == AnimatorControllerParameterType.Float)
                {
                    foreach (AnimatorControllerParameter q in to.parameters)
                        if (q.name == name && q.type == AnimatorControllerParameterType.Float) { to.SetFloat(name, from.GetFloat(name)); return; }
                    return;
                }
        }

        public void Seat(PlayerScript player, int driver)
        {
            Copy c;
            if (!copies.TryGetValue(driver, out c) || c.Go == null) return;
            GameObject prefab = Resources.Load<GameObject>("Mounts/" + c.Name);
            Mountable mo = prefab != null ? prefab.GetComponentInChildren<Mountable>(true) : null;
            seatDriver = driver;
            seatChanged = Time.realtimeSinceStartup;
            seatLocal = c.Offset + new Vector3(0f, 0.06f, -0.62f);
            player.isMounted = true;
            if (player._char != null) player._char.enabled = false;
            Animator a = player.anim;
            seatLayer = a != null && mo != null ? a.GetLayerIndex(mo.MountLayer) : -1;
            if (a != null && seatLayer >= 0)
            {
                a.SetLayerWeight(seatLayer, 1f);
                a.SetBool("Mount", true);
                a.Play(mo.MountIdle, seatLayer);
            }
            SendSeat(driver);
            Seated++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: riding along behind player " + driver + " ('" + c.Name + "', seat " + seatLocal.ToString("F2") + ", layer " + seatLayer + ")");
        }

        public void Unseat(PlayerScript player, string why)
        {
            if (seatDriver == 0) return;
            Transform t = MountOf(seatDriver);
            seatDriver = 0;
            seatChanged = Time.realtimeSinceStartup;
            player.isMounted = false;
            Animator a = player.anim;
            if (a != null && seatLayer >= 0)
            {
                a.SetBool("Mount", false);
                a.Play(MalbersAnimations.Hash.Null, seatLayer);
                a.SetLayerWeight(seatLayer, 0f);
            }
            seatLayer = -1;
            if (t != null) player.transform.position = t.position + t.right * 1.6f + Vector3.up * 0.3f;
            if (player._char != null) player._char.enabled = true;
            SendSeat(0);
            Unseated++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: off the seat (" + why + ")");
        }

        void SendSeat(int driver)
        {
            if (!Connected) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(driver);
                w.Write(seatLocal.x); w.Write(seatLocal.y); w.Write(seatLocal.z);
                w.Flush();
                net.SendMessage(Net.MsgSeat, 0, ms.ToArray(), true);
            }
        }

        void ApplySeat(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int driver = r.ReadInt32();
                Vector3 local = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                if (driver == 0) seats.Remove(m.Sender);
                else seats[m.Sender] = new KeyValuePair<int, Vector3>(driver, local);
                Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: player " + m.Sender + (driver == 0 ? " got off the seat" : " rides along behind " + driver));
            }
        }

        // Others' passengers sit on the mount as this PC shows it.
        void PlacePassengers()
        {
            foreach (KeyValuePair<int, KeyValuePair<int, Vector3>> s in seats)
            {
                Puppet p = puppetOf(s.Key);
                Transform t = MountOf(s.Value.Key);
                if (p == null || t == null || !Plugin.SameWorld(s.Key)) continue;
                p.Root.transform.position = t.TransformPoint(s.Value.Value);
                p.Root.transform.rotation = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
            }
        }

        // A passenger who dies or is unloaded gets off first (the game's death
        // code would otherwise kill "its" mount: PlayerScript.createdHorse).
        public static MountSync Instance;

        [HarmonyLib.HarmonyPatch(typeof(PlayerScript), "Death")]
        static class SeatDeathPatch
        {
            static void Prefix(PlayerScript __instance)
            {
                if (Instance != null && Instance.seatDriver != 0 && __instance == PlayerScript.instance) Instance.Unseat(__instance, "died");
            }
        }

        // A rideable animal of our loaded world with that name, nearest to where the rider is.
        static GameObject WorldMount(string name, Vector3 near)
        {
            GameObject best = null;
            float bestD = 40f;
            foreach (Mountable m in Resources.FindObjectsOfTypeAll<Mountable>())
            {
                if (m == null || !m.gameObject.scene.IsValid() || m.gameObject.name.StartsWith("Coop")) continue;
                GameObject root = m.transform.root.gameObject;
                if (root.name.Replace("(Clone)", "").Trim() != name || !root.activeInHierarchy) continue;
                float d = Vector3.Distance(root.transform.position, near);
                if (d < bestD) { bestD = d; best = root; }
            }
            return best;
        }

        Copy Build(int sender, string name)
        {
            GameObject prefab = Resources.Load<GameObject>("Mounts/" + name);
            Puppet p = puppetOf(sender);
            GameObject original = null;
            if (prefab == null)
            {
                // Not a summoned mount: an animal placed in the world. The same one
                // stands in our world too (if its cell is loaded): the copy is made
                // from it, and ours is hidden while the other player rides it.
                original = WorldMount(name, p != null ? p.Position : PlayerScript.instance.transform.position);
                if (original == null)
                {
                    Unknown++;
                    if (unknown.Add(name)) Plugin.Log.LogInfo(Plugin.Stamp() + " Player " + sender + " has mount '" + name + "', which is neither a summoned mount nor loaded here; not shown");
                    return null;
                }
                prefab = original;
            }
            GameObject holder = new GameObject("CoopMountHolder");
            holder.SetActive(false);
            try
            {
                EffectSync.Replaying = true;
                GameObject go = Object.Instantiate(prefab, holder.transform);
                // The rider's foot and knee targets (stirrups), read before the scripts go.
                Mountable mount = go.GetComponentInChildren<Mountable>(true);
                Transform[] ik = mount != null ? new Transform[] { mount.FootLeftIK, mount.FootRightIK, mount.KneeLeftIK, mount.KneeRightIK } : null;
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
                Copy c = new Copy { Go = go, Name = name, Target = go.transform.position, Ik = ik, Original = original };
                if (original != null) { original.SetActive(false); Plugin.Log.LogInfo(Plugin.Stamp() + " Mount: our own '" + original.name + "' hidden while player " + sender + " rides it"); }
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
            if (c.Original != null)
            {
                // The world animal stands where the rider left it.
                if (c.Go != null) { c.Original.transform.position = c.Go.transform.position; c.Original.transform.rotation = c.Go.transform.rotation; }
                c.Original.SetActive(true);
            }
            if (c.Go != null) Object.Destroy(c.Go);
            if (why != null) Plugin.Log.LogInfo(Plugin.Stamp() + " Player " + sender + " mount copy removed (" + why + ")");
        }

        void Drive(int sender, Copy c, float dt, float now)
        {
            Puppet p = puppetOf(sender);
            bool show = (p == null || p.Visible) && !(cand != null && sender == candOwner); // hidden with its rider (cutscenes), or while a real one stands in for it here
            if (c.Go.activeSelf != show) c.Go.SetActive(show);
            if (!show) return;
            Transform t = c.Go.transform;
            float k = 1f - Mathf.Exp(-15f * dt);
            t.rotation = Quaternion.Slerp(t.rotation, c.Rot, k);
            if (c.Ridden && p != null)
            {
                t.position = p.Position - t.rotation * c.Offset;
                p.Root.transform.rotation = t.rotation * c.RiderRot;
                RiderIK.Set(p, c.Ik);
            }
            else t.position = Vector3.Distance(t.position, c.Target) > 15f ? c.Target : Vector3.Lerp(t.position, c.Target, k);
            bool inTarget;
            if (c.Anim != null) c.Mirror.Apply(c.Anim, c.Pose.LayerStates, c.Pose.LayerTimes, c.Pose.Params, now, out inTarget, null, c.Pose.LayerWeights);
        }

        public Transform MineRoot { get { return mine != null ? mine.transform : null; } }

        // Where the sender's mount is shown here: the stand-in we may ride, else the copy.
        public Transform CopyRoot(int sender)
        {
            if (cand != null && sender == candOwner && !borrowing) return cand.transform;
            Copy c;
            return copies.TryGetValue(sender, out c) && c.Go != null ? c.Go.transform : null;
        }

        public void Tick(float now)
        {
            PlayerScript player = PlayerScript.instance;
            bool inWorld = player != null && SceneManager.GetActiveScene().name != "MainMenu";
            bool off = Plugin.NoFix("mount");
            foreach (Net.Message m in net.Messages)
            {
                if ((m.Type != Net.MsgMount && m.Type != Net.MsgSeat) || m.Sender == net.LocalId || !inWorld || off) continue;
                try { if (m.Type == Net.MsgSeat) ApplySeat(m); else Apply(m); }
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
            if (off) return;
            TickLending(player, now);
            if (!Connected && seatDriver != 0) Unseat(player, "alone");
            TickSeat(player, now);
            PlacePassengers();
        }

        public string Summary()
        {
            return "mounts sent=" + Sent + " built=" + Built + " ended=" + Ended + " unknown=" + Unknown + " shown=" + copies.Count +
                " lentTo=" + lentTo + " lent=" + Lent + " borrowCand=" + (cand != null ? candOwner.ToString() : "-") + " borrowing=" + borrowing + " borrowed=" + Borrowed + " returned=" + Returned +
                " seat=" + seatDriver + " seated=" + Seated + " unseated=" + Unseated + " passengers=" + seats.Count;
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
                if (p != null) s += " puppet=" + p.Position.ToString("F2") + " " + p.WeightsText() + RiderIK.Describe(p, c.Ik);
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
            // Borrow and seat steps: next to another player's mount (parked or ridden),
            // into the stand-in's trigger and on it, on/off the second seat.
            if (step == "mountnearcopy")
            {
                foreach (Copy k in copies.Values)
                    if (k.Go != null)
                    {
                        player._char.enabled = false;
                        player.transform.position = k.Go.transform.position + k.Go.transform.right * 2f + Vector3.up * 0.5f;
                        player._char.enabled = true;
                        Plugin.Log.LogInfo(Plugin.Stamp() + " Test: next to " + k.Go.name + " at " + player.transform.position.ToString("F1"));
                        break;
                    }
                return true;
            }
            if (step == "mountridecand")
            {
                MountSync s = Instance;
                if (s == null || s.cand == null) { Plugin.Log.LogWarning("Test: no borrow place"); return true; }
                MountTriggers mt = s.cand.GetComponentInChildren<MountTriggers>(true);
                player._char.enabled = false;
                player.transform.position = mt != null ? mt.transform.position : s.cand.transform.position;
                player._char.enabled = true;
                Rider3rdPerson r = player.GetComponent<Rider3rdPerson>();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: mounting the borrowed one, canMount=" + r.CanMount + " montura=" + (r.Montura != null ? r.Montura.name : "-"));
                if (r.Montura == null) { Mountable mo = s.cand.GetComponentInChildren<Mountable>(true); r.Montura = mo; r.MountTrigger = mt; }
                r.MountAnimal();
                return true;
            }
            if (step == "mountseat")
            {
                MountSync s = Instance;
                foreach (KeyValuePair<int, Copy> kv in copies)
                    if (kv.Value.Go != null && kv.Value.Ridden) { player.transform.position = kv.Value.Go.transform.position + kv.Value.Go.transform.right * 2f; s.Seat(player, kv.Key); return true; }
                Plugin.Log.LogWarning("Test: no ridden mount to sit on");
                return true;
            }
            // World animals one can ride (not summoned): list them, ride the nearest.
            if (step == "mountlist" || step == "mountworld")
            {
                Mountable best = null;
                float bestD = float.MaxValue;
                foreach (Mountable m in Resources.FindObjectsOfTypeAll<Mountable>())
                {
                    if (m == null || !m.gameObject.scene.IsValid() || m.transform.root.name.StartsWith("Coop") || m.transform.root.gameObject == player.createdHorse) continue;
                    float d = Vector3.Distance(m.transform.position, player.transform.position);
                    if (step == "mountlist") Plugin.Log.LogInfo("[mountlist] " + m.transform.root.name + " at " + m.transform.position.ToString("F1") + " d=" + d.ToString("F0") + " active=" + m.gameObject.activeInHierarchy + " scene=" + m.gameObject.scene.name);
                    if (m.gameObject.activeInHierarchy && d < bestD) { bestD = d; best = m; }
                }
                if (step == "mountworld" && best != null)
                {
                    MountTriggers mt = best.GetComponentInChildren<MountTriggers>(true);
                    player._char.enabled = false;
                    player.transform.position = mt != null ? mt.transform.position : best.transform.position + Vector3.right;
                    player._char.enabled = true;
                    Rider3rdPerson r = player.GetComponent<Rider3rdPerson>();
                    if (r.Montura == null) { r.Montura = best; r.MountTrigger = mt; }
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Test: mounting world animal " + best.transform.root.name + " d=" + bestD.ToString("F0"));
                    r.MountAnimal();
                }
                return true;
            }
            if (step == "mountunseat") { if (Instance != null) Instance.Unseat(player, "test"); return true; }
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

    // Feet in the stirrups for a rider shown as a puppet: the game's Rider3rdPerson
    // does this in OnAnimatorIK for the local rider (feet and knees on the mount's
    // IK points). This does the same on the puppet's Animator while MountSync keeps
    // setting the targets (each frame); without them the weights drop to 0.
    public class RiderIK : MonoBehaviour
    {
        Transform[] ik;
        int setFrame = -10;
        Animator anim;

        public static void Set(Puppet p, Transform[] targets)
        {
            if (p == null || targets == null || targets.Length < 4 || targets[0] == null || targets[1] == null || Plugin.NoFix("riderik")) return;
            Animator a = p.Root.GetComponentInChildren<Animator>(true);
            if (a == null) return;
            RiderIK r = a.GetComponent<RiderIK>();
            if (r == null) { r = a.gameObject.AddComponent<RiderIK>(); r.anim = a; }
            r.ik = targets;
            r.setFrame = Time.frameCount;
        }

        void OnAnimatorIK(int layer)
        {
            if (anim == null) return;
            // The mount copy (and its stirrups) may be gone already: Unity null check.
            float w = Time.frameCount - setFrame <= 2 && ik != null && ik[0] != null && ik[1] != null ? 1f : 0f;
            anim.SetIKPositionWeight(AvatarIKGoal.LeftFoot, w);
            anim.SetIKPositionWeight(AvatarIKGoal.RightFoot, w);
            anim.SetIKRotationWeight(AvatarIKGoal.LeftFoot, w);
            anim.SetIKRotationWeight(AvatarIKGoal.RightFoot, w);
            if (w == 0f) return;
            anim.SetIKPosition(AvatarIKGoal.LeftFoot, ik[0].position);
            anim.SetIKPosition(AvatarIKGoal.RightFoot, ik[1].position);
            anim.SetIKRotation(AvatarIKGoal.LeftFoot, ik[0].rotation);
            anim.SetIKRotation(AvatarIKGoal.RightFoot, ik[1].rotation);
            if (ik.Length > 3 && ik[2] != null && ik[3] != null)
            {
                anim.SetIKHintPositionWeight(AvatarIKHint.LeftKnee, 1f);
                anim.SetIKHintPositionWeight(AvatarIKHint.RightKnee, 1f);
                anim.SetIKHintPosition(AvatarIKHint.LeftKnee, ik[2].position);
                anim.SetIKHintPosition(AvatarIKHint.RightKnee, ik[3].position);
            }
        }

        // Test: how far the puppet's feet are from the stirrups (m).
        public static string Describe(Puppet p, Transform[] targets)
        {
            if (p == null || targets == null || targets.Length < 2 || targets[0] == null) return " ik=-";
            Animator a = p.Root.GetComponentInChildren<Animator>(true);
            if (a == null || !a.isHuman) return " ik=?";
            Transform lf = a.GetBoneTransform(HumanBodyBones.LeftFoot), rf = a.GetBoneTransform(HumanBodyBones.RightFoot);
            return " feetToStirrups=" + (lf != null ? Vector3.Distance(lf.position, targets[0].position).ToString("F2") : "-") + "/" + (rf != null ? Vector3.Distance(rf.position, targets[1].position).ToString("F2") : "-");
        }
    }
}
