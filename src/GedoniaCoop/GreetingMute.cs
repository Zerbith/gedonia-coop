using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // The game's short spoken lines when an NPC is addressed: DialogueNPCModule.Talk,
    // RandomTalkNPCModule.RandomSay and the merchant answer in InterfaceManager.OnDialogueButton
    // play one of the NPC's "greetings" clips on the camera's AudioSource. They are English
    // recordings with no German version, so they stay silent while the mod shows German,
    // whatever the game's "voices" option says. Sounds without words (animals, creatures, laughs)
    // are no language: they keep following that option, as in the original game.
    // Same for the bandits' idle lines in FighterNPCModule.Idle, the only other thing the option
    // silences outside the cutscenes. With the game's own language nothing here changes anything.
    public static class GreetingMute
    {
        // Test step audiolog:on writes every one-shot to the log (the proof that nothing else plays it).
        public static bool AudioLog;

        // Decided by clip name, from the speakers export (BepInEx/coop-speakers-en.tsv).
        static readonly string[] Wordless = { "ANIMAL_", "Bark", "Creature_", "Ghost Laughing", "Human_", "Monster_", "Whining" };

        internal static bool IsSpeech(string clip)
        {
            foreach (string w in Wordless) if (clip.StartsWith(w, StringComparison.Ordinal)) return false;
            return true;
        }

        // Replaces source.PlayOneShot(clip) at the greeting call sites.
        public static void Play(AudioSource source, AudioClip clip)
        {
            if (Lang.German && clip != null && IsSpeech(clip.name))
            {
                Plugin.Log.LogInfo("[voice] English greeting silent: " + clip.name);
                return;
            }
            source.PlayOneShot(clip);
        }

        // Put behind every read of FighterNPCModule.voiceInteger (0 = the "voices" option is off).
        public static int BanditVoice(int option) { return Lang.German ? 0 : option; }

        [HarmonyPatch]
        static class CallSites
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(DialogueNPCModule), "Talk");
                yield return AccessTools.Method(typeof(RandomTalkNPCModule), "RandomSay");
                yield return AccessTools.Method(typeof(InterfaceManager), "OnDialogueButton");
            }

            // The first PlayOneShot after a read of a "greetings" field plays that greeting.
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                MethodInfo oneShot = AccessTools.Method(typeof(AudioSource), "PlayOneShot", new Type[] { typeof(AudioClip) });
                bool armed = false;
                int patched = 0;
                foreach (CodeInstruction i in instructions)
                {
                    FieldInfo f = i.operand as FieldInfo;
                    if (i.opcode == OpCodes.Ldfld && f != null && f.Name == "greetings") armed = true;
                    if (armed && i.Calls(oneShot))
                    {
                        i.opcode = OpCodes.Call;
                        i.operand = AccessTools.Method(typeof(GreetingMute), "Play");
                        armed = false;
                        patched++;
                    }
                    yield return i;
                }
                if (patched != 1) Plugin.Log.LogWarning("[voice] greeting patch in " + original.Name + ": " + patched + " calls");
            }
        }

        [HarmonyPatch(typeof(FighterNPCModule), "Idle")]
        static class BanditPatch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction i in instructions)
                {
                    yield return i;
                    FieldInfo f = i.operand as FieldInfo;
                    if (i.opcode == OpCodes.Ldfld && f != null && f.Name == "voiceInteger")
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GreetingMute), "BanditVoice"));
                }
            }
        }

        // Test step "bark": what clicking a talking citizen does, on every loaded one with greetings (max 6).
        public static void TestBark()
        {
            int n = 0;
            foreach (RandomTalkNPCModule r in UnityEngine.Object.FindObjectsOfType<RandomTalkNPCModule>())
            {
                if (r.greetings == null || r.greetings.Length == 0 || r.phrases == null || r.phrases.Length == 0) continue;
                NPC npc = r.GetComponent<NPC>();
                if (npc == null || npc.anim == null) continue;
                Plugin.Log.LogInfo("Test: bark '" + r.name + "' greetings=" + r.greetings.Length + "/" + r.phrases.Length);
                r.RandomSay();
                if (++n >= 6) break;
            }
            Plugin.Log.LogInfo("Test: bark on " + n + " NPCs");
        }

        static DialogueNPCModule testMerchant;

        // Test step "merchanttalk": talks to a loaded merchant with greetings and a merchant answer.
        public static void TestMerchantTalk(PlayerScript player)
        {
            foreach (MerchantNPCModule m in UnityEngine.Object.FindObjectsOfType<MerchantNPCModule>())
            {
                DialogueNPCModule d = m.GetComponent<DialogueNPCModule>();
                if (d == null || m.greetings == null || m.greetings.Length == 0 || MerchantAnswer(d) < 0) continue;
                testMerchant = d;
                DialogueSync.TestTalk(player, DialogueSync.NpcId(d.gameObject));
                return;
            }
            Plugin.Log.LogWarning("Test: no merchant with greetings loaded");
        }

        // Test step "merchantanswer": gives the merchant answer of that talk, then closes the shop.
        public static void TestMerchantAnswer()
        {
            if (testMerchant == null || InterfaceManager.instance.currentTalkTarget != testMerchant.gameObject) { Plugin.Log.LogWarning("Test: not talking to a merchant"); return; }
            GameObject button = new GameObject(MerchantAnswer(testMerchant).ToString());
            Plugin.Log.LogInfo("Test: merchant answer " + button.name + " on '" + testMerchant.name + "'");
            InterfaceManager.instance.OnDialogueButton(button);
            UnityEngine.Object.Destroy(button);
            InterfaceManager.instance.panels[4].SetActive(false);
            InterfaceManager.instance.panels[0].SetActive(false);
        }

        static int MerchantAnswer(DialogueNPCModule d)
        {
            for (int i = 0; i < d.allDialogue.Length; i++)
                if (d.allDialogue[i].answerType == answerType.Merchant && d.allDialogue[i].requirements.Length == 0) return i;
            return -1;
        }
    }
}
