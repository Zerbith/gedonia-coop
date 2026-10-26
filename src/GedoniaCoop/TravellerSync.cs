using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Travellers (TravellersManager: merchants, bards, sages, shamans, citizens
    // and rabbits walking a fixed path). The game keeps a "virtual" position per
    // traveller, moved every 2 s while nobody is near, and spawns the NPC when
    // the LOCAL player comes within ~60 m. Each save has its own positions, so
    // without sync a bard walked beside the host and nowhere near the friend.
    //  - The host's table counts: it sends every traveller's position and path
    //    point every 2 s; friends stop moving their own virtual travellers and
    //    take the host's (so they load where the host's are).
    //  - A spawned traveller with a FighterNPCModule is shared through
    //    EnemySync (id "traveller/<scene>/<index>"): its owner walks it, the
    //    others mirror it. Others (citizens, rabbits) walk on each machine from
    //    the same start.
    //  - Whoever runs a spawned traveller reports where it is; the host takes
    //    friends' reports for travellers it hasn't spawned itself.
    public class TravellerSync
    {
        readonly Net net;
        readonly Func<IEnumerable<int>> peers;
        public static TravellerSync Instance;
        float nextSend;
        bool hostHere;
        public int TablesSent, TablesApplied, ReportsApplied, MovesSkipped, OtherWorld;
        // Host: travellers a friend reported running, and when.
        readonly Dictionary<int, float> reportedAt = new Dictionary<int, float>();

        public TravellerSync(Net net, Func<IEnumerable<int>> peers)
        {
            this.net = net;
            this.peers = peers;
            Instance = this;
        }

        bool Connected { get { foreach (int p in peers()) return true; return false; } }

        static TravellersManager Tm { get { return TravellersManager.instance; } }

        static bool InWorld
        {
            get { return Tm != null && Tm.travellers != null && PlayerScript.instance != null && SceneManager.GetActiveScene().name == "Game"; }
        }

        static string World { get { return GameManager.instance != null ? GameManager.instance.currentLoadedWorld ?? "" : ""; } }

        public static int IdOf(int index) { return WorldIds.Hash("traveller/" + Tm.gameObject.scene.name + "/" + index); }

        // EnemySync: a spawned traveller gets an id that is the same everywhere.
        public static bool IdFor(NPC npc, out int id)
        {
            id = 0;
            if (npc == null || !npc.isTravelling || Tm == null || Tm.travellers == null) return false;
            int i = npc.travellerID;
            if (i < 0 || i >= Tm.travellers.Length || Tm.travellers[i].createdGo != npc.gameObject) return false;
            id = IdOf(i);
            return true;
        }

        // Whether this machine walks traveller i itself right now.
        static bool RunsHere(int i)
        {
            GameObject go = Tm.travellers[i].createdGo;
            if (!Tm.travellers[i].isLoaded || go == null) return false;
            NPC npc = go.GetComponent<NPC>();
            return EnemySync.Instance == null || EnemySync.Instance.RunsHere(npc);
        }

        // ---------- sending ----------

        // all: the host's whole table; else only the travellers we walk.
        void Send(bool all)
        {
            TravellersManager.Traveller[] t = Tm.travellers;
            List<int> list = new List<int>();
            for (int i = 0; i < t.Length; i++)
                if (all || RunsHere(i)) list.Add(i);
            if (list.Count == 0) return;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(net.IsHost);
                w.Write(World);
                w.Write(t.Length);
                w.Write((short)list.Count);
                foreach (int i in list)
                {
                    w.Write((short)i);
                    w.Write(t[i].currentPosition.x); w.Write(t[i].currentPosition.y); w.Write(t[i].currentPosition.z);
                    w.Write((short)t[i].currentPP);
                    w.Write(RunsHere(i));
                }
                w.Flush();
                net.SendMessage(Net.MsgTravellers, 0, ms.ToArray(), false);
            }
            TablesSent++;
        }

        // ---------- receiving ----------

        void Apply(Net.Message m, float now)
        {

            using (BinaryReader r = new BinaryReader(new MemoryStream(m.Payload)))
            {
                fromHost = r.ReadBoolean() && !net.IsHost;
                string world = r.ReadString();
                int total = r.ReadInt32();
                TravellersManager.Traveller[] t = Tm.travellers;
                bool sameWorld = world == World && total == t.Length && Plugin.SameWorld(m.Sender);
                if (fromHost) hostHere = sameWorld;
                if (!sameWorld) { OtherWorld++; return; }
                int n = r.ReadInt16();
                for (int k = 0; k < n; k++)
                {
                    int i = r.ReadInt16();
                    Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    int pp = r.ReadInt16();
                    bool walked = r.ReadBoolean();
                    if (i < 0 || i >= t.Length || t[i].path == null || pp < 0 || pp >= t[i].path.Length) continue;
                    // A traveller we walk ourselves keeps our position.
                    if (RunsHere(i)) continue;
                    if (net.IsHost)
                    {
                        // Friends only report the ones they walk.
                        if (!walked) continue;
                        reportedAt[i] = now;
                        ReportsApplied++;
                    }
                    else if (!walked && t[i].isLoaded && t[i].createdGo != null && t[i].createdGo.GetComponent<FighterNPCModule>() == null)
                    {
                        // A citizen walking here on its own AI: leave it.
                        continue;
                    }
                    t[i].currentPosition = pos;
                    t[i].currentPP = pp;
                }
            }
            if (fromHost) TablesApplied++;
        }

        bool fromHost;

        // Harmony: friends (with the host in the same world) don't move their
        // virtual travellers; the host doesn't move the ones a friend walks.
        [HarmonyPatch(typeof(TravellersManager), "MoveTravellers")]
        static class MovePatch
        {
            static bool Prefix(TravellersManager __instance, out List<int> __state)
            {
                __state = null;
                TravellerSync s = Instance;
                if (s == null || !s.Connected || !InWorld) return true;
                if (!s.net.IsHost)
                {
                    if (!s.hostHere) return true;
                    s.MovesSkipped++;
                    return false;
                }
                // Host: the game moves all but the reported ones (held as
                // "stopped" for this one call).
                float now = Time.realtimeSinceStartup;
                TravellersManager.Traveller[] t = __instance.travellers;
                foreach (KeyValuePair<int, float> kv in s.reportedAt)
                {
                    if (now - kv.Value >= 5f || kv.Key >= t.Length || t[kv.Key].isStopped) continue;
                    if (__state == null) __state = new List<int>();
                    __state.Add(kv.Key);
                    t[kv.Key].isStopped = true;
                }
                return true;
            }

            static void Postfix(TravellersManager __instance, List<int> __state)
            {
                if (__state == null) return;
                foreach (int i in __state) __instance.travellers[i].isStopped = false;
            }
        }

        public void Tick(float now)
        {
            foreach (Net.Message m in net.Messages)
            {
                if (m.Type != Net.MsgTravellers || !InWorld) continue;
                try { Apply(m, now); }
                catch (Exception e) { Plugin.Log.LogError("Traveller message from " + m.Sender + " failed: " + e); }
            }
            if (InWorld && Connected && now >= nextSend)
            {
                nextSend = now + 2f;
                Send(net.IsHost);
            }
        }

        public string Summary()
        {
            return "travellers sent=" + TablesSent + (net.IsHost ? " reports=" + ReportsApplied : " applied=" + TablesApplied + " movesSkipped=" + MovesSkipped + " hostHere=" + hostHere) + " otherWorld=" + OtherWorld;
        }

        // ---------- test helpers ----------

        public static string Describe()
        {
            TravellersManager tm = TravellersManager.instance;
            if (tm == null || tm.travellers == null) return "no TravellersManager";
            Vector3 me = PlayerScript.instance != null ? PlayerScript.instance.transform.position : Vector3.zero;
            string s = tm.travellers.Length + " travellers (load " + tm.loadingDistance + ", unload " + tm.unloadingDistance + ", scene " + tm.gameObject.scene.name + ")";
            for (int i = 0; i < tm.travellers.Length; i++)
            {
                TravellersManager.Traveller t = tm.travellers[i];
                NPC n = t.prefab != null ? t.prefab.GetComponent<NPC>() : null;
                s += "\n  #" + i + " '" + t.name + "' prefab=" + (t.prefab != null ? t.prefab.name : "-") + " npc='" + (n != null ? n.npcName : "-") +
                    "' fighter=" + (t.prefab != null && t.prefab.GetComponent<FighterNPCModule>() != null) +
                    " pos=" + t.currentPosition.ToString("F1") + " dist=" + Vector3.Distance(me, t.currentPosition).ToString("F0") + " pp=" + t.currentPP + "/" + (t.path != null ? t.path.Length : 0) +
                    " loaded=" + t.isLoaded + (t.createdGo != null ? " go@" + t.createdGo.transform.position.ToString("F1") + (RunsHere(i) ? " runs" : " mirror") : "");
            }
            return s;
        }

        // Moves every traveller 5 path points on, like another save would have them.
        public static void TestShift()
        {
            TravellersManager.Traveller[] t = Tm.travellers;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i].path == null || t[i].path.Length == 0 || t[i].isLoaded) continue;
                t[i].currentPP = (t[i].currentPP + 5) % t[i].path.Length;
                t[i].currentPosition = t[i].path[t[i].currentPP].position;
            }
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: moved every traveller 5 path points on");
        }

        // Puts the player next to traveller i's current position.
        public static void TestGoto(PlayerScript player, int i)
        {
            Vector3 p = Tm.travellers[i].currentPosition;
            RaycastHit hit;
            Vector3 to = Physics.Raycast(p + new Vector3(6f, 200f, 0f), Vector3.down, out hit, 500f, ~0, QueryTriggerInteraction.Ignore) ? hit.point + Vector3.up * 1.5f : p + new Vector3(6f, 2f, 0f);
            if (player._char != null) player._char.enabled = false;
            player.transform.position = to;
            if (player._char != null) player._char.enabled = true;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Test: next to traveller #" + i + " '" + Tm.travellers[i].name + "' at " + p.ToString("F1"));
        }
    }
}
