using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Test-only (-coop-census 1): what the world holds right now, to decide
    // how enemies are identified and who should simulate them.
    public static class Census
    {
        public static void Log(Vector3 player)
        {
            FighterNPCModule[] all = Resources.FindObjectsOfTypeAll<FighterNPCModule>();
            int inScene = 0, active = 0, hostile = 0, withId = 0, zeroId = 0, dead = 0;
            Dictionary<int, int> idCount = new Dictionary<int, int>();
            Dictionary<string, int> perScene = new Dictionary<string, int>();
            float nearest = float.MaxValue;
            int within50 = 0, within150 = 0;
            foreach (FighterNPCModule f in all)
            {
                if (f == null || !f.gameObject.scene.IsValid()) continue; // prefabs/assets
                inScene++;
                string sc = f.gameObject.scene.name;
                perScene[sc] = perScene.ContainsKey(sc) ? perScene[sc] + 1 : 1;
                if (f.gameObject.activeInHierarchy) active++;
                NPC npc = f.npc != null ? f.npc : f.GetComponent<NPC>();
                if (npc != null && npc.isDead) dead++;
                if (npc != null && GameManager.instance != null && npc.allianceToPlayer() == ReputationLevel.Enemy) hostile++;
                SaveableItem s = f.GetComponent<SaveableItem>();
                if (s != null)
                {
                    withId++;
                    if (s.instanceID == 0) zeroId++;
                    idCount[s.instanceID] = idCount.ContainsKey(s.instanceID) ? idCount[s.instanceID] + 1 : 1;
                }
                if (f.gameObject.activeInHierarchy)
                {
                    float d = Vector3.Distance(player, f.transform.position);
                    nearest = Mathf.Min(nearest, d);
                    if (d < 50f) within50++;
                    if (d < 150f) within150++;
                }
            }
            int dupIds = 0;
            foreach (KeyValuePair<int, int> kv in idCount) if (kv.Value > 1) dupIds += kv.Value;

            StringBuilder sb = new StringBuilder("[census] fighters=" + inScene + " active=" + active + " hostile=" + hostile +
                " dead=" + dead + " withSaveableItem=" + withId + " idZero=" + zeroId + " inDuplicateIds=" + dupIds +
                " active<50m=" + within50 + " active<150m=" + within150 + " nearest=" + (nearest < float.MaxValue ? nearest.ToString("F0") : "-") + "m");
            sb.Append(" | per scene:");
            foreach (KeyValuePair<string, int> kv in perScene) sb.Append(" " + kv.Key + "=" + kv.Value);
            sb.Append(" | loaded scenes:");
            for (int i = 0; i < SceneManager.sceneCount; i++) sb.Append(" " + SceneManager.GetSceneAt(i).name);
            sb.Append(" | streamers=" + Object.FindObjectsOfType<ColliderStreamer>().Length);
            Plugin.Log.LogInfo(sb.ToString());

            // World events, nearest first.
            List<KeyValuePair<float, string>> events = new List<KeyValuePair<float, string>>();
            foreach (WorldEventBase ev in Resources.FindObjectsOfTypeAll<WorldEventBase>())
            {
                if (ev == null || !ev.gameObject.scene.IsValid()) continue;
                float d = Vector3.Distance(player, ev.transform.position);
                int spawners = 0;
                foreach (WorldEventBase.Phase ph in ev.phases) if (ph.spawners != null) spawners += ph.spawners.Length;
                events.Add(new KeyValuePair<float, string>(d, "'" + ev.eventName + "' scene=" + ev.gameObject.scene.name +
                    " at " + ev.transform.position.ToString("F0") + " d=" + d.ToString("F0") + " type=" + ev.eventType + " active=" + ev.isActive +
                    " go=" + ev.gameObject.activeInHierarchy + " phase=" + ev.currentPhase + "/" + ev.phases.Length + " count=" + ev.currentCount +
                    " spawners=" + spawners + " range=" + ev.activeRange));
            }
            events.Sort((a, b) => a.Key.CompareTo(b.Key));
            StringBuilder es = new StringBuilder("[census] world events: " + events.Count);
            for (int i = 0; i < events.Count && i < 8; i++) es.Append("\n  " + events[i].Value);
            Plugin.Log.LogInfo(es.ToString());
        }
    }
}
