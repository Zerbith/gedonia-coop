using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // While a player lies dead in co-op the camera follows a living teammate
    // instead of the body. CameraMovement follows its PlayerTr in NewMovement
    // (FixedUpdate) and NonFixedMovement (Update); for those two calls PlayerTr
    // is swapped for a proxy that stands where the teammate's puppet is, so the
    // game's own smoothing, zoom and wall collision keep working and the dead
    // player's own transform is not turned. Only teammates within Range count:
    // the world streams in around the local player, farther away is empty.
    // A teammate close to the body (Near) is already in view: no switch then.
    // Left mouse button: next teammate (with 3+ players).
    public static class Spectate
    {
        const float Range = 150f, Near = 12f;
        static Transform proxy;
        static int target; // puppet id, 0 = own camera
        public static int Switches;
        public static string Watching = "";

        public static bool Active { get { return target != 0 && proxy != null; } }

        public static void Tick(PlayerScript player, bool down)
        {
            Puppet pick = null;
            if (down && !Plugin.NoFix("spectate"))
            {
                Puppet current = null, first = null, next = null;
                bool passed = false;
                foreach (Puppet p in Plugin.AllPuppets())
                {
                    if (!Eligible(p, player)) continue;
                    if (first == null) first = p;
                    if (p.Id == target) { current = p; passed = true; }
                    else if (passed && next == null) next = p;
                }
                pick = current ?? first;
                if (pick != null && current != null && Input.GetKeyDown(KeyCode.Mouse0)) pick = next ?? first;
                // A teammate next to the body is in view already.
                if (pick != null && current == null && Vector3.Distance(pick.Position, player.transform.position) < Near) pick = null;
            }
            int id = pick != null ? pick.Id : 0;
            if (id != target)
            {
                target = id;
                Watching = pick != null && pick.Latest != null ? pick.Latest.Name : "";
                if (id != 0) Switches++;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Spectate: " + (id != 0 ? "following " + Watching + " (" + id + ")" : "own camera again"));
            }
            if (pick == null) return;
            if (proxy == null)
            {
                GameObject g = new GameObject("CoopSpectateProxy");
                Object.DontDestroyOnLoad(g);
                proxy = g.transform;
            }
            proxy.position = pick.Position;
        }

        static bool Eligible(Puppet p, PlayerScript player)
        {
            return p.Latest != null && !p.Latest.Dead && p.Visible && Plugin.SameWorld(p.Id) && Vector3.Distance(p.Position, player.transform.position) <= Range;
        }

        public static void Stop()
        {
            if (target == 0) return;
            target = 0;
            Watching = "";
            Plugin.Log.LogInfo(Plugin.Stamp() + " Spectate: own camera again");
        }

        static Transform saved;

        static void Swap(CameraMovement cam)
        {
            saved = null;
            if (!Active || cam == null || cam.PlayerTr == null || cam.onArrow) return;
            saved = cam.PlayerTr;
            cam.PlayerTr = proxy;
        }

        static void Restore(CameraMovement cam)
        {
            if (saved != null && cam != null) cam.PlayerTr = saved;
            saved = null;
        }

        [HarmonyPatch(typeof(CameraMovement), "NewMovement")]
        static class FixedPatch
        {
            static void Prefix(CameraMovement __instance) { Swap(__instance); }
            static void Finalizer(CameraMovement __instance) { Restore(__instance); }
        }

        [HarmonyPatch(typeof(CameraMovement), "NonFixedMovement")]
        static class FramePatch
        {
            static void Prefix(CameraMovement __instance) { Swap(__instance); }
            static void Finalizer(CameraMovement __instance) { Restore(__instance); }
        }
    }
}
