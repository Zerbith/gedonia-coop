using System;
using System.IO;
using BepInEx;

namespace GedoniaCoop
{
    // The mod's language choice. German is shown on top of the game's English
    // (the game itself stays on "English", so its English/Russian branches
    // take the English text); kept in BepInEx/config, not in the game's prefs.
    public static class Lang
    {
        static bool? german;

        static string ConfigFile { get { return Path.Combine(Paths.ConfigPath, "gedonia-coop-language.txt"); } }

        public static bool German
        {
            get
            {
                if (german == null)
                {
                    try { german = File.Exists(ConfigFile) && File.ReadAllText(ConfigFile).Trim() == "de"; }
                    catch { german = false; }
                }
                return german.Value;
            }
            set
            {
                german = value;
                try { File.WriteAllText(ConfigFile, value ? "de" : "en"); }
                catch (Exception e) { Plugin.Log.LogWarning("Saving the language failed: " + e.Message); }
            }
        }

        // The mod's own texts.
        public static string T(string english, string deutsch) { return German ? deutsch : english; }
    }
}
