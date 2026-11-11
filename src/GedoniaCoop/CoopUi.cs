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
    //    small menu: status, invite friends, host, leave, chat.
    // Clicks reach the plugin through EventDelegates on its public On* methods.
    public class CoopUi
    {
        readonly Plugin plugin;
        bool menuBuilt;
        GameObject topButton;
        bool loadingCreditBuilt;
        UILabel versionLabel;
        string gameVersionText;
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
            if (s != sceneName) { sceneName = s; sceneSince = now; menuBuilt = false; topButton = null; loadingCreditBuilt = false; versionLabel = null; credits.Clear(); ownLabels.Clear(); ownTexts.Clear(); JoinOpen = MenuOpen = HostOpen = false; }
            if (s == "MainMenu" && !menuBuilt && now - sceneSince > 1f) BuildMenu();
            if (s != "MainMenu" && topButton == null && now - sceneSince > 3f) BuildTopButton();
            if (s != "MainMenu" && !loadingCreditBuilt) BuildLoadingCredit();
            if (credits.Count > 0) PlaceCredits();
            StatueMenu.Instance.Tick();
            if (Note.Length > 0 && now > noteUntil) Note = "";
            if (JoinOpen && now >= nextFriendsRefresh)
            {
                nextFriendsRefresh = now + 2f;
                hosting.Clear();
                foreach (FriendInfo f in plugin.Friends()) if (f.Lobby != 0) hosting.Add(f);
            }
            if ((JoinOpen || MenuOpen || HostOpen || ExtraOpen) && Input.GetKeyDown(KeyCode.Escape)) { JoinOpen = MenuOpen = HostOpen = false; CloseExtra(); }
            // Searching the home network while the join list is open.
            if (JoinOpen && lanSearch == null)
            {
                try { lanSearch = LanDiscovery.Search(); } catch (System.Exception e) { Plugin.Log.LogWarning("LAN search off: " + e.Message); }
                nextLanProbe = 0f;
            }
            if (!JoinOpen && lanSearch != null) { lanSearch.Dispose(); lanSearch = null; }
            if (lanSearch != null && now >= nextLanProbe) { nextLanProbe = now + 2f; lanSearch.Probe(); }
            if (ChatSync.Instance != null && !plugin.CoopActive) ChatSync.Instance.Close();
            bool open = JoinOpen || MenuOpen || HostOpen || ExtraOpen || ChatSync.Typing;
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

        // Windows of other features opened from the co-op menu.
        static bool ExtraOpen { get { return (PartySync.Instance != null && PartySync.Instance.Open) || (TradeSync.Instance != null && TradeSync.Instance.Open) || StatueMenu.Instance.Open || Emotes.Open || SessionStats.Open; } }
        static void CloseExtra() { if (PartySync.Instance != null) PartySync.Instance.Open = false; if (TradeSync.Instance != null) TradeSync.Instance.Open = false; StatueMenu.Instance.Close("Esc"); Emotes.Open = false; SessionStats.Open = false; }

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
                if (Translator.Available) Clone(english, "Deutsch", new Vector3(bb.size.x + 8f, 0f, 0f), "Deutsch", "Deutsch", "OnMenuGerman");
                else Plugin.Log.LogInfo("No German tables installed: no \"Deutsch\" button");
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
            // Credit under the title logo, mod version next to the game's.
            Transform menu = mods != null ? mods.transform.root : null;
            UILabel copyright = null, version = null;
            if (menu != null)
                foreach (UILabel l in menu.GetComponentsInChildren<UILabel>(true))
                {
                    if (l.text.StartsWith("Version")) version = l;
                    else if (l.text.StartsWith("(c)")) copyright = l;
                }
            UITexture logo = FindLogo(menu, true);
            if (logo != null)
            {
                AddCredit(logo);
                // The menu's own (hidden) loading panel, shown while a save loads.
                foreach (UITexture t in menu.GetComponentsInChildren<UITexture>(true))
                    if (t != logo && t.mainTexture != null && t.mainTexture.name == "LogoPng3") AddCredit(t);
            }
            else Plugin.Log.LogWarning("Main menu: no title logo for the mod credit");
            if (version != null) { versionLabel = version; gameVersionText = version.text; ShowModVersion(); }
            else Plugin.Log.LogWarning("Main menu: no game version label");
        }

        // The game's title logo (texture LogoPng3) under a root. The main menu
        // has a second, hidden one (its own loading panel): the visible one wins.
        static UITexture FindLogo(Transform root, bool visibleOnly)
        {
            if (root == null) return null;
            foreach (UITexture t in root.GetComponentsInChildren<UITexture>(true))
                if (t.mainTexture != null && t.mainTexture.name == "LogoPng3" && (!visibleOnly || t.gameObject.activeInHierarchy)) return t;
            return null;
        }

        // "Coop Mod by Zerbith" as a picture in the logo's own look (gold gradient,
        // dark rim, warm glow; scripts\render-credit.ps1, embedded in the dll),
        // centred under the lettering. The logo picture is larger than its
        // lettering: the letters end at ~69 % of its height.
        class Credit { public UITexture Pic; public UITexture Logo; public Vector3 Placed = new Vector3(float.NaN, 0f, 0f); public int Logs; }
        readonly List<Credit> credits = new List<Credit>();
        const float CreditBelow = 0.68f, CreditWidth = 0.50f; // of the logo picture's height / width
        static Texture2D creditTex;

        static Texture2D CreditTexture()
        {
            if (creditTex != null) return creditTex;
            using (System.IO.Stream s = typeof(CoopUi).Assembly.GetManifestResourceStream("GedoniaCoop.credit.png"))
            {
                if (s == null) { Plugin.Log.LogWarning("Mod credit picture missing in the dll"); return null; }
                byte[] data = new byte[s.Length];
                int got = 0;
                while (got < data.Length) { int n = s.Read(data, got, data.Length - got); if (n <= 0) break; got += n; }
                Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                t.name = "CoopCredit";
                t.LoadImage(data);
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Trilinear;
                t.anisoLevel = 4;
                t.hideFlags = HideFlags.DontUnloadUnusedAsset;
                creditTex = t;
            }
            return creditTex;
        }

        // Every frame while the logo shows: the logo is laid out (anchors, tweens,
        // a panel that is still hidden) after the credit is made.
        void PlaceCredits()
        {
            for (int i = credits.Count - 1; i >= 0; i--)
            {
                Credit cr = credits[i];
                if (cr.Pic == null || cr.Logo == null) { credits.RemoveAt(i); continue; }
                if (!cr.Logo.gameObject.activeInHierarchy) continue;
                Vector3[] c = cr.Logo.worldCorners; // bottom-left, top-left, top-right, bottom-right
                Vector3 p = Vector3.Lerp((c[1] + c[2]) / 2f, (c[0] + c[3]) / 2f, CreditBelow);
                Transform t = cr.Pic.transform;
                if ((t.position - p).sqrMagnitude > 1e-10f) { t.position = p; t.localPosition = new Vector3(t.localPosition.x, t.localPosition.y, 0f); }
                int w = Mathf.Max(2, Mathf.RoundToInt(cr.Logo.width * CreditWidth));
                int h = Mathf.Max(2, Mathf.RoundToInt(w * (float)cr.Pic.mainTexture.height / cr.Pic.mainTexture.width));
                if (cr.Pic.width != w || cr.Pic.height != h) { cr.Pic.width = w; cr.Pic.height = h; }
                if (cr.Logs < 4 && (float.IsNaN(cr.Placed.x) || (cr.Placed - t.localPosition).sqrMagnitude > 1f))
                {
                    cr.Placed = t.localPosition; cr.Logs++;
                    Plugin.Log.LogInfo("Mod credit under '" + cr.Logo.name + "' placed at " + t.localPosition + " size " + w + "x" + h);
                }
            }
        }

        // A child of the logo, so it shows, fades and scales with it; sized in the
        // logo's own units (half its picture width) and placed by PlaceCredits.
        void AddCredit(UITexture logo)
        {
            Texture2D tex = CreditTexture();
            if (tex == null) return;
            GameObject g = new GameObject("CoopCredit");
            g.layer = logo.gameObject.layer;
            g.transform.parent = logo.transform;
            g.transform.localRotation = Quaternion.identity;
            g.transform.localScale = Vector3.one;
            g.transform.localPosition = Vector3.zero;
            UITexture pic = g.AddComponent<UITexture>();
            pic.shader = logo.shader != null ? logo.shader : Shader.Find("Unlit/Transparent Colored");
            pic.mainTexture = tex;
            pic.pivot = UIWidget.Pivot.Top;
            pic.depth = logo.depth + 1;
            credits.Add(new Credit { Pic = pic, Logo = logo });
            Plugin.Log.LogInfo("Mod credit added under the title logo '" + logo.name + "' (" + logo.width + "x" + logo.height + "), picture " + tex.width + "x" + tex.height + " shader " + (pic.shader != null ? pic.shader.name : "-"));
            PlaceCredits();
        }

        void ShowModVersion()
        {
            if (versionLabel == null) return;
            versionLabel.text = gameVersionText + Lang.T(" / Co-op mod ", " / Co-op-Mod ") + Plugin.Version;
        }

        // The loading screen (InterfaceManager.panels[10]) of the game scene
        // has its own smaller title logo: the same credit under it.
        void BuildLoadingCredit()
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null || im.panels == null || im.panels.Length <= 10 || im.panels[10] == null) return;
            loadingCreditBuilt = true;
            UITexture logo = FindLogo(im.panels[10].transform, false);
            if (logo == null) { Plugin.Log.LogWarning("Loading screen: no title logo for the mod credit"); return; }
            AddCredit(logo);
        }

        void BuildTopButton()
        {
            UIButton journal = FindButton("OpenJournal"), settings = FindButton("OpenPauseMenu");
            if (journal == null || settings == null) { sceneSince = Time.realtimeSinceStartup; return; }
            Vector3 step = settings.transform.localPosition - journal.transform.localPosition;
            topButton = Clone(settings, "Coop", step, "F8", "F8", "OnTopCoopButton");
            // "Other players": the game's own helmet icon twice, a second, slightly
            // smaller and darker one behind it to the upper left (the atlas has no
            // group icon). Untinted, so it keeps the gold of the other bar icons.
            UIButton b = topButton.GetComponent<UIButton>();
            b.normalSprite = "UpperPanelCharIcon";
            UISprite sp = b.tweenTarget != null ? b.tweenTarget.GetComponent<UISprite>() : null;
            if (sp == null) sp = topButton.GetComponentInChildren<UISprite>(true);
            if (sp != null)
            {
                sp.spriteName = "UpperPanelCharIcon";
                sp.MakePixelPerfect();
                GameObject back = new GameObject("CoopIconBack");
                back.layer = sp.gameObject.layer;
                back.transform.parent = sp.transform;
                back.transform.localScale = Vector3.one;
                back.transform.localRotation = Quaternion.identity;
                back.transform.localPosition = new Vector3(-sp.width * 0.58f, sp.height * 0.14f, 0f);
                UISprite bs = back.AddComponent<UISprite>();
                bs.atlas = sp.atlas;
                bs.spriteName = "UpperPanelCharIcon";
                bs.width = Mathf.RoundToInt(sp.width * 0.82f);
                bs.height = Mathf.RoundToInt(sp.height * 0.82f);
                bs.pivot = sp.pivot;
                bs.depth = sp.depth - 1;
                bs.color = new Color(0.62f, 0.58f, 0.5f, 1f);
                Plugin.Log.LogInfo("Upper bar: co-op icon " + sp.width + "x" + sp.height + " depth " + sp.depth + ", back " + bs.width + "x" + bs.height + " depth " + bs.depth);
            }
            // Make the bar's background (UpperButtons/Sprite, centred) one
            // button wider and keep the row centred on it.
            Transform bar = settings.transform.parent;
            foreach (UISprite bg in bar.GetComponentsInChildren<UISprite>(true))
            {
                if (bg.transform.parent != bar || bg.spriteName != "UpperPanel") continue;
                bg.width += Mathf.RoundToInt(step.x);
                Plugin.Log.LogInfo("Upper bar: background depth " + bg.depth + " at " + bg.transform.localPosition + " pivot " + bg.pivot + " width " + bg.width + ", bar at " + bar.localPosition + ", step " + step.x);
                // Only the icon row: other buttons of the bar (the "+" under its
                // middle notch) stay centred.
                foreach (UIButton ub in bar.GetComponentsInChildren<UIButton>(true))
                {
                    bool row = ub.transform.parent == bar && Mathf.Abs(ub.transform.localPosition.y - settings.transform.localPosition.y) < 2f;
                    Plugin.Log.LogInfo("Upper bar: " + ub.name + " at " + ub.transform.localPosition + (row ? " (row, moved)" : " (kept)"));
                    if (!row) continue;
                    ub.transform.localPosition -= new Vector3(step.x / 2f, 0f, 0f);
                    // The skill-points "+" hangs under Skills, which sat right under the
                    // bar's middle notch: keep it there instead of moving it with B.
                    HashSet<Transform> hang = new HashSet<Transform>();
                    foreach (UISprite s in ub.GetComponentsInChildren<UISprite>(true))
                        if (s.spriteName == "WindowFrameSmall" && s.transform != ub.transform) hang.Add(s.transform.parent == ub.transform ? s.transform : s.transform.parent);
                    foreach (Transform h in hang)
                    {
                        h.localPosition += new Vector3(step.x / 2f / Mathf.Max(0.0001f, ub.transform.localScale.x), 0f, 0f);
                        Plugin.Log.LogInfo("Upper bar: kept " + ub.name + "/" + h.name + " under the notch");
                    }
                }
                break;
            }
            Plugin.Log.LogInfo("Upper bar: co-op button added");
        }

        // After a language switch: our own button texts.
        public void Relabel()
        {
            for (int i = 0; i < ownLabels.Count; i++)
                if (ownLabels[i] != null) ownLabels[i].text = Lang.T(ownTexts[i].Key, ownTexts[i].Value);
            ShowModVersion();
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
            ChatSync chat = ChatSync.Instance;
            if (chat != null && plugin.CoopActive)
            {
                chat.Draw(sw, Screen.height / scale, panel);
                if (ChatSync.Typing)
                {
                    Rect a = chat.PanelRect;
                    panelOnScreen = new Rect(a.x * scale, a.y * scale, a.width * scale, a.height * scale);
                }
            }
            if (Note.Length > 0)
                GameSkin.Plate(new Rect(sw / 2f - 300f, 90f, 600f, 60f), Note, note);
            if (JoinOpen) DrawJoin(sw, scale);
            if (MenuOpen) DrawMenu(sw, scale);
            if (HostOpen) DrawHost(sw, scale);
            PartySync party = PartySync.Instance;
            if (party != null)
            {
                party.DrawFrames(panel);
                if (party.Open)
                {
                    party.DrawPanel(sw, panel);
                    Rect a = party.PanelRect;
                    panelOnScreen = new Rect(a.x * scale, a.y * scale, a.width * scale, a.height * scale);
                }
            }
            if (SessionStats.Open && plugin.CoopActive)
            {
                SessionStats.Draw(sw, panel);
                Rect a = SessionStats.PanelRect;
                panelOnScreen = new Rect(a.x * scale, a.y * scale, a.width * scale, a.height * scale);
            }
            if (Emotes.Open && PlayerScript.instance != null)
            {
                Emotes.Draw(sw, Screen.height / scale, panel);
                Rect a = Emotes.PanelRect;
                panelOnScreen = new Rect(a.x * scale, a.y * scale, a.width * scale, a.height * scale);
            }
            if (StatueMenu.Instance.Open && PlayerScript.instance != null)
            {
                StatueMenu.Instance.DrawPanel(sw, Screen.height / scale, panel);
                Rect a = StatueMenu.Instance.PanelRect;
                panelOnScreen = new Rect(a.x * scale, a.y * scale, a.width * scale, a.height * scale);
            }
            TradeSync trade = TradeSync.Instance;
            if (trade != null && trade.Open)
            {
                trade.DrawPanel(sw, panel);
                Rect a = trade.PanelRect;
                panelOnScreen = new Rect(a.x * scale, a.y * scale, a.width * scale, a.height * scale);
            }
        }

        void DrawJoin(float sw, float scale)
        {
            List<LanDiscovery.Found> lan = lanSearch != null ? lanSearch.Games() : new List<LanDiscovery.Found>();
            float w = 400f, h = 230f + (Mathf.Max(1, hosting.Count) + Mathf.Max(1, lan.Count)) * 30f;
            Rect area = new Rect(sw - w - 30f, 200f, w, h);
            panelOnScreen = new Rect(area.x * scale, area.y * scale, area.width * scale, area.height * scale);
            GameSkin.Window(area, Lang.T("Join a friend's co-op game", "Co-op-Spiel eines Freundes beitreten"), panel);
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 28f, area.width - 20f, area.height - 34f));
            GameSkin.LLabel("<b>" + Lang.T("Steam friends", "Steam-Freunde") + "</b>");
            if (hosting.Count == 0) GameSkin.LLabel(Lang.T("No Steam friend is hosting right now.", "Gerade hostet kein Steam-Freund."));
            foreach (FriendInfo f in hosting)
            {
                GUILayout.BeginHorizontal();
                GameSkin.LLabel(f.Name, GUILayout.Width(250f));
                if (GameSkin.LButton(Lang.T("Join", "Beitreten"), GUILayout.Width(110f))) { JoinOpen = false; plugin.CoopJoin(f.Lobby, f.Name); }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8f);
            GameSkin.LLabel("<b>" + Lang.T("Home network (LAN, no Steam servers)", "Heimnetz (LAN, ohne Steam-Server)") + "</b>");
            if (lan.Count == 0) GameSkin.LLabel(Lang.T("Searching ... no game found yet.", "Suche ... noch kein Spiel gefunden."));
            foreach (LanDiscovery.Found g in lan)
            {
                GUILayout.BeginHorizontal();
                bool sameVersion = g.Protocol == Net.ProtocolVersion;
                GameSkin.LLabel(g.Name + "  (" + g.Address + ")" + (sameVersion ? "" : Lang.T("  other version!", "  andere Version!")), GUILayout.Width(250f));
                GUI.enabled = sameVersion;
                if (GameSkin.LButton(Lang.T("Join", "Beitreten"), GUILayout.Width(110f))) { JoinOpen = false; plugin.LanJoin(g.Address); }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GameSkin.LLabel(Lang.T("Address:", "Adresse:"), GUILayout.Width(70f));
            lanAddress = GameSkin.LTextField(lanAddress, 40, GUILayout.Width(175f));
            if (GameSkin.LButton(Lang.T("Join", "Beitreten"), GUILayout.Width(110f)) && lanAddress.Trim().Length > 0) { JoinOpen = false; plugin.LanJoin(lanAddress); }
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            if (GameSkin.LButton(Lang.T("Close", "Schließen"))) JoinOpen = false;
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
            GameSkin.Window(area, Lang.T("Host a co-op game", "Co-op-Spiel hosten"), panel);
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 28f, area.width - 20f, area.height - 34f));
            if (GameSkin.LButton(Lang.T("Via Steam (invite friends, also over the internet)", "Über Steam (Freunde einladen, auch übers Internet)"), GUILayout.Height(34f)))
            { HostOpen = false; plugin.MenuSteamHost(); }
            if (GameSkin.LButton(Lang.T("In the home network (LAN, no Steam servers)", "Im Heimnetz (LAN, ohne Steam-Server)"), GUILayout.Height(34f)))
            { HostOpen = false; plugin.LanHost(); }
            GameSkin.LLabel(Lang.T("Then load or start your game as usual.", "Danach wie gewohnt dein Spiel laden oder starten."));
            GUILayout.FlexibleSpace();
            if (GameSkin.LButton(Lang.T("Close", "Schließen"))) HostOpen = false;
            GUILayout.EndArea();
        }

        void DrawMenu(float sw, float scale)
        {
            Vector2 at = TopButtonOnScreen() / scale;
            // Height from the content (status lines + one row per button).
            int buttons = 1;
            if (plugin.CoopActive) buttons += (plugin.CoopCanInvite ? 1 : 0) + (PartySync.Instance != null ? 1 : 0) + (TradeSync.Instance != null ? 1 : 0) + (ChatSync.Instance != null ? 1 : 0) + (GoldShare.Instance != null ? 1 : 0) + 2;
            else buttons += 3;
            float w = 300f;
            float h = 44f + GameSkin.LabelStyle.CalcHeight(new GUIContent(plugin.CoopStatus()), w - 28f) + buttons * 34f + 10f;
            Rect area = new Rect(Mathf.Clamp(at.x - w / 2f, 10f, sw - w - 10f), at.y + 30f, w, h);
            panelOnScreen = new Rect(area.x * scale, area.y * scale, area.width * scale, area.height * scale);
            GameSkin.Window(area, "Gedonia Co-op " + Plugin.Version, panel);
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 26f, area.width - 20f, area.height - 32f));
            GameSkin.LLabel(plugin.CoopStatus());
            if (plugin.CoopActive)
            {
                if (plugin.CoopCanInvite && GameSkin.LButton(Lang.T("Invite friends", "Freunde einladen"))) { MenuOpen = false; plugin.OpenInvite(); }
                if (PartySync.Instance != null)
                {
                    int inv = PartySync.Instance.InvitationCount;
                    string label = Lang.T("Group", "Gruppe") + (inv > 0 ? Lang.T(" (" + inv + " invitation)", " (" + inv + " Einladung)") : "");
                    if (GameSkin.LButton(label)) { MenuOpen = false; PartySync.Instance.Open = true; }
                }
                if (TradeSync.Instance != null)
                {
                    int asks = TradeSync.Instance.AskCount;
                    string label = Lang.T("Trade", "Handeln") + (asks > 0 ? Lang.T(" (" + asks + " request)", " (" + asks + " Anfrage)") : "");
                    if (GameSkin.LButton(label)) { MenuOpen = false; TradeSync.Instance.Open = true; }
                }
                if (ChatSync.Instance != null && GameSkin.LButton(Lang.T("Chat (Enter)", "Chat (Enter)"))) { MenuOpen = false; ChatSync.Instance.RequestOpen(); }
                if (GameSkin.LButton(Lang.T("Session statistics", "Sitzungsstatistik"))) { MenuOpen = false; SessionStats.Open = true; }
                if (GoldShare.Instance != null)
                {
                    // The host decides; the others see the rule.
                    string state = GoldShare.Active ? Lang.T("on", "an") : Lang.T("off", "aus");
                    if (GoldShare.Instance.Net.IsHost) { if (GameSkin.LButton(Lang.T("Share loot gold: ", "Beute-Gold teilen: ") + state)) GoldShare.Instance.Toggle(); }
                    else GameSkin.LLabel(Lang.T("Loot gold is shared: ", "Beute-Gold wird geteilt: ") + state + Lang.T(" (host)", " (Host)"));
                }
                if (GameSkin.LButton(Lang.T("Leave co-op", "Co-op verlassen"))) { MenuOpen = false; plugin.CoopLeave(); }
            }
            else
            {
                if (GameSkin.LButton(Lang.T("Host via Steam (friends)", "Über Steam hosten (Freunde)"))) { MenuOpen = false; plugin.CoopHost(); }
                if (GameSkin.LButton(Lang.T("Host in the home network (LAN)", "Im Heimnetz hosten (LAN)"))) { MenuOpen = false; plugin.LanHost(); }
                if (GameSkin.LButton(Lang.T("Join ...", "Beitreten ..."))) { MenuOpen = false; JoinOpen = true; }
            }
            if (GameSkin.LButton(Lang.T("Close", "Schließen"))) MenuOpen = false;
            GUILayout.EndArea();
        }
    }
}
