using UnityEngine;

namespace GedoniaCoop
{
    // Emotes with the game's own animations: B opens a small menu (or chat
    // commands like /sitzen, /prost). They are animator triggers of the local
    // player (SitDown is the game's own "Sit" button; Drink/Eat/Gather play when
    // drinking, eating, picking herbs); the puppets copy the animator states,
    // so everyone sees them without a message of their own.
    public static class Emotes
    {
        public const KeyCode Key = KeyCode.B;
        public static bool Open;
        public static Rect PanelRect;
        public static int Played;
        public static string Last = "";

        struct Emote { public string Trigger, En, De; public string[] Words; }
        static readonly Emote[] All =
        {
            new Emote { Trigger = "SitDown", En = "Sit down", De = "Hinsetzen", Words = new[] { "/sit", "/sitzen", "/setzen" } },
            new Emote { Trigger = "Drink", En = "Cheers (drink)", De = "Prost (trinken)", Words = new[] { "/cheers", "/prost", "/drink", "/trinken" } },
            new Emote { Trigger = "Eat", En = "Eat", De = "Essen", Words = new[] { "/eat", "/essen" } },
            new Emote { Trigger = "Gather", En = "Kneel", De = "Knien", Words = new[] { "/kneel", "/knien" } },
        };

        public static void Tick(bool inWorld)
        {
            if (!inWorld) { Open = false; return; }
            if (Input.GetKeyDown(Key) && !ChatSync.Typing && !UICamera.inputHasFocus && (Open || (InterfaceManager.instance != null && !InterfaceManager.instance.AnyPanelIsActive())))
                Open = !Open;
            if (Open && Input.GetKeyDown(KeyCode.Escape)) Open = false;
        }

        // Chat: "/sitzen" etc. True when the text was an emote command.
        public static bool Command(string text)
        {
            string t = text.Trim().ToLowerInvariant();
            for (int i = 0; i < All.Length; i++)
                foreach (string w in All[i].Words)
                    if (t == w) { Play(i); return true; }
            return false;
        }

        public static bool Play(int i)
        {
            PlayerScript p = PlayerScript.instance;
            if (p == null || p.anim == null || i < 0 || i >= All.Length) return false;
            if (p.isDead || p.isMounted || p.isSwimming || p.inBattle) { Refuse(Lang.T("Not now.", "Gerade nicht.")); return false; }
            if (All[i].Trigger == "SitDown" && (p._char == null || p._char.velocity.magnitude > 0.2f || !p._char.isGrounded)) { Refuse(Lang.T("Stand still to sit down.", "Zum Hinsetzen stehen bleiben.")); return false; }
            p.anim.SetTrigger(All[i].Trigger);
            Played++;
            Last = All[i].Trigger;
            Open = false;
            Plugin.Log.LogInfo(Plugin.Stamp() + " [emote] " + All[i].Trigger);
            return true;
        }

        static void Refuse(string text)
        {
            if (InterfaceManager.instance != null) InterfaceManager.instance.screenMessages.Add(text, Color.white, 1.5f);
        }

        public static void Draw(float sw, float sh, GUIStyle panel)
        {
            float w = 260f, h = 52f + All.Length * 34f + 34f;
            Rect area = new Rect(sw / 2f - w / 2f, sh * 0.55f, w, h);
            PanelRect = area;
            GameSkin.Window(area, Lang.T("Emotes (B)", "Emotes (B)"), panel);
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 28f, area.width - 20f, area.height - 34f));
            for (int i = 0; i < All.Length; i++)
                if (GameSkin.LButton(Lang.T(All[i].En, All[i].De) + "   " + All[i].Words[Lang.German ? 1 : 0])) Play(i);
            if (GameSkin.LButton(Lang.T("Close", "Schließen"))) Open = false;
            GUILayout.EndArea();
        }
    }
}
