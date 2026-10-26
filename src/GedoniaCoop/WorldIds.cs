using System.Collections.Generic;
using UnityEngine;

namespace GedoniaCoop
{
    // Ids for objects placed in a scene (enemies, chests), equal on every
    // machine: a hash of scene name + hierarchy path with sibling indices.
    // Take them when the scene loads, before the game destroys anything in it,
    // so the sibling indices are those of the scene file.
    public static class WorldIds
    {
        public static int Of(Transform t, string scene)
        {
            List<string> parts = new List<string>();
            for (Transform x = t; x != null; x = x.parent) parts.Add(x.name + "#" + x.GetSiblingIndex());
            parts.Add(scene);
            parts.Reverse();
            uint h = 2166136261; // FNV-1a
            foreach (string p in parts)
            {
                foreach (char c in p) { h ^= c; h *= 16777619; }
                h ^= '/'; h *= 16777619;
            }
            int id = unchecked((int)h);
            return id == 0 ? 1 : id;
        }

        // For objects built at runtime under a root that every machine builds
        // the same way (a generated dungeon): the path below that root only.
        public static int Under(Transform t, Transform root, string prefix)
        {
            List<string> parts = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent) parts.Add(x.name + "#" + x.GetSiblingIndex());
            parts.Add(prefix);
            parts.Reverse();
            return Hash(string.Join("/", parts.ToArray()));
        }

        public static int Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            int id = unchecked((int)h);
            return id == 0 ? 1 : id;
        }
    }
}
