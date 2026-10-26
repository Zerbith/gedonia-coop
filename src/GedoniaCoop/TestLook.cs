using System;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GedoniaCoop
{
    // Test-only appearance changes for a game COPY, so two copies loaded from
    // the same save can show different characters. Writes the same PlayerScript
    // fields the character creator and equip code write, then asks the game to
    // redraw the model.
    public static class TestLook
    {
        // Different hair, head, hair colour and skin, each wrapped into the
        // valid range of the customization arrays.
        public static void ApplyAlternateLook(PlayerScript p)
        {
            PlayerModelCustomization m = p.modelMain;
            int hairCount = p.isMale ? m.hairMale.Length : m.hairFemale.Length;
            int headCount = p.isMale ? m.heads.Length : m.headsFemale.Length;
            int colorCount = Math.Min(m.hairColors.Length, Math.Min(m.eyebrowsColors.Length, m.beardColors.Length));
            int skinCount = Math.Min(Math.Min(m.normalMaterials.Length, m.lighterMaterials.Length),
                Math.Min(m.darkerMaterials.Length, m.darkMaterials.Length)) > 0 ? 4 : 1;

            string before = Describe(p);
            if (hairCount > 2) p.hairModel = 1 + (p.hairModel + hairCount / 2 - 1) % (hairCount - 1); // 1..n-1, never "bald"
            if (headCount > 1) p.headType = (p.headType + 1) % headCount;
            if (colorCount > 1) p.hairColor = (p.hairColor + colorCount / 2) % colorCount;
            p.skinColor = (p.skinColor + 2) % skinCount;
            Plugin.Log.LogInfo("Test look: " + before + " -> " + Describe(p) +
                " (ranges hair=" + hairCount + " head=" + headCount + " color=" + colorCount + " skin=" + skinCount + ")");
            p.RefreshPlayerModel();
        }

        static string Describe(PlayerScript p)
        {
            return "hair=" + p.hairModel + " head=" + p.headType + " hairColor=" + p.hairColor + " skin=" + p.skinColor;
        }

        // One -coop-test-look step:
        //   female      switch to a female body with a valid hair/head
        //   helmet      equip a helmet, cloak: equip a cloak
        //   hidehelmet / hidecloak / showall   the options-menu switches (fields only, no PlayerPrefs)
        //   bow         equip a bow as the only weapon
        //   modweapon   wield a weapon item with mod model 1000 that no PC has installed
        //   badmodel    send helmet model 999 (as from another game version); the own model is unchanged
        public static void Step(PlayerScript p, string step)
        {
            PlayerModelCustomization m = p.modelMain;
            ItemsData db = ItemsData.instance;
            Traverse t = Traverse.Create(m);
            if (step == "female")
            {
                p.isMale = false;
                p.hairModel = Math.Min(1, m.hairFemale.Length - 1);
                p.headType = 0;
                p.beardModel = 0;
            }
            else if (step == "helmet" || step == "cloak")
            {
                int slot = step == "helmet" ? 0 : 9;
                int limit = step == "helmet" ? m.helmets.Length : m.cloaks.Length;
                foreach (ArmorItem a in db.armorDatabase)
                {
                    bool kind = step == "helmet" ? a != null && (a.armorType == ArmorType.steelHelmet || a.armorType == ArmorType.leatherHelmet)
                        : a != null && a.armorType == ArmorType.cloak;
                    if (kind && a.model > 0 && a.model < limit)
                    {
                        p.equip[slot] = new InventoryItem { item = a, additionalData = "", amount = 1 };
                        Plugin.Log.LogInfo("Test look: equipped " + step + " '" + a.name + "' model " + a.model);
                        break;
                    }
                }
            }
            else if (step == "hidehelmet") t.Field("showHelmet").SetValue(false);
            else if (step == "hidecloak") t.Field("showCloak").SetValue(false);
            else if (step == "showall") { t.Field("showHelmet").SetValue(true); t.Field("showCloak").SetValue(true); }
            else if (step == "bow")
            {
                foreach (WeaponItem w in db.weaponsDatabase)
                {
                    if (w != null && w.weaponType == WeaponType.Bow && w.model > 0 && w.model < m.weaponsMain.Length)
                    {
                        p.mainWeapon = new InventoryItem { item = w, additionalData = "", amount = 1 };
                        p.secondaryWeapon = null;
                        Plugin.Log.LogInfo("Test look: equipped bow '" + w.name + "' model " + w.model);
                        break;
                    }
                }
                // Shooting needs an "...arrows" stack in the inventory.
                bool hasArrows = false;
                foreach (InventoryItem it in p.inventory) if (it != null && it.item != null && it.item.name.Contains("arrows")) hasArrows = true;
                if (!hasArrows)
                {
                    foreach (Item it in db.basicItemsData)
                    {
                        if (it != null && it.name == "Simple arrows")
                        {
                            p.inventory.Add(new InventoryItem { item = it, additionalData = "", amount = 60 });
                            Plugin.Log.LogInfo("Test look: added 60 '" + it.name + "'");
                            break;
                        }
                    }
                }
                p.RecalculateStats(false);
            }
            else if (step == "torch")
            {
                foreach (WeaponItem w in db.weaponsDatabase)
                {
                    if (w != null && w.weaponType == WeaponType.Torch && w.model >= 138 && w.model <= 144)
                    {
                        p.secondaryWeapon = new InventoryItem { item = w, additionalData = "", amount = 1 };
                        Plugin.Log.LogInfo("Test look: torch '" + w.name + "' (model " + w.model + ") in the off hand");
                        break;
                    }
                }
            }
            else if (step == "fakemod")
            {
                // Test-only: "install" a weapon mod on this copy the way
                // GameModIntegrationManager knows mods - a ModData with the item
                // and its weapon model file in persistentDataPath/Mods/<mod>/ (the
                // test copy's own data folder).
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "Mods/CoopTestMod");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "blade.obj"),
                    "o blade\nv -0.02 0 -0.02\nv 0.02 0 -0.02\nv 0.02 0 0.02\nv -0.02 0 0.02\nv -0.02 1.2 -0.02\nv 0.02 1.2 -0.02\nv 0.02 1.2 0.02\nv -0.02 1.2 0.02\n" +
                    "f 1 2 3 4\nf 5 8 7 6\nf 1 5 6 2\nf 2 6 7 3\nf 3 7 8 4\nf 5 1 4 8\n");
                WeaponItem blade = FakeModBlade(p);
                ModData mod = new ModData { name = "CoopTestMod" };
                mod.playerItems.Add(blade);
                mod.customModels.Add(new CustomModel { name = "blade.obj", useCollision = true, modelType = CustomModelType.weaponModel });
                GameModIntegrationManager.instance.mods.Add(mod);
                Plugin.Log.LogInfo("Test look: weapon mod 'CoopTestMod' installed here (" + dir + ")");
                return;
            }
            else if (step == "modweapon")
            {
                WeaponItem fake = FakeModBlade(p);
                p.mainWeapon = new InventoryItem { item = fake, additionalData = "", amount = 1 };
                Plugin.Log.LogInfo("Test look: wielding '" + fake.name + "' (mod model 1000)");
            }
            else if (step == "badmodel")
            {
                Appearance a = LocalPlayer.Current;
                if (a == null) return;
                a.Models[0] = 999;
                a.Version += 1000; // a new version, so the puppets re-dress
                Plugin.Log.LogInfo("Test look: sending helmet model 999 in appearance v" + a.Version);
                return;
            }
            else { Plugin.Log.LogWarning("Unknown look test step '" + step + "'"); return; }
            Plugin.Log.LogInfo("Test look step '" + step + "': male=" + p.isMale + " showHelmet=" + t.Field("showHelmet").GetValue<bool>() +
                " showCloak=" + t.Field("showCloak").GetValue<bool>());
            p.RefreshPlayerModel();
        }

        static WeaponItem fakeBlade;

        // The test mod's weapon: a copy of the current main weapon (same
        // attacks) named like the mod item, with mod model 1000.
        static WeaponItem FakeModBlade(PlayerScript p)
        {
            if (fakeBlade != null) return fakeBlade;
            WeaponItem cur = (WeaponItem)p.mainWeapon.item;
            fakeBlade = Object.Instantiate(cur);
            fakeBlade.name = "CoopTestModBlade";
            fakeBlade.model = 1000;
            return fakeBlade;
        }

        // Swap to a steel chest piece and another weapon of the same type (so
        // the attack animations stay valid), as equipping them would.
        public static void SwapGear(PlayerScript p)
        {
            PlayerModelCustomization m = p.modelMain;
            ItemsData db = ItemsData.instance;
            string log = "Test gear swap:";

            ArmorItem chest = null;
            int curChest = p.equip[2] != null ? ((ArmorItem)p.equip[2].item).model : 0;
            int chestSlots = p.isMale ? m.chestsMale.Length : m.chestsFemale.Length;
            foreach (ArmorItem a in db.armorDatabase)
            {
                if (a != null && a.armorType == ArmorType.steelChestArmor && a.model != curChest && a.model > 0 && a.model < chestSlots)
                {
                    chest = a;
                    break;
                }
            }
            if (chest != null)
            {
                p.equip[2] = new InventoryItem { item = chest, additionalData = "", amount = 1 };
                log += " chest model " + curChest + " -> " + chest.model + " ('" + chest.name + "')";
            }

            if (p.mainWeapon != null)
            {
                WeaponItem cur = (WeaponItem)p.mainWeapon.item;
                foreach (WeaponItem w in db.weaponsDatabase)
                {
                    if (w != null && w.weaponType == cur.weaponType && w.model != cur.model && w.model > 0 && w.model < m.weaponsMain.Length)
                    {
                        p.mainWeapon = new InventoryItem { item = w, additionalData = "", amount = 1 };
                        log += "; weapon '" + cur.name + "' (model " + cur.model + ") -> '" + w.name + "' (model " + w.model + ")";
                        break;
                    }
                }
            }
            Plugin.Log.LogInfo(log);
            p.RefreshPlayerModel();
        }
    }
}
