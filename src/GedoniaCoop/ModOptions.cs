using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Two sliders of the mod in the game's options during play (cloned from its own; the main menu's graphics
    // page is full and already has the game's "landscape" option, the values are in the config file too):
    //  - Audio: volume of the German voice-over, 0..300 %, works through VoiceGain.
    //  - Graphics: view distance, 100..250 % on top of the game's "landscape" option.
    // The view distance only stretches what is scenery: camera range, fog, LOD distances and the streamers
    // of the far objects (Giant, Big) and full terrain. Medium, Small and City hold the NPCs and stay as they
    // are, so co-op sees the same enemies as before. The game writes camera, fog and streamer values again on
    // world changes, so each is compared with what was last applied and a new value counts as the new base.
    public class ModOptions : MonoBehaviour
    {
        public const float MaxVolume = 3f, MaxView = 2.5f;
        public static float ViewFactor = 1f;
        public static BepInEx.Configuration.ConfigEntry<float> VolumeEntry, ViewEntry;
        static ModOptions instance;

        string scene = "";
        bool built;
        float nextBuildTry, nextApply, saveAt, recheckAt;
        readonly List<KeyValuePair<UILabel, Func<string>>> labels = new List<KeyValuePair<UILabel, Func<string>>>();
        UISlider voiceSlider, viewSlider;
        bool settingSliders;

        public static void Init()
        {
            if (instance != null) return;
            GameObject go = new GameObject("GedoniaCoopOptions");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ModOptions>();
        }

        void Update()
        {
            string s = SceneManager.GetActiveScene().name;
            if (s != scene) { scene = s; built = false; nextBuildTry = Time.realtimeSinceStartup + 1f; labels.Clear(); voiceSlider = viewSlider = null; }
            float now = Time.realtimeSinceStartup;
            if (!built && now >= nextBuildTry) { nextBuildTry = now + 1f; try { Build(); } catch (Exception e) { built = true; Plugin.Log.LogWarning("[options] sliders: " + e); } }
            if (now >= nextApply) { nextApply = now + 0.5f; try { ApplyView(); } catch (Exception e) { Plugin.Log.LogWarning("[options] view: " + e.Message); } }
            if (recheckAt > 0f && now >= recheckAt) { recheckAt = 0f; Recheck(); }
            if (saveAt > 0f && now >= saveAt)
            {
                saveAt = 0f;
                if (VolumeEntry != null) VolumeEntry.Value = VoiceOver.Volume;
                if (ViewEntry != null) ViewEntry.Value = ViewFactor;
            }
        }

        // ---------- sliders ----------

        static UISlider FindSlider(string method)
        {
            foreach (UISlider u in Resources.FindObjectsOfTypeAll<UISlider>())
            {
                if (!u.gameObject.scene.IsValid()) continue;
                foreach (EventDelegate d in u.onChange) if (d != null && d.methodName == method) return u;
            }
            return null;
        }

        void Build()
        {
            InterfaceManager im = InterfaceManager.instance;
            UISlider sound = FindSlider("OnSoundVolume");
            if (sound == null || im == null || sound.transform.parent.parent != im.soundSlider.transform.parent.parent) return;
            built = true;
            UISlider bright = im.brightnessSettings;
            if (VoiceOver.Available)
                voiceSlider = Row(sound, "CoopVoiceVolume", AudioRowOffset, "OnVoiceSlider", "OnVoicePlus", "OnVoiceMinus", "OnSoundPlus", "OnSoundMinus", VoiceText);
            if (bright != null)
                viewSlider = Row(bright, "CoopViewDistance", ViewRowOffset, "OnViewSlider", "OnViewPlus", "OnViewMinus", null, null, ViewText);
            settingSliders = true;
            if (voiceSlider != null) voiceSlider.value = VoiceOver.Volume / MaxVolume;
            if (viewSlider != null) viewSlider.value = (ViewFactor - 1f) / (MaxView - 1f);
            settingSliders = false;
            RefreshLabels();
            Plugin.Log.LogInfo("[options] sliders added in " + scene + ": voice " + (voiceSlider != null) + ", view " + (viewSlider != null));
        }

        static readonly Vector3 AudioRowOffset = new Vector3(0f, 92f, 0f), ViewRowOffset = new Vector3(0f, 368f, 0f);

        // A copy of the slider, its plus/minus buttons if it has any and the label above it, moved by offset.
        UISlider Row(UISlider src, string name, Vector3 offset, string change, string plus, string minus, string srcPlus, string srcMinus, Func<string> text)
        {
            Transform parent = src.transform.parent;
            UILabel srcLabel = null;
            float best = float.MaxValue;
            foreach (Transform c in parent)
            {
                UILabel l = c.GetComponent<UILabel>();
                float above = c.localPosition.y - src.transform.localPosition.y;
                if (l != null && above > 0f && above < best) { best = above; srcLabel = l; }
                UIButton b = c.GetComponent<UIButton>();
                if (b == null) continue;
                foreach (EventDelegate d in b.onClick)
                {
                    if (d == null) continue;
                    if (d.methodName == srcPlus) CloneButton(b, name + "Plus", offset, plus);
                    else if (d.methodName == srcMinus) CloneButton(b, name + "Minus", offset, minus);
                    else continue;
                    break;
                }
            }
            GameObject g = Instantiate(src.gameObject, parent);
            g.name = name;
            g.transform.localPosition = src.transform.localPosition + offset;
            g.transform.localScale = src.transform.localScale;
            UISlider s = g.GetComponent<UISlider>();
            s.onChange.Clear();
            s.onChange.Add(new EventDelegate(this, change));
            foreach (UIKeyBinding k in g.GetComponentsInChildren<UIKeyBinding>(true)) Destroy(k);
            g.SetActive(true);
            if (srcLabel != null)
            {
                GameObject lg = Instantiate(srcLabel.gameObject, parent);
                lg.name = name + "Label";
                lg.transform.localPosition = srcLabel.transform.localPosition + offset;
                lg.transform.localScale = srcLabel.transform.localScale;
                foreach (UILocalize loc in lg.GetComponentsInChildren<UILocalize>(true)) Destroy(loc);
                labels.Add(new KeyValuePair<UILabel, Func<string>>(lg.GetComponent<UILabel>(), text));
            }
            return s;
        }

        void CloneButton(UIButton src, string name, Vector3 offset, string method)
        {
            GameObject g = Instantiate(src.gameObject, src.transform.parent);
            g.name = name;
            g.transform.localPosition = src.transform.localPosition + offset;
            g.transform.localScale = src.transform.localScale;
            foreach (UIKeyBinding k in g.GetComponentsInChildren<UIKeyBinding>(true)) Destroy(k);
            UIButton b = g.GetComponent<UIButton>();
            b.onClick.Clear();
            b.onClick.Add(new EventDelegate(this, method));
            g.SetActive(true);
        }

        static string VoiceText() { return Lang.T("Voice-over volume: ", "Sprachausgabe: ") + Mathf.RoundToInt(VoiceOver.Volume * 100f) + " %"; }
        static string ViewText() { return Lang.T("View distance: ", "Weitsicht: ") + Mathf.RoundToInt(ViewFactor * 100f) + " %"; }

        void RefreshLabels() { foreach (KeyValuePair<UILabel, Func<string>> l in labels) if (l.Key != null) l.Key.text = l.Value(); }

        // Clicks reach these through EventDelegates, so they stay public.
        public void OnVoiceSlider() { if (!settingSliders && voiceSlider != null) SetVoice(voiceSlider.value * MaxVolume); }
        public void OnViewSlider() { if (!settingSliders && viewSlider != null) SetView(1f + viewSlider.value * (MaxView - 1f)); }
        public void OnVoicePlus() { if (voiceSlider != null) voiceSlider.value = Mathf.Min(1f, voiceSlider.value + 0.05f); }
        public void OnVoiceMinus() { if (voiceSlider != null) voiceSlider.value = Mathf.Max(0f, voiceSlider.value - 0.05f); }
        public void OnViewPlus() { if (viewSlider != null) viewSlider.value = Mathf.Min(1f, viewSlider.value + 0.1f); }
        public void OnViewMinus() { if (viewSlider != null) viewSlider.value = Mathf.Max(0f, viewSlider.value - 0.1f); }

        void SetVoice(float v)
        {
            VoiceOver.Volume = Mathf.Clamp(Mathf.Round(v * 100f) / 100f, 0f, MaxVolume);
            saveAt = Time.realtimeSinceStartup + 1f;
            RefreshLabels();
        }

        void SetView(float k)
        {
            float old = ViewFactor;
            ViewFactor = Mathf.Clamp(Mathf.Round(k * 100f) / 100f, 1f, MaxView);
            foreach (TerrainDeactivation t in FindObjectsOfType<TerrainDeactivation>()) ActiveRange.SetValue(t, (float)ActiveRange.GetValue(t) * ViewFactor / old);
            nextApply = 0f;
            saveAt = Time.realtimeSinceStartup + 1f;
            RefreshLabels();
            recheckAt = Time.realtimeSinceStartup + 5f;
        }

        // Streamers only look at their ranges when the player enters a new tile: make them look, a little
        // later, since GameManager only writes the ranges now and then and a dragged slider moves for a while.
        static void Recheck()
        {
            GameManager gm = GameManager.instance;
            if (gm == null || gm.streamers == null || gm.streamers.Length != 7) return;
            foreach (int i in new int[] { 0, 1, 3 }) if (gm.streamers[i] != null) TileX.SetValue(gm.streamers[i], int.MinValue);
        }

        static readonly FieldInfo TileX = AccessTools.Field(typeof(Streamer), "xPos");

        // ---------- view distance ----------

        static readonly FieldInfo ActiveRange = AccessTools.Field(typeof(TerrainDeactivation), "activeRange");

        [HarmonyPatch(typeof(TerrainDeactivation), "Start")]
        static class TerrainPatch
        {
            static void Postfix(ref float ___activeRange) { ___activeRange *= ViewFactor; }
        }

        struct Scaled { public float Base, Applied; }
        float farBase = -1f, farApplied = -1f, lodBase = -1f, lodApplied = -1f;
        readonly Dictionary<EnviroWeatherPreset, Scaled[]> fogs = new Dictionary<EnviroWeatherPreset, Scaled[]>();
        readonly Dictionary<Streamer, Vector3[]> ranges = new Dictionary<Streamer, Vector3[]>();

        static float Track(float current, ref float bas, ref float applied, float factor)
        {
            if (bas < 0f || !Mathf.Approximately(current, applied)) bas = current;
            applied = bas * factor;
            return applied;
        }

        void ApplyView()
        {
            Camera cam = Camera.main;
            if (cam == null || PlayerScript.instance == null) return;
            float far = Track(cam.farClipPlane, ref farBase, ref farApplied, ViewFactor);
            if (!Mathf.Approximately(cam.farClipPlane, far)) cam.farClipPlane = far;
            float lod = Track(QualitySettings.lodBias, ref lodBase, ref lodApplied, ViewFactor);
            if (!Mathf.Approximately(QualitySettings.lodBias, lod)) QualitySettings.lodBias = lod;

            GameManager gm = GameManager.instance;
            if (gm == null) return;
            if (gm.standartPresetsWeather != null)
                foreach (EnviroWeatherPreset p in gm.standartPresetsWeather)
                {
                    if (p == null) continue;
                    Scaled[] f;
                    if (!fogs.TryGetValue(p, out f)) { f = new Scaled[] { new Scaled { Base = -1f, Applied = -1f }, new Scaled { Base = -1f, Applied = -1f } }; fogs[p] = f; }
                    p.fogDistance = Track(p.fogDistance, ref f[0].Base, ref f[0].Applied, ViewFactor);
                    p.fogStartDistance = Track(p.fogStartDistance, ref f[1].Base, ref f[1].Applied, ViewFactor);
                }
            if (gm.streamers == null || gm.streamers.Length != 7) return;
            StretchStreamer(gm.streamers[0]);
        }

        // GameManager writes the Giant and Big ranges every frame. During the loading screen (panels[10]) it
        // uses wider ones and waits for them: those stay, so a stretched range doesn't make loading longer.
        [HarmonyPatch(typeof(GameManager), "ControlStreamingRange")]
        static class RangePatch
        {
            static void Postfix(GameManager __instance)
            {
                if (ViewFactor <= 1f || __instance.streamers.Length != 7) return;
                bool loading = InterfaceManager.instance.panels[10].activeSelf;
                bool wasLoading = loadingScreen;
                loadingScreen = loading;
                if (loading) return;
                Stretch(__instance.streamers[1]);
                Stretch(__instance.streamers[3]);
                if (wasLoading) Recheck();
            }

            static bool loadingScreen;

            static void Stretch(Streamer s)
            {
                float load = Mathf.Round(s.loadingRange.x * ViewFactor), add = load - s.loadingRange.x;
                s.loadingRange = new Vector3(load, s.loadingRange.y, load);
                s.deloadingRange = new Vector3(s.deloadingRange.x + add, s.deloadingRange.y, s.deloadingRange.x + add);
            }
        }

        // The full terrain's streamer, which the game sets once: [0] base load, [1] base unload, [2] applied
        // load, [3] applied unload. Whole tiles of 250 m, so it steps slowly.
        void StretchStreamer(Streamer s)
        {
            if (s == null) return;
            Vector3[] r;
            if (!ranges.TryGetValue(s, out r) || s.loadingRange != r[2] || s.deloadingRange != r[3])
            {
                r = new Vector3[] { s.loadingRange, s.deloadingRange, s.loadingRange, s.deloadingRange };
                ranges[s] = r;
            }
            float load = Mathf.Round(r[0].x * ViewFactor), add = load - r[0].x;
            Vector3 l = new Vector3(load, r[0].y, load), u = new Vector3(r[1].x + add, r[1].y, r[1].x + add);
            if (s.loadingRange == l && s.deloadingRange == u) return;
            s.loadingRange = l;
            s.deloadingRange = u;
            r[2] = l;
            r[3] = u;
        }

        // ---------- test steps ----------

        // "optionslide:voice|view|<0..1>": moves the real slider, so its delegates run.
        public static void TestSlide(string which, float value)
        {
            if (instance == null) return;
            UISlider s = which == "voice" ? instance.voiceSlider : instance.viewSlider;
            if (s == null) { Plugin.Log.LogWarning("Test: no slider " + which); return; }
            instance.StartCoroutine(instance.Slide(s, which, value));
        }

        // NGUI only fires onChange on an active slider that has had its Start: open what holds it for two frames.
        System.Collections.IEnumerator Slide(UISlider s, string which, float value)
        {
            List<GameObject> opened = new List<GameObject>();
            for (Transform t = s.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) opened.Add(t.gameObject);
            for (int i = opened.Count - 1; i >= 0; i--) opened[i].SetActive(true);
            yield return null;
            yield return null;
            s.value = value;
            foreach (GameObject g in opened) g.SetActive(false);
            Plugin.Log.LogInfo("[options] slider " + which + " -> " + value.ToString("F2") + " voice " + VoiceOver.Volume.ToString("F2") + " view " + ViewFactor.ToString("F2") + " text '" + (which == "voice" ? VoiceText() : ViewText()) + "'");
        }

        // "optionsshow:<i>": opens the options on sub panel i, for a screenshot.
        public static void TestShow(int i)
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null) return;
            im.pauseMenuSP.SetActive(false);
            im.optionsMenuSp.SetActive(true);
            for (int k = 0; k < im.optionSubPanels.Length; k++) im.optionSubPanels[k].SetActive(k == i);
            Plugin.Log.LogInfo("[options] showing sub panel " + i + " of " + im.optionSubPanels.Length + " '" + im.optionSubPanels[i].name + "' brightness in '" + im.brightnessSettings.transform.parent.name + "'");
        }

        // "viewstate": what the view distance does right now.
        public static void TestState()
        {
            StringBuilder sb = new StringBuilder("[options] view " + ViewFactor.ToString("F2") + " far " + Camera.main.farClipPlane + " lodBias " + QualitySettings.lodBias.ToString("F2") + " fog " + RenderSettings.fogStartDistance.ToString("F0") + "-" + RenderSettings.fogEndDistance.ToString("F0"));
            GameObject streamers = GameObject.Find("streamers");
            if (streamers != null)
                foreach (Transform t in streamers.transform)
                {
                    Streamer s = t.GetComponent<Streamer>();
                    if (s != null) sb.Append(" ").Append(s.name.Replace("_Streamer_", "")).Append(" ").Append(s.loadingRange.x).Append("/").Append(s.deloadingRange.x).Append(":").Append(s.tilesLoaded);
                }
            sb.Append(" scenes ").Append(SceneManager.sceneCount).Append(" npcs ").Append(FindObjectsOfType<NPC>().Length);
            sb.Append(" allocatedMB ").Append(UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024));
            Plugin.Log.LogInfo(sb.ToString());
        }

        public static void TestDump()
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im != null) { DumpAround(im.brightnessSettings, "brightness"); foreach (GameObject g in im.optionSubPanels) Plugin.Log.LogInfo("[options] subpanel '" + g.name + "'"); }
        }

        static void DumpAround(UISlider slider, string what)
        {
            if (slider == null) { Plugin.Log.LogInfo("[options] " + what + ": none"); return; }
            Transform t = slider.transform;
            StringBuilder path = new StringBuilder();
            for (Transform x = t; x != null; x = x.parent) path.Insert(0, "/" + x.name);
            Plugin.Log.LogInfo("[options] " + what + " " + path + " pos=" + t.localPosition + " value=" + slider.value);
            foreach (EventDelegate d in slider.onChange) Plugin.Log.LogInfo("[options]   onChange " + d.methodName);
            foreach (Transform c in t.parent)
            {
                UILabel l = c.GetComponentInChildren<UILabel>(true);
                UIButton b = c.GetComponent<UIButton>();
                Plugin.Log.LogInfo("[options]   sibling '" + c.name + "' pos=" + c.localPosition + " slider=" + (c.GetComponent<UISlider>() != null) + " click=" + (b != null && b.onClick.Count > 0 && b.onClick[0] != null ? b.onClick[0].methodName : "-") + " text='" + (l != null ? l.text : "") + "'");
            }
        }
    }

    // Gain on the samples of a source's output, so the voice-over can go above the source's maximum of 1.
    // The takes are quiet (peaks of 0.2-0.5), so 200-300 % mostly stays below the limiter at 0.8.
    public class VoiceGain : MonoBehaviour
    {
        // Test: loudest sample before and after the gain since the last read.
        public static float PeakIn, PeakOut;
        public static int Blocks;

        void OnAudioFilterRead(float[] data, int channels)
        {
            float g = VoiceOver.Volume, inp = 0f, outp = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float a = data[i] < 0f ? -data[i] : data[i];
                if (a > inp) inp = a;
                float v = a * g;
                if (v > 0.8f) v = 0.8f + 0.2f * (1f - (float)Math.Exp(-(v - 0.8f) / 0.2f));
                if (v > outp) outp = v;
                data[i] = data[i] < 0f ? -v : v;
            }
            if (inp > PeakIn) PeakIn = inp;
            if (outp > PeakOut) PeakOut = outp;
            Blocks++;
        }
    }
}
