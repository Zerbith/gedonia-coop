using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GedoniaCoop
{
    // Arrows, spells and enemy shots for the eyes of the other players.
    // A projectile only exists on the shooter's PC (or, for an enemy or follower,
    // on the PC that simulates it, since the AI of its mirrors is off); its hit
    // already reaches the target's owner through EnemySync's damage paths. Here the
    // shooter announces each new projectile, and the others fly a copy that does no
    // damage and ends on its first impact like the game's own, with its explosion.
    public class ProjectileSync
    {
        readonly Net net;
        public static int Fired, FiredByActors, Shown, Unknown, OtherWorld, Ended, EndedAtOnce, Explosions, ExpiredInFlight;
        static double agesSum;
        static readonly HashSet<int> seen = new HashSet<int>();
        static readonly Dictionary<int, float> visual = new Dictionary<int, float>(); // copy -> born
        struct Shot { public Projectile P; public int Actor; }
        static readonly List<Shot> outbox = new List<Shot>();
        static readonly HashSet<string> unknownNames = new HashSet<string>();

        public ProjectileSync(Net net)
        {
            this.net = net;
        }

        const byte AimNone = 0, AimPlayer = 1, AimActor = 2;

        void Aim(Projectile p, out byte kind, out int id)
        {
            kind = AimNone; id = 0;
            Transform t = p.autoAimTarget;
            if (t == null) return;
            PuppetBody body = t.GetComponentInParent<PuppetBody>();
            int actor;
            Transform root;
            if (PlayerScript.instance != null && t.IsChildOf(PlayerScript.instance.transform)) { kind = AimPlayer; id = net.LocalId; }
            else if (body != null) { kind = AimPlayer; id = body.PlayerId; }
            else if (EnemySync.Instance != null && EnemySync.Instance.SharedActor(t, out actor, out root)) { kind = AimActor; id = actor; }
        }

        public void Tick(Func<int, Puppet> puppetOf)
        {
            foreach (Shot s in outbox)
            {
                Projectile p = s.P;
                if (p == null) continue;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    // Some spells keep the Projectile on a child of the prefab.
                    Transform t = p.transform.root;
                    w.Write(t.name.Replace("(Clone)", "").Trim());
                    w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
                    w.Write(t.rotation.x); w.Write(t.rotation.y); w.Write(t.rotation.z); w.Write(t.rotation.w);
                    w.Write(p.speed); w.Write(p.life);
                    w.Write((int)p.faction);
                    w.Write(s.Actor);
                    byte aim; int aimId;
                    Aim(p, out aim, out aimId);
                    w.Write(aim); w.Write(aimId); w.Write(p.aimSpeed);
                    w.Flush();
                    // Unreliable: an arrow that arrives late is worse than none.
                    net.SendMessage(Net.MsgProjectile, 0, ms.ToArray(), false);
                }
                Fired++;
                if (s.Actor != 0) FiredByActors++;
            }
            outbox.Clear();

            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgProjectile) continue;
                if (!Plugin.SameWorld(m.Sender)) { OtherWorld++; continue; }
                try { Show(m, puppetOf); }
                catch (Exception e) { Plugin.Log.LogWarning("Projectile from " + m.Sender + " failed: " + e.Message); }
            }
        }

        void Show(Net.Message m, Func<int, Puppet> puppetOf)
        {
            if (PlayerScript.instance == null || PlayerScript.instance.projectiles == null) return;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                string name = r.ReadString();
                Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Quaternion rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float speed = r.ReadSingle();
                int life = r.ReadInt32();
                NPCFaction faction = (NPCFaction)r.ReadInt32();
                int actor = r.ReadInt32();
                byte aim = r.ReadByte();
                int aimId = r.ReadInt32();
                float aimSpeed = r.ReadSingle();
                GameObject prefab = Prefab(name);
                if (prefab == null)
                {
                    Unknown++;
                    if (unknownNames.Add(name)) Plugin.Log.LogInfo(Plugin.Stamp() + " Projectile prefab '" + name + "' is not loaded here; not shown");
                    return;
                }
                EffectSync.Replaying = true;
                GameObject go;
                try { go = Object.Instantiate(prefab, pos, rot); }
                finally { EffectSync.Replaying = false; }
                Projectile p = go.GetComponentInChildren<Projectile>(true);
                if (p != null)
                {
                    visual[p.GetInstanceID()] = Time.time;
                    p.damage = 0;
                    p.speed = speed;
                    p.life = life;
                    p.faction = faction;
                    Puppet shooter = puppetOf(m.Sender);
                    if (actor != 0)
                    {
                        Transform root = EnemySync.Instance != null ? EnemySync.Instance.ActorRoot(actor) : null;
                        p.owner = root != null ? root.gameObject : null;
                    }
                    else p.owner = shooter != null ? shooter.Root : null;
                    p.aimSpeed = aimSpeed;
                    p.autoAimTarget = null;
                    if (aim == AimActor && EnemySync.Instance != null) p.autoAimTarget = EnemySync.Instance.ActorRoot(aimId);
                    else if (aim == AimPlayer)
                    {
                        if (aimId == net.LocalId) p.autoAimTarget = PlayerScript.instance.transform;
                        else { Puppet tp = puppetOf(aimId); p.autoAimTarget = tp != null ? tp.Root.transform : null; }
                    }
                }
                Shown++;
            }
        }

        // Arrows come from PlayerScript.projectiles, most spells from
        // PlayerScript.effectsGos, enemy shots from their abilities' own prefab
        // fields. Any prefab loaded here counts (a prefab asset has no scene);
        // the mirror of the shooter holds the same reference, so it is loaded.
        static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        static readonly Dictionary<string, float> missesUntil = new Dictionary<string, float>();

        static GameObject Prefab(string name)
        {
            GameObject g;
            if (prefabs.TryGetValue(name, out g) && g != null) return g;
            float until;
            if (missesUntil.TryGetValue(name, out until) && Time.time < until) return null;
            g = null;
            foreach (GameObject p in PlayerScript.instance.projectiles)
                if (p != null && p.name == name) { g = p; break; }
            if (g == null)
                foreach (GameObject p in PlayerScript.instance.effectsGos)
                    if (p != null && p.name == name) { g = p; break; }
            if (g == null)
                foreach (GameObject p in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (p != null && !p.scene.IsValid() && p.name == name && p.transform.parent == null && p.GetComponentInChildren<Projectile>(true) != null) { g = p; break; }
            if (g != null) prefabs[name] = g;
            else missesUntil[name] = Time.time + 5f;
            return g;
        }

        public static string Summary()
        {
            return "projectiles fired=" + Fired + "(" + FiredByActors + " by enemies/followers) shown=" + Shown + " unknown=" + Unknown + " otherWorld=" + OtherWorld +
                " ended=" + Ended + " atOnce=" + EndedAtOnce + " meanFlight=" + (Ended > 0 ? (agesSum / Ended).ToString("F2") : "-") + "s explosions=" + Explosions;
        }

        // A player projectile's first frame: the local player's own shot, or the
        // shot of an enemy or follower simulated here.
        [HarmonyPatch(typeof(Projectile), "Update")]
        static class FirePatch
        {
            static void Prefix(Projectile __instance)
            {
                if (!seen.Add(__instance.GetInstanceID())) return;
                if (TestSpeed > 0f && !visual.ContainsKey(__instance.GetInstanceID()) && PlayerScript.instance != null && __instance.owner == PlayerScript.instance.gameObject) { __instance.speed = TestSpeed; __instance.life = 2000; }
                if (seen.Count > 2000) seen.Clear();
                if (visual.ContainsKey(__instance.GetInstanceID()) || __instance.owner == null) return;
                if (PlayerScript.instance != null && __instance.owner == PlayerScript.instance.gameObject) { outbox.Add(new Shot { P = __instance }); return; }
                if (Plugin.NoFix("enemyshots") || EnemySync.Instance == null) return;
                int actor;
                Transform root;
                if (EnemySync.Instance.SimulatedActor(__instance.owner.transform, out actor, out root)) outbox.Add(new Shot { P = __instance, Actor = actor });
            }
        }

        // A copy only shows the flight: it ends where the game's would (same
        // filters), harms nobody, and shows its explosion.
        [HarmonyPatch(typeof(Projectile), "OnTriggerEnter")]
        static class ImpactPatch
        {
            static bool Prefix(Projectile __instance, Collider other)
            {
                float born;
                if (!visual.TryGetValue(__instance.GetInstanceID(), out born)) return true;
                if (Plugin.NoFix("projectile"))
                {
                    if (other.GetComponentInParent<PuppetBody>() != null) return false;
                    Note(__instance, born, other);
                    visual.Remove(__instance.GetInstanceID());
                    Object.Destroy(__instance.gameObject);
                    return false;
                }
                NPC npc = other.GetComponent<NPC>();
                if (npc != null && (npc.faction == __instance.faction || npc.isDead)) return false;
                if (other.gameObject.layer == 2) return false;
                if (__instance.faction == NPCFaction.Player && other.CompareTag("Player")) return false;
                if (__instance.owner != null && other.transform.IsChildOf(__instance.owner.transform)) return false;
                Note(__instance, born, other);
                if (__instance.explosion != null && EffectSync.Instance != null && EffectSync.Instance.ShowExplosion(__instance.explosion, __instance.transform.position)) Explosions++;
                if (__instance.piercingThrough) return false;
                if (__instance.trail != null)
                {
                    __instance.trail.transform.parent = null;
                    __instance.trail.AddComponent<DestroyTimed>().time = 1f;
                }
                visual.Remove(__instance.GetInstanceID());
                Object.Destroy(__instance.gameObject);
                return false;
            }

            static void Note(Projectile p, float born, Collider other)
            {
                float age = Time.time - born;
                Ended++;
                agesSum += age;
                if (age < 0.1f) EndedAtOnce++;
                if (Plugin.Verbose) Plugin.Log.LogInfo(Plugin.Stamp() + " [projcopy] '" + p.transform.root.name + "' ended after " + age.ToString("F2") + " s, " +
                    Vector3.Distance(p.transform.position, p.transform.root.position).ToString("F0") + " on '" + other.name + "' layer " + other.gameObject.layer + (other.isTrigger ? " (trigger)" : ""));
            }
        }

        // ---------- test helpers ----------

        static float TestSpeed;

        // Test steps: realcast:<element> (the real spell routine, EnergySpread.MagicAttack:
        // 7 fire, 0 lava), handstate (which hand effects emit, on me and on the puppets).
        public static bool TestStep(string step, PlayerScript player)
        {
            if (step.StartsWith("projslow:"))
            {
                // Slower own shots (also in the announcement), to catch one on a screenshot.
                TestSpeed = float.Parse(step.Substring(9), System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            if (step == "lockenemy")
            {
                // As locking on with the camera: spells then fly at the target, not at the ground ahead.
                NPC best = null;
                foreach (NPC n in Object.FindObjectsOfType<NPC>())
                    if (n != null && !n.isDead && n.faction != NPCFaction.Player && n.allianceToPlayer() == ReputationLevel.Enemy && (best == null || Vector3.Distance(n.transform.position, player.transform.position) < Vector3.Distance(best.transform.position, player.transform.position))) best = n;
                CameraMovement.instance.lockOnTarget = best;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: locked on " + (best != null ? "'" + best.npcName + "' " + Vector3.Distance(best.transform.position, player.transform.position).ToString("F0") + " m away" : "nothing"));
                return true;
            }
            if (step.StartsWith("realcast:"))
            {
                Abilities.EnergySpread a = player.GetComponent<Abilities.EnergySpread>() ?? player.gameObject.AddComponent<Abilities.EnergySpread>();
                int element = int.Parse(step.Substring(9));
                Traverse t = Traverse.Create(a);
                t.Field("currentElement").SetValue(element);
                t.Field("canAttack").SetValue(true);
                player.mana = player.maxMana;
                int cast = element != 7 ? element : 0;
                player.CastEffects[cast].Play();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: casting element " + element + " (hand effect " + LocalPlayer.Path(player.CastEffects[cast].transform, player.transform) + " playing=" + player.CastEffects[cast].isPlaying + ")");
                GameManager.instance.StartCoroutine(FinishCast(a, player, cast));
                return true;
            }
            if (step == "handstate")
            {
                Plugin.Log.LogInfo(Plugin.Stamp() + " [handstate] mine playing=" + Hands(player.transform));
                foreach (Puppet p in Plugin.AllPuppets())
                    if (p.ModelRoot != null) Plugin.Log.LogInfo(Plugin.Stamp() + " [handstate] puppet " + p.Id + " playing=" + Hands(p.ModelRoot));
                return true;
            }
            return false;
        }

        static System.Collections.IEnumerator FinishCast(Abilities.EnergySpread a, PlayerScript player, int cast)
        {
            yield return new WaitForSeconds(1f);
            a.MagicAttack();
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: spell released, hand effect playing=" + player.CastEffects[cast].isPlaying);
        }

        // The hand effects by name (PlayerScript.CastEffects lives on the left hand, CasetEffectsRight on the right).
        static string Hands(Transform root)
        {
            PlayerScript pl = PlayerScript.instance;
            List<string> on = new List<string>();
            foreach (ParticleSystem[] arr in new ParticleSystem[][] { pl.CastEffects, pl.CasetEffectsRight })
                foreach (ParticleSystem ps in arr)
                {
                    if (ps == null) continue;
                    string rel = LocalPlayer.Path(ps.transform, pl.transform);
                    rel = rel.Substring(rel.IndexOf('/') + 1);
                    Transform t = root.Find(rel);
                    ParticleSystem c = t != null ? t.GetComponent<ParticleSystem>() : null;
                    if (c != null && c.isEmitting) on.Add(t.parent.name + "/" + t.name);
                }
            return on.Count + " [" + string.Join(", ", on.ToArray()) + "]";
        }
    }
}
