using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Same time of day, season and weather for everyone; the host's clock counts.
    //
    // Gedonia keeps time and weather in the Enviro sky asset: EnviroSky.GameTime
    // (saved as day + hour) runs on its own on every machine, and each
    // EnviroZone rolls its weather at random every few game hours.
    //  - The host sends its clock, season and every zone's weather every 2 s
    //    (and at once after its own SetTime, e.g. after sleeping).
    //  - A friend whose clock is more than 2 game minutes off is set to the
    //    host's; its zones stop rolling weather and take the host's instead.
    //  - A friend who sleeps (EnviroSky.SetTime outside story events and our
    //    own corrections) asks the host to skip the same time for everyone.
    //    Time skips in story functions already run on every machine.
    public class WorldClockSync
    {
        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        public static WorldClockSync Instance;
        static bool correcting;
        float nextSend, lastCorrectionLog;
        bool sendNow;
        public int Corrections, WeatherApplied, RollsSkipped, SkipsSent, SkipsApplied, OtherWorldClocks;
        bool HostHere = true;
        public double LastOffsetSeconds;

        public WorldClockSync(Net net, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.peers = peers;
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        static EnviroSky Sky { get { return EnviroSky.instance; } }

        static bool InWorld
        {
            get { return Sky != null && Sky.started && GameManager.instance != null && PlayerScript.instance != null && SceneManager.GetActiveScene().name == "Game"; }
        }

        // Game time as one number, for comparing clocks.
        static double TotalSeconds(int years, int days, int hours, int minutes, int seconds)
        {
            return (((years * 366.0 + days) * 24.0 + hours) * 60.0 + minutes) * 60.0 + seconds;
        }

        static double Now(EnviroTime t) { return TotalSeconds(t.Years, t.Days, t.Hours, t.Minutes, t.Seconds); }

        static void SetClock(int years, int days, int hours, int minutes, int seconds)
        {
            correcting = true;
            try { Sky.SetTime(years, days, hours, minutes, seconds); }
            finally { correcting = false; }
        }

        // ---------- host ----------

        void SendClock()
        {
            EnviroTime t = Sky.GameTime;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(t.Years); w.Write(t.Days); w.Write(t.Hours); w.Write(t.Minutes); w.Write(t.Seconds);
                w.Write((int)Sky.Seasons.currentSeasons);
                List<EnviroZone> zones = Sky.Weather.zones;
                w.Write(zones.Count);
                foreach (EnviroZone z in zones)
                {
                    w.Write(z != null ? z.zoneName ?? "" : "");
                    w.Write(z != null && z.currentActiveZoneWeatherPreset != null ? z.currentActiveZoneWeatherPreset.Name : "");
                }
                w.Flush();
                net.SendMessage(Net.MsgWorldClock, 0, ms.ToArray(), false);
            }
        }

        void HostSkip(Net.Message m)
        {
            int seconds = BitConverter.ToInt32(m.Payload, 0);
            if (seconds <= 0 || seconds > 48 * 3600) return;
            EnviroTime t = Sky.GameTime;
            DateTime d = new DateTime(2000, 1, 1).AddDays(t.Days).AddHours(t.Hours).AddMinutes(t.Minutes).AddSeconds(t.Seconds + seconds);
            int days = (int)(d - new DateTime(2000, 1, 1)).TotalDays;
            SetClock(t.Years, days, d.Hour, d.Minute, d.Second);
            SkipsApplied++;
            sendNow = true;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Time skip of " + (seconds / 3600f).ToString("F1") + " h asked by " + m.Sender + " (sleeping) -> day " + days + " " + d.ToString("HH:mm"));
        }

        // ---------- friend ----------

        void ApplyClock(Net.Message m)
        {
            int years, days, hours, minutes, seconds, season;
            List<KeyValuePair<string, string>> weather = new List<KeyValuePair<string, string>>();
            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                years = r.ReadInt32(); days = r.ReadInt32(); hours = r.ReadInt32(); minutes = r.ReadInt32(); seconds = r.ReadInt32();
                season = r.ReadInt32();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) weather.Add(new KeyValuePair<string, string>(r.ReadString(), r.ReadString()));
            }

            double offset = Now(Sky.GameTime) - TotalSeconds(years, days, hours, minutes, seconds);
            LastOffsetSeconds = offset;
            if (Math.Abs(offset) > 120.0)
            {
                SetClock(years, days, hours, minutes, seconds);
                Corrections++;
                float now = Time.realtimeSinceStartup;
                if (Math.Abs(offset) > 600.0 || now - lastCorrectionLog > 30f)
                {
                    lastCorrectionLog = now;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Clock set to the host's: day " + days + " " + hours.ToString("00") + ":" + minutes.ToString("00") +
                        " (was " + (offset / 60.0).ToString("+0;-0") + " game min off)");
                }
            }

            // Season and weather belong to the hub world (the Northern Kingdoms
            // set winter, the islands summer; each world has its own zones):
            // only from a host in our world. The time of day is shared.
            HostHere = Plugin.SameWorld(m.Sender);
            if (!HostHere) { OtherWorldClocks++; return; }
            if ((int)Sky.Seasons.currentSeasons != season)
            {
                Plugin.Log.LogInfo(Plugin.Stamp() + " Season set to the host's: " + (EnviroSeasons.Seasons)season);
                Sky.ChangeSeason((EnviroSeasons.Seasons)season);
            }

            List<EnviroZone> zones = Sky.Weather.zones;
            for (int i = 0; i < weather.Count && i < zones.Count; i++)
            {
                EnviroZone z = zones[i];
                if (z == null || z.zoneName != weather[i].Key || weather[i].Value.Length == 0) continue;
                if (z.currentActiveZoneWeatherPreset != null && z.currentActiveZoneWeatherPreset.Name == weather[i].Value) continue;
                EnviroWeatherPrefab prefab = Sky.Weather.WeatherPrefabs.Find(p => p != null && p.weatherPreset != null && p.weatherPreset.Name == weather[i].Value);
                if (prefab == null) continue;
                z.lastActiveZoneWeatherPrefab = z.currentActiveZoneWeatherPrefab;
                z.lastActiveZoneWeatherPreset = z.currentActiveZoneWeatherPreset;
                z.currentActiveZoneWeatherPrefab = prefab;
                z.currentActiveZoneWeatherPreset = prefab.weatherPreset;
                Sky.NotifyZoneWeatherChanged(prefab.weatherPreset, z);
                WeatherApplied++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Weather in zone '" + z.zoneName + "' set to the host's: " + prefab.weatherPreset.Name);
            }
        }

        // Harmony: on a friend, zones don't roll their own weather.
        [HarmonyPatch(typeof(EnviroZone), "WeatherUpdate")]
        static class WeatherRollPatch
        {
            static bool Prefix()
            {
                WorldClockSync s = Instance;
                if (s == null || s.net.IsHost || !s.Connected || !s.HostHere) return true;
                s.RollsSkipped++;
                return false;
            }
        }

        // Harmony: a friend sleeping asks the host to skip the time for
        // everyone; the host sends its clock at once after its own skips.
        [HarmonyPatch(typeof(EnviroSky), "SetTime", new Type[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) })]
        static class SetTimePatch
        {
            static void Prefix(EnviroSky __instance, int year, int dayOfYear, int hour, int minute, int seconds)
            {
                WorldClockSync s = Instance;
                if (s == null || correcting || StorySync.Applying || !s.Connected || !InWorld) return;
                if (s.net.IsHost) { s.sendNow = true; return; }
                double delta = TotalSeconds(year, dayOfYear, hour, minute, seconds) - Now(__instance.GameTime);
                if (delta < 60.0) return;
                s.SkipsSent++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Local time skip of " + (delta / 3600.0).ToString("F1") + " h (sleeping): asking the host to skip for everyone");
                s.net.SendMessage(Net.MsgTimeSkip, 0, BitConverter.GetBytes((int)delta), true);
            }

            static void Postfix()
            {
                WorldClockSync s = Instance;
                if (s != null && s.net.IsHost && s.sendNow && InWorld) { s.sendNow = false; s.SendClock(); }
            }
        }

        public void Tick(float now)
        {
            foreach (Net.Message m in net.Messages)
            {
                try
                {
                    if (m.Type == Net.MsgWorldClock && !net.IsHost && InWorld) ApplyClock(m);
                    else if (m.Type == Net.MsgTimeSkip && net.IsHost && InWorld) HostSkip(m);
                }
                catch (Exception e) { Plugin.Log.LogError("Clock message " + m.Type + " from " + m.Sender + " failed: " + e); }
            }
            if (net.IsHost && InWorld && Connected && (sendNow || now >= nextSend))
            {
                sendNow = false;
                nextSend = now + 2f;
                SendClock();
            }
        }

        public static string Describe()
        {
            if (!InWorld) return "not in world";
            EnviroTime t = Sky.GameTime;
            EnviroZone z = Sky.Weather.currentActiveZone;
            return "day " + t.Days + " " + t.Hours.ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") +
                " season=" + Sky.Seasons.currentSeasons + " zone='" + (z != null ? z.zoneName : "-") + "' weather=" +
                (Sky.Weather.currentActiveWeatherPreset != null ? Sky.Weather.currentActiveWeatherPreset.Name : "-");
        }

        public string Summary()
        {
            return net.IsHost ? "clock skipsApplied=" + SkipsApplied
                : "clock offset=" + (LastOffsetSeconds / 60.0).ToString("F1") + "min corrections=" + Corrections + " weatherApplied=" + WeatherApplied +
                  " rollsSkipped=" + RollsSkipped + " skipsSent=" + SkipsSent + " hostHere=" + HostHere + " otherWorldClocks=" + OtherWorldClocks;
        }

        // Test helpers.
        public static void TestShift(float hours)
        {
            EnviroTime t = Sky.GameTime;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: shifting the local clock by " + hours + " h (like a different save)");
            SetClock(t.Years, t.Days, t.Hours + (int)hours, t.Minutes, t.Seconds);
        }

        public static void TestSleep(float hours)
        {
            EnviroTime t = Sky.GameTime;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: sleeping " + hours + " h (same SetTime call as the bed)");
            Sky.SetTime(t.Years, t.Days, t.Hours + (int)hours, t.Minutes, t.Seconds);
        }

        public static void TestWeather()
        {
            // Pick a preset different from the current one and change to it the
            // game's own way.
            List<EnviroWeatherPrefab> all = Sky.Weather.WeatherPrefabs;
            string cur = Sky.Weather.currentActiveWeatherPreset != null ? Sky.Weather.currentActiveWeatherPreset.Name : "";
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null || all[i].weatherPreset == null || all[i].weatherPreset.Name == cur) continue;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Test: changing the weather from '" + cur + "' to '" + all[i].weatherPreset.Name + "'");
                Sky.ChangeWeather(i);
                return;
            }
        }
    }
}
