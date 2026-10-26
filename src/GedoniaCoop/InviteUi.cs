using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GedoniaCoop
{
    // In-game Steam invites that don't need the overlay (which doesn't open
    // when the game is started outside Steam):
    //   host:   F8 opens a list of online friends with Invite buttons
    //           (ISteamMatchmaking.InviteUserToLobby, a normal Steam invite);
    //   friend: an incoming invite shows a prompt, F9 joins, F10 ignores.
    // While the panel is open the game behaves as if one of its own menus were
    // open (no attacks, camera still, cursor free), and the game's UI ignores
    // clicks over the panel.
    public class InviteUi
    {
        public bool Open;
        public static bool BlocksGame;
        List<FriendInfo> friends = new List<FriendInfo>();
        float nextRefresh;
        Vector2 scroll;
        string note = "";
        bool savedCursor, savedVisible;
        CursorLockMode savedLock;
        Rect panelOnScreen; // GUI coordinates (y down), unscaled
        bool blockingNgui;
        public int ClicksShielded;
        public static Vector3? TestMouse; // test-only stand-in for the mouse position

        public void Toggle(SteamTransport t)
        {
            if (Open) Close();
            else if (t != null && t.IsHost && t.Lobby != 0)
            {
                Open = true;
                BlocksGame = true;
                nextRefresh = 0f;
                note = "";
                savedVisible = Cursor.visible;
                savedLock = Cursor.lockState;
                savedCursor = true;
                Plugin.Log.LogInfo("Invite panel opened");
            }
        }

        // Test-only: open the panel for a while, whatever the transport.
        public void ForceOpen()
        {
            if (Open) return;
            Open = true;
            BlocksGame = true;
            savedVisible = Cursor.visible;
            savedLock = Cursor.lockState;
            savedCursor = true;
            Plugin.Log.LogInfo("Invite panel opened (test)");
        }

        public void Close()
        {
            if (!Open) return;
            Open = false;
            BlocksGame = false;
            ShieldNgui(false);
            if (savedCursor) { Cursor.visible = savedVisible; Cursor.lockState = savedLock; savedCursor = false; }
            Plugin.Log.LogInfo("Invite panel closed");
        }

        public void Update(SteamTransport t, float now)
        {
            if (t == null) { Close(); return; }
            if (Input.GetKeyDown(KeyCode.F9) && t.InviteLobby != 0) t.AcceptInvite();
            if (Input.GetKeyDown(KeyCode.F10) && t.InviteLobby != 0) t.DeclineInvite();
            if (!Open) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            // The game hides and locks the cursor during play; keep it usable.
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            ShieldNgui(IsOverPanel(TestMouse.HasValue ? TestMouse.Value : Input.mousePosition));
            if (blockingNgui && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))) ClicksShielded++;
            if (now >= nextRefresh)
            {
                nextRefresh = now + 2f;
                friends = t.Api.OnlineFriends();
            }
        }

        // Input.mousePosition has y up, GUI rects y down.
        public bool IsOverPanel(Vector3 mouse)
        {
            return Open && panelOnScreen.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
        }

        public Vector2 PanelCenter { get { return panelOnScreen.center; } }

        void ShieldNgui(bool on)
        {
            if (on == blockingNgui) return;
            blockingNgui = on;
            UICamera.ignoreAllEvents = on;
        }

        // Same path as clicking a row's Invite button.
        public bool Invite(SteamTransport t, ulong friendId)
        {
            foreach (FriendInfo f in friends.Count > 0 ? friends : t.Api.OnlineFriends())
            {
                if (f.Id != friendId) continue;
                bool ok = t.InviteFriend(f);
                note = ok ? Lang.T("Invite sent to " + f.Name + ".", "Einladung an " + f.Name + " gesendet.") : Lang.T("Steam refused the invite to " + f.Name + ".", "Steam hat die Einladung an " + f.Name + " abgelehnt.");
                return ok;
            }
            note = Lang.T("That friend is not online.", "Dieser Freund ist nicht online.");
            return false;
        }

        GUIStyle promptStyle, statusStyle, panelStyle;
        Texture2D solid;

        void EnsureStyles()
        {
            if (promptStyle != null) return;
            solid = new Texture2D(1, 1);
            solid.SetPixel(0, 0, new Color(0.08f, 0.06f, 0.04f, 0.92f));
            solid.Apply();
            promptStyle = new GUIStyle(GUI.skin.label);
            promptStyle.alignment = TextAnchor.MiddleCenter;
            promptStyle.normal.textColor = Color.white;
            promptStyle.normal.background = solid;
            promptStyle.richText = true;
            statusStyle = new GUIStyle(GUI.skin.label);
            statusStyle.fontSize = 15;
            statusStyle.wordWrap = true;
            statusStyle.richText = true;
            statusStyle.normal.textColor = new Color(1f, 0.95f, 0.85f);
            statusStyle.normal.background = solid;
            statusStyle.padding = new RectOffset(8, 8, 4, 4);
            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = solid;
        }

        // IMGUI's default font is tiny on large screens; scale with height.
        public static float Scale { get { return Mathf.Max(1f, Screen.height / 900f); } }

        public void Draw(SteamTransport t, string status, CoopUi coop)
        {
            EnsureStyles();
            float scale = Scale;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float sw = Screen.width / scale, sh = Screen.height / scale;

            if (status != null)
            {
                // Bottom left, left of the action bar (which starts near 29 %
                // of the width); long text wraps upwards.
                GUIContent c = new GUIContent(status);
                float w = Mathf.Min(statusStyle.CalcSize(c).x + 2f, sw * 0.28f);
                float h = statusStyle.CalcHeight(c, w);
                GUI.Label(new Rect(8f, sh - h - 8f, w, h), c, statusStyle);
            }
            if (RespawnStones.Banner.Length > 0)
            {
                promptStyle.fontSize = 20;
                GUI.Label(new Rect(sw / 2f - 300f, sh * 0.72f, 600f, 56f), RespawnStones.Banner, promptStyle); // below the death screen's buttons
            }
            if (t != null && t.InviteLobby != 0)
            {
                promptStyle.fontSize = 16;
                string msg = "<b>" + t.InviteFromName + "</b> invited you to Gedonia co-op\n<b>F9</b> join      <b>F10</b> ignore";
                GUI.Label(new Rect(sw / 2f - 230f, sh * 0.16f, 460f, 58f), msg, promptStyle);
            }
            if (t != null && Open) DrawPanel(t, sw, sh, scale);
            if (coop != null) coop.Draw(scale, panelStyle);
            GUI.matrix = old;
        }

        void DrawPanel(SteamTransport t, float sw, float sh, float scale)
        {
            float w = 380f, h = Mathf.Min(120f + friends.Count * 28f, sh - 80f);
            Rect area = new Rect(sw - w - 20f, 60f, w, h);
            panelOnScreen = new Rect(area.x * scale, area.y * scale, area.width * scale, area.height * scale);
            GUI.Box(area, "", panelStyle); // opaque: the quest tracker sits behind it
            GUI.Box(area, Lang.T("Invite friends to co-op   (Esc closes)", "Freunde zu Co-op einladen   (Esc schließt)"));
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 26f, area.width - 20f, area.height - 32f));
            if (friends.Count == 0) GUILayout.Label(Lang.T("No Steam friends online.", "Keine Steam-Freunde online."));
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (FriendInfo f in friends)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(f.Name + (f.InGedonia ? Lang.T("   (playing Gedonia)", "   (spielt Gedonia)") : ""), GUILayout.Width(250f));
                bool invited = t.Invited.Contains(f.Id);
                if (GUILayout.Button(invited ? Lang.T("Invite again", "Erneut") : Lang.T("Invite", "Einladen"), GUILayout.Width(90f))) Invite(t, f.Id);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (note.Length > 0) GUILayout.Label(note);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Lang.T("Steam overlay", "Steam-Overlay"))) t.Api.OpenInviteDialog(t.Lobby);
            if (GUILayout.Button(Lang.T("Copy lobby id", "Lobby-ID kopieren")))
            {
                GUIUtility.systemCopyBuffer = t.Lobby.ToString();
                note = Lang.T("Lobby id copied. A friend can join with -coop-connect steam:", "Lobby-ID kopiert. Ein Freund kann mit -coop-connect steam:") + t.Lobby + Lang.T("", " beitreten.");
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // Clicks on the panel are mouse buttons, which the game also reads as
        // "Attack" / "Secondary" (and queues them for after the menu closes).
        [HarmonyPatch(typeof(vp_Input), "GetButton")]
        static class HeldPatch
        {
            static bool Prefix(string button, ref bool __result) { return Pass(button, ref __result); }
        }

        [HarmonyPatch(typeof(vp_Input), "GetButtonDown")]
        static class DownPatch
        {
            static bool Prefix(string button, ref bool __result) { return Pass(button, ref __result); }
        }

        static bool Pass(string button, ref bool __result)
        {
            if (!(BlocksGame || CoopUi.BlocksGame) || (button != "Attack" && button != "Secondary")) return true;
            __result = false;
            return false;
        }

        // The game checks this before attacks, camera turns and cursor
        // locking; with the panel open the answer is "a menu is open".
        [HarmonyPatch(typeof(InterfaceManager), "AnyPanelIsActive")]
        static class MenuOpenPatch
        {
            static void Postfix(ref bool __result)
            {
                if (BlocksGame || CoopUi.BlocksGame) __result = true;
            }
        }
    }
}
