using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Session statistics per player: enemies defeated (every player who hit an
    // enemy in its last 10 s counts it), damage dealt (the hit amounts the
    // player sent, before the enemy's armour), deaths and loot gold found
    // (before sharing). Each player counts its own and sends the numbers every
    // 5 s (MsgStats); the co-op menu shows the table, and leaving co-op shows
    // the own summary. A new connection starts a new session.
    public static class SessionStats
    {
        public class Row { public string Name = ""; public int Kills, Damage, Deaths, Gold; }
        public static readonly Row Mine = new Row();
        public static readonly Dictionary<int, Row> Others = new Dictionary<int, Row>();
        public static bool Open;
        public static Rect PanelRect;
        static Net current;
        static float nextSend, since;
        static readonly Dictionary<NPC, float> hitByMe = new Dictionary<NPC, float>();

        public static void Tick(Net net, float now)
        {
            if (net != current)
            {
                if (current != null && net == null && (Mine.Kills + Mine.Damage + Mine.Deaths + Mine.Gold) > 0 && Plugin.Ui != null)
                    Plugin.Ui.ShowNote(Lang.T("Co-op session: ", "Co-op-Sitzung: ") + Line(Mine) + "  (" + Mathf.RoundToInt((now - since) / 60f) + " min)", 15f);
                if (current != null) Plugin.Log.LogInfo(Plugin.Stamp() + " [stats] session over: " + Line(Mine));
                current = net;
                Mine.Kills = Mine.Damage = Mine.Deaths = Mine.Gold = 0;
                Others.Clear();
                hitByMe.Clear();
                since = now;
                Open = false;
            }
            if (net == null) return;
            Mine.Name = Plugin.LocalName;
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgStats || m.Sender == net.LocalId) continue;
                try
                {
                    using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
                        Others[m.Sender] = new Row { Name = r.ReadString(), Kills = r.ReadInt32(), Damage = r.ReadInt32(), Deaths = r.ReadInt32(), Gold = r.ReadInt32() };
                }
                catch (Exception e) { Plugin.Log.LogWarning("Stats unreadable: " + e.Message); }
            }
            if (now >= nextSend)
            {
                nextSend = now + 5f;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(Mine.Name); w.Write(Mine.Kills); w.Write(Mine.Damage); w.Write(Mine.Deaths); w.Write(Mine.Gold);
                    w.Flush();
                    net.SendMessage(Net.MsgStats, 0, ms.ToArray(), false);
                }
            }
            // Players who left drop out of the table.
            List<int> gone = null;
            foreach (int id in Others.Keys) { bool here = false; foreach (Puppet p in Plugin.AllPuppets()) if (p.Id == id) here = true; if (!here) { if (gone == null) gone = new List<int>(); gone.Add(id); } }
            if (gone != null) foreach (int id in gone) Others.Remove(id);
        }

        static string Line(Row r)
        {
            return Lang.T(r.Kills + " defeated, " + r.Damage + " damage, " + r.Deaths + " deaths, " + r.Gold + " gold",
                r.Kills + " besiegt, " + r.Damage + " Schaden, " + r.Deaths + " Tode, " + r.Gold + " Gold");
        }

        public static string Summary()
        {
            string s = "stats me[" + Line(Mine) + "]";
            foreach (KeyValuePair<int, Row> kv in Others) s += " " + kv.Value.Name + "[" + Line(kv.Value) + "]";
            return s;
        }

        public static void Died() { if (current != null) Mine.Deaths++; }
        public static void Looted(int gold) { if (current != null && gold > 0) Mine.Gold += gold; }

        // ---------- hits and kills ----------

        [HarmonyPatch(typeof(NPC), "Damage", new Type[] { typeof(int), typeof(NPC.DamageType), typeof(GameObject), typeof(bool), typeof(KnockPower), typeof(bool), typeof(bool) })]
        static class HitPatch
        {
            static void Prefix(NPC __instance, int amount, GameObject attacker)
            {
                if (current == null || __instance == null || __instance.isDead || PlayerScript.instance == null || attacker != PlayerScript.instance.gameObject) return;
                Mine.Damage += Math.Max(0, Math.Min(amount, __instance.health)); // no overkill
                hitByMe[__instance] = Time.realtimeSinceStartup;
            }
        }

        [HarmonyPatch(typeof(NPC), "Death")]
        static class KillPatch
        {
            static void Postfix(NPC __instance)
            {
                float at;
                if (current == null || __instance == null || !hitByMe.TryGetValue(__instance, out at)) return;
                hitByMe.Remove(__instance);
                if (Time.realtimeSinceStartup - at > 10f) return;
                Mine.Kills++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " [stats] defeated " + __instance.npcName + " (kills " + Mine.Kills + ")");
            }
        }

        // ---------- window ----------

        public static void Draw(float sw, GUIStyle panel)
        {
            List<Row> rows = new List<Row> { Mine };
            rows.AddRange(Others.Values);
            float w = 560f, h = 110f + rows.Count * 28f;
            Rect area = new Rect(sw / 2f - w / 2f, 160f, w, h);
            PanelRect = area;
            GameSkin.Window(area, Lang.T("Session statistics", "Sitzungsstatistik") + "  (" + Mathf.RoundToInt((Time.realtimeSinceStartup - since) / 60f) + " min)", panel);
            GUILayout.BeginArea(new Rect(area.x + 12f, area.y + 30f, area.width - 24f, area.height - 36f));
            Cells(Lang.T("Player", "Spieler"), Lang.T("Defeated", "Besiegt"), Lang.T("Damage", "Schaden"), Lang.T("Deaths", "Tode"), Lang.T("Gold found", "Gold gefunden"));
            foreach (Row r in rows) Cells(r.Name, r.Kills.ToString(), r.Damage.ToString(), r.Deaths.ToString(), r.Gold.ToString());
            if (GameSkin.LButton(Lang.T("Close", "Schließen"))) Open = false;
            GUILayout.EndArea();
        }

        static void Cells(string a, string b, string c, string d, string e)
        {
            GUILayout.BeginHorizontal();
            GameSkin.LLabel(a, GUILayout.Width(150f));
            GameSkin.LLabel(b, GUILayout.Width(80f));
            GameSkin.LLabel(c, GUILayout.Width(90f));
            GameSkin.LLabel(d, GUILayout.Width(60f));
            GameSkin.LLabel(e, GUILayout.Width(110f));
            GUILayout.EndHorizontal();
        }
    }
}
