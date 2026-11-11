using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Travelling to another hub world (GameManager.ToNorthernKigdoms / ToGedonia: ship, map, homing
    // stone) while the current world is still loading left the old world's scene loaded (seen on the
    // test host: travel 15 s after start, initial streaming not done). LoadHubWorld then takes
    // GameObject.Find("streamers") - the OLD world's (inactive) streamers - so the new world's
    // streamers never get their player and load no cell at all: no NPCs, no objects, nothing to do.
    // The guard checks every 2 s: the current world's active streamers without a player get
    // PlayerPosForStreamers, GameManager's list points at them again, and an old hub scene still
    // loaded 10 s after the new world came up is unloaded.
    public static class WorldLoadGuard
    {
        static readonly string[] Hubs = { "Gedonia", "NorthernKingdoms", "UnchartedIslands" };
        static float nextCheck, staleSince = -1f;
        public static int Repairs;

        public static void Tick(float now)
        {
            if (now < nextCheck || Plugin.NoFix("worldguard")) return;
            nextCheck = now + 2f;
            GameManager gm = GameManager.instance;
            if (gm == null || PlayerScript.instance == null || string.IsNullOrEmpty(gm.currentLoadedWorld)) return;
            // Only once the game is done loading (it sets the streamers up itself on the way; the
            // first version also "repaired" that half-done state at the start, harmless but needless).
            if (!PlayerScript.instance.enabled || (InterfaceManager.instance != null && InterfaceManager.instance.panels[10].activeSelf)) return;
            string world = gm.currentLoadedWorld;
            Scene current = SceneManager.GetSceneByName(world);
            if (!current.IsValid() || !current.isLoaded) return;

            // The current world's streamers, in the order of their "streamers" parent.
            List<Streamer> mine = new List<Streamer>();
            foreach (GameObject root in current.GetRootGameObjects())
            {
                if (root.name != "streamers") continue;
                for (int i = 0; i < root.transform.childCount; i++)
                {
                    Streamer s = root.transform.GetChild(i).GetComponent<Streamer>();
                    if (s != null) mine.Add(s);
                }
            }
            if (mine.Count == 0) return;
            bool listWrong = gm.streamers == null || gm.streamers.Length == 0 || gm.streamers[0] == null || gm.streamers[0].gameObject.scene != current;
            int noPlayer = 0;
            foreach (Streamer s in mine) if (s.streamerActive && s.player == null) noPlayer++;
            if (listWrong || noPlayer > 0)
            {
                GameObject pos = GameObject.Find("PlayerPosForStreamers");
                Transform follow = pos != null ? pos.transform : PlayerScript.instance.transform;
                foreach (Streamer s in mine) if (s.player == null) s.player = follow;
                if (listWrong && mine.Count == (gm.streamers != null ? gm.streamers.Length : mine.Count)) gm.streamers = mine.ToArray();
                Repairs++;
                Plugin.Log.LogWarning(Plugin.Stamp() + " [worldguard] " + world + ": " + noPlayer + " streamer(s) had no player" + (listWrong ? ", GameManager listed another world's streamers" : "") +
                    " - fixed (following " + follow.name + ")");
            }

            // An old hub world still loaded: unload it once the new one has been up for 10 s.
            string stale = null;
            foreach (string h in Hubs)
            {
                if (h == world) continue;
                Scene sc = SceneManager.GetSceneByName(h);
                if (sc.IsValid() && sc.isLoaded) stale = h;
            }
            if (stale == null) { staleSince = -1f; return; }
            if (staleSince < 0f) { staleSince = now; return; }
            if (now - staleSince < 10f) return;
            staleSince = -1f;
            Repairs++;
            Plugin.Log.LogWarning(Plugin.Stamp() + " [worldguard] old world '" + stale + "' still loaded in " + world + "; unloading it");
            SceneManager.UnloadSceneAsync(stale);
        }
    }
}
