using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GedoniaCoop
{
    // Body for a remote player: a stripped clone of the local player's model,
    // re-dressed with the remote player's appearance and driven by their
    // Animator state. Falls back to a capsule if the clone can't be built.
    // Horizontal speed from successive positions, smoothed over ~0.25 s.
    // Used identically by the sender (its player) and the puppet.
    public class TravelMeter
    {
        Vector3 last;
        bool has;
        public float Value;

        public float Sample(Vector3 pos, float dt)
        {
            if (has && dt > 0f)
            {
                Vector3 d = pos - last;
                d.y = 0f;
                Value = Mathf.Lerp(Value, d.magnitude / dt, 1f - Mathf.Exp(-dt / 0.25f));
            }
            last = pos;
            has = true;
            return Value;
        }
    }

    public class Puppet
    {
        // Set while we call RefreshPlayerModel on a puppet, so the local
        // appearance capture ignores it.
        public static bool Applying;

        public readonly int Id;
        public PlayerState Latest;
        public Appearance Look;
        public float LastHeard;

        readonly GameObject root;
        readonly GameObject capsule;
        readonly NPC stand; // makes enemies accept the puppet as a target
        readonly Transform label;
        GameObject model;
        Animator anim;
        PlayerModelCustomization custom;
        readonly AnimMirror mirror = new AnimMirror();
        int appliedLook = -1;
        // Clone's counterparts of PlayerScript.rightHandActive/leftHandActive/back/arrowForBow.
        GameObject[] holders = new GameObject[4];
        bool cloneFailed;

        int samples, matches;

        // Test metrics, per status window (W) and for the whole run (T).
        readonly List<string> sigEntries = new List<string>();
        int puppetSig;
        bool lastLookMatch;
        readonly TravelMeter travelMeter = new TravelMeter();
        float moveSpeed, metricsFrom = -1f;
        bool hasSpeedParam;
        public int SpeedN, MoveN, ClipN, ClipOk, LookN, LookOk;
        public double AnimErrSum, MoveErrSum;
        public float AnimErrMax;
        public int TSpeedN, TMoveN, TClipN, TClipOk, TLookN, TLookOk;
        public double TAnimErrSum, TMoveErrSum;
        public float TAnimErrMax;

        public Puppet(int id, string name)
        {
            Id = id;
            root = new GameObject("CoopPuppet_" + id);
            Object.DontDestroyOnLoad(root);

            // Enemies see and hit the puppet through this trigger: rays and
            // weapons find it, the local player walks through it, and zone
            // triggers ignore it (no "Player" tag).
            CapsuleCollider hitbox = root.AddComponent<CapsuleCollider>();
            hitbox.isTrigger = true;
            hitbox.center = new Vector3(0f, 1f, 0f);
            hitbox.height = 2f;
            hitbox.radius = 0.4f;
            root.AddComponent<PuppetBody>().PlayerId = id;
            // An attacking enemy drops any target that has neither the "Player"
            // tag nor an NPC component. A switched-off NPC (faction Player,
            // invincible, no health bar) keeps the puppet a valid target.
            // Added while the root is inactive so its fields are set before
            // Awake; instantAwake skips the activation queue. PuppetBody comes
            // first, so GetComponent<Damagable>() still finds it.
            root.SetActive(false);
            stand = root.AddComponent<NPC>();
            stand.instantAwake = true;
            stand.createHealthPanel = false;
            stand.invincible = true;
            stand.faction = NPCFaction.Player;
            stand.npcName = name;
            stand.russianName = name;
            stand.health = stand.maxHealth = 100;
            stand.watchPoint = root.transform;
            root.SetActive(true);
            stand.enabled = false;

            capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(capsule.GetComponent<Collider>());
            capsule.transform.SetParent(root.transform, false);
            capsule.transform.localPosition = new Vector3(0f, 1f, 0f);
            capsule.transform.localScale = new Vector3(0.7f, 1f, 0.7f);
            capsule.SetActive(false);

            GameObject tag = new GameObject("Label");
            tag.transform.SetParent(root.transform, false);
            tag.transform.localPosition = new Vector3(0f, 2.3f, 0f);
            TextMesh text = tag.AddComponent<TextMesh>();
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.font = font;
            tag.GetComponent<MeshRenderer>().material = font.material;
            text.text = name;
            text.characterSize = 0.04f;
            text.fontSize = 48;
            text.anchor = TextAnchor.MiddleCenter;
            text.color = Color.white;
            label = tag.transform;
        }

        string shownName;

        // The sender's name can change after joining (Steam name read late).
        public void SetName(string name)
        {
            if (name == null || name == shownName) return;
            shownName = name;
            label.GetComponent<TextMesh>().text = name;
            stand.npcName = name;
            stand.russianName = name;
        }

        public Vector3 Position { get { return root.transform.position; } }
        public GameObject Root { get { return root; } }
        public bool Visible { get { return root.activeSelf; } }
        public bool HasModel { get { return model != null; } }

        public void SetVisible(bool visible)
        {
            if (root.activeSelf != visible) root.SetActive(visible);
        }

        // Components the clone keeps; every other component is removed before
        // the clone is ever activated, so no game script on it runs Awake.
        static bool Keep(Component c)
        {
            return c is Transform || c is Animator || c is Renderer || c is MeshFilter ||
                c is LODGroup || c is ParticleSystem || c is Light || c is PlayerModelCustomization ||
                c is PlayerWeaponCustomLoader; // loads mod weapon models (RefreshPlayerModel calls it)
        }

        void BuildModel()
        {
            PlayerScript player = PlayerScript.instance;
            if (model != null || cloneFailed || player == null || player.modelMain == null) return;
            GameObject holder = new GameObject("CoopCloneHolder");
            holder.SetActive(false);
            try
            {
                Transform src = player.modelMain.transform;
                GameObject clone = Object.Instantiate(src.gameObject, holder.transform);
                clone.name = "Model";

                // Dependent components first (e.g. scripts requiring a collider).
                int removed = 0;
                for (int pass = 0; pass < 3; pass++)
                {
                    foreach (Component c in clone.GetComponentsInChildren<Component>(true))
                    {
                        if (c == null || Keep(c)) continue;
                        try { Object.DestroyImmediate(c); removed++; } catch { }
                    }
                }

                anim = clone.GetComponentInChildren<Animator>(true);
                custom = clone.GetComponent<PlayerModelCustomization>();
                if (anim == null) throw new Exception("clone has no Animator");
                anim.applyRootMotion = false;
                anim.fireEvents = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // Keep the model's offset from the player root.
                clone.transform.SetParent(root.transform, false);
                clone.transform.localPosition = player.transform.InverseTransformPoint(src.position);
                clone.transform.localRotation = Quaternion.Inverse(player.transform.rotation) * src.rotation;
                clone.transform.localScale = src.lossyScale;
                model = clone;
                capsule.SetActive(false);
                GameObject[] src4 = { player.rightHandActive, player.leftHandActive, player.back, player.arrowForBow };
                for (int i = 0; i < 4; i++)
                {
                    if (src4[i] == null) continue;
                    string rel = LocalPlayer.Path(src4[i].transform, player.transform);
                    rel = rel.Substring(rel.IndexOf('/') + 1); // drop "PlayerFinal/"
                    Transform found = clone.transform.Find(rel);
                    holders[i] = found != null ? found.gameObject : null;
                    if (found == null) Plugin.Log.LogWarning("Puppet " + Id + ": weapon holder '" + rel + "' not found in clone");
                }
                if (custom != null)
                {
                    List<string> external = ExternalRefs(custom, clone.transform);
                    if (external.Count > 0)
                    {
                        Plugin.Log.LogWarning("Puppet " + Id + ": customization references objects outside the clone (" +
                            string.Join(", ", external.ToArray()) + "); appearance changes disabled so the local player is never touched.");
                        custom = null;
                    }
                }
                Plugin.Log.LogInfo("Puppet " + Id + " model cloned from " + LocalPlayer.Path(src, player.transform) +
                    " (removed " + removed + " components, customization=" + (custom != null) + ")");
            }
            catch (Exception e)
            {
                cloneFailed = true;
                if (model == null) StyleCapsule();
                Plugin.Log.LogError("Puppet model clone failed, using capsule: " + e);
            }
            finally
            {
                Object.Destroy(holder);
            }
        }

        // Instantiate remaps references inside the cloned subtree only. Any
        // field still pointing elsewhere would let RefreshPlayerModel edit the
        // local player's objects.
        static List<string> ExternalRefs(Component c, Transform cloneRoot)
        {
            List<string> found = new List<string>();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (System.Reflection.FieldInfo f in c.GetType().GetFields(flags))
            {
                object v = f.GetValue(c);
                Array arr = v as Array;
                if (arr != null)
                {
                    foreach (object e in arr) if (IsExternal(e as Object, cloneRoot)) { found.Add(f.Name + "[]"); break; }
                }
                else if (IsExternal(v as Object, cloneRoot))
                {
                    found.Add(f.Name);
                }
            }
            return found;
        }

        static bool IsExternal(Object o, Transform cloneRoot)
        {
            if (o == null) return false;
            Transform t = o is GameObject ? ((GameObject)o).transform : o is Component ? ((Component)o).transform : null;
            return t != null && !t.IsChildOf(cloneRoot);
        }

        void StyleCapsule()
        {
            capsule.SetActive(true);
            if (PlayerScript.instance == null) return;
            foreach (Renderer r in PlayerScript.instance.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer && r.sharedMaterial != null)
                {
                    Material m = new Material(r.sharedMaterial.shader);
                    if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.2f, 0.6f, 1f));
                    if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", Texture2D.whiteTexture);
                    capsule.GetComponent<Renderer>().material = m;
                    return;
                }
            }
        }

        void ApplyLook()
        {
            if (custom == null || Look == null || Look.Version == appliedLook) return;
            appliedLook = Look.Version;
            Applying = true;
            try
            {
                Appearance a = Look;
                List<string> notes = new List<string>();
                int[] models = FitModels(custom, a, notes);
                bool male = a.IsMale;
                int hair = Fit(a.Hair, male ? custom.hairMale : custom.hairFemale, "hair", notes);
                int head = Fit(a.Head, male ? custom.heads : custom.headsFemale, "head", notes);
                int colors = Math.Min(custom.hairColors.Length, Math.Min(custom.eyebrowsColors.Length, custom.beardColors.Length));
                int hairColor = a.HairColor >= 0 && a.HairColor < colors ? a.HairColor : 0;
                if (hairColor != a.HairColor) notes.Add("hairColor " + a.HairColor + "->0");
                // The game reads these from this PC's PlayerPrefs; the puppet
                // shows the sender's choice instead.
                Traverse t = Traverse.Create(custom);
                t.Field("showHelmet").SetValue(a.ShowHelmet);
                t.Field("showCloak").SetValue(a.ShowCloak);
                custom.RefreshPlayerModel(models[0], models[1], models[2], models[3], models[4], models[5], models[6], models[7],
                    hair, a.Beard, hairColor, head, (BodyType)a.BodyType, a.SkinColor, male, a.FaceDetail,
                    new List<string>(a.AdditionalData), a.EyeColor, a.WeaponName, a.WeaponNameSecondary);
                // The game shows the quiver by the LOCAL player's weapon.
                if (custom.quiverModel != null) custom.quiverModel.SetActive(models[6] > 0 && IsBow(a.WeaponName));
                torchSec = models[7];
                torchLightTrinket = a.AdditionalData.Contains("LightTrinket");
                ApplyTorch();
                Plugin.Log.LogInfo(Plugin.Stamp() + " Puppet " + Id + " dressed with appearance v" + a.Version + " models=" +
                    string.Join(",", Array.ConvertAll(models, x => x.ToString())) + " male=" + male + " hair=" + hair + " head=" + head +
                    " hairColor=" + hairColor + " skin=" + a.SkinColor + " weapon='" + a.WeaponName + "' showHelmet=" + a.ShowHelmet +
                    " showCloak=" + a.ShowCloak + (notes.Count > 0 ? " ADJUSTED: " + string.Join(", ", notes.ToArray()) : ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Puppet " + Id + " RefreshPlayerModel failed: " + e);
            }
            finally
            {
                Applying = false;
            }
        }

        // RefreshPlayerModel indexes its arrays with the numbers it gets, so a
        // number this PC doesn't have (another game version, an armor mod)
        // would throw halfway through. Such parts are left off instead.
        static int[] FitModels(PlayerModelCustomization c, Appearance a, List<string> notes)
        {
            bool male = a.IsMale;
            int[] m = (int[])a.Models.Clone();
            m[0] = Fit(m[0], Math.Min(c.helmets.Length, Math.Min(c.helmetsDisableHair.Length,
                Math.Min(c.helmetsDisableHead.Length, c.helmetsDisableBeard.Length))), "helmet", notes);
            m[1] = Fit(m[1], male ? c.chestsMale : c.chestsFemale, "chest", notes);
            m[2] = Fit(m[2], male ? c.pantsMale : c.pantsFemale, "pants", notes);
            m[3] = Fit(m[3], male ? c.bootsMale : c.bootsFemale, "boots", notes);
            m[4] = Fit(m[4], male ? c.glovesMale : c.glovesFemale, "gloves", notes);
            m[5] = Fit(m[5], c.cloaks, "cloak", notes);
            m[6] = FitWeapon(m[6], a.WeaponName, Math.Min(c.weaponsMain.Length, c.weaponsMainHolsered.Length), "weapon", notes);
            m[7] = FitWeapon(m[7], a.WeaponNameSecondary, Math.Min(c.weaponsSecondary.Length, c.weaponsSecHolstered.Length), "secondary", notes);
            return m;
        }

        static int Fit(int v, GameObject[] parts, string what, List<string> notes)
        {
            return Fit(v, parts != null ? parts.Length : 0, what, notes);
        }

        static int Fit(int v, int count, string what, List<string> notes)
        {
            if (v >= 0 && v < count) return v;
            notes.Add(what + " " + v + "->0 (this PC has " + count + ")");
            return 0;
        }

        // Models from 1000 up are mod weapons, found by item name in this
        // PC's mods. Without that mod the hand stays empty.
        static int FitWeapon(int v, string name, int count, string what, List<string> notes)
        {
            if (v < 1000) return Fit(v, count, what, notes);
            if (HasModWeapon(v, name)) return v;
            notes.Add(what + " '" + name + "' (mod model " + v + ") not installed here");
            return 0;
        }

        // RefreshPlayerModel lights a held torch (or a light trinket) unless
        // the LOCAL player sneaks; the puppet's light follows the sender's
        // sneaking instead.
        int torchSec = -1;
        bool torchLightTrinket, torchStealth;
        public int TorchLightsOn;

        void ApplyTorch()
        {
            if (custom == null || custom.torchesLights == null || custom.torchesLights.Length == 0) return;
            torchStealth = Latest != null && Latest.Stealth;
            int[] secModels = { 138, 140, 141, 142, 143, 144 }; // light 0 is also 139 and the trinket
            TorchLightsOn = 0;
            for (int i = 0; i < custom.torchesLights.Length && i < secModels.Length; i++)
            {
                bool on = !torchStealth && (torchSec == secModels[i] || (i == 0 && (torchSec == 139 || torchLightTrinket)));
                if (custom.torchesLights[i] != null) custom.torchesLights[i].SetActive(on);
                if (on) TorchLightsOn++;
            }
        }

        // Test check: whether the puppet's mod weapon model was loaded.
        public string ModWeaponState()
        {
            PlayerWeaponCustomLoader l = custom != null ? custom.customLoader : null;
            if (l == null) return "no loader";
            int r = l.weaponParent != null ? l.weaponParent.GetComponentsInChildren<Renderer>(true).Length : 0;
            return "loaded=" + l.loaded + " renderers=" + r + " torchLights=" + TorchLightsOn;
        }

        static bool IsBow(string weaponName)
        {
            if (ItemsData.instance == null || string.IsNullOrEmpty(weaponName)) return false;
            foreach (WeaponItem w in ItemsData.instance.weaponsDatabase)
                if (w != null && w.name == weaponName) return w.weaponType == WeaponType.Bow;
            return false;
        }

        static bool HasModWeapon(int model, string name)
        {
            GameModIntegrationManager g = GameModIntegrationManager.instance;
            if (g == null || g.mods == null) return false;
            foreach (ModData mod in g.mods)
            {
                if (mod == null) continue;
                bool named = false;
                foreach (Item it in mod.playerItems) if (it != null && it.name == name) named = true;
                if (!named) continue;
                int weaponModels = 0;
                foreach (CustomModel cm in mod.customModels) if (cm != null && cm.modelType == CustomModelType.weaponModel) weaponModels++;
                if (model - 1000 < weaponModels) return true;
            }
            return false;
        }

        void ApplyAnimator(float now)
        {
            if (anim == null || Latest == null) return;
            bool hadParams = mirror.Params != null && mirror.Params.Count > 0;
            bool inTarget;
            if (!mirror.Apply(anim, Latest.LayerStates, Latest.LayerTimes, Latest.Params, now, out inTarget,
                (l, target) => Plugin.Log.LogInfo("Puppet " + Id + " layer" + l + " -> " + target))) return;
            if (!hadParams)
            {
                foreach (AnimatorControllerParameter p in mirror.Params) if (p.name == "Speed") hasSpeedParam = true;
                metricsFrom = now + 1f;
                Plugin.Log.LogInfo("Puppet " + Id + " animator: layers=" + anim.layerCount + " params=" + mirror.Params.Count +
                    " (sender has " + Latest.Params.Length + ")");
            }
            samples++;
            if (inTarget) matches++;
        }

        // Fraction of frames where layer 0 was in the sender's state since the last call.
        public string TakeAnimMatch()
        {
            string r = samples == 0 ? "n/a" : ((float)matches / samples).ToString("P0");
            samples = 0; matches = 0;
            return r;
        }

        public void Tick(float dt, float now)
        {
            if (Latest == null) return;
            // A dead friend is no target: enemies drop targets whose NPC is dead.
            stand.isDead = Latest.Dead;
            BuildModel();
            ApplyLook();
            if (Latest.Stealth != torchStealth) ApplyTorch();
            ApplyAnimator(now);
            for (int i = 0; i < 4; i++)
            {
                bool on = (Latest.WeaponFlags & (1 << i)) != 0;
                if (holders[i] != null && holders[i].activeSelf != on) holders[i].SetActive(on);
            }

            Vector3 target = new Vector3(Latest.X, Latest.Y, Latest.Z);
            Transform t = root.transform;
            if ((t.position - target).sqrMagnitude > 20f * 20f)
            {
                t.position = target; // teleport, fast travel, respawn
            }
            else
            {
                t.position = Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-15f * dt));
            }
            t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, Latest.Yaw, 0f), 1f - Mathf.Exp(-15f * dt));

            Camera cam = Camera.main;
            if (cam != null) label.rotation = Quaternion.LookRotation(label.position - cam.transform.position);

            Measure(dt, now);
        }

        void Measure(float dt, float now)
        {
            moveSpeed = travelMeter.Sample(root.transform.position, dt);
            if (anim == null || metricsFrom < 0f || now < metricsFrom) return;

            // Speed as the puppet's Animator sees it (read by name, so a
            // parameter-order mismatch between the two sides would show here).
            if (hasSpeedParam)
            {
                float err = Mathf.Abs(anim.GetFloat("Speed") - Latest.Speed);
                SpeedN++; TSpeedN++;
                AnimErrSum += err; TAnimErrSum += err;
                AnimErrMax = Mathf.Max(AnimErrMax, err); TAnimErrMax = Mathf.Max(TAnimErrMax, err);
            }
            // How fast the puppet really travels vs how fast the sender travels.
            if (Latest.TravelSpeed > 0.5f || moveSpeed > 0.5f)
            {
                float err = Mathf.Abs(moveSpeed - Latest.TravelSpeed);
                MoveN++; TMoveN++;
                MoveErrSum += err; TMoveErrSum += err;
            }
            if (Latest.Clip.Length > 0)
            {
                bool ok = LocalPlayer.DominantClip(anim, 0) == Latest.Clip;
                ClipN++; TClipN++;
                if (ok) { ClipOk++; TClipOk++; }
            }
        }

        // Compares the puppet's look with the sender's real model; called from
        // the status log every 2 s.
        public void CheckLook()
        {
            if (model == null || Look == null || Look.Signature == 0) return;
            int sig = LocalPlayer.LookSignature(model.transform, sigEntries);
            if (sig != puppetSig)
            {
                puppetSig = sig;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Puppet " + Id + " look signature " + sig + " (" + sigEntries.Count +
                    " renderers): " + string.Join("; ", sigEntries.ToArray()));
            }
            bool match = sig == Look.Signature;
            LookN++; TLookN++;
            if (match) { LookOk++; TLookOk++; }
            if (match != lastLookMatch)
            {
                lastLookMatch = match;
                Plugin.Log.LogInfo(Plugin.Stamp() + " Puppet " + Id + " lookMatch=" + match + " (puppet " + sig + ", sender " + Look.Signature + ", look v" + Look.Version + ")");
            }
        }

        public string TakeMetrics()
        {
            string r = "speed sent=" + Latest.Speed.ToString("F2") +
                " anim=" + (anim != null && hasSpeedParam ? anim.GetFloat("Speed").ToString("F2") : "-") +
                " travel sent=" + Latest.TravelSpeed.ToString("F2") + " puppet=" + moveSpeed.ToString("F2") +
                " animErr(mean/max)=" + (SpeedN > 0 ? (AnimErrSum / SpeedN).ToString("F3") : "-") + "/" + AnimErrMax.ToString("F3") +
                " moveErr(mean,n)=" + (MoveN > 0 ? (MoveErrSum / MoveN).ToString("F2") : "-") + "," + MoveN +
                " clipMatch=" + (ClipN > 0 ? ((float)ClipOk / ClipN).ToString("P0") : "-") +
                " lookMatch=" + (LookN > 0 ? ((float)LookOk / LookN).ToString("P0") : "-");
            SpeedN = MoveN = ClipN = ClipOk = LookN = LookOk = 0;
            AnimErrSum = MoveErrSum = 0; AnimErrMax = 0f;
            return r;
        }

        public string Totals()
        {
            return "speed samples=" + TSpeedN + " animErr mean=" + (TSpeedN > 0 ? (TAnimErrSum / TSpeedN).ToString("F4") : "-") +
                " max=" + TAnimErrMax.ToString("F4") +
                " | moving samples=" + TMoveN + " travelErr mean=" + (TMoveN > 0 ? (TMoveErrSum / TMoveN).ToString("F3") : "-") +
                " | clip " + TClipOk + "/" + TClipN + (TClipN > 0 ? " (" + ((float)TClipOk / TClipN).ToString("P1") + ")" : "") +
                " | look " + TLookOk + "/" + TLookN;
        }

        public string Describe()
        {
            Camera cam = Camera.main;
            Vector3 vp = cam != null ? cam.WorldToViewportPoint(Position + Vector3.up) : Vector3.zero;
            string state = anim != null ? anim.GetCurrentAnimatorStateInfo(0).fullPathHash.ToString() : "-";
            return "body=" + (model != null ? "model" : "capsule") + " look=v" + appliedLook +
                " layer0=" + state + " animMatch=" + TakeAnimMatch() + " " + (Latest != null ? TakeMetrics() : "") +
                " viewport=(" + vp.x.ToString("F2") + "," + vp.y.ToString("F2") + "," + vp.z.ToString("F1") + ")";
        }

        public void Destroy()
        {
            Object.Destroy(root);
        }
    }
}
