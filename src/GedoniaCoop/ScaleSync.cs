using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Stronger enemies for groups (owner's choice: health and damage; counted
    // are the members of one group near the enemy).
    //
    // The host decides, with its own settings ([Scaling] in gedonia.coop.cfg):
    // for every shared enemy, players within Range of it (EnemySync presence)
    // are counted per group (a player without a group counts alone); the
    // biggest count is the enemy's player count. It goes up at once and down
    // only when the smaller count has held for 10 s and the enemy is unhurt
    // (or not loaded at the host) and has no target, so a fight keeps its numbers. The host
    // sends the settings and every count above 1 (on change, and every 2 s).
    // Every machine scales the same way:
    //  - maxHealth = base * (1 + HealthPerExtraPlayer * (count - 1)), health
    //    kept in proportion (the owner's state stream carries health anyway);
    //  - damage an enemy does to the local player (PlayerScript.Damage, also
    //    hits forwarded by the enemy's owner) * (1 + DamagePerExtraPlayer * (count - 1)).
    public class ScaleSync
    {
        public static bool Enabled = true;
        public static float HealthPerExtra = 0.5f, DamagePerExtra = 0.2f, Range = 150f;
        const float DropHold = 10f;

        readonly Net net;
        readonly EnemySync enemies;
        public static ScaleSync Instance;
        public static bool Skip; // a hit already scaled (EnemySync.ApplyPlayerHit)

        // What everyone uses: the host's settings and counts.
        bool hostEnabled;
        float hostHealth, hostDamage;
        bool haveSettings;
        readonly Dictionary<int, int> counts = new Dictionary<int, int>();
        readonly Dictionary<NPC, int> baseMax = new Dictionary<NPC, int>();
        // Host only: when a smaller count was first wanted.
        readonly Dictionary<int, float> lowerSince = new Dictionary<int, float>();
        float nextDecide, nextSend, nextApply;
        bool dirty;
        public int TablesSent, TablesGot, Rescaled, HitsScaled, HitDamageBase, HitDamageScaled;

        public ScaleSync(Net net, EnemySync enemies)
        {
            this.net = net;
            this.enemies = enemies;
            Instance = this;
        }

        bool On { get { return haveSettings && hostEnabled; } }

        public int CountOf(int id)
        {
            int c;
            return counts.TryGetValue(id, out c) ? c : 1;
        }

        public void Tick(float now, bool inWorld)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgScale || m.Sender == net.LocalId || net.IsHost) continue;
                try { ReadTable(m.Payload); }
                catch (Exception e) { Plugin.Log.LogError("Scaling table from " + m.Sender + " failed: " + e); }
            }
            if (net.IsHost)
            {
                hostEnabled = Enabled; hostHealth = HealthPerExtra; hostDamage = DamagePerExtra; haveSettings = true;
                if (now >= nextDecide) { nextDecide = now + 1f; Decide(now); }
                if (dirty || now >= nextSend) { nextSend = now + 2f; dirty = false; SendTable(); }
            }
            if (inWorld && now >= nextApply) { nextApply = now + 0.5f; Apply(); }
        }

        // ---------- host ----------

        void Decide(float now)
        {
            Dictionary<int, Dictionary<int, int>> near = new Dictionary<int, Dictionary<int, int>>(); // enemy -> group key -> players
            foreach (KeyValuePair<int, Dictionary<int, float>> pl in enemies.Presence)
            {
                PartySync.Group g = PartySync.Instance != null ? PartySync.Instance.GroupOf(pl.Key) : null;
                int key = g != null ? -g.Id : pl.Key; // alone: a group of one
                foreach (KeyValuePair<int, float> en in pl.Value)
                {
                    if (en.Value > Range) continue;
                    Dictionary<int, int> byGroup;
                    if (!near.TryGetValue(en.Key, out byGroup)) near[en.Key] = byGroup = new Dictionary<int, int>();
                    int c;
                    byGroup.TryGetValue(key, out c);
                    byGroup[key] = c + 1;
                }
            }
            Dictionary<int, NPC> loaded = new Dictionary<int, NPC>();
            foreach (KeyValuePair<int, NPC> kv in enemies.SharedEnemies()) loaded[kv.Key] = kv.Value;

            HashSet<int> ids = new HashSet<int>(near.Keys);
            foreach (int id in counts.Keys) ids.Add(id);
            foreach (int id in ids)
            {
                int want = 1;
                Dictionary<int, int> byGroup;
                if (near.TryGetValue(id, out byGroup)) foreach (int c in byGroup.Values) want = Math.Max(want, c);
                int have = CountOf(id);
                if (want > have)
                {
                    counts[id] = want;
                    lowerSince.Remove(id);
                    dirty = true;
                    raised.Add(id + ":" + want);
                    continue;
                }
                if (want == have) { lowerSince.Remove(id); continue; }
                float since;
                if (!lowerSince.TryGetValue(id, out since)) { lowerSince[id] = now; continue; }
                NPC n;
                bool calm = !loaded.TryGetValue(id, out n) || n == null || n.isDead || (n.health >= n.maxHealth && !Fighting(n));
                if (now - since < DropHold || !calm) continue;
                lowerSince.Remove(id);
                if (want <= 1) counts.Remove(id); else counts[id] = want;
                dirty = true;
                lowered.Add(id + ":" + want);
            }
            if (raised.Count > 0) Plugin.Log.LogInfo(Plugin.Stamp() + " Scaling: " + raised.Count + " enemies up (id:players) " + Few(raised));
            if (lowered.Count > 0) Plugin.Log.LogInfo(Plugin.Stamp() + " Scaling: " + lowered.Count + " enemies down " + Few(lowered));
            raised.Clear(); lowered.Clear();
        }

        readonly List<string> raised = new List<string>(), lowered = new List<string>(), changed = new List<string>();

        static string Few(List<string> l)
        {
            return string.Join(" ", l.GetRange(0, Math.Min(6, l.Count)).ToArray()) + (l.Count > 6 ? " ..." : "");
        }

        static bool Fighting(NPC n)
        {
            FighterNPCModule f = n.GetComponent<FighterNPCModule>();
            return f != null && f.currentTarget != null;
        }

        void SendTable()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(hostEnabled); w.Write(hostHealth); w.Write(hostDamage);
                w.Write(counts.Count);
                foreach (KeyValuePair<int, int> kv in counts) { w.Write(kv.Key); w.Write((byte)Math.Min(kv.Value, 255)); }
                w.Flush();
                net.SendMessage(Net.MsgScale, 0, ms.ToArray(), true);
            }
            TablesSent++;
        }

        void ReadTable(byte[] payload)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
            {
                bool en = r.ReadBoolean();
                float h = r.ReadSingle(), d = r.ReadSingle();
                if (!haveSettings || en != hostEnabled || h != hostHealth || d != hostDamage)
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Scaling: host settings enabled=" + en + " health+" + (h * 100f).ToString("F0") + "% damage+" + (d * 100f).ToString("F0") + "% per extra player");
                hostEnabled = en; hostHealth = h; hostDamage = d; haveSettings = true;
                Dictionary<int, int> fresh = new Dictionary<int, int>();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) { int id = r.ReadInt32(); fresh[id] = r.ReadByte(); }
                foreach (KeyValuePair<int, int> kv in fresh) if (CountOf(kv.Key) != kv.Value) raised.Add(kv.Key + ":" + kv.Value);
                foreach (int id in counts.Keys) if (!fresh.ContainsKey(id)) lowered.Add(id + ":1");
                if (raised.Count > 0) Plugin.Log.LogInfo(Plugin.Stamp() + " Scaling (host): " + raised.Count + " enemies changed (id:players) " + Few(raised));
                if (lowered.Count > 0) Plugin.Log.LogInfo(Plugin.Stamp() + " Scaling (host): " + lowered.Count + " enemies back to 1 " + Few(lowered));
                raised.Clear(); lowered.Clear();
                counts.Clear();
                foreach (KeyValuePair<int, int> kv in fresh) counts[kv.Key] = kv.Value;
            }
            TablesGot++;
        }

        // ---------- everyone ----------

        float HealthFactor(int count) { return On ? 1f + hostHealth * (count - 1) : 1f; }
        float DamageFactor(int count) { return On ? 1f + hostDamage * (count - 1) : 1f; }

        void Apply()
        {
            foreach (KeyValuePair<int, NPC> kv in enemies.SharedEnemies())
            {
                NPC n = kv.Value;
                int b;
                if (!baseMax.TryGetValue(n, out b)) { b = n.maxHealth; baseMax[n] = b; }
                int want = Mathf.Max(1, Mathf.RoundToInt(b * HealthFactor(CountOf(kv.Key))));
                if (want == n.maxHealth || b <= 0) continue;
                int old = n.maxHealth;
                if (!n.isDead) n.health = Mathf.Clamp(Mathf.RoundToInt((float)n.health * want / Mathf.Max(1, old)), 1, want);
                n.maxHealth = want;
                Rescaled++;
                changed.Add(kv.Key + " '" + n.npcName + "' " + old + "->" + want);
            }
            if (changed.Count > 0) Plugin.Log.LogInfo(Plugin.Stamp() + " Scaling: max health of " + changed.Count + " enemies: " + Few(changed));
            changed.Clear();
            // Destroyed enemies (unloaded cells) let go of their base values.
            List<NPC> gone = null;
            foreach (NPC n in baseMax.Keys) if (n == null) { if (gone == null) gone = new List<NPC>(); gone.Add(n); }
            if (gone != null) foreach (NPC n in gone) baseMax.Remove(n);
        }

        int ScaleDamage(int enemyId, int amount)
        {
            int c = CountOf(enemyId);
            float f = DamageFactor(c);
            if (f == 1f || amount <= 0) return amount;
            int scaled = Mathf.RoundToInt(amount * f);
            HitsScaled++; HitDamageBase += amount; HitDamageScaled += scaled;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Scaling: hit from enemy " + enemyId + " " + amount + " -> " + scaled + " (" + c + " players)");
            return scaled;
        }

        public static int ScaleHit(int enemyId, int amount) { return Instance != null ? Instance.ScaleDamage(enemyId, amount) : amount; }

        public string Summary()
        {
            return "scale on=" + On + " hp+" + hostHealth + " dmg+" + hostDamage + " scaled=" + counts.Count + " rescaled=" + Rescaled +
                " hits=" + HitsScaled + "(" + HitDamageBase + "->" + HitDamageScaled + ") tables=" + TablesSent + "/" + TablesGot;
        }

        // Test step "scalelog": the nearest shared enemies' numbers, to compare both machines.
        public string Report(Vector3 from)
        {
            List<KeyValuePair<float, string>> l = new List<KeyValuePair<float, string>>();
            foreach (KeyValuePair<int, NPC> kv in enemies.SharedEnemies())
            {
                NPC n = kv.Value;
                if (n == null || n.isDead || !n.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(from, n.transform.position);
                if (d > 80f) continue;
                int b;
                baseMax.TryGetValue(n, out b);
                l.Add(new KeyValuePair<float, string>(d, kv.Key + ":" + CountOf(kv.Key) + "p," + n.health + "/" + n.maxHealth + ",base" + b));
            }
            l.Sort((a, c) => a.Key.CompareTo(c.Key));
            List<string> s = new List<string>();
            for (int i = 0; i < l.Count && i < 8; i++) s.Add(l[i].Value);
            return Summary() + " near=[" + string.Join(" ", s.ToArray()) + "]";
        }

        // Damage an enemy does to the local player.
        [HarmonyPatch(typeof(PlayerScript), "Damage", new Type[] { typeof(int), typeof(NPC.DamageType), typeof(GameObject), typeof(bool), typeof(KnockPower), typeof(bool), typeof(bool) })]
        static class PlayerDamagePatch
        {
            static void Prefix(PlayerScript __instance, ref int amount, GameObject attacker)
            {
                ScaleSync s = Instance;
                if (s == null || Skip || __instance != PlayerScript.instance) return;
                int id;
                if (!s.enemies.SharedEnemyOf(attacker, out id)) return;
                amount = s.ScaleDamage(id, amount);
            }
        }
    }
}
