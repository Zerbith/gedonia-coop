using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Reads what other players need to know about the local player.
    public static class LocalPlayer
    {
        public static Appearance Current;
        static int version;

        // Same order as RefreshPlayerModel's parameters.
        [HarmonyPatch(typeof(PlayerModelCustomization), "RefreshPlayerModel")]
        static class CaptureAppearance
        {
            static void Postfix(PlayerModelCustomization __instance, object[] __args)
            {
                if (PlayerScript.instance == null || __instance != PlayerScript.instance.modelMain) return;
                if (Puppet.Applying) return;
                try
                {
                    Appearance a = new Appearance();
                    for (int i = 0; i < 8; i++) a.Models[i] = (int)__args[i];
                    a.Hair = (int)__args[8]; a.Beard = (int)__args[9]; a.HairColor = (int)__args[10]; a.Head = (int)__args[11];
                    a.BodyType = Convert.ToInt32(__args[12]); a.SkinColor = (int)__args[13]; a.IsMale = (bool)__args[14];
                    a.FaceDetail = (int)__args[15];
                    List<string> extra = __args[16] as List<string>;
                    if (extra != null) a.AdditionalData = new List<string>(extra);
                    a.EyeColor = (int)__args[17];
                    a.WeaponName = (string)__args[18] ?? ""; a.WeaponNameSecondary = (string)__args[19] ?? "";
                    // Read from PlayerPrefs into private fields; the options menu
                    // refreshes them and then the model, so this sees changes.
                    Traverse t = Traverse.Create(__instance);
                    a.ShowHelmet = t.Field("showHelmet").GetValue<bool>();
                    a.ShowCloak = t.Field("showCloak").GetValue<bool>();
                    a.Version = ++version;
                    Current = a;
                    Plugin.Log.LogInfo(Plugin.Stamp() + " Captured local appearance v" + a.Version + " models=" + string.Join(",", Array.ConvertAll(a.Models, m => m.ToString())) +
                        " male=" + a.IsMale + " hair=" + a.Hair + " head=" + a.Head + " hairColor=" + a.HairColor +
                        " skin=" + a.SkinColor + " weapon='" + a.WeaponName + "' showHelmet=" + a.ShowHelmet + " showCloak=" + a.ShowCloak);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Appearance capture failed: " + e);
                }
            }
        }

        // Animator snapshot: destination state while transitioning, so the
        // remote side starts the same move without waiting for it to finish.
        public static void ReadAnimator(Animator anim, PlayerState s, List<AnimatorControllerParameter> syncParams)
        {
            if (anim == null || anim.runtimeAnimatorController == null) return;
            int layers = anim.layerCount;
            if (s.LayerStates.Length != layers) { s.LayerStates = new int[layers]; s.LayerTimes = new float[layers]; }
            for (int l = 0; l < layers; l++)
            {
                AnimatorStateInfo info = anim.IsInTransition(l) ? anim.GetNextAnimatorStateInfo(l) : anim.GetCurrentAnimatorStateInfo(l);
                s.LayerStates[l] = info.fullPathHash;
                s.LayerTimes[l] = info.normalizedTime;
            }
            if (s.Params.Length != syncParams.Count) s.Params = new float[syncParams.Count];
            for (int i = 0; i < syncParams.Count; i++)
            {
                AnimatorControllerParameter p = syncParams[i];
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: s.Params[i] = anim.GetFloat(p.nameHash); break;
                    case AnimatorControllerParameterType.Int: s.Params[i] = anim.GetInteger(p.nameHash); break;
                    case AnimatorControllerParameterType.Bool: s.Params[i] = anim.GetBool(p.nameHash) ? 1f : 0f; break;
                }
            }
        }

        static readonly List<AnimatorClipInfo> clipBuffer = new List<AnimatorClipInfo>();

        // Clip with the highest blend weight on a layer ("" if none).
        public static string DominantClip(Animator anim, int layer)
        {
            if (anim == null || layer >= anim.layerCount) return "";
            clipBuffer.Clear();
            anim.GetCurrentAnimatorClipInfo(layer, clipBuffer);
            string best = "";
            float bestWeight = -1f;
            foreach (AnimatorClipInfo c in clipBuffer)
                if (c.clip != null && c.weight > bestWeight) { bestWeight = c.weight; best = c.clip.name; }
            return best;
        }

        public static List<AnimatorControllerParameter> SyncableParams(Animator anim)
        {
            List<AnimatorControllerParameter> list = new List<AnimatorControllerParameter>();
            foreach (AnimatorControllerParameter p in anim.parameters)
                if (p.type != AnimatorControllerParameterType.Trigger) list.Add(p);
            return list;
        }

        // Fingerprint of what a model looks like: every visible mesh renderer
        // (path relative to the model root) and its material names. Equal on
        // sender and puppet means the puppet is dressed like the real player.
        public static int LookSignature(Transform root, List<string> entries)
        {
            entries.Clear();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer) || !r.enabled) continue;
                // The arrow nocked while aiming comes and goes with each shot
                // (synced as a weapon flag); it is not part of the look.
                if (r.transform.name == "Arrow01" && r.transform.parent != null && r.transform.parent.name == "Hand_R") continue;
                StringBuilder e = new StringBuilder(RelPath(r.transform, root));
                foreach (Material m in r.sharedMaterials)
                    e.Append('|').Append(m == null ? "null" : m.name.Replace(" (Instance)", ""));
                entries.Add(e.ToString());
            }
            entries.Sort(StringComparer.Ordinal);
            uint h = 2166136261; // FNV-1a, identical in every process
            foreach (string e in entries)
            {
                foreach (char c in e) { h ^= c; h *= 16777619; }
                h ^= '\n'; h *= 16777619;
            }
            return unchecked((int)h);
        }

        static string RelPath(Transform t, Transform root)
        {
            if (t == root) return ".";
            string p = t.name;
            while (t.parent != null && t.parent != root) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        public static void Dump(PlayerScript player)
        {
            StringBuilder sb = new StringBuilder("Player hierarchy (depth 3):\n");
            DumpNode(player.transform, 0, sb);
            Transform model = player.modelMain != null ? player.modelMain.transform : null;
            sb.Append("modelMain = " + (model == null ? "null" : Path(model, player.transform)) + "\n");
            sb.Append("anim on = " + (player.anim == null ? "null" : Path(player.anim.transform, player.transform)) +
                " layers=" + (player.anim != null ? player.anim.layerCount : 0) +
                " params=" + (player.anim != null ? player.anim.parameterCount : 0) + "\n");
            Plugin.Log.LogInfo(sb.ToString());
        }

        static void DumpNode(Transform t, int depth, StringBuilder sb)
        {
            List<string> comps = new List<string>();
            foreach (Component c in t.GetComponents<Component>())
                if (c != null && !(c is Transform)) comps.Add(c.GetType().Name);
            sb.Append(new string(' ', depth * 2) + t.name + (t.gameObject.activeSelf ? "" : " (off)") + " [" + string.Join(", ", comps.ToArray()) + "]\n");
            if (depth >= 3) return;
            foreach (Transform child in t) DumpNode(child, depth + 1, sb);
        }

        public static string Path(Transform t, Transform root)
        {
            string p = t.name;
            while (t != root && t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
