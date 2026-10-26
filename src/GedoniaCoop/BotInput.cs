using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Test-only virtual stick. While Active, vp_Input.GetAxisRaw returns these
    // values for the movement axes, so the game moves the player exactly as
    // for a keyboard (velocity, "Speed", footsteps, walk animations).
    public static class BotInput
    {
        public static bool Active;
        public static float Horizontal, Vertical;

        // Camera-relative stick for walking along a world-space direction.
        public static void Steer(Vector3 worldDir)
        {
            Camera cam = Camera.main;
            float yaw = cam != null ? cam.transform.eulerAngles.y : 0f;
            Vector3 local = Quaternion.Euler(0f, -yaw, 0f) * worldDir;
            local.y = 0f;
            local = Vector3.ClampMagnitude(local, 1f);
            Horizontal = local.x;
            Vertical = local.z;
        }

        public static void Stop()
        {
            Horizontal = 0f;
            Vertical = 0f;
        }

        // Bow: hold "Secondary" (aim) and press "Attack" once (shoot). A press
        // stays pending until the game asks for it, whatever the update order.
        public static bool HoldSecondary;
        static bool attackPending, secondaryDownPending;

        public static void Aim(bool on)
        {
            if (on && !HoldSecondary) secondaryDownPending = true;
            HoldSecondary = on;
        }

        public static void PressAttack() { attackPending = true; }
        // Test: the next GetButtonDown("Interact") answers true, like pressing F.
        public static bool InteractPending;

        [HarmonyPatch(typeof(vp_Input), "GetButton")]
        static class ButtonPatch
        {
            static bool Prefix(string button, ref bool __result)
            {
                if (!Active || button != "Secondary" || !HoldSecondary) return true;
                __result = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(vp_Input), "GetButtonDown")]
        static class ButtonDownPatch
        {
            static bool Prefix(string button, ref bool __result)
            {
                if (button == "Interact" && InteractPending) { InteractPending = false; __result = true; return false; }
                if (!Active) return true;
                if (button == "Attack" && attackPending) { attackPending = false; __result = true; return false; }
                if (button == "Secondary" && secondaryDownPending) { secondaryDownPending = false; __result = true; return false; }
                return true;
            }
        }

        [HarmonyPatch(typeof(vp_Input), "GetAxisRaw")]
        static class AxisPatch
        {
            static bool Prefix(string axis, ref float __result)
            {
                if (!Active) return true;
                if (axis == "Horizontal") { __result = Horizontal; return false; }
                if (axis == "Vertical") { __result = Vertical; return false; }
                return true;
            }
        }
    }
}
