using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // Controllers can carry state scripts (StateMachineBehaviour: MountBehavior moves
    // "its rider", RecoverBehavior drives "its animal"). On a copy that only looks
    // their owner's scripts are gone, so they threw every frame, and those that reach for
    // PlayerScript.instance would touch the local player. On these Animators they don't run.
    public static class QuietAnimators
    {
        static readonly HashSet<Animator> quiet = new HashSet<Animator>();

        public static void Add(Animator a)
        {
            if (quiet.Count > 500) quiet.RemoveWhere(x => x == null);
            if (a != null) quiet.Add(a);
        }

        [HarmonyPatch]
        static class BehaviourPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (Type t in typeof(PlayerScript).Assembly.GetTypes())
                {
                    if (!typeof(StateMachineBehaviour).IsAssignableFrom(t) || t.IsAbstract) continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (!m.Name.StartsWith("OnState") || m.IsAbstract) continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length > 0 && ps[0].ParameterType == typeof(Animator) && ps[0].Name == "animator") yield return m;
                    }
                }
            }

            static bool Prefix(Animator animator)
            {
                return !quiet.Contains(animator);
            }
        }
    }

    // Makes a local Animator follow a remote one: copies the non-trigger
    // parameters and cross-fades each layer to the sender's state. Triggers
    // can't be read back, so one-shot moves (attacks, rolls) arrive as states.
    public class AnimMirror
    {
        public List<AnimatorControllerParameter> Params;
        int[] lastTarget = new int[0];
        float[] mismatchSince = new float[0];

        // Returns false until the Animator is initialised (it reports no
        // parameters before it has been active). layer0InTarget tells whether
        // layer 0 was already in the sender's state this frame.
        public bool Apply(Animator anim, int[] states, float[] times, float[] values, float now,
            out bool layer0InTarget, Action<int, int> onCrossFade, float[] weights = null)
        {
            layer0InTarget = false;
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) return false;
            if (Params == null || Params.Count == 0)
            {
                Params = LocalPlayer.SyncableParams(anim);
                if (Params.Count == 0) return false;
            }
            int n = Math.Min(values.Length, Params.Count);
            for (int i = 0; i < n; i++)
            {
                AnimatorControllerParameter p = Params[i];
                float v = values[i];
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: anim.SetFloat(p.nameHash, v); break;
                    case AnimatorControllerParameterType.Int: anim.SetInteger(p.nameHash, Mathf.RoundToInt(v)); break;
                    case AnimatorControllerParameterType.Bool: anim.SetBool(p.nameHash, v > 0.5f); break;
                }
            }

            int layers = Math.Min(states.Length, anim.layerCount);
            if (lastTarget.Length != layers) { lastTarget = new int[layers]; mismatchSince = new float[layers]; }
            for (int l = 0; l < layers; l++)
            {
                // The riding layer only counts while the rider sets its weight (Rider3rdPerson).
                if (weights != null && l < weights.Length) anim.SetLayerWeight(l, weights[l]);
                int target = states[l];
                AnimatorStateInfo cur = anim.GetCurrentAnimatorStateInfo(l);
                bool inTarget = cur.fullPathHash == target ||
                    (anim.IsInTransition(l) && anim.GetNextAnimatorStateInfo(l).fullPathHash == target);
                if (l == 0) layer0InTarget = inTarget;
                if (inTarget) { mismatchSince[l] = -1f; lastTarget[l] = target; continue; }
                if (mismatchSince[l] < 0f) mismatchSince[l] = now;

                // A new state from the sender plays at once; an old one only if
                // our own controller has wandered off it for a while.
                bool newMove = target != lastTarget[l];
                if ((newMove || now - mismatchSince[l] > 0.5f) && anim.HasState(l, target))
                {
                    float t = times[l];
                    anim.CrossFade(target, 0.1f, l, t - Mathf.Floor(t));
                    lastTarget[l] = target;
                    mismatchSince[l] = -1f;
                    if (onCrossFade != null) onCrossFade(l, target);
                }
            }
            return true;
        }
    }
}
