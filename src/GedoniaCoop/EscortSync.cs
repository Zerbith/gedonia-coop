using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Escorts (EscortNPCModule): the NPC walks its path while the player is within 6 m and at the
    // end sends its completePathMessage to GameManager every physics frame until the story stops it.
    // Before, every PC walked its own copy and ran the completion itself.
    //  - Any player near the NPC counts, not only the local one (a friend can escort alone; copies
    //    on both PCs walk together).
    //  - A copy EnemySync mirrors from another PC (replica) does not walk itself: the owner moves it.
    //  - The completion goes through the story log once per escort (StorySync.EscortDone): the first
    //    PC whose copy arrives triggers it, the event runs on every PC, later arrivals are dropped.
    public static class EscortSync
    {
        public static int Completions, Dropped;
        static readonly Dictionary<int, float> sentAt = new Dictionary<int, float>();

        static bool Active { get { return StorySync.Instance != null && StorySync.Instance.Connected && !Plugin.NoFix("escorts"); } }

        [HarmonyPatch(typeof(EscortNPCModule), "FixedUpdate")]
        static class WalkPatch
        {
            static bool Prefix(EscortNPCModule __instance)
            {
                if (!Active || !__instance.isWalking) return true;
                int id;
                NPC n = __instance.GetComponent<NPC>();
                // Mirrored from its owner: position and animation come from there. Its path point
                // follows the mirrored position (never back), so if the sharing ends the copy walks
                // on from there instead of back to the start (escort test: role 2 -> 0).
                if (EnemySync.StoryRole(n, out id) != 2) return true;
                Transform[] path = __instance.path;
                if (path != null && path.Length > 0)
                {
                    int best = __instance.currentPathP; float bestD = float.MaxValue;
                    for (int i = 0; i < path.Length; i++)
                    {
                        if (path[i] == null) continue;
                        float d = Vector3.Distance(path[i].position, __instance.transform.position);
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    if (best > __instance.currentPathP) __instance.currentPathP = best;
                }
                return false;
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo dist = AccessTools.Method(typeof(Vector3), "Distance");
                MethodInfo near = AccessTools.Method(typeof(EscortSync), "Dist");
                MethodInfo send2 = AccessTools.Method(typeof(GameObject), "SendMessage", new Type[] { typeof(string), typeof(SendMessageOptions) });
                MethodInfo done = AccessTools.Method(typeof(EscortSync), "Complete");
                int d = 0, s = 0;
                foreach (CodeInstruction i in instructions)
                {
                    if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && Equals(i.operand, dist)) { i.opcode = OpCodes.Call; i.operand = near; d++; }
                    else if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && Equals(i.operand, send2)) { i.opcode = OpCodes.Call; i.operand = done; s++; }
                    yield return i;
                }
                Plugin.Log.LogInfo("Escort hook: " + d + " distance and " + s + " message call(s)");
            }
        }

        // Vector3.Distance, except when b is the local player's position: then the nearest player.
        public static float Dist(Vector3 a, Vector3 b)
        {
            float best = Vector3.Distance(a, b);
            PlayerScript p = PlayerScript.instance;
            if (!Active || p == null || p.tr == null || b != p.tr.position) return best;
            foreach (Puppet pp in Plugin.AllPuppets())
                if (pp != null && pp.Visible) best = Mathf.Min(best, Vector3.Distance(a, pp.Position));
            return best;
        }

        // The two SendMessage calls of FixedUpdate: "UpdateDatabase" on the escort itself (kept) and
        // the completion on GameManager (once, through the story log).
        public static void Complete(GameObject target, string name, SendMessageOptions o)
        {
            if (!Active || GameManager.instance == null || target != GameManager.instance.gameObject || string.IsNullOrEmpty(name))
            {
                target.SendMessage(name, o);
                return;
            }
            // Which escort: the one whose FixedUpdate is running (the only caller of this hook).
            EscortNPCModule e = current;
            int id = e != null ? DialogueSync.NpcId(e.gameObject) : 0;
            float at;
            if (sentAt.TryGetValue(id, out at) && Time.realtimeSinceStartup - at < 30f) return; // every frame until stopped
            sentAt[id] = Time.realtimeSinceStartup;
            Completions++;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Escort '" + (e != null ? e.name : "?") + "' (" + id + ") reached its end: '" + name + "' goes through the story log");
            StorySync.Instance.Escort(name, id);
        }

        static EscortNPCModule current;

        // ---------- test steps ----------
        // escortfollow:<save id> - stay 2.5 m behind that escort (like a player following it);
        // escortlog:<save id> - log its walk state every 10 s from now on.
        static int followId, logId;
        static float nextFollow, nextLog, nextClear, stuckSince;
        static Vector3 stuckPos;

        static EscortNPCModule Find(int id)
        {
            foreach (EscortNPCModule e in Resources.FindObjectsOfTypeAll<EscortNPCModule>())
                if (e != null && e.gameObject.scene.IsValid() && DialogueSync.NpcId(e.gameObject) == id) return e;
            return null;
        }

        static string FightState(EscortNPCModule e)
        {
            FighterNPCModule f = e.GetComponent<FighterNPCModule>();
            NPC n = e.GetComponent<NPC>();
            return " state=" + (f != null ? f.state.ToString() : "-") + " target=" + (f != null && f.currentTarget != null ? f.currentTarget.name : "-") +
                " stoppedTravel=" + (n != null && n.stoppedTravel);
        }

        public static void TestFollow(string arg) { int.TryParse(arg, out followId); Plugin.Log.LogInfo(Plugin.Stamp() + " [escort] following " + followId); }
        public static void TestLog(string arg) { int.TryParse(arg, out logId); }

        public static void TestTick(PlayerScript player, float now)
        {
            if (player == null) return;
            if (followId != 0 && now >= nextFollow)
            {
                nextFollow = now + 0.5f;
                EscortNPCModule e = Find(followId);
                if (e != null && e.isWalking)
                {
                    Vector3 back = e.transform.position - e.transform.forward * 2.5f;
                    if (Vector3.Distance(player.transform.position, back) > 2f) player.transform.position = back + Vector3.up * 0.3f;
                }
            }
            // Test only: the escort can't die (enemies on the path killed Ned in the first run).
            int guard = logId != 0 ? logId : followId;
            if (guard != 0)
            {
                EscortNPCModule g = Find(guard);
                NPC gn = g != null ? g.GetComponent<NPC>() : null;
                if (gn != null && !gn.isDead && gn.health < gn.maxHealth) gn.health = gn.maxHealth;
                // ...and it doesn't hang where its path agent gets stuck (Ned at path point 6 stood still
                // for minutes, idle, not fighting): after 15 s in one spot the owner's copy is put on
                // its current path point (a player would push or lure it on).
                int gid;
                if (g != null && g.isWalking && gn != null && EnemySync.StoryRole(gn, out gid) != 2 && g.path != null && g.currentPathP < g.path.Length)
                {
                    if (Vector3.Distance(g.transform.position, stuckPos) > 0.5f) { stuckPos = g.transform.position; stuckSince = now; }
                    else if (now - stuckSince > 15f)
                    {
                        Plugin.Log.LogInfo(Plugin.Stamp() + " [escort] test: stuck at point " + g.currentPathP + " for 15 s; put on it");
                        g.transform.position = g.path[g.currentPathP].position;
                        stuckSince = now;
                    }
                }
                // ...and its attackers fall, as players would see to (an escort in a fight doesn't walk).
                if (gn != null && now >= nextClear)
                {
                    nextClear = now + 2f;
                    foreach (FighterNPCModule f in UnityEngine.Object.FindObjectsOfType<FighterNPCModule>())
                    {
                        NPC e = f.GetComponent<NPC>();
                        if (e == null || e == gn || e.isDead || Vector3.Distance(e.transform.position, gn.transform.position) > 60f) continue;
                        FighterNPCModule gf = gn.GetComponent<FighterNPCModule>();
                        bool fighting = f.currentTarget == gn.gameObject || (gf != null && gf.currentTarget == f.gameObject);
                        if (!fighting && (e.allianceToPlayer() != ReputationLevel.Enemy || Vector3.Distance(e.transform.position, gn.transform.position) > 20f)) continue;
                        int eid;
                        if (EnemySync.StoryRole(e, out eid) == 2) continue; // its owner kills it
                        Plugin.Log.LogInfo(Plugin.Stamp() + " [escort] test: '" + e.npcName + "' near the escort falls");
                        e.Damage(e.health + 100000, NPC.DamageType.Melee, player.gameObject, false, KnockPower.None, false, false);
                    }
                }
            }
            if (logId != 0 && now >= nextLog)
            {
                nextLog = now + 10f;
                EscortNPCModule e = Find(logId);
                int id;
                Plugin.Log.LogInfo(Plugin.Stamp() + " [escort] " + logId + ": " + (e == null ? "not loaded" :
                    "walking=" + e.isWalking + " point " + e.currentPathP + "/" + (e.path != null ? e.path.Length : 0) + " at " + e.transform.position.ToString("F1") +
                    " role=" + EnemySync.StoryRole(e.GetComponent<NPC>(), out id) + FightState(e) + " me at " + player.transform.position.ToString("F1")) +
                    " completions=" + Completions);
            }
        }

        [HarmonyPatch(typeof(EscortNPCModule), "FixedUpdate")]
        static class CurrentPatch
        {
            static void Prefix(EscortNPCModule __instance) { current = __instance; }
            static void Postfix() { current = null; }
        }
    }
}
