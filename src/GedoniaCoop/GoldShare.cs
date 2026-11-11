using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Group loot rule: shared gold. Items stay as they are (chests: first come,
    // see LootSync; enemy bodies: everyone loots their own), but gold taken
    // from a loot window (chest, body, herb ... InterfaceManager.TakeLootItem /
    // TakeAllLoot) is split evenly between the players in the same world; the
    // looter keeps the remainder of the division. The host decides for
    // everyone (co-op menu, config Loot/ShareGold) and repeats the rule every
    // few seconds, so late joiners get it. Quest rewards, selling and trading
    // are not split.
    public class GoldShare
    {
        public static ConfigEntry<bool> Entry;
        public static bool On { get { return Entry == null || Entry.Value; } } // the host's setting
        public static bool Active; // what counts in this session (host: own setting; client: the host's)
        public static GoldShare Instance;
        readonly Net net;
        float nextRule;
        public int SharesSent, SharesReceived, GoldGiven, GoldGot;

        const byte KindRule = 0, KindShare = 1;

        public GoldShare(Net net) { this.net = net; Instance = this; }
        public Net Net { get { return net; } }

        public void Tick(float now)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgGold || m.Sender == net.LocalId) continue;
                try { Receive(m); } catch (Exception e) { Plugin.Log.LogWarning("Gold message unreadable: " + e.Message); }
            }
            if (net.IsHost)
            {
                if (Active != On) { Active = On; Plugin.Log.LogInfo(Plugin.Stamp() + " [gold] sharing " + (Active ? "on" : "off") + " (host setting)"); nextRule = 0f; }
                if (now >= nextRule) { nextRule = now + 5f; SendRule(); }
            }
        }

        void SendRule()
        {
            net.SendMessage(Net.MsgGold, 0, new byte[] { KindRule, (byte)(Active ? 1 : 0) }, true);
        }

        public void Toggle()
        {
            if (Entry != null) Entry.Value = !Entry.Value;
            nextRule = 0f;
        }

        void Receive(Net.Message m)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                byte kind = r.ReadByte();
                if (kind == KindRule)
                {
                    bool on = r.ReadByte() != 0;
                    if (on != Active) { Active = on; Plugin.Log.LogInfo(Plugin.Stamp() + " [gold] sharing " + (on ? "on" : "off") + " (from the host)"); }
                    return;
                }
                if (kind != KindShare || m.Target != net.LocalId) return;
                int amount = r.ReadInt32();
                string from = r.ReadString();
                PlayerScript p = PlayerScript.instance;
                if (p == null || amount <= 0) return;
                p.gold += amount;
                SharesReceived++; GoldGot += amount;
                InterfaceManager im = InterfaceManager.instance;
                if (im != null)
                {
                    if (im.moneyLabel != null) im.moneyLabel.text = p.gold.ToString();
                    im.screenMessages.Add(Lang.T("+" + amount + " gold (share from " + from + ")", "+" + amount + " Gold (Anteil von " + from + ")"), new Color(1f, 0.85f, 0.4f), 2.5f);
                }
                Plugin.Log.LogInfo(Plugin.Stamp() + " [gold] got " + amount + " from " + from + ", now " + p.gold);
            }
        }

        // The local player took `gold` from a loot window.
        void Looted(int gold)
        {
            PlayerScript p = PlayerScript.instance;
            if (!Active || p == null || gold <= 0) return;
            List<Puppet> mates = new List<Puppet>();
            foreach (Puppet t in Plugin.AllPuppets()) if (t.Latest != null && Plugin.SameWorld(t.Id)) mates.Add(t);
            if (mates.Count == 0) return;
            int share = gold / (mates.Count + 1);
            if (share <= 0) return;
            foreach (Puppet t in mates)
            {
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(KindShare); w.Write(share); w.Write(Plugin.LocalName);
                    w.Flush();
                    net.SendMessage(Net.MsgGold, t.Id, ms.ToArray(), true);
                }
                p.gold -= share;
                SharesSent++; GoldGiven += share;
            }
            InterfaceManager im = InterfaceManager.instance;
            if (im != null && im.moneyLabel != null) im.moneyLabel.text = p.gold.ToString();
            Plugin.Log.LogInfo(Plugin.Stamp() + " [gold] looted " + gold + ", " + share + " each to " + mates.Count + " teammate(s), kept " + (gold - share * mates.Count) + ", now " + p.gold);
        }

        public string Summary() { return "gold share=" + Active + " sent=" + SharesSent + "(" + GoldGiven + ") got=" + SharesReceived + "(" + GoldGot + ")"; }

        // ---------- the game's loot window ----------

        static int goldBefore = -1;
        static void Before() { goldBefore = PlayerScript.instance != null ? PlayerScript.instance.gold : -1; }
        static void After()
        {
            if (goldBefore < 0 || PlayerScript.instance == null || Instance == null) return;
            int d = PlayerScript.instance.gold - goldBefore;
            goldBefore = -1;
            if (d > 0) { SessionStats.Looted(d); Instance.Looted(d); }
        }

        [HarmonyPatch(typeof(InterfaceManager), "TakeLootItem")]
        static class TakeOnePatch
        {
            static void Prefix() { Before(); }
            static void Postfix() { After(); }
        }

        [HarmonyPatch(typeof(InterfaceManager), "TakeAllLoot")]
        static class TakeAllPatch
        {
            static void Prefix() { Before(); }
            static void Postfix() { After(); }
        }
    }
}
