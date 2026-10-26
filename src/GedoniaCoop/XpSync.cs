using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Shared combat experience (owner's wish: everyone levels at the same pace).
    //
    // An enemy's death runs on every machine (EnemySync), and each gave its own
    // player XP through PlayerScript.AddExperience - multiplied by that player's
    // intellect, experience rings, sleep and dungeon blessings, so equal kills
    // gave different XP. An execution's bonus XP came only to the executor.
    //  - Where the kill really happens (the enemy's owner, or a local-only
    //    enemy) the XP is given as usual and the amount gained is sent.
    //  - A death copied from the owner gives no XP of its own.
    //  - Receivers in the same world within 100 m of the sender add exactly
    //    that amount (no local multipliers), with the game's level-ups.
    // Quest XP already runs on every machine (StorySync); exploring, crafting
    // and puzzles stay personal.
    public class XpSync
    {
        const float Range = 100f;
        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        public static XpSync Instance;
        static int combat;  // inside an enemy death or an execution
        static bool adding; // adding a friend's XP

        readonly List<int> outbox = new List<int>();
        public int Sent, SentXp, Received, Applied, AppliedXp, TooFar, Suppressed;

        public XpSync(Net net, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.peers = peers;
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        static long Total(PlayerScript p)
        {
            long t = p.experience;
            for (int i = 0; i < p.level && i < p.levelingTable.Length; i++) t += p.levelingTable[i];
            return t;
        }

        public void Tick()
        {
            PlayerScript player = PlayerScript.instance;
            if (outbox.Count > 0 && player != null)
            {
                int sum = 0;
                foreach (int a in outbox) sum += a;
                outbox.Clear();
                Vector3 p = player.transform.position;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(sum); w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Flush();
                    net.SendMessage(Net.MsgXp, 0, ms.ToArray(), true);
                }
                Sent++;
                SentXp += sum;
                Plugin.Log.LogInfo(Plugin.Stamp() + " XP: sent " + sum + " combat XP to the others");
            }
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgXp || m.Sender == net.LocalId) continue;
                try { Receive(m, player); }
                catch (Exception e) { Plugin.Log.LogError("XP from " + m.Sender + " failed: " + e); }
            }
        }

        void Receive(Net.Message m, PlayerScript player)
        {
            int amount; Vector3 at;
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                amount = r.ReadInt32();
                at = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
            Received++;
            if (player == null || amount <= 0) return;
            float d = Vector3.Distance(player.transform.position, at);
            if (!Plugin.SameWorld(m.Sender) || d > Range)
            {
                TooFar++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " XP: " + amount + " from player " + m.Sender + " not shared (" + (Plugin.SameWorld(m.Sender) ? d.ToString("F0") + " m away" : "other world") + ")");
                return;
            }
            Add(player, amount);
            Applied++;
            AppliedXp += amount;
            Plugin.Log.LogInfo(Plugin.Stamp() + " XP: +" + amount + " from player " + m.Sender + " (" + d.ToString("F0") + " m) -> level " + player.level + " " + player.experience + "/" + player.levelingTable[player.level]);
        }

        // Exactly amount XP. Level-ups go through the game's AddExperience (its
        // loop also does stat points, effects, companions, messages): called
        // with 1, whatever that 1 became after the multipliers is taken off again.
        static void Add(PlayerScript p, int amount)
        {
            if (p.level >= 80) { adding = true; try { p.AddExperience(amount); } finally { adding = false; } return; } // the game's own end-game rules
            p.experience += amount;
            if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessages.Add("+ " + amount + " exp", new Color(1f, 0f, 1f, 1f), 0f);
            if (p.experience >= p.levelingTable[p.level])
            {
                long before = Total(p);
                adding = true;
                try { p.AddExperience(1); }
                finally { adding = false; }
                long extra = Total(p) - before;
                if (extra > 0 && p.experience >= extra) p.experience -= (int)extra;
            }
            if (InterfaceManager.instance != null)
            {
                InterfaceManager.instance.xpBar.value = (float)p.experience / (float)p.levelingTable[p.level];
                InterfaceManager.instance.xpLabel.text = "Experience: " + p.experience + "/" + p.levelingTable[p.level];
            }
        }

        public string Summary()
        {
            return "xp sent=" + Sent + "(" + SentXp + ") got=" + Received + " added=" + Applied + "(" + AppliedXp + ") far=" + TooFar + " copiedDeathsNoXp=" + Suppressed;
        }

        // ---------- Harmony ----------

        [HarmonyPatch]
        static class CombatPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(FighterNPCModule), "Death");
                foreach (Type t in typeof(FighterNPCModule).GetNestedTypes(BindingFlags.NonPublic))
                    if (t.Name.Contains("<Execution>"))
                    {
                        MethodInfo next = AccessTools.Method(t, "MoveNext");
                        if (next != null) yield return next;
                    }
            }

            static void Prefix() { combat++; }
            static void Finalizer() { combat--; }
        }

        [HarmonyPatch(typeof(PlayerScript), "AddExperience")]
        static class AddPatch
        {
            static bool Prefix(PlayerScript __instance, int amount, out long __state)
            {
                __state = -1;
                XpSync s = Instance;
                if (adding || combat <= 0 || s == null || !s.Connected || __instance != PlayerScript.instance) return true;
                if (EnemySync.RemoteDeath)
                {
                    s.Suppressed++;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " XP: " + amount + " from a death copied from its owner skipped (the owner shares its XP)");
                    return false;
                }
                __state = Total(__instance);
                return true;
            }

            static void Postfix(PlayerScript __instance, long __state)
            {
                if (__state < 0 || Instance == null) return;
                long gained = Total(__instance) - __state;
                if (gained > 0) Instance.outbox.Add((int)gained);
            }
        }
    }
}
