using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // The co-op entries in the game's own UI (NGUI), made by cloning the
    // game's buttons so they look like the rest:
    //  - main menu: "Deutsch" under the language buttons, "Host co-op" and
    //    "Join co-op" above "Mods" (join lists Steam friends in a co-op lobby);
    //  - in game: a co-op button at the end of the upper bar (also F8) with a
    //    small menu: status, invite friends, host, leave, chat (later).
    // Clicks reach the plugin through EventDelegates on its public On* methods.
    public class CoopUi
    {
        readonly Plugin plugin;
        bool menuBuilt;
        GameObject topButton;
        readonly List<UILabel> ownLabels = new List<UILabel>();
        readonly List<KeyValuePair<string, string>> ownTexts = new List<KeyValuePair<string, string>>();
        float sceneSince = -1f;
        string sceneName = "";

        public bool JoinOpen, MenuOpen, HostOpen;
        LanDiscovery lanSearch;
        float nextLanProbe;
        string lanAddress = "";
        public string Note = "";
        float noteUntil;
        List<FriendInfo> hosting = new List<FriendInfo>();
        float nextFriendsRefresh;
        Rect panelOnScreen;
        bool shielding;

        public CoopUi(Plugin plugin) { this.plugin = plugin; }

        public void Tick(float now)
        {
            string s = SceneManager.GetActiveScene().name;
            if (s != sceneName) { sceneName = s; sceneSince = now; menuBuilt = false; topButton = null; ownLabels.Clear(); ownTexts.Clear(); JoinOpen = MenuOpen = HostOpen = false; }
            if (s == "MainMenu" && !menuBuilt && now - sceneSince > 1f) BuildMenu();
            if (s != "MainMenu" && topButton == null && now - sceneSince > 3f) BuildTopButton();
            if (Note.Length > 0 && now > noteUntil) Note = "";
            if (JoinOpen && now >= nextFriendsRefresh)
            {
                nextFriendsRefresh = now + 2f;
                hosting.Clear();
                foreach (FriendInfo f in plugin.Friends()) if (f.Lobby != 0) hosting.Add(f);
            }
            if ((JoinOpen || MenuOpen || HostOpen) && Input.GetKeyDown(KeyCode.Escape)) { JoinOpen = MenuOpen = HostOpen = false; }
            // Searching the home network while the join list is open.
            if (JoinOpen && lanSearch == null)
            {
                try { lanSearch = LanDiscovery.Search(); } catch (System.Exception e) { Plugin.Log.LogWarning("LAN search off: " + e.Message); }
                nextLanProbe = 0f;
            }
            if (!JoinOpen && lanSearch != null) { lanSearch.Dispose(); lanSearch = null; }
            if (lanSearch != null && now >= nextLanProbe) { nextLanProbe = now + 2f; lanSearch.Probe(); }
            bool open = JoinOpen || MenuOpen || HostOpen;
            bool over = open && panelOnScreen.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
            if (over != shielding) { shielding = over; UICamera.ignoreAllEvents = over; }
            // In play the game hides and locks the cursor; with the menu open
            // it behaves as if one of its own windows were open.
            if (open && !BlocksGame && s != "MainMenu")
            {
                BlocksGame = true;
                savedVisible = Cursor.visible;
                savedLock = Cursor.lockState;
            }
            if (BlocksGame && !open)
            {
                BlocksGame = false;
                Cursor.visible = savedVisible;
                Cursor.lockState = savedLock;
            }
            if (BlocksGame) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }

        public static bool BlocksGame;
        bool savedVisible;
        CursorLockMode savedLock;

        public void ShowNote(string text, float seconds)
        {
            Note = text;
            noteUntil = Time.realtimeSinceStartup + seconds;
        }

        // ---------- the game's buttons ----------

        static UIButton FindButton(string method)
        {
            foreach (UIButton b in Resources.FindObjectsOfTypeAll<UIButton>())
            {
                if (!b.gameObject.scene.IsValid()) continue;
                foreach (EventDelegate d in b.onClick) if (d != null && d.methodName == method) return b;
            }
            return null;
        }

        // A copy of a game button next to it, with its own text and action.
        GameObject Clone(UIButton src, string name, Vector3 localOffset, string english, string deutsch, string method)
        {
            GameObject g = Object.Instantiate(src.gameObject, src.transform.parent);
            g.name = name;
            // Anchored copies would be pulled back onto the original's spot.
            foreach (UIRect r in g.GetComponentsInChildren<UIRect>(true))
                if (r.isAnchored) { r.leftAnchor.target = r.rightAnchor.target = r.topAnchor.target = r.bottomAnchor.target = null; }
            g.transform.localPosition = src.transform.localPosition + localOffset;
            g.transform.localScale = src.transform.localScale;
            foreach (UILocalize loc in g.GetComponentsInChildren<UILocalize>(true)) Object.Destroy(loc);
            foreach (UIKeyBinding key in g.GetComponentsInChildren<UIKeyBinding>(true)) Object.Destroy(key);
            UIButton b = g.GetComponent<UIButton>();
            b.onClick.Clear();
            b.onClick.Add(new EventDelegate(plugin, method));
            UILabel l = g.GetComponentInChildren<UILabel>(true);
            if (l != null) { ownLabels.Add(l); ownTexts.Add(new KeyValuePair<string, string>(english, deutsch)); l.text = Lang.T(english, deutsch); }
            g.SetActive(true);
            return g;
        }

        void BuildMenu()
        {
            menuBuilt = true;
            UIButton english = FindButton("OnEnglishLanguage"), russian = FindButton("OnRussianLanguage");
            UIButton mods = FindButton("OnOpenMods"), bonus = FindButton("OpenGameMode");
            if (english != null && russian != null)
            {
                // Right of "English" (above it is the wishlist banner).
                Bounds bb = NGUIMath.CalculateRelativeWidgetBounds(english.transform.parent, english.transform);
                Clone(english, "Deutsch", new Vector3(bb.size.x + 8f, 0f, 0f), "Deutsch", "Deutsch", "OnMenuGerman");
                // The game's own language buttons end German.
                english.onClick.Add(new EventDelegate(plugin, "OnMenuGameLanguage"));
                russian.onClick.Add(new EventDelegate(plugin, "OnMenuGameLanguage"));
            }
            if (mods != null && bonus != null)
            {
                Vector3 step = bonus.transform.localPosition - mods.transform.localPosition;
                Clone(mods, "CoopJoin", -step, "Join co-op", "Co-op beitreten", "OnMenuCoopJoin");
                Clone(mods, "CoopHost", -step * 2f, "Host co-op", "Co-op hosten", "OnMenuCoopHost");
            }
            Plugin.Log.LogInfo("Main menu: co-op and language buttons added (" + (english != null) + "/" + (mods != null) + ")");
        }

        void BuildTopButton()
        {
            UIButton journal = FindButton("OpenJournal"), settings = FindButton("OpenPauseMenu");
            if (journal == null || settings == null) { sceneSince = Time.realtimeSinceStartup; return; }
            Vector3 step = settings.transform.localPosition - journal.transform.localPosition;
            topButton = Clone(settings, "Coop", step, "F8", "F8", "OnTopCoopButton");
            // The character icon, tinted, for "other players".
            UIButton b = topButton.GetComponent<UIButton>();
            b.normalSprite = "UpperPanelCharIcon";
            b.defaultColor = new Color(0.6f, 0.85f, 1f);
            UISprite sp = topButton.GetComponent<UISprite>();
            if (sp != null) { sp.spriteName = "UpperPanelCharIcon"; sp.color = b.defaultColor; }
            // Make the bar's background (UpperButtons/Sprite, centred) one
            // button wider and keep the row centred on it.
            Transform bar = settings.transform.parent;
            foreach (UISprite bg in bar.GetComponentsInChildren<UISprite>(true))
            {
                if (bg.transform.parent != bar || bg.spriteName != "UpperPanel") continue;
                bg.width += Mathf.RoundToInt(step.x);
                foreach (UIButton ub in bar.GetComponentsInChildren<UIButton>(true))
                    ub.transform.localPosition -= new Vector3(step.x / 2f, 0f, 0f);
                break;
            }
            Plugin.Log.LogInfo("Upper bar: co-op button added");
        }

        // After a language switch: our own button texts.
        public void Relabel()
        {
            for (int i = 0; i < ownLabels.Count; i++)
                if (ownLabels[i] != null) ownLabels[i].text = Lang.T(ownTexts[i].Key, ownTexts[i].Value);
        }

        // Where the upper bar's co-op button is on screen (GUI coordinates).
        Vector2 TopButtonOnScreen()
        {
            if (topButton == null) return new Vector2(Screen.width / 2f, 60f);
            Camera cam = NGUITools.FindCameraForLayer(topButton.layer);
            if (cam == null) return new Vector2(Screen.width / 2f, 60f);
            Vector3 p = cam.WorldToScreenPoint(topButton.transform.position);
            return new Vector2(p.x, Screen.height - p.y);
        }

        // ---------- IMGUI panels ----------

        GUIStyle panel, note;

        public void Draw(float scale, GUIStyle panelStyle)
        {
            if (note == null)
            {
                panel = panelStyle;
                note = new GUIStyle(panelStyle);
                note.alignment = TextAnchor.MiddleCenter;
                note.wordWrap = true;
                note.fontSize = 16;
                note.normal.textColor = Color.white;
            }
            float sw = Screen.width / scale;
            if (Note.Length > 0)
                GUI.Box(new Rect(sw / 2f - 300f, 90f, 600f, 60f), Note, note);
            if (JoinOpen) DrawJoin(sw, scale);
            if (MenuOpen) DrawMenu(sw, scale);
            if (HostOpen) DrawHost(sw, scale);
        }

        void DrawJoin(float sw, float scale)
        {
            List<LanDiscovery.Found> lan = lanSearch != null ? lanSearch.Games() : new List<LanDiscovery.Found>();
            float w = 400f, h = 230f + (Mathf.Max(1, hosting.Count) + Mathf.Max(1, lan.Count)) * 30f;
            Rect area = new Rect(sw - w - 30f, 200f, w, h);
            panelOnScreen = new Rect(area.x * scale, area.y * scale, area.width * scale, area.height * scale);
            GUI.Box(area, "", panel);
            GUI.Box(area, Lang.T("Join a friend's co-op game", "Co-op-Spiel eines Freundes beitreten"));
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 28f, area.width - 20f, area.height - 34f));
            GUILayout.Label("<b>" + Lang.T("Steam friends", "Steam-Freunde") + "</b>");
            if (hosting.Count == 0) GUILayout.Label(Lang.T("No Steam friend is hosting right now.", "Gerade hostet kein Steam-Freund."));
            foreach (FriendInfo f in hosting)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(f.Name, GUILayout.Width(250f));
                if (GUILayout.Button(Lang.T("Join", "Beitreten"), GUILayout.Width(110f))) { JoinOpen = false; plugin.CoopJoin(f.Lobby, f.Name); }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8f);
            GUILayout.Label("<b>" + Lang.T("Home network (LAN, no Steam servers)", "Heimnetz (LAN, ohne Steam-Server)") + "</b>");
            if (lan.Count == 0) GUILayout.Label(Lang.T("Searching ... no game found yet.", "Suche ... noch kein Spiel gefunden."));
            foreach (LanDiscovery.Found g in lan)
            {
                GUILayout.BeginHorizontal();
                bool sameVersion = g.Protocol == Net.ProtocolVersion;
                GUILayout.Label(g.Name + "  (" + g.Address + ")" + (sameVersion ? "" : Lang.T("  other version!", "  andere Version!")), GUILayout.Width(250f));
                GUI.enabled = sameVersion;
                if (GUILayout.Button(Lang.T("Join", "Beitreten"), GUILayout.Width(110f))) { JoinOpen = false; plugin.LanJoin(g.Address); }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Lang.T("Address:", "Adresse:"), GUILayout.Width(70f));
            lanAddress = GUILayout.TextField(lanAddress, 40, GUILayout.Width(175f));
            if (GUILayout.Button(Lang.T("Join", "Beitreten"), GUILayout.Width(110f)) && lanAddress.Trim().Length > 0) { JoinOpen = false; plugin.LanJoin(lanAddress); }
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("Close", "Schließen"))) JoinOpen = false;
            GUILayout.EndArea();
        }

        // Test: join the first game the home-network search found.
        public string FirstLanGame()
        {
            if (lanSearch == null) return null;
            foreach (LanDiscovery.Found g in lanSearch.Games()) return g.Address + " " + g.Name;
            return null;
        }

        // Main menu: how to host.
        void DrawHost(float sw, float scale)
        {
            float w = 400f, h = 200f;
            Rect area = new Rect(sw - w - 30f, 200f, w, h);
            panelOnScreen = new Rect(area.x * scale, area.y * scale, area.width * scale, area.height * scale);
            GUI.Box(area, "", panel);
            GUI.Box(area, Lang.T("Host a co-op game", "Co-op-Spiel hosten"));
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 28f, area.width - 20f, area.height - 34f));
            if (GUILayout.Button(Lang.T("Via Steam (invite friends, also over the internet)", "Über Steam (Freunde einladen, auch übers Internet)"), GUILayout.Height(34f)))
            { HostOpen = false; plugin.MenuSteamHost(); }
            if (GUILayout.Button(Lang.T("In the home network (LAN, no Steam servers)", "Im Heimnetz (LAN, ohne Steam-Server)"), GUILayout.Height(34f)))
            { HostOpen = false; plugin.LanHost(); }
            GUILayout.Label(Lang.T("Then load or start your game as usual.", "Danach wie gewohnt dein Spiel laden oder starten."));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.T("Close", "Schließen"))) HostOpen = false;
            GUILayout.EndArea();
        }

        void DrawMenu(float sw, float scale)
        {
            Vector2 at = TopButtonOnScreen() / scale;
            float w = 300f, h = 260f;
            Rect area = new Rect(Mathf.Clamp(at.x - w / 2f, 10f, sw - w - 10f), at.y + 30f, w, h);
            panelOnScreen = new Rect(area.x * scale, area.y * scale, area.width * scale, area.height * scale);
            GUI.Box(area, "", panel);
            GUI.Box(area, "Gedonia Co-op " + Plugin.Version);
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 26f, area.width - 20f, area.height - 32f));
            GUILayout.Label(plugin.CoopStatus());
            if (plugin.CoopActive)
            {
                if (plugin.CoopCanInvite && GUILayout.Button(Lang.T("Invite friends", "Freunde einladen"))) { MenuOpen = false; plugin.OpenInvite(); }
                if (GUILayout.Button(Lang.T("Leave co-op", "Co-op verlassen"))) { MenuOpen = false; plugin.CoopLeave(); }
            }
            else
            {
                if (GUILayout.Button(Lang.T("Host via Steam (friends)", "Über Steam hosten (Freunde)"))) { MenuOpen = false; plugin.CoopHost(); }
                if (GUILayout.Button(Lang.T("Host in the home network (LAN)", "Im Heimnetz hosten (LAN)"))) { MenuOpen = false; plugin.LanHost(); }
                if (GUILayout.Button(Lang.T("Join ...", "Beitreten ..."))) { MenuOpen = false; JoinOpen = true; }
            }
            GUI.enabled = false;
            GUILayout.Button(Lang.T("Chat (coming later)", "Chat (kommt später)"));
            GUI.enabled = true;
            if (GUILayout.Button(Lang.T("Close", "Schließen"))) MenuOpen = false;
            GUILayout.EndArea();
        }
    }
}
