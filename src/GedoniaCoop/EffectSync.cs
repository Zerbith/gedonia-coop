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
    // Spell and combat effects for the eyes of the other players.
    //
    // Gedonia makes them two ways, both only on the caster's PC:
    //  - effect prefabs: Object.Instantiate(PlayerScript.effectsGos[i], ...)
    //    (~170 places: spell impacts, buffs on the player or a target, dodge,
    //    jumps, level-up), often parented to the player or an enemy;
    //  - particle systems already on the player, started with Play() (sword
    //    slashes: PlayerScript.slashEffect/slashEffects2/dualSlashes).
    // Here every such spawn and Play on our player, or on an enemy/follower we
    // simulate, is sent (prefab index, place, what it hangs on; particle path).
    // Receivers spawn a copy that only looks: built under an inactive holder,
    // every script except visual ones (timers, rotation, light flicker, trail,
    // random sound) and every collider removed before it wakes, so it deals no
    // damage, heals nobody and starts nothing. When the original disappears
    // the copy goes too. Projectiles stay with ProjectileSync; deaths copied
    // from an enemy's owner make their effects everywhere already.
    public class EffectSync
    {
        const byte KParticle = 0, KSpawn = 1, KEnd = 2;
        const int MaxPerSecond = 40;
        const float CopyLifetime = 120f;

        static readonly HashSet<string> Visual = new HashSet<string>
        {
            "DestroyTimed", "MagicRotation", "PlayRandomSound", "AnimatedUVLightning", "MagicLightFlicker", "TweenScale",
            "MagicLightFade", "Rotate", "AraTrail", "ETFXLightFade", "TweenRotation", "TweenPosition", "Rotation",
            "ETFXRotation", "ETFXPitchRandomizer", "DynamicBone", "TVEElement",
        };
        public static bool IsVisual(MonoBehaviour mb) { return Visual.Contains(mb.GetType().Name); }

        // Prefabs that are a body or a building, not an effect: never copied (forms: FormSync).
        static readonly HashSet<string> NotEffects = new HashSet<string>
        {
            "PlayerElementalScript", "PlayerWerewolfScript", "PlayerDemonScript", "DrakeAI", "CraftingPlace", "BedObject",
            "SaveableItem", "FlowerDefenderAI",
        };

        class Pending { public GameObject Go; public int Index; public bool Send; }
        struct LocalSpawn { public int Index; public Vector3 Pos; public float At; }

        readonly Net net;
        readonly Func<int, Puppet> puppetOf;
        readonly Func<IEnumerable<int>> peers;
        public static EffectSync Instance;
        public static bool Replaying; // building a copy: its own spawns and plays stay here

        static PlayerScript indexedFor;
        static readonly Dictionary<GameObject, int> indexOf = new Dictionary<GameObject, int>();
        static readonly Dictionary<int, bool> copyable = new Dictionary<int, bool>();

        static readonly List<Pending> pending = new List<Pending>();
        static readonly HashSet<int> pendingIds = new HashSet<int>();
        readonly List<LocalSpawn> localSpawns = new List<LocalSpawn>();
        readonly Dictionary<int, GameObject> live = new Dictionary<int, GameObject>();     // our spawn id -> original
        readonly Dictionary<string, GameObject> copies = new Dictionary<string, GameObject>(); // "player|id" -> copy
        readonly Dictionary<string, float> lastPlay = new Dictionary<string, float>();
        readonly MemoryStream outMs = new MemoryStream();
        BinaryWriter outW;
        int outCount, nextSpawnId, sentInWindow;
        bool outHasEnd;
        float nextFlush, windowStart;

        public int SpawnsSent, ParticlesSent, EndsSent, Capped, SpawnsShown, ParticlesShown, Deduped, NoTarget, Skipped;

        public EffectSync(Net net, Func<int, Puppet> puppetOf, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.puppetOf = puppetOf;
            this.peers = peers;
            outW = new BinaryWriter(outMs);
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        static void Reindex()
        {
            PlayerScript p = PlayerScript.instance;
            if (p == indexedFor) return;
            indexedFor = p;
            indexOf.Clear();
            copyable.Clear();
            if (p == null || p.effectsGos == null) return;
            for (int i = 0; i < p.effectsGos.Length; i++)
                if (p.effectsGos[i] != null && !indexOf.ContainsKey(p.effectsGos[i])) indexOf[p.effectsGos[i]] = i;
        }

        // Not a projectile (ProjectileSync) and not a body or building.
        static bool Copyable(int index)
        {
            bool ok;
            if (copyable.TryGetValue(index, out ok)) return ok;
            GameObject proto = PlayerScript.instance.effectsGos[index];
            ok = proto.GetComponentInChildren<Projectile>(true) == null;
            if (ok)
                foreach (MonoBehaviour mb in proto.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && NotEffects.Contains(mb.GetType().Name)) { ok = false; break; }
            copyable[index] = ok;
            return ok;
        }

        static string PathBelow(Transform t, Transform root)
        {
            if (t == root) return "";
            List<string> parts = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        // ---------- sending ----------

        bool Budget()
        {
            float now = Time.realtimeSinceStartup;
            if (now - windowStart >= 1f) { windowStart = now; sentInWindow = 0; }
            if (sentInWindow >= MaxPerSecond) { Capped++; return false; }
            sentInWindow++;
            return true;
        }

        static void OnInstantiate(Object original, Object result)
        {
            if (Replaying || Instance == null) return;
            GameObject proto = original as GameObject;
            GameObject go = result as GameObject;
            if (proto == null || go == null) return;
            Reindex();
            int index;
            if (!indexOf.TryGetValue(proto, out index)) return;
            if (!pendingIds.Add(go.GetInstanceID())) return; // one overload calling another
            // Parents are often set right after Instantiate: look at it next frame.
            pending.Add(new Pending { Go = go, Index = index, Send = !EnemySync.RemoteDeath && !StorySync.Applying });
        }

        void OnParticlePlay(ParticleSystem ps)
        {
            if (Replaying || !Connected) return;
            PlayerScript player = PlayerScript.instance;
            if (player == null) return;
            Transform t = ps.transform;
            int actor;
            Transform root;
            if (t.IsChildOf(player.transform)) { actor = 0; root = player.transform; }
            else if (EnemySync.Instance == null || !EnemySync.Instance.SimulatedActor(t, out actor, out root)) return;
            // Only the particles the character carries (slashes, hands), not
            // those inside a spawned effect (that effect is copied whole).
            for (Transform x = t; x != null && x != root; x = x.parent) if (x.name.EndsWith("(Clone)")) return;
            string path = PathBelow(t, root);
            string key = actor + "|" + path;
            float now = Time.realtimeSinceStartup, last;
            if (lastPlay.TryGetValue(key, out last) && now - last < 0.1f) return;
            lastPlay[key] = now;
            if (!Budget()) return;
            outW.Write(KParticle); outW.Write(actor); outW.Write(path);
            outCount++;
            ParticlesSent++;
        }

        void CapturePending()
        {
            if (pending.Count == 0) return;
            PlayerScript player = PlayerScript.instance;
            bool connected = Connected;
            float now = Time.realtimeSinceStartup;
            foreach (Pending p in pending)
            {
                if (p.Go == null || player == null) continue;
                Transform t = p.Go.transform;
                // Hit sparks and blood: the game's pool, made once at the start
                // and moved around (EffectsPoolingManager) - hits show on both sides.
                if (t.parent != null && t.parent.GetComponent<EffectsPoolingManager>() != null) continue;
                localSpawns.Add(new LocalSpawn { Index = p.Index, Pos = t.position, At = now });
                if (!p.Send || !connected || !Copyable(p.Index) || !Budget()) continue;
                byte attach = 0;
                int actor = 0;
                string path = "";
                Transform parent = t.parent;
                if (parent != null && parent.IsChildOf(player.transform)) { attach = 1; path = PathBelow(parent, player.transform); }
                else if (parent != null && EnemySync.Instance != null)
                {
                    Transform root;
                    if (EnemySync.Instance.SharedActor(parent, out actor, out root)) { attach = 2; path = PathBelow(parent, root); }
                }
                Vector3 pos = attach == 0 ? t.position : t.localPosition;
                Quaternion rot = attach == 0 ? t.rotation : t.localRotation;
                int id = ++nextSpawnId;
                live[id] = p.Go;
                outW.Write(KSpawn); outW.Write(id); outW.Write((short)p.Index); outW.Write(attach); outW.Write(actor); outW.Write(path);
                outW.Write(pos.x); outW.Write(pos.y); outW.Write(pos.z);
                outW.Write(rot.x); outW.Write(rot.y); outW.Write(rot.z); outW.Write(rot.w);
                outCount++;
                SpawnsSent++;
            }
            pending.Clear();
            pendingIds.Clear();
            if (localSpawns.Count > 200) localSpawns.RemoveRange(0, localSpawns.Count - 200);
        }

        void CheckEnded()
        {
            List<int> ended = null;
            foreach (KeyValuePair<int, GameObject> kv in live) if (kv.Value == null) { if (ended == null) ended = new List<int>(); ended.Add(kv.Key); }
            if (ended == null) return;
            foreach (int id in ended)
            {
                live.Remove(id);
                outW.Write(KEnd); outW.Write(id);
                outCount++;
                outHasEnd = true;
                EndsSent++;
            }
        }

        void Flush()
        {
            if (outCount == 0) return;
            outW.Flush();
            byte[] body = outMs.ToArray();
            byte[] payload = new byte[body.Length + 2];
            payload[0] = (byte)(outCount & 0xff); payload[1] = (byte)(outCount >> 8);
            Buffer.BlockCopy(body, 0, payload, 2, body.Length);
            // A lost spawn is no worse than a late one; a lost end leaves a copy hanging.
            net.SendMessage(Net.MsgEffects, 0, payload, outHasEnd);
            outMs.SetLength(0);
            outCount = 0;
            outHasEnd = false;
        }

        // ---------- receiving ----------

        void Apply(Net.Message m)
        {
            if (m.Sender == net.LocalId || PlayerScript.instance == null) return;
            bool here = Plugin.SameWorld(m.Sender);
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                int n = r.ReadUInt16();
                for (int k = 0; k < n; k++)
                {
                    byte kind = r.ReadByte();
                    if (kind == KParticle)
                    {
                        int actor = r.ReadInt32();
                        string path = r.ReadString();
                        if (here) PlayParticle(m.Sender, actor, path);
                    }
                    else if (kind == KSpawn)
                    {
                        int id = r.ReadInt32();
                        int index = r.ReadInt16();
                        byte attach = r.ReadByte();
                        int actor = r.ReadInt32();
                        string path = r.ReadString();
                        Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        Quaternion rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (here) Spawn(m.Sender, id, index, attach, actor, path, pos, rot);
                    }
                    else if (kind == KEnd)
                    {
                        string key = m.Sender + "|" + r.ReadInt32();
                        GameObject c;
                        if (copies.TryGetValue(key, out c)) { if (c != null) Object.Destroy(c); copies.Remove(key); }
                    }
                    else return;
                }
            }
        }

        Transform Target(int sender, int actor, string path)
        {
            Transform root = null;
            if (actor == 0)
            {
                Puppet p = puppetOf(sender);
                if (p != null && p.Root.activeInHierarchy) root = p.ModelRoot;
            }
            else if (EnemySync.Instance != null) root = EnemySync.Instance.ActorRoot(actor);
            if (root == null) return null;
            return path.Length == 0 ? root : root.Find(path);
        }

        void PlayParticle(int sender, int actor, string path)
        {
            Transform t = Target(sender, actor, path);
            ParticleSystem ps = t != null ? t.GetComponent<ParticleSystem>() : null;
            if (ps == null) { NoTarget++; return; }
            Replaying = true;
            try { ps.Play(); }
            finally { Replaying = false; }
            ParticlesShown++;
        }

        void Spawn(int sender, int id, int index, byte attach, int actor, string path, Vector3 pos, Quaternion rot)
        {
            PlayerScript player = PlayerScript.instance;
            if (player.effectsGos == null || index < 0 || index >= player.effectsGos.Length || player.effectsGos[index] == null) { Skipped++; return; }
            Transform parent = null;
            if (attach != 0)
            {
                parent = Target(sender, attach == 1 ? 0 : actor, path);
                if (parent == null) { NoTarget++; return; }
            }
            Vector3 world = parent != null ? parent.TransformPoint(pos) : pos;
            // The same effect made here a moment ago at that place (a hit or a
            // death both sides show): once is enough.
            float now = Time.realtimeSinceStartup;
            foreach (LocalSpawn s in localSpawns)
                if (s.Index == index && now - s.At < 1f && Vector3.Distance(s.Pos, world) < 3f) { Deduped++; return; }

            GameObject holder = new GameObject("CoopEffectHolder");
            holder.SetActive(false);
            Replaying = true;
            try
            {
                GameObject copy = Object.Instantiate(player.effectsGos[index], holder.transform);
                bool timed = false;
                for (int pass = 0; pass < 3; pass++)
                {
                    foreach (MonoBehaviour mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        if (Visual.Contains(mb.GetType().Name)) { if (mb is DestroyTimed) timed = true; continue; }
                        Object.DestroyImmediate(mb);
                    }
                }
                foreach (Collider c in copy.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
                if (parent != null)
                {
                    copy.transform.SetParent(parent, false);
                    copy.transform.localPosition = pos;
                    copy.transform.localRotation = rot;
                }
                else
                {
                    copy.transform.SetParent(null, false);
                    SceneManager.MoveGameObjectToScene(copy, SceneManager.GetActiveScene());
                    copy.transform.position = pos;
                    copy.transform.rotation = rot;
                }
                copy.name = "CoopEffect " + player.effectsGos[index].name;
                Object.Destroy(copy, CopyLifetime);
                string key = sender + "|" + id;
                if (!timed) copies[key] = copy; // ends with the original
                SpawnsShown++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Effect " + index + " from player " + sender + " failed: " + e.Message); }
            finally { Replaying = false; Object.Destroy(holder); }
        }

        public void Tick(float now)
        {
            bool inWorld = PlayerScript.instance != null && SceneManager.GetActiveScene().name != "MainMenu";
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgEffects || !inWorld) continue;
                try { Apply(m); }
                catch (Exception e) { Plugin.Log.LogError("Effect message from " + m.Sender + " failed: " + e); }
            }
            if (!inWorld) { pending.Clear(); pendingIds.Clear(); return; }
            CapturePending();
            CheckEnded();
            if (now >= nextFlush)
            {
                nextFlush = now + 0.05f;
                if (Connected) Flush();
                else { outMs.SetLength(0); outCount = 0; outHasEnd = false; }
            }
            if (copies.Count > 100)
            {
                List<string> gone = new List<string>();
                foreach (KeyValuePair<string, GameObject> kv in copies) if (kv.Value == null) gone.Add(kv.Key);
                foreach (string k in gone) copies.Remove(k);
            }
        }

        public string Summary()
        {
            return "effects sent=" + SpawnsSent + "+" + ParticlesSent + "p ends=" + EndsSent + " capped=" + Capped + " shown=" + SpawnsShown + "+" + ParticlesShown + "p deduped=" + Deduped + " noTarget=" + NoTarget + " skipped=" + Skipped;
        }

        // ---------- Harmony ----------

        [HarmonyPatch]
        static class InstantiatePatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (MethodInfo m in typeof(Object).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Instantiate" || m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length > 0 && ps[0].ParameterType == typeof(Object) && m.ReturnType == typeof(Object)) yield return m;
                }
            }

            static void Postfix(Object original, Object __result)
            {
                try { OnInstantiate(original, __result); }
                catch (Exception e) { Plugin.Log.LogWarning("Effect capture failed: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(ParticleSystem), "Play", new Type[0])]
        static class PlayPatch
        {
            static void Postfix(ParticleSystem __instance)
            {
                EffectSync s = Instance;
                if (s == null || __instance == null) return;
                try { s.OnParticlePlay(__instance); }
                catch (Exception e) { Plugin.Log.LogWarning("Particle capture failed: " + e.Message); }
            }
        }

        // ---------- test helpers ----------

        // Test-only: casts like the game does: effect prefab index i at the
        // player (attached when "on"), or the next sword slash particle.
        public static void TestEffect(PlayerScript player, string what)
        {
            if (what == "slash")
            {
                ParticleSystem ps = player.slashEffect != null && player.slashEffect.Length > 0 ? player.slashEffect[0] : null;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: slash particle " + (ps != null ? LocalPlayer.Path(ps.transform, player.transform) : "none"));
                if (ps != null) ps.Play();
                return;
            }
            bool on = what.EndsWith("on");
            int i = int.Parse(on ? what.Substring(0, what.Length - 2) : what);
            GameObject g = on ? Object.Instantiate(player.effectsGos[i], player.transform.position + Vector3.up, Quaternion.identity, player.transform)
                              : Object.Instantiate(player.effectsGos[i], player.transform.position + player.transform.forward * 3f, Quaternion.identity);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: effect " + i + " '" + player.effectsGos[i].name + "'" + (on ? " on the player" : " 3 m ahead") + " at " + g.transform.position.ToString("F1"));
        }

        public string Describe()
        {
            int n = 0;
            foreach (GameObject g in copies.Values) if (g != null) n++;
            List<string> names = new List<string>();
            foreach (GameObject g in Object.FindObjectsOfType<GameObject>())
                if (g.name.StartsWith("CoopEffect ") && names.Count < 20) names.Add(g.name.Substring(11) + "@" + g.transform.position.ToString("F0") + (g.transform.parent != null ? " on " + g.transform.parent.name : ""));
            return Summary() + " copiesWaitingForEnd=" + n + " live=[" + string.Join(", ", names.ToArray()) + "]";
        }
    }
}
