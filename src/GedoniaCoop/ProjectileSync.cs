using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GedoniaCoop
{
    // Arrows (and other player projectiles) for the eyes of the other players.
    // A projectile only exists on the shooter's PC; its hit already reaches the
    // enemy's owner through EnemySync's damage path. Here the shooter announces
    // each new projectile it fires, and the others fly a copy that does no
    // damage and vanishes on its first impact.
    public class ProjectileSync
    {
        readonly Net net;
        public static int Fired, Shown, Unknown, OtherWorld;
        static readonly HashSet<int> seen = new HashSet<int>();
        static readonly HashSet<int> visual = new HashSet<int>();
        static readonly List<Projectile> outbox = new List<Projectile>();

        public ProjectileSync(Net net)
        {
            this.net = net;
        }

        public void Tick(Func<int, Puppet> puppetOf)
        {
            foreach (Projectile p in outbox)
            {
                if (p == null) continue;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    Transform t = p.transform;
                    w.Write(p.gameObject.name.Replace("(Clone)", "").Trim());
                    w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
                    w.Write(t.rotation.x); w.Write(t.rotation.y); w.Write(t.rotation.z); w.Write(t.rotation.w);
                    w.Write(p.speed); w.Write(p.life);
                    w.Flush();
                    // Unreliable: an arrow that arrives late is worse than none.
                    net.SendMessage(Net.MsgProjectile, 0, ms.ToArray(), false);
                }
                Fired++;
            }
            outbox.Clear();

            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgProjectile) continue;
                if (!Plugin.SameWorld(m.Sender)) { OtherWorld++; continue; }
                try { Show(m, puppetOf(m.Sender)); }
                catch (Exception e) { Plugin.Log.LogWarning("Projectile from " + m.Sender + " failed: " + e.Message); }
            }
        }

        void Show(Net.Message m, Puppet shooter)
        {
            if (PlayerScript.instance == null || PlayerScript.instance.projectiles == null) return;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                string name = r.ReadString();
                Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                Quaternion rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float speed = r.ReadSingle();
                int life = r.ReadInt32();
                GameObject prefab = Prefab(name);
                if (prefab == null) { Unknown++; return; }
                GameObject go = Object.Instantiate(prefab, pos, rot);
                Projectile p = go.GetComponent<Projectile>();
                if (p != null)
                {
                    visual.Add(p.GetInstanceID());
                    p.damage = 0;
                    p.speed = speed;
                    p.life = life;
                    p.owner = shooter != null ? shooter.Root : null;
                    p.faction = NPCFaction.Player;
                }
                Shown++;
            }
        }

        // Arrows come from PlayerScript.projectiles; spells from their abilities'
        // own prefab fields. Any projectile prefab loaded here counts (a prefab
        // asset has no scene); looked up once per name.
        static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

        static GameObject Prefab(string name)
        {
            GameObject g;
            if (prefabs.TryGetValue(name, out g) && g != null) return g;
            g = null;
            foreach (GameObject p in PlayerScript.instance.projectiles)
                if (p != null && p.name == name) { g = p; break; }
            if (g == null)
                foreach (Projectile p in Resources.FindObjectsOfTypeAll<Projectile>())
                    if (p != null && !p.gameObject.scene.IsValid() && p.gameObject.name == name && p.transform.parent == null) { g = p.gameObject; break; }
            if (g != null) prefabs[name] = g;
            return g;
        }

        public static string Summary()
        {
            return "projectiles fired=" + Fired + " shown=" + Shown + " unknown=" + Unknown + " otherWorld=" + OtherWorld;
        }

        // A player projectile's first frame: the local player's own shot.
        [HarmonyPatch(typeof(Projectile), "Update")]
        static class FirePatch
        {
            static void Prefix(Projectile __instance)
            {
                if (!seen.Add(__instance.GetInstanceID())) return;
                if (seen.Count > 2000) seen.Clear();
                if (visual.Contains(__instance.GetInstanceID())) return;
                if (PlayerScript.instance != null && __instance.owner == PlayerScript.instance.gameObject) outbox.Add(__instance);
            }
        }

        // A copy only shows the flight: it hits nothing and is gone on impact
        // (but passes the shooter's own puppet it starts inside).
        [HarmonyPatch(typeof(Projectile), "OnTriggerEnter")]
        static class ImpactPatch
        {
            static bool Prefix(Projectile __instance, Collider other)
            {
                if (!visual.Contains(__instance.GetInstanceID())) return true;
                if (other.GetComponentInParent<PuppetBody>() != null) return false;
                visual.Remove(__instance.GetInstanceID());
                Object.Destroy(__instance.gameObject);
                return false;
            }
        }
    }
}
