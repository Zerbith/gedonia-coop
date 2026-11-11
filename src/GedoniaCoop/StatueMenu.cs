using System.Collections.Generic;
using UnityEngine;

namespace GedoniaCoop
{
    // F at an angel statue opens this window (game skin, IMGUI):
    //  - bind here as the respawn point (as F did before);
    //  - travel to a teammate: to the angel statue nearest to them (not right
    //    next to them, so travel stays tied to the statues);
    //  - redistribute the four base attributes for a fee. The game fixes them
    //    at character creation (8 points, each 0..4, the 4th step of one costs
    //    an extra point: MainMenuManager.StartNewGame) and never changes them;
    //    the same rule applies here. They are saved with the character
    //    (SaveLoadManager writes strengthBasic etc.), so the change stays.
    // Esc or "Close" shuts it; walking away too.
    public class StatueMenu
    {
        public static StatueMenu Instance = new StatueMenu();
        public bool Open;
        public Rect PanelRect;
        RespawnStones owner;
        string village = "";
        Vector3 openedAt;
        int page; // 0 main, 1 attributes
        readonly int[] stats = new int[4];
        public static string Last = ""; // test log: the last thing done
        public int Travels, Respecs;

        static readonly string[] En = { "Strength", "Agility", "Intellect", "Charisma" };
        static readonly string[] De = { "Stärke", "Geschick", "Intellekt", "Charisma" };

        public static int Fee
        {
            get { PlayerScript p = PlayerScript.instance; return p == null ? 0 : Mathf.Max(50, p.level * 20); }
        }

        public void Show(RespawnStones o, string v)
        {
            owner = o; village = v; page = 0; Open = true;
            PlayerScript p = PlayerScript.instance;
            if (p != null) openedAt = p.transform.position;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [statue] menu opened at " + v);
        }

        public void BindHere() { if (Open && owner != null) { owner.Bind(village); Last = "bound " + village; } }
        public void ShowStats() { if (Open && PlayerScript.instance != null) { Read(stats); page = 1; } }
        public void ShowChest() { if (Open && ChestAvailable) page = 2; }
        static bool ChestAvailable { get { return GroupChest.Instance != null && RespawnStones.Connected; } }
        Vector2 chestScroll, bagScroll;

        public void Close(string why)
        {
            if (!Open) return;
            Open = false;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [statue] menu closed (" + why + ")");
        }

        public void Tick()
        {
            if (!Open) return;
            PlayerScript p = PlayerScript.instance;
            if (p == null || p.isDead) { Close("no player"); return; }
            if (Vector3.Distance(p.transform.position, openedAt) > 4f) Close("walked away");
        }

        static void Read(int[] s)
        {
            PlayerScript p = PlayerScript.instance;
            s[0] = p.strengthBasic; s[1] = p.agilityBasic; s[2] = p.intellectBasic; s[3] = p.charismaBasic;
        }

        // Points still free under the creation rule (negative: too many).
        public static int Left(int[] s)
        {
            int n = 8;
            foreach (int v in s) { n -= v; if (v == 4) n--; }
            return n;
        }

        static bool Same(int[] s)
        {
            PlayerScript p = PlayerScript.instance;
            return s[0] == p.strengthBasic && s[1] == p.agilityBasic && s[2] == p.intellectBasic && s[3] == p.charismaBasic;
        }

        // ---------- actions (also used by tests) ----------

        public bool Respec(int[] s, out string why)
        {
            PlayerScript p = PlayerScript.instance;
            why = "";
            if (p == null) { why = "no player"; return false; }
            foreach (int v in s) if (v < 0 || v > 4) { why = Lang.T("Each attribute is 0 to 4.", "Jedes Attribut geht von 0 bis 4."); return false; }
            if (Left(s) > 0) { why = Lang.T("Distribute all 8 points.", "Verteile alle 8 Punkte."); return false; }
            if (Left(s) < 0) { why = Lang.T("That is more than 8 points (the 4th point of one costs 2).", "Das sind mehr als 8 Punkte (der 4. Punkt kostet 2)."); return false; }
            if (Same(s)) { why = Lang.T("Nothing changed.", "Nichts geändert."); return false; }
            int fee = Fee;
            if (p.gold < fee) { why = Lang.T("Not enough gold (" + fee + " needed).", "Nicht genug Gold (" + fee + " nötig)."); return false; }
            string before = p.strengthBasic + "/" + p.agilityBasic + "/" + p.intellectBasic + "/" + p.charismaBasic;
            p.gold -= fee;
            p.strengthBasic = s[0]; p.agilityBasic = s[1]; p.intellectBasic = s[2]; p.charismaBasic = s[3];
            InterfaceManager im = InterfaceManager.instance;
            try { p.RecalculateStats(false); } catch (System.Exception e) { Plugin.Log.LogWarning("RecalculateStats after the respec: " + e.Message); }
            if (im != null)
            {
                if (im.moneyLabel != null) im.moneyLabel.text = p.gold.ToString();
                try { im.RefreshCharacterWindow(); } catch (System.Exception) { }
                im.screenMessagesLarge.Add(Lang.T("Attributes redistributed", "Attribute neu verteilt"), Color.white, 3f);
            }
            Respecs++;
            Last = "respec " + before + " -> " + s[0] + "/" + s[1] + "/" + s[2] + "/" + s[3] + " fee " + fee + " gold left " + p.gold;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [statue] " + Last);
            return true;
        }

        public bool Travel(int puppetId, out string why)
        {
            why = "";
            Puppet target = null;
            foreach (Puppet t in Plugin.AllPuppets()) if (t.Id == puppetId) target = t;
            if (target == null || target.Latest == null || !Plugin.SameWorld(puppetId)) { why = Lang.T("That player is not in this world.", "Dieser Spieler ist nicht in dieser Welt."); return false; }
            if (target.Latest.InDungeon) { why = Lang.T(target.Latest.Name + " is in a dungeon.", target.Latest.Name + " ist in einem Dungeon."); return false; }
            string to;
            if (owner == null || !owner.TravelNear(target.Position, out to)) { why = Lang.T("No angel statue in this world.", "Keine Engelsstatue in dieser Welt."); return false; }
            Travels++;
            Last = "travel to " + target.Latest.Name + " -> statue " + to;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [statue] " + Last);
            Close("travelled");
            return true;
        }

        // ---------- window ----------

        public void DrawPanel(float sw, float sh, GUIStyle panel)
        {
            PlayerScript p = PlayerScript.instance;
            if (p == null) return;
            string de = Lang.German ? Translator.German(village) : null;
            float w = page == 2 ? 700f : 440f, h = page == 0 ? 330f + Mathf.Max(1, Mates().Count) * 32f + (ChestAvailable ? 40f : 0f) : page == 1 ? 330f : 460f;
            Rect area = new Rect(sw / 2f - w / 2f, sh / 2f - h / 2f, w, h);
            PanelRect = area;
            GameSkin.Window(area, Lang.T("Angel statue (" + village + ")   (Esc closes)", "Engelsstatue (" + (de ?? village) + ")   (Esc schließt)"), panel);
            GUILayout.BeginArea(new Rect(area.x + 12f, area.y + 30f, area.width - 24f, area.height - 38f));
            if (page == 0) DrawMain(p); else if (page == 1) DrawStats(p); else DrawChest(p);
            GUILayout.EndArea();
        }

        List<Puppet> Mates()
        {
            List<Puppet> l = new List<Puppet>();
            foreach (Puppet t in Plugin.AllPuppets()) if (t.Latest != null && Plugin.SameWorld(t.Id)) l.Add(t);
            return l;
        }

        void DrawMain(PlayerScript p)
        {
            bool bound = owner != null && owner.IsBound(village);
            GameSkin.LLabel(Lang.T("Respawn point", "Wiederbelebungspunkt"));
            if (bound) GameSkin.LLabel(Lang.T("  You are bound here.", "  Du bist hier gebunden."));
            else if (GameSkin.LButton(Lang.T("Bind yourself here", "Hier binden"))) { owner.Bind(village); Last = "bound " + village; }
            GUILayout.Space(8f);
            GameSkin.LLabel(Lang.T("Travel to a teammate (to the statue nearest them)", "Zu Mitspieler reisen (zur Statue in seiner Nähe)"));
            List<Puppet> mates = Mates();
            if (mates.Count == 0) GameSkin.LLabel(Lang.T("  No teammate in this world.", "  Kein Mitspieler in dieser Welt."));
            foreach (Puppet t in mates)
            {
                string near = owner != null ? owner.NearestStatueName(t.Position) : "?";
                string nd = Lang.German ? Translator.German(near) : null;
                string why;
                if (GameSkin.LButton(t.Latest.Name + "  →  " + (nd ?? near)) && !Travel(t.Id, out why)) Message(why);
            }
            GUILayout.Space(8f);
            GameSkin.LLabel(Lang.T("Base attributes (fee " + Fee + " gold, you have " + p.gold + ")", "Grundattribute (Gebühr " + Fee + " Gold, du hast " + p.gold + ")"));
            if (GameSkin.LButton(Lang.T("Redistribute attributes", "Attribute neu verteilen"))) { Read(stats); page = 1; }
            if (ChestAvailable)
            {
                GUILayout.Space(8f);
                if (GameSkin.LButton(Lang.T("Group chest (" + GroupChest.Instance.Items.Count + " stacks)", "Gruppentruhe (" + GroupChest.Instance.Items.Count + " Stapel)"))) page = 2;
            }
            GUILayout.Space(8f);
            if (GameSkin.LButton(Lang.T("Close", "Schließen"))) Close("button");
        }

        void DrawStats(PlayerScript p)
        {
            int left = Left(stats);
            GameSkin.LLabel(Lang.T("8 points; each attribute 0 to 4 (the 4th point costs 2).", "8 Punkte; jedes Attribut 0 bis 4 (der 4. Punkt kostet 2)."));
            for (int i = 0; i < 4; i++)
            {
                GUILayout.BeginHorizontal();
                GameSkin.LLabel(Lang.T(En[i], De[i]), GUILayout.Width(140f));
                if (GameSkin.LButton("-", GUILayout.Width(40f)) && stats[i] > 0) stats[i]--;
                GameSkin.LLabel(stats[i] + " / 4", GUILayout.Width(60f));
                int cost = stats[i] == 3 ? 2 : 1;
                if (GameSkin.LButton("+", GUILayout.Width(40f)) && stats[i] < 4 && left >= cost) stats[i]++;
                GUILayout.EndHorizontal();
            }
            GameSkin.LLabel(Lang.T("Points left: ", "Freie Punkte: ") + left);
            GameSkin.LLabel(Lang.T("Fee: " + Fee + " gold (you have " + p.gold + ")", "Gebühr: " + Fee + " Gold (du hast " + p.gold + ")"));
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            string why;
            if (GameSkin.LButton(Lang.T("Confirm", "Bestätigen"))) { if (Respec(stats, out why)) page = 0; else Message(why); }
            if (GameSkin.LButton(Lang.T("Back", "Zurück"))) page = 0;
            GUILayout.EndHorizontal();
        }

        // Left: the chest (click takes the stack). Right: own tradable stacks (click puts them in).
        void DrawChest(PlayerScript p)
        {
            GroupChest chest = GroupChest.Instance;
            if (chest == null) { page = 0; return; }
            GameSkin.LLabel(Lang.T("Shared by everyone in co-op; the host keeps it. Click a stack to move it.", "Gehört allen im Co-op, der Host bewahrt sie auf. Klick verschiebt einen Stapel."));
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(330f));
            GameSkin.LLabel(Lang.T("Group chest", "Gruppentruhe"));
            chestScroll = GameSkin.BeginScroll(chestScroll, GUILayout.Height(300f));
            if (chest.Items.Count == 0) GameSkin.LLabel(Lang.T("(empty)", "(leer)"));
            for (int i = 0; i < chest.Items.Count; i++)
            {
                TradeSync.Line l = chest.Items[i];
                if (GameSkin.LButton(l.Amount + "x " + TradeSync.Show(l.Name))) { chest.Take(i); break; }
            }
            GameSkin.EndScroll();
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUILayout.Width(330f));
            GameSkin.LLabel(Lang.T("Your bag", "Dein Beutel"));
            bagScroll = GameSkin.BeginScroll(bagScroll, GUILayout.Height(300f));
            foreach (InventoryItem ii in new List<InventoryItem>(p.inventory))
            {
                if (ii == null || ii.item == null || !TradeSync.Tradable(ii.item)) continue;
                if (GameSkin.LButton(ii.amount + "x " + TradeSync.Show(ii.item.name))) { chest.Put(ii); break; }
            }
            GameSkin.EndScroll();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            if (GameSkin.LButton(Lang.T("Back", "Zurück"))) page = 0;
        }

        static void Message(string text)
        {
            if (InterfaceManager.instance != null && text.Length > 0) InterfaceManager.instance.screenMessages.Add(text, Color.red, 2.5f);
            Plugin.Log.LogInfo(Plugin.Stamp() + " [statue] refused: " + text);
        }
    }
}
