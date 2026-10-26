using System;
using System.Collections.Generic;
using UnityEngine;

namespace GedoniaCoop
{
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
            out bool layer0InTarget, Action<int, int> onCrossFade)
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
