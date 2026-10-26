using System.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GedoniaCoop
{
    // Command line (with none, the game listens for Steam invites; F7 hosts, F8 opens
    // the in-game friends list to invite, F9 accepts an invite):
    //   -coop-host steam               host a friends-only Steam lobby (no port forwarding)
    //   -coop-connect steam[:<lobby>]  join a Steam lobby by id, or wait for an invite
    //   +connect_lobby <lobby>         added by Steam when launching from an invite
    //   -coop-host <port>              host over plain UDP (needs an open port)
    //   -coop-bind <ip>                UDP host only: listen address (127.0.0.1 for local tests)
    //   -coop-connect <ip>:<port>      join a UDP host
    //   -coop-name <name>              label shown above you on other screens
    // Test-only switches (do nothing unless passed):
    //   -coop-mock-steam <n>           fake Steam between local copies (player n; see MockSteam)
    //   -coop-mock-invite <n>          25 s in, act as if player n's invite was accepted
    //   -coop-mock-loss <0..1>         drop this share of unreliable mock-Steam packets
    //   -coop-steam-selftest 1         host only: read the lobby back, open the invite dialog, quit
    //   -coop-census 1                 every 5 s log all enemies, their scenes and ids
    //   -coop-bot line                 walk straight ahead (for streaming checks)
    //   -coop-bot fight                walk to the nearest hostile enemy and fight it
    //   -coop-test-regen 1             refill health below half (damage still arrives and is logged)
    //   -coop-udp-loss <0..1>          drop this share of ALL UDP packets on send (data and acks)
    //   -coop-bot-delay <s>            seconds in world before the line bot starts walking (default 20)
    //   -coop-test-reload <s>          after dying, reload coop-test.es3 this many seconds later
    //   -coop-test-hp <n>              fight bot: set health to n once when the fight starts
    //   -coop-test-charisma <n>        10 s in world, set charisma (recruiting needs >= 2)
    //   -coop-test-strong <x>          fight bot: melee damage factor (so the bot gets kills itself)
    //   -coop-test-clock <steps>       e.g. shift:5@30,sleep:8@45,weather@55 (shift = like another save's
    //                                  time, sleep = the bed's SetTime call, weather = change it here)
    //   -coop-test-loot <steps>        e.g. open@40,take1@44,takeall@48 on the nearest chest (s in world)
    //   -coop-test-look <steps>        e.g. female@20,helmet@30,hidehelmet@40,bow@60 (see TestLook.Step)
    //   -coop-test-events 1           every 5 s log the nearest world event's state
    //   -coop-test-story <steps>       e.g. answer:TakeHorseQuest@40,unsynced:Lost_knowledge@50 (s in world;
    //                                  also gotopos:x;y;z and goto:<world object id> to teleport;
    //                                  answer = same path as a dialogue answer, unsynced = behind the sync's back)
    //   -coop-off 1                    no network at all (no Steam either)
    //   -coop-mock-friends <a,b>       mock players listed as online friends
    //   -coop-test-invite <n>          host: open the F8 panel and press Invite for mock player n
    //   -coop-test-accept <s>          friend: press F9 s seconds after an invite prompt appears
    //   -coop-autoload <save.es3>      skip the menu and load this save
    //   -coop-bot circle               walk the local player in a circle, stopping to attack
    //   -coop-shots <n>                save n screenshots, 5 s apart, once in game
    //   -coop-dump 1                   log the player's object tree and Animator once
    //   -coop-animlog 1                log every local Animator state change
    //   -coop-look alt                 10 s in, switch this copy to a different hair/head/colours/skin
    //   -coop-gear-swap <s>            s seconds in, equip another chest piece and weapon
    [BepInPlugin(Guid, "Gedonia Coop", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gedonia.coop";
        public const string Version = "0.6.0";
        const float SendInterval = 0.05f;
        const float AppearanceInterval = 1f;
        const float PeerTimeout = 5f;

        internal static ManualLogSource Log;

        Net net;
        EnemySync enemies;
        Net enemiesNet;
        WorldEventSync events;
        Net eventsNet;
        float nextEventReport;
        WorldEventBase dumpedEvent;
        StorySync story;
        Net storyNet;
        WorldStateSync worldState;
        Net worldStateNet;
        LootSync loot;
        Net lootNet;
        WorldClockSync clock;
        TravellerSync travellers;
        SoundSync sounds;
        DialogueSync dialogue;
        Net dialogueNet;
        Net soundsNet;
        Net travellersNet;
        ProjectileSync projectiles;
        readonly RespawnStones respawn = new RespawnStones();
        ExpeditionSync expeditions;
        Net expeditionsNet;
        Net projectilesNet;
        Net clockNet;
        readonly List<KeyValuePair<float, string>> clockTests = new List<KeyValuePair<float, string>>();
        readonly List<KeyValuePair<float, string>> lookTests = new List<KeyValuePair<float, string>>();
        float nextClockReport;
        readonly List<KeyValuePair<float, string>> lootTests = new List<KeyValuePair<float, string>>();
        Container lootTarget;
        int lootTargetId;
        readonly List<Vector3> tourStops = new List<Vector3>();
        readonly HashSet<string> seenChests = new HashSet<string>();
        float nextTourStop;
        float nextLootReport;
        // Test-only story steps: (seconds in world, kind, name)
        readonly List<KeyValuePair<float, string>> storyTests = new List<KeyValuePair<float, string>>();
        float nextQuestReport;
        ISteamApi steam;
        SteamTransport steamTransport;
        bool selfTest, census, testRegen;
        float nextRegen;
        float nextCensus;
        int selfTestStep;
        float selfTestAt;
        readonly InviteUi ui = new InviteUi();
        CoopUi coopUi;
        readonly MapMarkers mapMarkers = new MapMarkers();
        int testInviteFriend, testInviteStep;
        float testInviteAt;
        float testAcceptAfter = -1f, inviteSeenAt = -1f;
        readonly PlayerState local = new PlayerState();
        static Plugin self;

        // Whether a player is in the same hub world as we are (Gedonia,
        // NorthernKingdoms, UnchartedIslands: separate scenes whose coordinates
        // overlap). Unknown players count as elsewhere.
        public static string LocalName { get { return self != null ? self.local.Name : "?"; } }
        public static CoopUi Ui { get { return self != null ? self.coopUi : null; } }

        public static bool PlayerPosition(int playerId, out Vector3 pos)
        {
            pos = Vector3.zero;
            Puppet p;
            if (self == null || !self.puppets.TryGetValue(playerId, out p)) return false;
            pos = p.Position;
            return true;
        }

        public static bool SameWorld(int playerId)
        {
            if (self == null || self.local.World.Length == 0) return false;
            Puppet p;
            if (!self.puppets.TryGetValue(playerId, out p) || p.Latest == null) return false;
            return p.Latest.Scene == self.local.Scene && p.Latest.World == self.local.World;
        }
        readonly Dictionary<int, Puppet> puppets = new Dictionary<int, Puppet>();
        readonly Dictionary<int, Appearance> looks = new Dictionary<int, Appearance>();
        float nextSend, nextStatus, nextAppearance;

        Animator localAnim;
        List<AnimatorControllerParameter> localParams;
        int[] lastLocalStates = new int[0];
        readonly List<string> localSigEntries = new List<string>();
        readonly TravelMeter travel = new TravelMeter();
        bool testLook;
        float gearSwapAt = -1f;
        float testUiAt = -1f, nextUiCheck;
        bool testUiOpened, testUiClosed, testUiShot;
        bool lookDone, gearDone;
        float nextTotals;
        float inWorldSince = -1f;
        bool appearanceFallbackDone, dumped;
        bool dump, animLog;

        string autoload;
        bool autoloadDone;
        float sceneLoadedAt;

        string bot;
        Vector3 botCenter;
        float botStart = -1f;
        float botAngle;
        int botAttacks;
        int botTarget;
        float botNextAttack, botNextLog;
        float botDelay = 20f;
        bool botWalkLogged;
        float testReloadAfter = -1f, deadSince = -1f;
        int testHp;
        bool testHpDone;
        int testCharisma = -1;
        float testStrong;
        bool reloaded;
        float botAttackQueued = -10f;

        int shotsLeft;
        float nextShot = -1f;
        int shotIndex;

        void Awake()
        {
            Log = Logger;
            self = this;
            coopUi = new CoopUi(this);

            // Safety net for the developer's test copies (marked with a
            // coop-test-copy file): one that still shares the real game's
            // product name would read and write the real saves and settings.
            // scripts/set-product-name.ps1 gives each copy its own name. In a
            // player's normal install the real saves are the point.
            bool testCopy = File.Exists(Path.Combine(Paths.GameRootPath, "coop-test-copy"));
            if (testCopy && Path.GetFileName(Application.persistentDataPath.TrimEnd('/', '\\')) == "Gedonia")
            {
                Log.LogFatal("This copy uses the REAL save folder (" + Application.persistentDataPath + "). Quitting.");
                Application.Quit();
                enabled = false;
                return;
            }
            Log.LogInfo("Data folder: " + Application.persistentDataPath);

            new Harmony(Guid).PatchAll();
            DialogueSync.VoteSeconds = Config.Bind("Dialogue", "VoteSeconds", 10f, "Shared conversations: seconds after the first vote until the leader's vote counts (0 = only when everyone agrees).").Value;
            DialogueSync.StartIndexing();

            string[] args = Environment.GetCommandLineArgs();
            if (Arg(args, "-coop-vote-seconds") != null) DialogueSync.VoteSeconds = ParseFloat(Arg(args, "-coop-vote-seconds"), 10f);
            local.Id = new System.Random().Next(1, int.MaxValue);
            local.Name = Arg(args, "-coop-name") ?? "Player";
            nameFromArg = Arg(args, "-coop-name") != null;
            autoload = Arg(args, "-coop-autoload");
            bot = Arg(args, "-coop-bot");
            dump = Arg(args, "-coop-dump") != null;
            animLog = Arg(args, "-coop-animlog") != null;
            testLook = Arg(args, "-coop-look") == "alt";
            gearSwapAt = ParseFloat(Arg(args, "-coop-gear-swap"), -1f);
            testUiAt = ParseFloat(Arg(args, "-coop-test-ui"), -1f);
            selfTest = Arg(args, "-coop-steam-selftest") != null;
            census = Arg(args, "-coop-census") != null;
            botDelay = ParseFloat(Arg(args, "-coop-bot-delay"), 20f);
            testReloadAfter = ParseFloat(Arg(args, "-coop-test-reload"), -1f);
            int.TryParse(Arg(args, "-coop-test-hp") ?? "0", out testHp);
            if (!int.TryParse(Arg(args, "-coop-test-charisma") ?? "-1", out testCharisma)) testCharisma = -1;
            testStrong = ParseFloat(Arg(args, "-coop-test-strong"), 0f);
            UdpTransport.SimulatedLoss = ParseFloat(Arg(args, "-coop-udp-loss"), 0f);
            if (UdpTransport.SimulatedLoss > 0f) Log.LogWarning("Test: dropping " + (UdpTransport.SimulatedLoss * 100f).ToString("F0") + "% of all UDP packets on send");
            testRegen = Arg(args, "-coop-test-regen") != null;
            foreach (string step in (Arg(args, "-coop-test-clock") ?? "").Split(','))
            {
                int at = step.LastIndexOf('@');
                if (at > 0) clockTests.Add(new KeyValuePair<float, string>(ParseFloat(step.Substring(at + 1), 0f), step.Substring(0, at)));
            }
            foreach (string step in (Arg(args, "-coop-test-look") ?? "").Split(','))
            {
                int at = step.LastIndexOf('@');
                if (at > 0) lookTests.Add(new KeyValuePair<float, string>(ParseFloat(step.Substring(at + 1), 0f), step.Substring(0, at)));
            }
            foreach (string step in (Arg(args, "-coop-test-loot") ?? "").Split(','))
            {
                int at = step.LastIndexOf('@');
                if (at > 0) lootTests.Add(new KeyValuePair<float, string>(ParseFloat(step.Substring(at + 1), 0f), step.Substring(0, at)));
            }
            // "answer:TakeHorseQuest@40,unsynced:Lost_knowledge@50" (underscores = spaces)
            foreach (string step in (Arg(args, "-coop-test-story") ?? "").Split(','))
            {
                int at = step.LastIndexOf('@');
                if (at <= 0) continue;
                storyTests.Add(new KeyValuePair<float, string>(ParseFloat(step.Substring(at + 1), 0f), step.Substring(0, at).Replace('_', ' ')));
            }
            int.TryParse(Arg(args, "-coop-test-invite") ?? "0", out testInviteFriend);
            testAcceptAfter = ParseFloat(Arg(args, "-coop-test-accept"), -1f);
            int.TryParse(Arg(args, "-coop-shots") ?? "0", out shotsLeft);

            string hostArg = Arg(args, "-coop-host");
            string connect = Arg(args, "-coop-connect");
            string steamLobby = Arg(args, "+connect_lobby"); // Steam adds this when launching from an invite
            string mock = Arg(args, "-coop-mock-steam");
            if (mock != null)
            {
                List<int> mockFriends = new List<int>();
                foreach (string f in (Arg(args, "-coop-mock-friends") ?? "").Split(','))
                    if (f.Trim().Length > 0) mockFriends.Add(int.Parse(f.Trim()));
                MockSteam m = new MockSteam(int.Parse(mock), ParseFloat(Arg(args, "-coop-mock-loss"), 0f), mockFriends);
                string invite = Arg(args, "-coop-mock-invite");
                if (invite != null) m.ScheduleInvite(MockSteam.LobbyOf(int.Parse(invite)), 25f);
                steam = m;
            }
            else
            {
                steam = new RealSteam();
            }

            try
            {
                if (Arg(args, "-coop-off") != null)
                {
                    Log.LogInfo("-coop-off: no network");
                }
                else if (hostArg == "steam")
                {
                    StartSteam(true, 0);
                }
                else if (hostArg != null)
                {
                    string bind = Arg(args, "-coop-bind");
                    net = new Net(UdpTransport.Host(int.Parse(hostArg), bind != null ? System.Net.IPAddress.Parse(bind) : System.Net.IPAddress.Any));
                    Log.LogInfo("Hosting on UDP " + (bind ?? "*") + ":" + hostArg + " as '" + local.Name + "' id=" + local.Id);
                }
                else if (steamLobby != null || (connect != null && connect.StartsWith("steam")))
                {
                    // "-coop-connect steam" waits for an invite; "steam:<lobby>" joins that lobby.
                    string id = steamLobby ?? (connect.Length > 6 ? connect.Substring(6) : "0");
                    StartSteam(false, ulong.Parse(id));
                }
                else if (connect != null)
                {
                    int colon = connect.LastIndexOf(':');
                    net = new Net(UdpTransport.Connect(connect.Substring(0, colon), int.Parse(connect.Substring(colon + 1))));
                    Log.LogInfo("Connecting to " + connect + " as '" + local.Name + "' id=" + local.Id);
                }
                else
                {
                    // Nothing asked for: listen for Steam invites, and let F7 host.
                    StartSteam(false, 0);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Network start failed: " + e);
            }

            // Unity pauses unfocused windows; co-op peers must keep ticking.
            Application.runInBackground = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // Wall-clock stamp so events can be lined up across the two processes' logs.
        internal static string Stamp()
        {
            return DateTime.Now.ToString("HH:mm:ss.fff");
        }

        void StartSteam(bool host, ulong joinLobby)
        {
            if (net != null) net.Dispose();
            StopLanResponder();
            steamTransport = new SteamTransport(steam, host, joinLobby);
            steamTransport.LobbyReady += id =>
            {
                // Manual fallback for friends: the id is on screen, in the
                // clipboard and in a file (also read by the test launcher).
                try { GUIUtility.systemCopyBuffer = id.ToString(); } catch { }
                try { File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "coop-lobby.txt"), id.ToString()); } catch { }
            };
            net = new Net(steamTransport);
            Log.LogInfo((host ? "Hosting via Steam" : joinLobby != 0 ? "Joining Steam lobby " + joinLobby : "Listening for Steam invites") +
                " as '" + local.Name + "' id=" + local.Id);
        }

        // ---------- called by CoopUi (NGUI EventDelegates need public methods) ----------

        public bool CoopActive { get { return net != null && (steamTransport == null || steamTransport.IsHost || steamTransport.Joined); } }
        public bool CoopCanInvite { get { return steamTransport != null && steamTransport.IsHost && steamTransport.Lobby != 0; } }

        public List<FriendInfo> Friends() { return steam != null ? steam.OnlineFriends() : new List<FriendInfo>(); }

        public string CoopStatus()
        {
            if (!CoopActive) return Lang.T("Not in a co-op game.", "Kein Co-op-Spiel aktiv.");
            string s = net.Transport.Status + "\n" + Lang.T("Players here: ", "Spieler hier: ") + (puppets.Count + 1);
            if (steamTransport != null) s += "\n" + (steamTransport.IsHost ? Lang.T("You are the host.", "Du bist der Host.") : Lang.T("You joined a friend.", "Du bist einem Freund beigetreten."));
            if (LanActive) s += "\n" + (net.IsHost ? Lang.T("Home network. Your address: ", "Heimnetz. Deine Adresse: ") + LanDiscovery.OwnAddresses() : Lang.T("Home network: joined.", "Heimnetz: beigetreten."));
            return s;
        }

        public void CoopHost()
        {
            if (steamTransport != null && (steamTransport.IsHost || steamTransport.Joined)) return;
            if (net != null && steamTransport == null) return; // a UDP test session
            Log.LogInfo("Co-op button: hosting via Steam");
            StartSteam(true, 0);
        }

        public void CoopJoin(ulong lobby, string friend)
        {
            Log.LogInfo("Co-op button: joining " + friend + " (lobby " + lobby + ")");
            StartSteam(false, lobby);
            coopUi.ShowNote(Lang.T("Joining " + friend + " ... then load or start your character.",
                "Trete " + friend + " bei ... dann deinen Charakter laden oder neu starten."), 8f);
        }

        // Back to "listening for invites": the others' figures go away.
        public void CoopLeave()
        {
            Log.LogInfo("Co-op button: leaving the session");
            ui.Close();
            foreach (KeyValuePair<int, Puppet> kv in puppets)
            {
                kv.Value.Destroy();
                if (enemies != null) enemies.PlayerLeft(kv.Key);
            }
            puppets.Clear();
            looks.Clear();
            StartSteam(false, 0);
        }

        public void OpenInvite() { if (!ui.Open) ui.Toggle(steamTransport); }

        // ---------- home network (LAN): plain UDP, no Steam servers ----------

        LanDiscovery lanResponder;
        public bool LanActive { get { return net != null && net.Transport is UdpTransport; } }

        void StopLanResponder()
        {
            if (lanResponder != null) { lanResponder.Dispose(); lanResponder = null; }
        }

        public void LanHost()
        {
            if (LanActive && net.IsHost) return;
            Log.LogInfo("Co-op button: hosting in the home network on UDP " + LanDiscovery.GamePort);
            try
            {
                if (net != null) net.Dispose();
                steamTransport = null;
                StopLanResponder();
                net = new Net(UdpTransport.Host(LanDiscovery.GamePort, System.Net.IPAddress.Any));
                try { lanResponder = LanDiscovery.Respond(local.Name); }
                catch (Exception e) { Log.LogWarning("LAN search answers off: " + e.Message); }
                coopUi.ShowNote(Lang.T("Hosting in the home network. Your address: ", "Co-op im Heimnetz gehostet. Deine Adresse: ") + LanDiscovery.OwnAddresses(), 10f);
            }
            catch (Exception e)
            {
                Log.LogError("LAN host failed: " + e);
                coopUi.ShowNote(Lang.T("Could not host (port " + LanDiscovery.GamePort + " in use?)", "Hosten nicht möglich (Port " + LanDiscovery.GamePort + " belegt?)"), 8f);
                StartSteam(false, 0);
            }
        }

        public void LanJoin(string address)
        {
            address = (address ?? "").Trim();
            if (address.Length == 0) return;
            Log.LogInfo("Co-op button: joining " + address + " in the home network");
            try
            {
                if (net != null) net.Dispose();
                steamTransport = null;
                StopLanResponder();
                net = new Net(UdpTransport.Connect(address, LanDiscovery.GamePort));
                coopUi.ShowNote(Lang.T("Joining " + address + " ... then load or start your character.", "Trete " + address + " bei ... dann deinen Charakter laden oder neu starten."), 8f);
            }
            catch (Exception e)
            {
                Log.LogError("LAN join failed: " + e);
                coopUi.ShowNote(Lang.T("Address not found: ", "Adresse nicht gefunden: ") + address, 8f);
                StartSteam(false, 0);
            }
        }

        public void OnMenuCoopHost() { coopUi.HostOpen = !coopUi.HostOpen; coopUi.JoinOpen = false; }

        // Main menu, "Host co-op" -> "via Steam".
        public void MenuSteamHost()
        {
            CoopHost();
            coopUi.ShowNote(Lang.T("Co-op hosting started. Load or start a game, then invite friends with the Co-op button (F8).",
                "Co-op wird gehostet. Lade oder starte ein Spiel und lade dann Freunde über den Co-op-Knopf (F8) ein."), 8f);
        }

        public void OnMenuCoopJoin() { coopUi.JoinOpen = !coopUi.JoinOpen; coopUi.HostOpen = false; }

        public void OnTopCoopButton() { coopUi.MenuOpen = !coopUi.MenuOpen; if (ui.Open) ui.Close(); }

        public void OnMenuGerman() { Lang.German = true; coopUi.Relabel(); Translator.Refresh(); Log.LogInfo("Language: German"); }

        public void OnMenuGameLanguage() { if (!Lang.German) return; Lang.German = false; coopUi.Relabel(); Translator.Refresh(); Log.LogInfo("Language: game's own"); }

        void CheckHotkeys()
        {
            // F7: host a Steam lobby (when not already in a co-op session).
            if (Input.GetKeyDown(KeyCode.F7) && (steamTransport == null || (!steamTransport.IsHost && !steamTransport.Joined)) &&
                (net == null || steamTransport != null))
            {
                Log.LogInfo("F7: hosting via Steam");
                StartSteam(true, 0);
            }
            // F8: in-game friends list with Invite buttons (works without the overlay).
            // F8: the co-op menu (same as the upper bar's co-op button).
            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (ui.Open) ui.Close();
                else if (SceneManager.GetActiveScene().name == "MainMenu") coopUi.JoinOpen = !coopUi.JoinOpen;
                else coopUi.MenuOpen = !coopUi.MenuOpen;
            }
            ui.Update(steamTransport, Time.realtimeSinceStartup);
            coopUi.Tick(Time.realtimeSinceStartup);
        }

        // Test-only: drive the invite UI the way a player would.
        //   -coop-test-invite <n> (host): open the panel, press Invite for mock
        //     player n, screenshot the panel, close it.
        //   -coop-test-accept <s> (friend): when an invite prompt shows, screenshot
        //     it and press F9 after s seconds.
        void InviteTest(float now)
        {
            if (testInviteFriend > 0 && steamTransport != null && steamTransport.IsHost && steamTransport.Lobby != 0 && testInviteStep < 4)
            {
                if (testInviteStep == 0) { testInviteStep = 1; testInviteAt = now + 4f; }
                else if (now >= testInviteAt)
                {
                    if (testInviteStep == 1) { ui.Toggle(steamTransport); testInviteAt = now + 2f; }
                    else if (testInviteStep == 2) { ui.Invite(steamTransport, 900000000UL + (ulong)testInviteFriend); Shot("Host_invite_panel"); testInviteAt = now + 3f; }
                    else if (testInviteStep == 3) ui.Close();
                    testInviteStep++;
                }
            }
            if (testAcceptAfter >= 0f && steamTransport != null && steamTransport.InviteLobby != 0)
            {
                if (inviteSeenAt < 0f) { inviteSeenAt = now; Shot("Client_invite_prompt"); }
                else if (now - inviteSeenAt >= testAcceptAfter)
                {
                    Log.LogInfo("Test: pressing F9");
                    steamTransport.AcceptInvite();
                    inviteSeenAt = -1f;
                }
            }
        }

        void Shot(string name)
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "coop-shots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, name + ".png");
            ScreenCapture.CaptureScreenshot(file);
            Log.LogInfo(Stamp() + " Screenshot: " + file);
        }

        // Test-only (-coop-steam-selftest 1): once the lobby exists, read it back
        // from Steam, open the invite dialog as F8 does, then quit normally so
        // the lobby is left and rich presence cleared.
        void SteamSelfTest(float now)
        {
            if (!selfTest || steamTransport == null || !steamTransport.IsHost || steamTransport.Lobby == 0) return;
            if (selfTestStep == 0) { selfTestStep = 1; selfTestAt = now + 3f; return; }
            if (now < selfTestAt) return;
            RealSteam real = steam as RealSteam;
            switch (selfTestStep)
            {
                case 1:
                    Log.LogInfo(Stamp() + " Self-test read-back: " + (real != null ? real.Report(steamTransport.Lobby) : "mock"));
                    selfTestStep = 2; selfTestAt = now + 2f;
                    break;
                case 2:
                    Log.LogInfo(Stamp() + " Self-test: opening the invite dialog (same call as F8)");
                    steam.OpenInviteDialog(steamTransport.Lobby);
                    selfTestStep = 3; selfTestAt = now + 8f;
                    break;
                case 3:
                    Log.LogInfo(Stamp() + " Self-test: quitting");
                    selfTestStep = 4;
                    Application.Quit();
                    break;
            }
        }

        void OnGUI()
        {
            // The status lives in the co-op menu now; only a version clash is
            // worth a line on screen.
            string text = null;
            if (net != null && net.OtherVersion != 0)
                text = "<color=#ff8060><b>" + Lang.T("A player has a different mod version (protocol " + net.OtherVersion + ", ours " + Net.ProtocolVersion + "); everyone needs the same version.",
                    "Ein Spieler hat eine andere Mod-Version (Protokoll " + net.OtherVersion + ", unseres " + Net.ProtocolVersion + "); alle brauchen dieselbe Version.") + "</b></color>";
            ui.Draw(steamTransport, text, coopUi);
        }

        // Test-only expedition steps:
        //   exprank:<n>   set the expedition rank and roll the list (as the story does)
        //   explist       log the list
        //   expgo:dungeon teleport next to the first site whose last phase is a dungeon
        //   expstart      use the nearest site marker (GameManager.ActivateExpedition, as the interact key)
        //   expdungeon    raise and generate that site's dungeon and walk in (skipping phases 1-2)
        void TestExpedition(PlayerScript player, string step)
        {
            ExpeditionsManager em = ExpeditionsManager.instance;
            if (em == null) { Log.LogWarning("Test: no ExpeditionsManager"); return; }
            if (step.StartsWith("exprank:"))
            {
                em.currentRank = int.Parse(step.Substring(8));
                em.RefreshExpeditions();
                Log.LogInfo(Stamp() + " Test: expedition rank " + em.currentRank + ", rolled " + ExpeditionSync.Describe());
            }
            else if (step == "explist") Log.LogInfo(Stamp() + " [expcheck] " + ExpeditionSync.Describe());
            else if (step.StartsWith("exproll"))
            {
                // Roll until a site matches the pattern ("exproll:p0|p1|p2", each a
                // list a/b/c or * for the kinds the auto mode can always play).
                if (step.StartsWith("exproll:")) rollPattern = step.Substring(8);
                for (int k = 0; k < 1500 && AutoSite(em) < 0; k++) em.RefreshExpeditions();
                int i = AutoSite(em);
                Log.LogInfo(Stamp() + " Test: playable site for '" + rollPattern + "': " + (i >= 0 ? em.expeditions[i].savedId + " [" + string.Join("+", em.expeditions[i].eventsTypes) + "]" : "none") + " in " + ExpeditionSync.Describe());
            }
            else if (step.StartsWith("exppattern:")) rollPattern = step.Substring(11);
            else if (step == "expgo:auto")
            {
                int i = AutoSite(em);
                if (i < 0) { Log.LogWarning("Test: no playable site"); return; }
                Vector3 p = em.expeditions[i].spot + new Vector3(3f, 2f, 0f);
                player._char.enabled = false;
                player.transform.position = p;
                player._char.enabled = true;
                Log.LogInfo(Stamp() + " Test: at expedition " + em.expeditions[i].savedId + " [" + string.Join("+", em.expeditions[i].eventsTypes) + "] " + p.ToString("F0"));
            }
            else if (step == "expauto") { expAuto = true; badgesBefore = Badges(player); Log.LogInfo(Stamp() + " Test: expedition auto play on, badges=" + badgesBefore); }
            else if (step == "expprefabs")
            {
                // Which scripts the expedition objects carry (how their objectives work).
                foreach (string n in ("AncientDemonWarrior,BossBullDog,CatchGhosts,CollectableCrystal,CorruptedCrystals,DeactivatableAnomaly,DiggableDirt,DungeonEntrance," +
                    "EnragedSoul,EstrangedHallucination,ExplosiveBug,GatherTablets,GatherableTablet,GrowingCorruption,HallucinationSpawnedEvent,Imp,LostSoul," +
                    "MinableCrystals,OldVolflarWarrior,SpectralFireElemental,SpectralFlame,StandaloneTower0,SummoningPortal,TobyExpeditionScientist,UnstableAnomaly," +
                    "VoidSpawn,VoidTentacle,AncientSpider,GhostReaper").Split(','))
                {
                    GameObject g = Resources.Load<GameObject>("Expeditions/" + n);
                    if (g == null) { Log.LogInfo("[expprefab] " + n + ": not found"); continue; }
                    List<string> types = new List<string>();
                    foreach (MonoBehaviour mb in g.GetComponentsInChildren<MonoBehaviour>(true))
                        if (mb != null && !types.Contains(mb.GetType().Name)) types.Add(mb.GetType().Name);
                    Log.LogInfo("[expprefab] " + n + ": " + string.Join(",", types.ToArray()));
                }
            }
            else if (step == "expgo:dungeon")
            {
                int i = em.expeditions.FindIndex(e => !e.isSolved && e.eventsTypes != null && e.eventsTypes[2] == "Dungeon");
                if (i < 0) { Log.LogWarning("Test: no dungeon expedition in " + ExpeditionSync.Describe()); return; }
                Vector3 p = em.expeditions[i].spot + new Vector3(3f, 2f, 0f);
                player._char.enabled = false;
                player.transform.position = p;
                player._char.enabled = true;
                Log.LogInfo(Stamp() + " Test: at expedition " + em.expeditions[i].savedId + " " + p.ToString("F0"));
            }
            else if (step == "expstart")
            {
                GameObject best = null;
                foreach (GameObject g in em.createdActivations)
                    if (g != null && (best == null || Vector3.Distance(g.transform.position, player.transform.position) < Vector3.Distance(best.transform.position, player.transform.position))) best = g;
                if (best == null || Vector3.Distance(best.transform.position, player.transform.position) > 15f) { Log.LogWarning("Test: no site marker near"); return; }
                Log.LogInfo(Stamp() + " Test: using the site marker at " + best.transform.position.ToString("F0"));
                GameManager.instance.ActivateExpedition(best);
            }
            else if (step == "expdungeon")
            {
                foreach (ExpeditionsManager.Expedition e in em.expeditions)
                {
                    if (e.worldEvent == null || e.eventsTypes[2] != "Dungeon" || Vector3.Distance(e.spot, player.transform.position) > 30f) continue;
                    GameObject entrance = e.worldEvent.phases[2].activatedGameObjects[0];
                    entrance.SetActive(true);
                    StandaloneGeneratedDungeon d = entrance.transform.GetChild(1).GetChild(1).GetComponentInChildren<StandaloneGeneratedDungeon>();
                    if (d.dungeonGenerator.Root.transform.childCount < 1) d.Generate();
                    Log.LogInfo(Stamp() + " Test: into the dungeon of expedition " + e.savedId + " (seed " + d.dungeonGenerator.Seed + ") at " + d.generatedDungeonStart.ToString("F0"));
                    GameManager.instance.StartCoroutine(GameManager.instance.MoveToRoutine(d.generatedDungeonStart, Quaternion.identity));
                    return;
                }
                Log.LogWarning("Test: no running dungeon expedition near");
            }
        }

        bool expAuto;
        float nextExpAuto;
        int badgesBefore;

        // Phase kinds the auto mode can play: usable objects, destructible
        // objects, enemies placed in the event or spawned by its spawners.
        static readonly string[] AutoPhase0 = { "DeactivateAnomalies", "Digging", "MineCrystals" };
        static readonly string[] AutoPhase1 = { "DestroyCorruptedCrystals", "DestroySpectralFire", "VolflarPack", "DemonsPack", "GhostsPack", "AnimalsPack", "AnimalsPackBulldog" };
        static readonly string[] AutoPhase2 = { "VolflarPackBig", "DemonsPackBig", "GhostsPackBig", "AnimalsPackBig", "BossGolem1", "BossGolem2", "BossGolem3",
            "BossBullDog", "BossSickEye", "BossVolflarBerserk", "BossVolflarMage", "BossDemonBruiser", "BossVampire" };

        string rollPattern = "*|*|*";

        int AutoSite(ExpeditionsManager em)
        {
            string[] parts = rollPattern.Split('|');
            string[][] defaults = { AutoPhase0, AutoPhase1, AutoPhase2 };
            for (int i = 0; i < em.expeditions.Count; i++)
            {
                ExpeditionsManager.Expedition e = em.expeditions[i];
                if (e.isSolved || e.isCreated || e.eventsTypes == null) continue;
                bool ok = true;
                for (int p = 0; p < 3 && ok; p++)
                {
                    string[] allowed = p < parts.Length && parts[p] != "*" ? parts[p].Split('/') : defaults[p];
                    ok = Array.IndexOf(allowed, e.eventsTypes[p]) >= 0;
                }
                if (ok) return i;
            }
            return -1;
        }

        static int Badges(PlayerScript p)
        {
            Item badge = ItemsData.instance.basicItemsData[129];
            int n = 0;
            foreach (InventoryItem it in p.inventory) if (it != null && it.item == badge) n += it.amount;
            return n;
        }

        // Test-only: play the current phase of the nearest running expedition
        // one action at a time, through the game's own paths: use an object,
        // hit a destructible, strike an enemy as the player (a friend's strike
        // on a mirrored enemy goes to its owner).
        void TickExpAuto(PlayerScript player, float now)
        {
            if (!expAuto || now < nextExpAuto) return;
            nextExpAuto = now + 1.5f;
            // Test-only: expedition enemies hit far harder than this test
            // character can take; keep it alive so the run can finish.
            if (player.maxHealth < 100000) player.maxHealth = 100000;
            if (!player.isDead) player.health = player.maxHealth;
            ExpeditionsManager em = ExpeditionsManager.instance;
            if (em == null) return;
            // The site being played (kept while inside its dungeon, far from the site).
            ExpeditionsManager.Expedition site = default(ExpeditionsManager.Expedition);
            WorldEventBase ev = null;
            foreach (ExpeditionsManager.Expedition e in em.expeditions)
                if (e.worldEvent != null && (e.savedId == autoSite || Vector3.Distance(e.spot, player.transform.position) < 80f)) { ev = e.worldEvent; site = e; }
            if (ev == null || !ev.isActive)
            {
                // Still inside the dungeon after someone else finished it: leave through its end.
                if (lastDungeon != null && Vector3.Distance(player.transform.position, lastDungeon.generatedDungeonStart) < 300f && Time.realtimeSinceStartup - dungeonEnteredAt > 25f)
                {
                    foreach (UsableObject u in lastDungeon.dungeonGenerator.Root.GetComponentsInChildren<UsableObject>())
                        if (u.message == "DungeonUseRepository") { u.Interact(); Log.LogInfo(Stamp() + " [expauto] expedition over; left the dungeon through its end"); break; }
                    lastDungeon = null;
                }
                if (now >= nextExpReport) { nextExpReport = now + 5f; Log.LogInfo(Stamp() + " [expauto] no running expedition near; badges=" + Badges(player) + " (before " + badgesBefore + ") " + ExpeditionSync.Describe()); }
                return;
            }
            autoSite = site.savedId;
            int phase = ev.currentPhase;
            WorldEventBase.Phase ph = ev.phases[phase];
            string kind = site.eventsTypes[phase];
            string done = ExpAutoSpecial(player, ev, ph, kind);
            if (done == null && (kind == "Dialog" || kind == "CatchVisions" || kind == "Dungeon" || kind == "Platforming" || kind.StartsWith("Collect")))
                done = "waiting (" + kind + ")";
            foreach (GameObject g in ph.activatedGameObjects ?? new GameObject[0])
            {
                if (g == null || !g.activeInHierarchy || done != null) continue;
                foreach (WorldEventUsableObject u in g.GetComponentsInChildren<WorldEventUsableObject>())
                    if (!u.isUsed && done == null) { AccessTools.Method(typeof(WorldEventUsableObject), "Interact").Invoke(u, null); done = "used '" + u.name + "'"; }
                foreach (WorldEventDestructibleObject d in g.GetComponentsInChildren<WorldEventDestructibleObject>())
                    if (d.health > 0 && done == null) { d.Damage(1, NPC.DamageType.Melee, player.gameObject, false); done = "hit '" + d.name + "' (health " + d.health + ")"; }
                foreach (NPC n in g.GetComponentsInChildren<NPC>())
                    if (!n.isDead && done == null) { n.Damage(Math.Max(400, n.maxHealth / 2 + 1), NPC.DamageType.Melee, player.gameObject, false, KnockPower.Weak, false, false); done = "struck '" + n.npcName + "' (health " + n.health + "/" + n.maxHealth + (n.invincible ? " invincible" : "") + ")"; }
            }
            if (done == null && ph.spawners != null)
                foreach (WorldEventNPCSpawner sp in ph.spawners)
                    if (sp != null) foreach (NPC n in sp.spawnedNPCs)
                            if (n != null && !n.isDead && done == null) { n.Damage(Math.Max(400, n.maxHealth / 2 + 1), NPC.DamageType.Melee, player.gameObject, false, KnockPower.Weak, false, false); done = "struck spawned '" + n.npcName + "' (health " + n.health + "/" + n.maxHealth + (n.invincible ? " invincible" : "") + ")"; }
            Log.LogInfo(Stamp() + " [expauto] phase=" + phase + " (" + kind + ") count=" + ev.currentCount + "/" + ph.directionPlusAmount + " " + (done ?? "nothing to do"));
        }
        float nextExpReport;
        int autoSite;
        static readonly MethodInfo pickInteract = AccessTools.Method(typeof(PickableObject), "Interact");
        static readonly MethodInfo catchEnter = AccessTools.Method(typeof(WorldEventCatchNPC), "OnTriggerEnter");

        // The phases that need more than hitting or using things, through the
        // game's own calls. Returns what was done, or null.
        string ExpAutoSpecial(PlayerScript player, WorldEventBase ev, WorldEventBase.Phase ph, string kind)
        {
            GameObject[] objs = ph.activatedGameObjects ?? new GameObject[0];
            if (kind == "CollectCrystals" || kind == "CollectStones")
            {
                // Pick one up (interact key), carry it into the collect spot.
                WorldEventItemCollectSpot spot = null;
                foreach (GameObject g in objs) if (g != null && g.activeInHierarchy && g.GetComponent<WorldEventItemCollectSpot>() != null) spot = g.GetComponent<WorldEventItemCollectSpot>();
                if (player.carrying != null && spot != null)
                {
                    string what = player.carrying.name;
                    spot.OnTriggerEnter(player.GetComponent<Collider>());
                    return "delivered '" + what + "' (spot wants '" + spot.pickableObjectName + "'" + (spot.itemToCollect != null ? " or item " + spot.itemToCollect.name : "") + ")";
                }
                foreach (GameObject g in objs)
                {
                    PickableObject p = g != null && g.activeInHierarchy ? g.GetComponent<PickableObject>() : null;
                    if (p == null) continue;
                    pickInteract.Invoke(p, null);
                    return "picked up '" + g.name + "'";
                }
                return null;
            }
            if (kind == "CatchVisions")
            {
                // Lure a vision into the trap: the trap's trigger with the vision's collider
                // (only where its AI runs; the others see it die).
                WorldEventCatchNPC trap = null;
                foreach (GameObject g in objs) if (g != null && g.activeInHierarchy && g.GetComponentInChildren<WorldEventCatchNPC>() != null) trap = g.GetComponentInChildren<WorldEventCatchNPC>();
                if (trap == null) return null;
                foreach (WorldEventNPCSpawner sp in ph.spawners ?? new WorldEventNPCSpawner[0])
                    if (sp != null) foreach (NPC n in sp.spawnedNPCs)
                            if (n != null && !n.isDead && n.gameObject.activeInHierarchy && enemies != null && enemies.RunsHere(n))
                            {
                                catchEnter.Invoke(trap, new object[] { n.GetComponent<Collider>() });
                                return "trapped '" + n.npcName + "'";
                            }
                return null;
            }
            if (kind == "Dialog")
            {
                // The friend talks to the soul and picks the answer (dialogue button path).
                if (net != null && net.IsHost) return null;
                foreach (GameObject g in objs)
                {
                    if (g == null || !g.activeInHierarchy || g.GetComponent<DialogueNPCModule>() == null) continue;
                    InterfaceManager.instance.currentTalkTarget = g;
                    StorySync.Dispatch(GameManager.instance, "TalkOutGhostSoul", SendMessageOptions.DontRequireReceiver);
                    return "talked out '" + g.name + "'";
                }
                return null;
            }
            if (kind == "NPCProtection")
            {
                // The friend asks the scientist to start the reveal ritual (dialogue
                // button path); then everyone fights what it summons. Never hit him.
                if (net != null && !net.IsHost && ritualStartedFor != autoSite)
                {
                    foreach (GameObject g in objs)
                    {
                        if (g == null || !g.activeInHierarchy || g.GetComponent<DialogueNPCModule>() == null) continue;
                        InterfaceManager.instance.currentTalkTarget = g;
                        StorySync.Dispatch(GameManager.instance, "BeginRevealRitual", SendMessageOptions.DontRequireReceiver);
                        ritualStartedFor = autoSite;
                        return "asked '" + g.name + "' to start the ritual";
                    }
                }
                foreach (NPC n in ev.GetComponentsInChildren<NPC>())
                    if (n.gameObject.name == "EnemyToSpawn(Clone)" && !n.isDead)
                    {
                        n.Damage(Math.Max(400, n.maxHealth / 2 + 1), NPC.DamageType.Melee, player.gameObject, false, KnockPower.Weak, false, false);
                        return "struck summoned '" + n.npcName + "' (health " + n.health + "/" + n.maxHealth + ")";
                    }
                return "protecting";
            }
            if (kind == "EscapeAOEAttack")
            {
                int blasts = 0;
                foreach (WorldEventAoeAttack a in ev.GetComponentsInChildren<WorldEventAoeAttack>()) blasts++;
                return "dodging (" + blasts + " anomalies here)";
            }
            if (kind == "Platforming")
            {
                // The friend reaches the top and uses it.
                if (net != null && net.IsHost) return null;
                foreach (GameObject g in objs)
                    if (g != null && g.activeInHierarchy)
                        foreach (UsableObject u in g.GetComponentsInChildren<UsableObject>())
                            if (u.message == "CompletePlatformingTower") { u.Interact(); return "used the tower top"; }
                return null;
            }
            if (kind == "Dungeon")
            {
                GameObject entrance = objs.Length > 0 ? objs[0] : null;
                if (entrance == null || !entrance.activeInHierarchy) return null;
                StandaloneGeneratedDungeon d = entrance.GetComponentInChildren<StandaloneGeneratedDungeon>(true);
                if (d == null) return null;
                if (Vector3.Distance(player.transform.position, d.generatedDungeonStart) > 300f)
                {
                    if (exitUsedFor == autoSite) return "left; waiting for the end";
                    if (dungeonEnteredAt > 0f && Time.realtimeSinceStartup - dungeonEnteredAt < 20f) return "travelling";
                    if (d.dungeonGenerator.Root.transform.childCount < 1) d.Generate();
                    lastDungeon = d;
                    dungeonEnteredAt = Time.realtimeSinceStartup;
                    GameManager.instance.StartCoroutine(GameManager.instance.MoveToRoutine(d.generatedDungeonStart, Quaternion.identity));
                    return "entered the dungeon (seed " + d.dungeonGenerator.Seed + ")";
                }
                // Inside: walk to the end and use the repository (the host first, the friend 10 s later).
                if (Time.realtimeSinceStartup - dungeonEnteredAt < (net != null && net.IsHost ? 10f : 20f)) return "exploring";
                foreach (UsableObject u in d.dungeonGenerator.Root.GetComponentsInChildren<UsableObject>())
                    if (u.message == "DungeonUseRepository") { u.Interact(); dungeonEnteredAt = -1f; exitUsedFor = autoSite; return "used the repository at the end"; }
                return "no repository in the dungeon";
            }
            return null;
        }
        float dungeonEnteredAt = -1f;
        StandaloneGeneratedDungeon lastDungeon;
        int exitUsedFor, ritualStartedFor;

        // Test-only companion steps, through the game's own code:
        //   summon:skeleton  the Raise Skeleton ability's spawn (added as when learned)
        //   summon:cat       GameManager.SummonCatPet (a pet item)
        //   cmd:moveaway     the "move there" command (DelayBeforeMove on the ground) 12 m east
        //   cmd:back         the "come back" key (CompControlBack)
        //   cmd:killsummon   a summoned skeleton dies
        void TestCompanions(PlayerScript player, string step)
        {
            if (step == "summon:skeleton")
            {
                Abilities.RaiseSkeleton rs = player.GetComponent<Abilities.RaiseSkeleton>() ?? player.gameObject.AddComponent<Abilities.RaiseSkeleton>();
                rs.GetRunicKoefs(AbilityType.Raise_Skeleton);
                player.mana = player.maxMana;
                player.StartCoroutine(rs.Spawn());
                Log.LogInfo(Stamp() + " Test: raising a skeleton (level " + rs.abilityLevel + ")");
            }
            else if (step == "summon:cat")
            {
                GameManager.instance.SendMessage("SummonCatPet");
                Log.LogInfo(Stamp() + " Test: summoned the cat pet");
            }
            else if (step == "cmd:moveaway")
            {
                Vector3 to = player.transform.position + new Vector3(12f, 0f, 0f);
                foreach (CompanionNPCModule c in player.currentCOmpanions)
                {
                    c.currentTarget = null;
                    c.state = CompanionNPCModule.CompanionState.Waiting;
                    c._agent.destination = to;
                    c._agent.isStopped = false;
                    c.currentDelta = to;
                }
                Log.LogInfo(Stamp() + " Test: companions sent to " + to.ToString("F1"));
            }
            else if (step == "cmd:back")
            {
                AccessTools.Method(typeof(PlayerScript), "CompControlBack").Invoke(player, null);
                Log.LogInfo(Stamp() + " Test: companions called back");
            }
            else if (step == "cmd:killsummon")
            {
                foreach (CompanionNPCModule c in player.currentCOmpanions)
                    if (c != null && c.isSummoned && c.npc != null && !c.npc.isDead)
                    {
                        Log.LogInfo(Stamp() + " Test: summoned '" + c.npc.npcName + "' dies");
                        c.npc.Death(null);
                        break;
                    }
            }
        }

        // Dungeon portals in the loaded world, in a stable order (by position).
        static List<PortalToInstance> Portals()
        {
            List<PortalToInstance> ps = new List<PortalToInstance>();
            foreach (PortalToInstance p in Resources.FindObjectsOfTypeAll<PortalToInstance>())
                if (p != null && p.gameObject.scene.IsValid()) ps.Add(p);
            ps.Sort((a, b) => a.transform.position.x != b.transform.position.x ? a.transform.position.x.CompareTo(b.transform.position.x)
                : a.transform.position.z.CompareTo(b.transform.position.z));
            return ps;
        }

        // The game's own menus, without the co-op panel's answer.
        static bool GamePanelsOpen()
        {
            InterfaceManager im = InterfaceManager.instance;
            if (im == null) return false;
            foreach (GameObject p in im.panels) if (p != null && p.activeSelf) return true;
            return im.lootWindow != null && im.lootWindow.activeSelf;
        }

        // Command-line numbers always use a dot: float.Parse follows the PC's
        // language, and on a German Windows "0.05" became 5.
        static float ParseFloat(string s, float fallback)
        {
            float v;
            return s != null && float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.LogInfo("Scene loaded: " + scene.name + " (" + mode + ")");
            if (mode == LoadSceneMode.Single) sceneLoadedAt = Time.realtimeSinceStartup;
            Application.runInBackground = true;
        }

        // After the game's own minimap moved this frame.
        void LateUpdate()
        {
            bool inWorld = PlayerScript.instance != null && SceneManager.GetActiveScene().name != "MainMenu";
            if (inWorld && net != null && puppets.Count > 0) mapMarkers.Tick(puppets.Values, local.World, local.InDungeon);
            else mapMarkers.Clear();
        }

        bool nameFromArg, steamNameTaken;

        // Without -coop-name the others see our Steam name (SteamManager may
        // come up after Awake, so this is checked every frame until it works).
        void TakeSteamName()
        {
            if (nameFromArg || steamNameTaken || !SteamManager.Initialized) return;
            steamNameTaken = true;
            try
            {
                string name = CleanName(Steamworks.SteamFriends.GetPersonaName());
                if (name.Length == 0) return;
                local.Name = name;
                Log.LogInfo("Player name from Steam: '" + name + "'");
            }
            catch (Exception e) { Log.LogWarning("Could not read the Steam name: " + e.Message); }
        }

        // The pause menu (Esc, InterfaceManager.OpenPauseMenu) and the gamepad
        // menu stop time (timeScale 0). In co-op the world must go on for the
        // others, so the menu stays open but the game keeps running.
        public int PausesSkipped;
        void KeepRunning()
        {
            if (Time.timeScale != 0f || puppets.Count == 0 || PlayerScript.instance == null) return;
            Time.timeScale = 1f;
            PausesSkipped++;
            Log.LogInfo(Stamp() + " Pause skipped: in co-op the game keeps running");
            coopUi.ShowNote(Lang.T("Co-op: the game keeps running while the menu is open.", "Co-op: Das Spiel läuft weiter, während das Menü offen ist."), 4f);
        }

        // Names are shown in a TextMesh (rich text) and NGUI labels (BBCode).
        public static string CleanName(string name)
        {
            if (name == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char ch in name)
                if (!char.IsControl(ch) && ch != '<' && ch != '>' && ch != '[' && ch != ']') sb.Append(ch);
            string s = sb.ToString().Trim();
            return s.Length > 24 ? s.Substring(0, 24).Trim() : s;
        }

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            TryAutoload(now);
            CheckHotkeys();
            TakeSteamName();
            KeepRunning();
            SteamSelfTest(now);
            InviteTest(now);

            PlayerScript player = PlayerScript.instance;
            bool inWorld = player != null && SceneManager.GetActiveScene().name != "MainMenu";
            local.Scene = SceneManager.GetActiveScene().name;
            if (inWorld)
            {
                if (inWorldSince < 0f) inWorldSince = now;
                Transform t = player.transform;
                local.TravelSpeed = travel.Sample(t.position, Time.deltaTime);
                local.Dead = player.isDead;
                local.Stealth = player.inStealth > 0;
                local.InDungeon = player.subZOneIn != null && player.subZOneIn.isDungeon;
                // Test-only: after dying, load the save again like the death
                // screen's button (the same LoadFromGame the main menu uses).
                if (player.isDead && deadSince < 0f) { deadSince = now; Log.LogInfo(Stamp() + " Local player died (health " + player.health + ")"); }
                if (!player.isDead) deadSince = -1f;
                if (testReloadAfter >= 0f && !reloaded && deadSince >= 0f && now - deadSince > testReloadAfter)
                {
                    reloaded = true;
                    Log.LogInfo(Stamp() + " Test: reloading the save after death");
                    SaveLoadManager.instance.StartCoroutine(SaveLoadManager.instance.LoadFromGame("coop-test.es3"));
                }
                if (testRegen && !player.isDead && player.health < player.maxHealth / 2 && now >= nextRegen)
                {
                    nextRegen = now + 1f;
                    Log.LogInfo("Test regen: health " + player.health + " -> " + player.maxHealth);
                    player.health = player.maxHealth;
                }
                local.X = t.position.x; local.Y = t.position.y; local.Z = t.position.z;
                local.Yaw = t.eulerAngles.y;
                // The loaded hub world (Gedonia, NorthernKingdoms, UnchartedIslands);
                // "WorldToLoad" only says which one the save started in.
                local.World = GameManager.instance != null && !string.IsNullOrEmpty(GameManager.instance.currentLoadedWorld)
                    ? GameManager.instance.currentLoadedWorld : PlayerPrefs.GetString("WorldToLoad", "Gedonia");
                ReadLocalAnimator(player);
                local.WeaponFlags = (byte)(
                    (player.rightHandActive != null && player.rightHandActive.activeSelf ? 1 : 0) |
                    (player.leftHandActive != null && player.leftHandActive.activeSelf ? 2 : 0) |
                    (player.back != null && player.back.activeSelf ? 4 : 0) |
                    (player.arrowForBow != null && player.arrowForBow.activeSelf ? 8 : 0));
                EnsureAppearance(player, now);
                if (dump && !dumped && now - inWorldSince > 5f) { dumped = true; LocalPlayer.Dump(player); }
                RunTestChanges(player, now);
                if (census && now >= nextCensus) { nextCensus = now + 5f; Census.Log(player.transform.position); }
                TickBot(player, now);
                TickExpAuto(player, now);
                respawn.Tick(player, net != null && puppets.Count > 0, now);
                TickShots(now);
            }
            else
            {
                inWorldSince = -1f;
                local.World = "";
                botStart = -1f; // a reloaded world starts the bot's wait again
            }

            if (net == null) return;

            if (now >= nextSend)
            {
                nextSend = now + SendInterval;
                local.Seq++;
                net.SendState(local);
            }
            if (now >= nextAppearance && LocalPlayer.Current != null)
            {
                nextAppearance = now + AppearanceInterval;
                LocalPlayer.Current.Id = local.Id;
                if (inWorld)
                {
                    int sig = LocalPlayer.LookSignature(player.transform, localSigEntries);
                    if (sig != LocalPlayer.Current.Signature)
                        Log.LogInfo(Stamp() + " Local look signature " + sig + " (" + localSigEntries.Count + " renderers, look v" +
                            LocalPlayer.Current.Version + "): " + string.Join("; ", localSigEntries.ToArray()));
                    LocalPlayer.Current.Signature = sig;
                }
                net.SendAppearance(LocalPlayer.Current);
            }

            net.LocalId = local.Id;
            net.Poll();
            foreach (Appearance a in net.Appearances)
            {
                if (a.Id == local.Id) continue;
                Appearance old;
                // Same version with a new signature: gear unchanged but e.g. the
                // weapon was drawn; keep the newest signature for the look check.
                if (!looks.TryGetValue(a.Id, out old) || a.Version > old.Version) looks[a.Id] = a;
                else if (a.Version == old.Version) old.Signature = a.Signature;
            }
            foreach (PlayerState s in net.States)
            {
                if (s.Id == local.Id) continue;
                Puppet p;
                if (!puppets.TryGetValue(s.Id, out p))
                {
                    p = new Puppet(s.Id, s.Name);
                    puppets[s.Id] = p;
                    Log.LogInfo("Player joined: '" + s.Name + "' id=" + s.Id);
                }
                if (p.Latest == null || unchecked((int)(s.Seq - p.Latest.Seq)) > 0)
                {
                    if (p.Latest != null && p.Latest.Name != s.Name) Log.LogInfo("Player " + s.Id + " is now called '" + s.Name + "'");
                    p.Latest = s;
                    p.SetName(s.Name);
                }
                p.LastHeard = now;
            }

            List<int> gone = null;
            foreach (Puppet p in puppets.Values)
            {
                if (now - p.LastHeard > PeerTimeout)
                {
                    if (gone == null) gone = new List<int>();
                    gone.Add(p.Id);
                    continue;
                }
                Appearance look;
                if (looks.TryGetValue(p.Id, out look)) p.Look = look;
                bool sameWorld = inWorld && p.Latest.Scene == local.Scene && p.Latest.World == local.World;
                p.SetVisible(sameWorld);
                if (sameWorld) p.Tick(Time.deltaTime, now);
            }
            if (gone != null)
            {
                foreach (int id in gone)
                {
                    Log.LogInfo("Player left (timeout): id=" + id);
                    puppets[id].Destroy();
                    puppets.Remove(id);
                    looks.Remove(id);
                    net.Forget(id);
                    if (enemies != null) enemies.PlayerLeft(id);
                }
            }

            if (enemies == null || enemiesNet != net)
            {
                enemiesNet = net;
                enemies = new EnemySync(net, pid => { Puppet pp; return puppets.TryGetValue(pid, out pp) ? pp : null; }, () => puppets.Values);
            }
            enemies.Tick(now, inWorld, new Vector3(local.X, local.Y, local.Z));
            if (events == null || eventsNet != net)
            {
                eventsNet = net;
                events = new WorldEventSync(net, enemies, () => puppets.Keys);
            }
            events.Tick(now, inWorld);
            if (story == null || storyNet != net)
            {
                storyNet = net;
                story = new StorySync(net, () => puppets.Keys);
            }
            story.Tick(now);
            if (worldState == null || worldStateNet != net)
            {
                worldStateNet = net;
                worldState = new WorldStateSync(net, () => puppets.Keys);
            }
            worldState.Tick(now, inWorld);
            if (loot == null || lootNet != net)
            {
                lootNet = net;
                loot = new LootSync(net, () => puppets.Keys, pid => { Puppet pp; return puppets.TryGetValue(pid, out pp) && pp.Latest != null ? pp.Latest.Name : null; });
            }
            loot.Tick(now);
            if (clock == null || clockNet != net)
            {
                clockNet = net;
                clock = new WorldClockSync(net, () => puppets.Keys);
            }
            clock.Tick(now);
            if (travellers == null || travellersNet != net)
            {
                travellersNet = net;
                travellers = new TravellerSync(net, () => puppets.Keys);
            }
            travellers.Tick(now);
            if (sounds == null || soundsNet != net)
            {
                soundsNet = net;
                sounds = new SoundSync(net, pid => { Puppet pp; return puppets.TryGetValue(pid, out pp) ? pp : null; }, () => puppets.Keys);
            }
            sounds.Tick(now);
            if (dialogue == null || dialogueNet != net)
            {
                dialogueNet = net;
                dialogue = new DialogueSync(net, pid => { Puppet pp; return puppets.TryGetValue(pid, out pp) && pp.Latest != null ? pp.Latest.Name : null; }, () => puppets.Keys);
            }
            dialogue.Tick(now);
            if (expeditions == null || expeditionsNet != net)
            {
                expeditionsNet = net;
                expeditions = new ExpeditionSync(net, () => puppets.Keys);
            }
            expeditions.Tick(now, inWorld);
            if (projectiles == null || projectilesNet != net)
            {
                projectilesNet = net;
                projectiles = new ProjectileSync(net);
            }
            projectiles.Tick(pid => { Puppet pp; return puppets.TryGetValue(pid, out pp) ? pp : null; });

            if (now >= nextStatus)
            {
                nextStatus = now + 2f;
                LogStatus();
            }
        }

        void ReadLocalAnimator(PlayerScript player)
        {
            // An Animator reports no parameters until it is initialised, so
            // keep asking while the list is empty.
            if (player.anim != localAnim || (localAnim != null && localParams.Count == 0))
            {
                localAnim = player.anim;
                localParams = localAnim != null ? LocalPlayer.SyncableParams(localAnim) : null;
                if (localAnim != null && localParams.Count > 0)
                    Log.LogInfo("Local animator: layers=" + localAnim.layerCount + " params=" + localParams.Count);
            }
            if (localAnim == null) return;
            LocalPlayer.ReadAnimator(localAnim, local, localParams);
            local.Speed = localAnim.GetFloat("Speed");
            local.Clip = LocalPlayer.DominantClip(localAnim, 0);
            if (!animLog) return;
            if (lastLocalStates.Length != local.LayerStates.Length) lastLocalStates = new int[local.LayerStates.Length];
            for (int l = 0; l < local.LayerStates.Length; l++)
            {
                if (local.LayerStates[l] == lastLocalStates[l]) continue;
                lastLocalStates[l] = local.LayerStates[l];
                Log.LogInfo("Local layer" + l + " -> " + lastLocalStates[l]);
            }
        }

        // The game refreshes the model on load and on every gear change, which
        // the Harmony hook captures. If we joined after that, ask for one.
        void EnsureAppearance(PlayerScript player, float now)
        {
            if (LocalPlayer.Current != null || appearanceFallbackDone || now - inWorldSince < 5f) return;
            appearanceFallbackDone = true;
            Log.LogInfo("No appearance captured yet; calling PlayerScript.RefreshPlayerModel()");
            try { player.RefreshPlayerModel(); }
            catch (Exception e) { Log.LogError("RefreshPlayerModel failed: " + e); }
        }

        void RunTestChanges(PlayerScript player, float now)
        {
            try
            {
                if (testLook && !lookDone && now - inWorldSince > 10f)
                {
                    lookDone = true;
                    Log.LogInfo(Stamp() + " Test: changing look");
                    TestLook.ApplyAlternateLook(player);
                }
                if (testCharisma >= 0 && now - inWorldSince > 10f)
                {
                    // Test-only: recruiting needs charisma >= 2 (GameManager.GetMaxCompanions).
                    Log.LogInfo(Stamp() + " Test: charisma " + player.charismaBasic + " -> " + testCharisma);
                    player.charismaBasic = testCharisma;
                    testCharisma = -1;
                }
                for (int i = storyTests.Count - 1; i >= 0; i--)
                {
                    if (now - inWorldSince < storyTests[i].Key) continue;
                    string step = storyTests[i].Value;
                    storyTests.RemoveAt(i);
                    if (step.StartsWith("gotopos:"))
                    {
                        string[] c = step.Substring(8).Split(';');
                        Vector3 p = new Vector3(ParseFloat(c[0], 0f), ParseFloat(c[1], 0f), ParseFloat(c[2], 0f));
                        player._char.enabled = false;
                        player.transform.position = p;
                        player._char.enabled = true;
                        Log.LogInfo(Stamp() + " Test: teleported to " + p.ToString("F1"));
                    }
                    else if (step.StartsWith("goto:")) TestGoto(player, int.Parse(step.Substring(5)));
                    else if (step == "tour")
                    {
                        // Visit every world event spot (spread over the map), 10 s each.
                        foreach (WorldEventBase ev in UnityEngine.Object.FindObjectsOfType<WorldEventBase>()) tourStops.Add(ev.transform.position + Vector3.up * 3f);
                        foreach (WorldEventBase ev in Resources.FindObjectsOfTypeAll<WorldEventBase>())
                            if (ev.gameObject.scene.IsValid() && !tourStops.Contains(ev.transform.position + Vector3.up * 3f)) tourStops.Add(ev.transform.position + Vector3.up * 3f);
                        nextTourStop = now;
                        Log.LogInfo(Stamp() + " Test: chest tour over " + tourStops.Count + " spots");
                    }
                    else if (step.StartsWith("useevent:"))
                    {
                        WorldEventBase ev = WorldEventSync.Nearest(player.transform.position);
                        if (ev != null) WorldEventSync.TestUse(ev, int.Parse(step.Substring(9)));
                    }
                    // World travel the way the road signs do it (without their level check).
                    else if (step == "north") GameManager.instance.StartCoroutine(GameManager.instance.ToNorthernKigdoms(new Vector3(1387f, 78f, -267.4f)));
                    else if (step == "gedonia") GameManager.instance.StartCoroutine(GameManager.instance.ToGedonia(new Vector3(2538.15f, 15.624f, 916.24f)));
                    else if (step == "portals")
                    {
                        List<PortalToInstance> ps = Portals();
                        for (int k = 0; k < ps.Count; k++)
                            Log.LogInfo("[portal] " + k + " '" + ps[k].name + "' at " + ps[k].transform.position.ToString("F0") + " -> " +
                                (ps[k].spawnPoint != null ? ps[k].spawnPoint.position.ToString("F0") : "none") + " minLevel=" + ps[k].minimumLevel +
                                " onEnter=" + ps[k].onEnter + " scene=" + ps[k].gameObject.scene.name + " active=" + ps[k].gameObject.activeInHierarchy);
                    }
                    else if (step.StartsWith("portal:"))
                    {
                        // Through a dungeon portal: the same MoveToRoutine as PortalToInstance.
                        List<PortalToInstance> ps = Portals();
                        int k = int.Parse(step.Substring(7));
                        if (k < ps.Count && ps[k].spawnPoint != null)
                        {
                            Log.LogInfo(Stamp() + " Test: through portal " + k + " '" + ps[k].name + "' to " + ps[k].spawnPoint.position.ToString("F0"));
                            GameManager.instance.StartCoroutine(GameManager.instance.MoveToRoutine(ps[k].spawnPoint.position, ps[k].spawnPoint.rotation));
                        }
                    }
                    else if (step.StartsWith("exp")) TestExpedition(player, step);
                    else if (step.StartsWith("summon:") || step.StartsWith("cmd:")) TestCompanions(player, step);
                    else if (step == "waypoints")
                    {
                        // Survey: waypoint stones, the named area (SubZone) each stands in,
                        // and every named area.
                        List<SubZone> zones = new List<SubZone>();
                        foreach (SubZone z in Resources.FindObjectsOfTypeAll<SubZone>()) if (z.gameObject.scene.IsValid()) zones.Add(z);
                        foreach (WaypointEnabler w in Resources.FindObjectsOfTypeAll<WaypointEnabler>())
                        {
                            if (!w.gameObject.scene.IsValid()) continue;
                            string inZone = "-";
                            foreach (SubZone z in zones)
                            {
                                Collider c = z.GetComponent<Collider>();
                                if (c != null && c.bounds.Contains(w.transform.position)) inZone = z.nameofZone + (z.isDungeon ? "(dungeon)" : "");
                            }
                            WaypointTrigger t = w.trigger != null ? w.trigger.GetComponent<WaypointTrigger>() : null;
                            Log.LogInfo("[waypoint] id=" + (t != null ? t.id : -1) + " '" + w.name + "' at " + w.transform.position.ToString("F0") + " scene=" + w.gameObject.scene.name +
                                " activated=" + w.activated + " zone=" + inZone);
                        }
                        Vector3[] all = InterfaceManager.instance.waypointTriggers;
                        for (int k = 0; k < all.Length; k++)
                        {
                            string inZone = "-";
                            foreach (SubZone z in zones)
                            {
                                Collider c = z.GetComponent<Collider>();
                                if (c != null && c.bounds.Contains(all[k])) inZone = z.nameofZone;
                            }
                            Log.LogInfo("[waypointpos] " + k + " at " + all[k].ToString("F0") + " opened=" + InterfaceManager.instance.waypointEnablers[k] + " zone=" + inZone);
                        }
                        foreach (SubZone z in zones)
                        {
                            Collider c = z.GetComponent<Collider>();
                            Log.LogInfo("[subzone] '" + z.nameofZone + "' center=" + (c != null ? c.bounds.center.ToString("F0") : "-") + " size=" + (c != null ? c.bounds.size.ToString("F0") : "-") + " dungeon=" + z.isDungeon);
                        }
                    }
                    else if (step.StartsWith("gotovillage:") || step == "gotocrystal")
                    {
                        // Test-only: to a village's middle (on the ground), or onto the
                        // respawn crystal placed nearest to us (touching it binds).
                        Vector3 to = Vector3.zero;
                        Log.LogInfo(Stamp() + " Test: " + step + " (zones " + (GameManager.instance == null ? "no GameManager" : GameManager.instance.subLocations == null ? "null" : GameManager.instance.subLocations.Length.ToString()) + ")");
                        if (step == "gotocrystal")
                        {
                            float best = float.MaxValue;
                            foreach (GameObject g in UnityEngine.Object.FindObjectsOfType<GameObject>())
                                if (g.name.StartsWith("CoopRespawnCrystal") && Vector3.Distance(g.transform.position, player.transform.position) < best)
                                { best = Vector3.Distance(g.transform.position, player.transform.position); to = g.transform.position + new Vector3(1f, 1f, 0f); }
                        }
                        else
                        {
                            foreach (SubZone z in GameManager.instance.subLocations ?? new SubZone[0])
                            {
                                try
                                {
                                    if (z == null || z.nameofZone != step.Substring(12)) continue;
                                    Collider zc = z.GetComponent<Collider>();
                                    if (zc == null) continue;
                                    Vector3 c = zc.bounds.center;
                                    RaycastHit hit;
                                    to = Physics.Raycast(c + Vector3.up * 200f, Vector3.down, out hit, 500f, ~0, QueryTriggerInteraction.Ignore) ? hit.point + Vector3.up * 1.5f + new Vector3(4f, 0f, 4f) : c;
                                }
                                catch (Exception e) { Log.LogWarning("Test: gotovillage zone check failed: " + e.GetType().Name + " " + e.StackTrace); }
                            }
                        }
                        if (to == Vector3.zero) Log.LogWarning("Test: no target for " + step);
                        else
                        {
                            if (player._char != null) player._char.enabled = false; // the controller comes a while after loading
                            player.transform.position = to;
                            if (player._char != null) player._char.enabled = true;
                            Log.LogInfo(Stamp() + " Test: " + step + " -> " + to.ToString("F1"));
                        }
                    }
                    else if (step.StartsWith("shot:")) Shot(local.Name + "_" + step.Substring(5));
                    else if (step == "travellers") Log.LogInfo(Stamp() + " [travellers] " + TravellerSync.Describe());
                    else if (step.StartsWith("gototraveller:")) TravellerSync.TestGoto(player, int.Parse(step.Substring(14)));
                    else if (step == "travellershift") TravellerSync.TestShift();
                    else if (step == "facecrystal")
                    {
                        // Test-only: 2 m from the nearest crystal, camera turned to it.
                        CoopCrystal best = null;
                        foreach (CoopCrystal c in UnityEngine.Object.FindObjectsOfType<CoopCrystal>())
                            if (best == null || Vector3.Distance(c.transform.position, player.transform.position) < Vector3.Distance(best.transform.position, player.transform.position)) best = c;
                        if (best == null) Log.LogWarning("Test: no crystal near");
                        else
                        {
                            Vector3 cp = best.transform.position;
                            Vector3 from = player.transform.position - cp; from.y = 0f;
                            if (from.sqrMagnitude < 0.01f) from = Vector3.forward;
                            Vector3 to = cp + from.normalized * 2f;
                            RaycastHit hit;
                            if (Physics.Raycast(to + Vector3.up * 50f, Vector3.down, out hit, 100f, ~0, QueryTriggerInteraction.Ignore)) to = hit.point + Vector3.up * 0.2f;
                            if (player._char != null) player._char.enabled = false;
                            player.transform.position = to;
                            if (player._char != null) player._char.enabled = true;
                            Vector3 look = cp - to; look.y = 0f;
                            CameraMovement.instance.parentTr.rotation = Quaternion.Euler(10f, Quaternion.LookRotation(look).eulerAngles.y, 0f);
                            Log.LogInfo(Stamp() + " Test: facing the crystal of " + best.Village + " from " + to.ToString("F1"));
                        }
                    }
                    else if (step == "pressF") { BotInput.InteractPending = true; Log.LogInfo(Stamp() + " Test: pressing F (target '" + (player.currentInteractTarget != null ? player.currentInteractTarget.name : "-") + "')"); }
                    else if (step == "crystalstate")
                        Log.LogInfo(Stamp() + " [crystal] target='" + (player.currentInteractTarget != null ? player.currentInteractTarget.name : "-") + "' hover='" + InterfaceManager.instance.tooltipHover.text.Replace("\n", "\\n") + "' " + respawn.Summary());
                    else if (step == "dialogsurvey") Log.LogInfo(Stamp() + " [dialogsurvey] " + DialogueSync.Survey());
                    else if (step.StartsWith("talk:")) DialogueSync.TestTalk(player, int.Parse(step.Substring(5)));
                    else if (step.StartsWith("vote:")) DialogueSync.TestVote(int.Parse(step.Substring(5)));
                    else if (step.StartsWith("voteopt:")) DialogueSync.TestVoteAnswer(int.Parse(step.Substring(8)));
                    else if (step == "dialogstate") Log.LogInfo(Stamp() + " [dialog] " + (dialogue != null ? dialogue.Describe() : "-"));
                    else if (step == "leavedialog") InterfaceManager.instance.ExitDialogue();
                    else if (step == "pause") InterfaceManager.instance.OpenPauseMenu();
                    else if (step == "unpause") InterfaceManager.instance.ClosePauseMenu();
                    else if (step == "timescale") Log.LogInfo(Stamp() + " [pause] timeScale=" + Time.timeScale + " pauseMenu=" + InterfaceManager.instance.panels[13].activeSelf + " peers=" + puppets.Count + " skipped=" + PausesSkipped + " clock " + WorldClockSync.Describe() + " pos=" + player.transform.position.ToString("F1"));
                    else if (step.StartsWith("gotoxz:"))
                    {
                        // Test-only: onto the ground at x;z.
                        string[] c = step.Substring(7).Split(';');
                        Vector3 p = new Vector3(ParseFloat(c[0], 0f), 400f, ParseFloat(c[1], 0f));
                        RaycastHit hit;
                        if (Physics.Raycast(p, Vector3.down, out hit, 800f, ~0, QueryTriggerInteraction.Ignore)) p = hit.point + Vector3.up * 1.5f;
                        else p.y = 150f;
                        if (player._char != null) player._char.enabled = false;
                        player.transform.position = p;
                        if (player._char != null) player._char.enabled = true;
                        Log.LogInfo(Stamp() + " Test: to " + p.ToString("F1") + (hit.collider != null ? " on '" + hit.collider.name + "'" : " (no ground yet)"));
                    }
                    else if (step == "npcsnear")
                    {
                        // Survey: friendly NPCs within 300 m (a village's people) and their middle.
                        Vector3 me = player.transform.position, sum = Vector3.zero;
                        int n = 0;
                        foreach (NPC c in UnityEngine.Object.FindObjectsOfType<NPC>())
                        {
                            if (c.isDead || Vector3.Distance(c.transform.position, me) > 700f || c.allianceToPlayer() == ReputationLevel.Enemy) continue;
                            if (c.GetComponent<CompanionNPCModule>() != null) continue;
                            n++; sum += c.transform.position;
                            Log.LogInfo("[npcnear] '" + c.npcName + "' at " + c.transform.position.ToString("F0") + " dialogue=" + (c.GetComponent<DialogueNPCModule>() != null));
                        }
                        Log.LogInfo("[npcnear] " + n + " friendly NPCs" + (n > 0 ? ", middle " + (sum / n).ToString("F0") : "") + " around " + me.ToString("F0"));
                        // Buildings: renderers named like houses, grouped in 50 m cells.
                        Dictionary<string, int> cells = new Dictionary<string, int>();
                        foreach (MeshRenderer r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                        {
                            string nm = r.name.ToLowerInvariant();
                            if (!(nm.Contains("house") || nm.Contains("roof") || nm.Contains("hut") || nm.Contains("tent") || nm.Contains("building") || nm.Contains("wall"))) continue;
                            Vector3 rp = r.transform.position;
                            if (Vector3.Distance(rp, me) > 700f) continue;
                            string key = (Mathf.Round(rp.x / 50f) * 50f) + "," + (Mathf.Round(rp.z / 50f) * 50f);
                            int cnt; cells.TryGetValue(key, out cnt); cells[key] = cnt + 1;
                        }
                        List<string> top = new List<string>(cells.Keys);
                        top.Sort((a, b) => cells[b].CompareTo(cells[a]));
                        for (int k = 0; k < top.Count && k < 6; k++) Log.LogInfo("[buildings] cell x,z=" + top[k] + " count=" + cells[top[k]]);
                    }
                    else if (step == "coopmenu") OnTopCoopButton();
                    else if (step == "mapdump")
                    {
                        // Survey: how the map markers are built.
                        InterfaceManager im = InterfaceManager.instance;
                        foreach (Transform t in new Transform[] { im.worldMapPlayer, im.minimapPlayer, im.minimapIconPrefab.transform, im.markerOnMap.transform, im.markerOnMinimap.transform })
                        {
                            string path = t.name; for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
                            Log.LogInfo("[mapdump] " + path + " active=" + t.gameObject.activeInHierarchy + " lpos=" + t.localPosition + " lscale=" + t.localScale);
                            foreach (Component c in t.GetComponentsInChildren<Component>(true))
                            {
                                UISprite s = c as UISprite; UILabel l = c as UILabel;
                                Log.LogInfo("[mapdump]   " + c.gameObject.name + " " + c.GetType().Name + (s != null ? " sprite=" + s.spriteName + " atlas=" + (s.atlas != null ? ((UnityEngine.Object)s.atlas).name : "-") + " size=" + s.width + "x" + s.height + " color=" + s.color : "") + (l != null ? " text=" + l.text + " font=" + l.fontSize : ""));
                            }
                        }
                        UISprite ps = im.minimapIconPrefab.GetComponentInChildren<UISprite>(true);
                        if (ps != null && ps.atlas != null)
                        {
                            List<string> names = new List<string>();
                            foreach (UISpriteData d in ps.atlas.spriteList) names.Add(d.name);
                            Log.LogInfo("[mapdump] icon atlas sprites: " + string.Join(",", names.ToArray()));
                        }
                        Log.LogInfo("[mapdump] scale=" + im.scale + " delta=" + im.delta + " wmScale=" + im.worldmapScale + " wmDelta=" + im.worldmapdelta + " hub=" + im.hubWorldDelta + " minimapNotCompass=" + im.minimapNotCompass);
                    }
                    else if (step == "maplog") mapMarkers.Log();
                    else if (step == "lanhost") LanHost();
                    else if (step == "lanlist") { coopUi.JoinOpen = true; coopUi.HostOpen = false; }
                    else if (step == "lanjoin")
                    {
                        string g = coopUi.FirstLanGame();
                        Log.LogInfo("Test: home-network search found " + (g ?? "nothing"));
                        Shot(local.Name + "_lanlist");
                        coopUi.JoinOpen = false;
                        if (g != null) LanJoin(g.Split(' ')[0]);
                    }
                    else if (step.StartsWith("face:")) player.transform.rotation = Quaternion.Euler(0f, float.Parse(step.Substring(5), CultureInfo.InvariantCulture), 0f);
                    else if (step == "compass:on" || step == "compass:off")
                    {
                        // As the options' minimap/compass switch, without saving it.
                        InterfaceManager im = InterfaceManager.instance;
                        im.minimapNotCompass = step == "compass:off";
                        im.minimap.SetActive(im.minimapNotCompass);
                        im.compass.SetActive(!im.minimapNotCompass);
                    }
                    else if (step == "lang:en") OnMenuGameLanguage();
                    else if (step == "lang:de") OnMenuGerman();
                    else if (step.StartsWith("window:"))
                    {
                        // Opens one of the game's windows the way its button does
                        // (InterfaceManager.OpenInventory, OpenJournal, ...).
                        string m = step.Substring(7);
                        System.Reflection.MethodInfo mi = typeof(InterfaceManager).GetMethod(m, Type.EmptyTypes);
                        if (mi == null) Log.LogWarning("No InterfaceManager." + m);
                        else { mi.Invoke(InterfaceManager.instance, null); Log.LogInfo("Test: window " + m); }
                    }
                    else if (step == "uidump") { DumpButtons("uibutton"); Shot(local.Name + "_ui"); }
                    else if (step == "textcount")
                    {
                        // Survey: how much English text the game has, by source.
                        int n = 0; long chars = 0;
                        foreach (KeyValuePair<string, string[]> kv in Localization.dictionary) { n++; if (kv.Value.Length > 0 && kv.Value[0] != null) chars += kv.Value[0].Length; }
                        Log.LogInfo("[textcount] NGUI localization keys=" + n + " chars=" + chars + " languages=" + string.Join(",", Localization.knownLanguages));
                        n = 0; chars = 0;
                        foreach (Quest q in QuestsData.instance.questDatabase)
                        {
                            if (q == null) continue;
                            n++; chars += (q.questName ?? "").Length + (q.descriptionShort ?? "").Length;
                            if (q.objectiveText != null) foreach (string o in q.objectiveText) chars += (o ?? "").Length;
                        }
                        Log.LogInfo("[textcount] quests=" + n + " chars=" + chars);
                        n = 0; chars = 0;
                        foreach (Item[] arr in new Item[][] { ItemsData.instance.basicItemsData, ItemsData.instance.weaponsDatabase, ItemsData.instance.armorDatabase, ItemsData.instance.consumablesData })
                            foreach (Item it in arr) if (it != null) { n++; chars += (it.name ?? "").Length + (it.basicDescription ?? "").Length; }
                        Log.LogInfo("[textcount] items=" + n + " chars=" + chars);
                        n = 0; chars = 0; int modules = 0;
                        foreach (DialogueNPCModule d in Resources.FindObjectsOfTypeAll<DialogueNPCModule>())
                        {
                            modules++;
                            if (d.allDialogue == null) continue;
                            foreach (DialogiePoint p in d.allDialogue) { n++; chars += (p.playerQuestionText ?? "").Length + (p.npcAnswerText ?? "").Length; }
                        }
                        Log.LogInfo("[textcount] dialogue modules loaded=" + modules + " lines=" + n + " chars=" + chars);
                    }
                    else if (step == "textexport") Translator.Export();
                    else if (step == "textexportall") StartCoroutine(Translator.ExportAll());
                    else if (step == "die")
                    {
                        Log.LogInfo(Stamp() + " Test: dying");
                        player.health = 0;
                        player.Death(null);
                    }
                    else if (step == "settlements")
                    {
                        // Survey for a world without SubZones: its big zones and where
                        // merchants stand (villages have merchants).
                        foreach (Zone z in Resources.FindObjectsOfTypeAll<Zone>())
                        {
                            if (!z.gameObject.scene.IsValid()) continue;
                            Collider c = z.GetComponent<Collider>();
                            Log.LogInfo("[zone] '" + z.nameofZone + "' scene=" + z.gameObject.scene.name + " center=" + (c != null ? c.bounds.center.ToString("F0") : z.transform.position.ToString("F0")) +
                                " size=" + (c != null ? c.bounds.size.ToString("F0") : "-") + " active=" + z.gameObject.activeInHierarchy);
                        }
                        foreach (SubZone z in Resources.FindObjectsOfTypeAll<SubZone>())
                            if (z.gameObject.scene.IsValid() && z.gameObject.activeInHierarchy && z.GetComponent<Collider>() != null && z.GetComponent<Collider>().enabled)
                                Log.LogInfo("[activesubzone] '" + z.nameofZone + "' scene=" + z.gameObject.scene.name + " center=" + z.GetComponent<Collider>().bounds.center.ToString("F0"));
                        foreach (MerchantNPCModule m in Resources.FindObjectsOfTypeAll<MerchantNPCModule>())
                        {
                            if (!m.gameObject.scene.IsValid()) continue;
                            NPC n = m.GetComponent<NPC>();
                            Log.LogInfo("[merchant] '" + (n != null ? n.npcName : m.name) + "' at " + m.transform.position.ToString("F0") + " scene=" + m.gameObject.scene.name);
                        }
                    }
                    else if (step == "maplabels")
                    {
                        // Place names on the world map, turned back into world x/z with
                        // the map's own mapping (local = xz * scale + delta + hubDelta * scale).
                        InterfaceManager im = InterfaceManager.instance;
                        Transform space = im.worldMapPlayer.parent;
                        for (int k = 0; k < im.worldMapRegions.Length; k++)
                        {
                            float sc = im.worldMapScales[k];
                            Vector3 d = im.worldMapDeltas[k];
                            foreach (UILabel l in im.worldMapRegions[k].GetComponentsInChildren<UILabel>(true))
                            {
                                if (string.IsNullOrEmpty(l.text) || sc == 0f) continue;
                                Vector3 lp = space.InverseTransformPoint(l.transform.position);
                                Vector3 w = (lp - d) / sc - im.hubWorldDelta;
                                Log.LogInfo("[maplabel] region=" + k + " '" + l.text.Replace("\n", " ") + "' world x=" + w.x.ToString("F0") + " z=" + w.y.ToString("F0"));
                            }
                        }
                        Log.LogInfo("[maplabel] hubWorldDelta=" + im.hubWorldDelta + " player " + player.transform.position.ToString("F0"));
                    }
                    else if (step == "probe")
                    {
                        // What a ray from above hits here (water is a trigger?).
                        Vector3 p = player.transform.position;
                        foreach (RaycastHit h in Physics.RaycastAll(p + Vector3.up * 100f, Vector3.down, 200f, ~0, QueryTriggerInteraction.Collide))
                            Log.LogInfo("[probe] '" + h.collider.name + "' y=" + h.point.y.ToString("F1") + " trigger=" + h.collider.isTrigger + " layer=" + LayerMask.LayerToName(h.collider.gameObject.layer) + " tag=" + h.collider.tag);
                    }
                    else if (step == "reloadsave")
                    {
                        Log.LogInfo(Stamp() + " Test: loading the save again (like the death screen's load)");
                        SaveLoadManager.instance.StartCoroutine(SaveLoadManager.instance.LoadFromGame("coop-test.es3"));
                    }
                    else if (step == "stonepreview")
                    {
                        // Candidates for a respawn stone's look, in a row in front of the camera.
                        Transform cam = Camera.main.transform;
                        Vector3 basePos = player.transform.position + Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized * 6f;
                        Vector3 side = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
                        UnityEngine.Object[] cands = { ExpeditionsManager.instance != null ? ExpeditionsManager.instance.activatePrefab : null,
                            Resources.Load("Expeditions/MinableCrystals"), Resources.Load("Expeditions/CorruptedCrystals"), Resources.Load("Expeditions/CollectableCrystal") };
                        for (int k = 0; k < cands.Length; k++)
                        {
                            if (cands[k] == null) continue;
                            GameObject g = UnityEngine.Object.Instantiate(cands[k], basePos + side * (k - 1.5f) * 3f, Quaternion.identity) as GameObject;
                            foreach (MonoBehaviour mb in g.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
                            g.SetActive(true);
                            Log.LogInfo("[stonepreview] " + k + " '" + cands[k].name + "'");
                        }
                        Shot("Host_stonepreview");
                    }
                    else if (step == "modcheck")
                    {
                        PlayerWeaponCustomLoader l = player.modelMain.customLoader;
                        string s = "[modcheck] own " + (l == null ? "no loader" : "loaded=" + l.loaded + " renderers=" + (l.weaponParent != null ? l.weaponParent.GetComponentsInChildren<Renderer>(true).Length : 0));
                        foreach (Puppet p in puppets.Values) s += " | puppet " + p.Id + " " + p.ModWeaponState();
                        Log.LogInfo(Stamp() + " " + s);
                    }
                    else if (step == "spellshot")
                    {
                        // A spell projectile (from effectsGos, not the arrow list) fired
                        // the way abilities do: instantiated with the player as owner.
                        GameObject proto = null;
                        foreach (GameObject g in player.effectsGos)
                            if (g != null && g.GetComponent<Projectile>() != null && Array.IndexOf(player.projectiles, g) < 0) { proto = g; break; }
                        if (proto == null) { Log.LogWarning("Test: no spell projectile in effectsGos"); }
                        else
                        {
                            GameObject shot = UnityEngine.Object.Instantiate(proto, player.transform.position + Vector3.up * 1.5f + player.transform.forward, player.transform.rotation);
                            shot.SetActive(true);
                            Projectile pr = shot.GetComponent<Projectile>();
                            pr.owner = player.gameObject;
                            pr.faction = NPCFaction.Player;
                            Log.LogInfo(Stamp() + " Test: cast spell projectile '" + proto.name + "'");
                        }
                    }
                    else if (step == "sneak:on" || step == "sneak:off")
                    {
                        player.inStealth = step == "sneak:on" ? 1 : 0;
                        Log.LogInfo(Stamp() + " Test: sneaking " + (player.inStealth > 0));
                    }
                    else if (step.StartsWith("answer:")) StorySync.TestAnswer(step.Substring(7));
                    else if (step.StartsWith("unsynced:")) StorySync.TestUnsyncedQuest(step.Substring(9));
                }
                if (Arg(Environment.GetCommandLineArgs(), "-coop-test-events") != null && now >= nextEventReport)
                {
                    nextEventReport = now + 5f;
                    Log.LogInfo(Stamp() + " [eventcheck] " + WorldEventSync.Describe(player.transform.position) + " myHealth=" + player.health);
                    WorldEventBase near = WorldEventSync.Nearest(player.transform.position);
                    if (near != null && near != dumpedEvent && Vector3.Distance(near.transform.position, player.transform.position) < 50f)
                    {
                        dumpedEvent = near;
                        Log.LogInfo(WorldEventSync.Structure(near));
                    }
                }
                if (Arg(Environment.GetCommandLineArgs(), "-coop-test-story") != null && now >= nextQuestReport)
                {
                    nextQuestReport = now + 5f;
                    Dictionary<int, string> wdb = SaveLoadManager.instance != null ? SaveLoadManager.instance.WorldDatabase : null;
                    string d1, d2;
                    Log.LogInfo("[questcheck] " + StorySync.QuestReport("Get a horse", "Protect the town", "Lost knowledge", "A greatest hero") +
                        " | dialogue 89849269=" + (wdb != null && wdb.TryGetValue(89849269, out d1) ? d1 : "-") +
                        " | 4022744=" + (wdb != null && wdb.TryGetValue(4022744, out d2) ? d2 : "-"));
                }
                RunLootTests(player, now);
                if (tourStops.Count > 0 && now >= nextTourStop && loot != null)
                {
                    foreach (string ch in loot.Chests(player.transform.position)) if (seenChests.Add(ch.Split(' ')[0])) Log.LogInfo("[chest] " + ch);
                    Vector3 p = tourStops[0];
                    tourStops.RemoveAt(0);
                    player._char.enabled = false;
                    player.transform.position = p;
                    player._char.enabled = true;
                    nextTourStop = now + 10f;
                    Log.LogInfo(Stamp() + " Tour: at " + p.ToString("F0") + " (" + tourStops.Count + " left)");
                }
                for (int i = clockTests.Count - 1; i >= 0; i--)
                {
                    if (now - inWorldSince < clockTests[i].Key) continue;
                    string step = clockTests[i].Value;
                    clockTests.RemoveAt(i);
                    try
                    {
                        if (step.StartsWith("shift:")) WorldClockSync.TestShift(ParseFloat(step.Substring(6), 0f));
                        else if (step.StartsWith("sleep:")) WorldClockSync.TestSleep(ParseFloat(step.Substring(6), 0f));
                        else if (step == "weather") WorldClockSync.TestWeather();
                    }
                    catch (Exception e) { Log.LogError("Clock test '" + step + "' failed: " + e); }
                }
                // UI test: keep the F8 panel open 60 s and log what the game sees,
                // so real clicks on and next to the panel can be checked.
                if (testUiAt > 0f && !testUiClosed && now - inWorldSince >= testUiAt)
                {
                    if (!testUiOpened) { testUiOpened = true; ui.ForceOpen(); }
                    // 5-15 s: mouse over the panel's centre, 15-25 s: next to it.
                    float t = now - inWorldSince - testUiAt;
                    Vector2 c = ui.PanelCenter;
                    InviteUi.TestMouse = t >= 5f && t < 15f ? new Vector3(c.x, Screen.height - c.y, 0f)
                        : t >= 15f && t < 25f ? new Vector3(c.x - 600f, Screen.height - c.y, 0f) : (Vector3?)null;
                    if (t >= 8f && !testUiShot) { testUiShot = true; Shot("Host_ui_panel"); }
                    if (now >= nextUiCheck)
                    {
                        nextUiCheck = now + 1f;
                        Log.LogInfo(Stamp() + " [uicheck] open=" + ui.Open + " gameMenuOpen=" + InterfaceManager.instance.AnyPanelIsActive() +
                            " cursorVisible=" + Cursor.visible + " lock=" + Cursor.lockState + " mouse=" + Input.mousePosition.ToString("F0") +
                            " testMouse=" + (InviteUi.TestMouse.HasValue ? InviteUi.TestMouse.Value.ToString("F0") : "-") + " overPanel=" + ui.IsOverPanel(InviteUi.TestMouse.HasValue ? InviteUi.TestMouse.Value : Input.mousePosition) + " panelCenter=" + ui.PanelCenter.ToString("F0") +
                            " clicksShielded=" + ui.ClicksShielded + " nguiIgnore=" + UICamera.ignoreAllEvents +
                            " inputQueue=" + Traverse.Create(player).Field("inputQeue").GetValue() +
                            " camYaw=" + (CameraMovement.instance != null ? CameraMovement.instance.parentTr.eulerAngles.y.ToString("F1") : "-") +
                            " anyPanelsOfGame=" + GamePanelsOpen() + " screen=" + Screen.width + "x" + Screen.height);
                    }
                    if (now - inWorldSince >= testUiAt + 30f) { testUiClosed = true; InviteUi.TestMouse = null; ui.Close(); Log.LogInfo(Stamp() + " [uicheck] closed, clicksShielded=" + ui.ClicksShielded); }
                }
                // In the given order, so steps due at the same time run left to right.
                while (lookTests.Count > 0 && now - inWorldSince >= lookTests[0].Key)
                {
                    string step = lookTests[0].Value;
                    lookTests.RemoveAt(0);
                    try { TestLook.Step(player, step); }
                    catch (Exception e) { Log.LogError("Look test '" + step + "' failed: " + e); }
                }
                if (Arg(Environment.GetCommandLineArgs(), "-coop-test-clock") != null && now >= nextClockReport)
                {
                    nextClockReport = now + 5f;
                    Log.LogInfo(Stamp() + " [clockcheck] " + WorldClockSync.Describe());
                }
                if (gearSwapAt > 0f && !gearDone && now - inWorldSince > gearSwapAt)
                {
                    gearDone = true;
                    Log.LogInfo(Stamp() + " Test: swapping gear");
                    TestLook.SwapGear(player);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Test change failed: " + e);
            }
        }

        // Test-only: open the nearest chest, take one item, take the rest -
        // through the same calls as the interact key and the loot window.
        void RunLootTests(PlayerScript player, float now)
        {
            if (loot == null || (lootTests.Count == 0 && lootTarget == null && lootTargetId == 0)) return;
            if (lootTargetId != 0 && lootTarget == null) lootTarget = loot.ById(lootTargetId);
            if (lootTargetId != 0 && lootTarget == null && now >= nextLootReport)
            {
                nextLootReport = now + 5f;
                Log.LogInfo("[lootcheck] container " + lootTargetId + " " + loot.Whereabouts(lootTargetId));
            }
            for (int i = lootTests.Count - 1; i >= 0; i--)
            {
                if (now - inWorldSince < lootTests[i].Key) continue;
                string step = lootTests[i].Value;
                lootTests.RemoveAt(i);
                if (step.StartsWith("look:"))
                {
                    // Watch one container by id (it may not be loaded yet).
                    lootTargetId = int.Parse(step.Substring(5));
                    continue;
                }
                if (lootTarget == null)
                {
                    lootTarget = loot.Nearest(player.transform.position, 120f);
                    Log.LogInfo(Stamp() + " Loot test target: " + LootSync.Describe(lootTarget));
                    if (lootTarget == null) continue;
                }
                Log.LogInfo(Stamp() + " Loot test: " + step);
                if (step == "open") lootTarget.Interact();
                else if (step == "take1" && InterfaceManager.instance.lootWindow.activeSelf)
                {
                    GameObject slot = new GameObject("0"); // TakeLootItem reads the slot number from the button's name
                    InterfaceManager.instance.TakeLootItem(slot);
                    UnityEngine.Object.Destroy(slot);
                }
                else if (step == "takeall" && InterfaceManager.instance.lootWindow.activeSelf) InterfaceManager.instance.TakeAllLoot();
                else if (step == "close" && InterfaceManager.instance.lootWindow.activeSelf) InterfaceManager.instance.CLoseLootWindow();
                else Log.LogInfo("Loot test: '" + step + "' skipped (loot window not open)");
            }
            if (lootTarget != null && now >= nextLootReport)
            {
                nextLootReport = now + 5f;
                SaveableItem si = lootTarget != null ? lootTarget.GetComponent<SaveableItem>() : null;
                string dbEntry = "-";
                if (si != null && SaveLoadManager.instance != null && !SaveLoadManager.instance.WorldDatabase.TryGetValue(si.instanceID, out dbEntry)) dbEntry = "(none)";
                Log.LogInfo("[lootcheck] " + LootSync.Describe(lootTarget) + " window=" + InterfaceManager.instance.lootWindow.activeSelf +
                    " myGold=" + (PlayerScript.instance != null ? PlayerScript.instance.gold : 0) +
                    " worldDb[" + (si != null ? si.instanceID : 0) + "]=" + dbEntry);
            }
        }

        // Test-only: teleport next to a saved world object (its position is in
        // the save's world database), e.g. a companion to recruit.
        void TestGoto(PlayerScript player, int id)
        {
            string data;
            if (SaveLoadManager.instance == null || !SaveLoadManager.instance.WorldDatabase.TryGetValue(id, out data))
            {
                Log.LogWarning("Test goto: object " + id + " is not in the world database");
                return;
            }
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(data, @"\((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)");
            if (!m.Success) { Log.LogWarning("Test goto: no position for " + id + " in '" + data + "'"); return; }
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            Vector3 p = new Vector3(float.Parse(m.Groups[1].Value, inv), float.Parse(m.Groups[2].Value, inv) + 1f, float.Parse(m.Groups[3].Value, inv) + 2f);
            player._char.enabled = false;
            player.transform.position = p;
            player._char.enabled = true;
            Log.LogInfo(Stamp() + " Test: teleported next to object " + id + " at " + p.ToString("F1"));
        }

        void LogStatus()
        {
            foreach (Puppet p in puppets.Values) p.CheckLook();
            string line = "[status] scene=" + local.Scene + " world=" + local.World +
                " local=" + Fmt(local.X, local.Y, local.Z) +
                " layer0=" + (local.LayerStates.Length > 0 ? local.LayerStates[0].ToString() : "-") +
                " peers=" + puppets.Count + " tx=" + net.Sent + " rx=" + net.Received + " drop=" + net.Dropped +
                (enemies != null ? " " + enemies.OwnedSummary() : "") + (events != null ? " " + events.Summary() : "") +
                (story != null ? " " + story.Summary() : "") + (worldState != null ? " " + worldState.Summary() : "") +
                (loot != null ? " " + loot.Summary() : "") + (clock != null ? " " + clock.Summary() : "") + (travellers != null ? " " + travellers.Summary() : "") + (sounds != null ? " " + sounds.Summary() : "") + (dialogue != null ? " " + dialogue.Summary() : "") + " " + ProjectileSync.Summary() + (expeditions != null ? " " + expeditions.Summary() : "") + " " + respawn.Summary() +
                (net.Transport is UdpTransport ? " " + ((UdpTransport)net.Transport).Stats : "");
            foreach (Puppet p in puppets.Values)
            {
                PlayerState s = p.Latest;
                float dist = Vector3.Distance(new Vector3(local.X, local.Y, local.Z), new Vector3(s.X, s.Y, s.Z));
                line += " | remote '" + s.Name + "' scene=" + s.Scene + " world=" + s.World +
                    " pos=" + Fmt(s.X, s.Y, s.Z) + " dist=" + dist.ToString("F1") +
                    " sentLayer0=" + (s.LayerStates.Length > 0 ? s.LayerStates[0].ToString() : "-") + " " + p.Describe();
            }
            Log.LogInfo(line);

            if (Time.realtimeSinceStartup >= nextTotals)
            {
                nextTotals = Time.realtimeSinceStartup + 10f;
                foreach (Puppet p in puppets.Values) Log.LogInfo("[totals] '" + p.Latest.Name + "' " + p.Totals());
            }
        }

        static string Fmt(float x, float y, float z)
        {
            return "(" + x.ToString("F1") + "," + y.ToString("F1") + "," + z.ToString("F1") + ")";
        }

        // Test-only: mirror SaveLoadManager.LoadFromGame from the main menu.
        bool menuDumped;

        // Test-only (-coop-menudump 1): list the main menu's buttons (what they
        // call, their text and sprite) and take a screenshot, 8 s into the menu.
        void TryMenuDump(float now)
        {
            if (menuDumped || Arg(Environment.GetCommandLineArgs(), "-coop-menudump") == null) return;
            if (SceneManager.GetActiveScene().name != "MainMenu" || now - sceneLoadedAt < 8f) return;
            menuDumped = true;
            DumpButtons("menubutton");
            Shot("Menu_main");
        }

        // Test-only (-coop-test-menu): in the main menu press "Deutsch", show the
        // join list, take screenshots; the autoload waits until this is done.
        int menuTestStep = -1;
        bool MenuTest(float now)
        {
            if (menuTestStep == -1) menuTestStep = Arg(Environment.GetCommandLineArgs(), "-coop-test-menu") != null ? 0 : 99;
            if (menuTestStep >= 99) return true;
            if (SceneManager.GetActiveScene().name != "MainMenu") return true;
            float t = now - sceneLoadedAt;
            if (menuTestStep == 0 && t > 6f) { Log.LogInfo("Test: menu Deutsch"); OnMenuGerman(); menuTestStep = 1; }
            else if (menuTestStep == 1 && t > 8f) { DumpButtons("menubutton"); Shot("Menu_de"); menuTestStep = 2; }
            else if (menuTestStep == 2 && t > 9f) { OnMenuCoopJoin(); menuTestStep = 3; }
            else if (menuTestStep == 3 && t > 11f) { Shot("Menu_join"); menuTestStep = 4; }
            else if (menuTestStep == 4 && t > 12f) { coopUi.JoinOpen = false; OnMenuCoopHost(); menuTestStep = 5; }
            else if (menuTestStep == 5 && t > 14f) { Shot("Menu_host"); menuTestStep = 6; }
            else if (menuTestStep == 6 && t > 15f) { coopUi.HostOpen = false; menuTestStep = 99; sceneLoadedAt = now - 5f; }
            return false;
        }

        // Test survey: every visible NGUI button with what it calls, its text, sprite and place.
        void DumpButtons(string tag)
        {
            foreach (UIButton b in Resources.FindObjectsOfTypeAll<UIButton>())
            {
                if (!b.gameObject.scene.IsValid() || !b.gameObject.activeInHierarchy) continue;
                List<string> calls = new List<string>();
                foreach (EventDelegate d in b.onClick) if (d != null) calls.Add((d.target != null ? d.target.GetType().Name : "?") + "." + d.methodName);
                UILabel l = b.GetComponentInChildren<UILabel>(true);
                UISprite s = b.GetComponentInChildren<UISprite>(true);
                string path = b.name;
                for (Transform t = b.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                Log.LogInfo("[" + tag + "] " + path + " calls=" + string.Join(",", calls.ToArray()) +
                    " label='" + (l != null ? l.text : "") + "' sprite=" + (s != null ? s.spriteName : "-") + " pos=" + b.transform.position.ToString("F2"));
            }
        }

        void TryAutoload(float now)
        {
            TryMenuDump(now);
            if (!MenuTest(now)) return;
            if (autoload == null || autoloadDone) return;
            if (SceneManager.GetActiveScene().name != "MainMenu" || now - sceneLoadedAt < 5f) return;
            autoloadDone = true;
            string path = Path.Combine(Application.persistentDataPath, autoload);
            if (!File.Exists(path))
            {
                Log.LogError("Autoload save not found: " + path);
                return;
            }
            string mode = ES3.Load<string>("mode", autoload, "Game");
            string hub = ES3.Load<string>("hubWorld", autoload, "Gedonia");
            Log.LogInfo("Autoloading " + autoload + " (mode=" + mode + ", world=" + hub + ")");
            PlayerPrefs.SetInt("IsNewGame", 0);
            PlayerPrefs.SetString("LoadGameName", autoload);
            PlayerPrefs.SetString("WorldToLoad", hub);
            SceneManager.LoadScene(mode);
        }

        // Test-only: walk a 4 m circle for 6 s, stop and attack, repeat, so the
        // other window has walking, idle and attack animations to mirror.
        void TickBot(PlayerScript player, float now)
        {
            if (bot == "fight" && player._char != null)
            {
                TickFightBot(player, now);
                return;
            }
            if (bot == "line" && player._char != null)
            {
                // Census walk: straight along the camera's start direction.
                if (botStart < 0f) { botStart = now + botDelay; return; }
                if (!botWalkLogged && now >= botStart) { botWalkLogged = true; Log.LogInfo(Stamp() + " Bot: walking away"); }
                if (now < botStart) return;
                BotInput.Active = true;
                BotInput.Horizontal = 0f;
                BotInput.Vertical = 1f;
                return;
            }
            if (bot != "circle" || player._char == null) return;
            if (botStart < 0f)
            {
                botStart = now + 20f; // let the world finish streaming in first
                return;
            }
            if (now < botStart)
            {
                botCenter = player.transform.position;
                return;
            }
            BotInput.Active = true;
            float cycle = (now - botStart) % 9f;
            if (cycle < 6f)
            {
                // Head for a point that runs ahead on a 4 m circle; the game
                // itself moves the player from the virtual stick.
                botAngle += 0.5f * Time.deltaTime;
                Vector3 target = botCenter + new Vector3(Mathf.Cos(botAngle + 0.6f), 0f, Mathf.Sin(botAngle + 0.6f)) * 4f;
                Vector3 delta = target - player.transform.position;
                delta.y = 0f;
                if (delta.magnitude > 0.3f) BotInput.Steer(delta.normalized); else BotInput.Stop();
            }
            else
            {
                BotInput.Stop();
                // Queue an attack through the game's own input buffer
                // (PlayerScript.inputQeue = Attack1), the same path a mouse
                // click takes, so the weapon is drawn and combos run as normal.
                int n = (int)((now - botStart) / 9f) + 1;
                if (cycle < 7f && botAttacks < n)
                {
                    Traverse iq = Traverse.Create(player).Field("inputQeue");
                    iq.SetValue(Enum.ToObject(iq.GetValueType(), 1));
                    if (now - botAttackQueued > 0.5f) { botAttackQueued = now; Log.LogInfo("Bot: attack #" + n); }
                    if (cycle > 6.5f) botAttacks = n;
                }
            }
        }

        // Test-only: walk to the nearest hostile enemy and fight it with the
        // game's own attack input, so hits on a mirrored enemy go to its owner.
        void TickFightBot(PlayerScript player, float now)
        {
            if (botStart < 0f) { botStart = now + 25f; return; }
            if (now < botStart || enemies == null) return;
            if (testStrong > 0f && player.meleeDamageKoefficent < testStrong) player.meleeDamageKoefficent = testStrong; // test-only
            if (testHp > 0 && !testHpDone && !player.isDead)
            {
                // Test-only: start the fight weak so an enemy's (forwarded) hit kills.
                testHpDone = true;
                Log.LogInfo(Stamp() + " Test: health " + player.health + " -> " + testHp);
                player.health = testHp;
            }
            BotInput.Active = true;
            int id;
            string name;
            GameObject target = enemies.NearestHostile(player.transform.position, 80f, out id, out name);
            if (target == null)
            {
                BotInput.Stop();
                if (now >= botNextLog) { botNextLog = now + 5f; Log.LogInfo("Fight bot: no hostile enemy within 80 m"); }
                return;
            }
            if (id != botTarget)
            {
                botTarget = id;
                Log.LogInfo(Stamp() + " Fight bot: target " + enemies.Describe(id));
            }
            Vector3 d = target.transform.position - player.transform.position;
            d.y = 0f;
            bool bow = player.mainWeapon != null && ((WeaponItem)player.mainWeapon.item).weaponType == WeaponType.Bow;
            if (bow)
            {
                // Archer: stop at range, lock on (arrows fly at the lock-on
                // target), aim and shoot through the game's own bow input.
                if (d.magnitude > 12f) { BotInput.Aim(false); BotInput.Steer(d.normalized); }
                else
                {
                    BotInput.Stop();
                    player.transform.rotation = Quaternion.LookRotation(d);
                    NPC npc = target.GetComponent<NPC>();
                    if (npc != null) CameraMovement.instance.lockOnTarget = npc;
                    BotInput.Aim(true);
                    if (now >= botNextAttack) { botNextAttack = now + 1.5f; BotInput.PressAttack(); }
                }
                if (now >= botNextLog) { botNextLog = now + 3f; Log.LogInfo("Fight bot (bow): dist=" + d.magnitude.ToString("F1") + " " + enemies.Describe(id) + " arrowsShot=" + ProjectileSync.Fired + " myHealth=" + player.health); }
                return;
            }
            if (d.magnitude > 2.2f)
            {
                BotInput.Steer(d.normalized);
            }
            else
            {
                BotInput.Stop();
                player.transform.rotation = Quaternion.LookRotation(d);
                if (now >= botNextAttack)
                {
                    botNextAttack = now + 0.6f;
                    Traverse iq = Traverse.Create(player).Field("inputQeue");
                    iq.SetValue(Enum.ToObject(iq.GetValueType(), 1));
                }
            }
            if (now >= botNextLog) { botNextLog = now + 3f; Log.LogInfo("Fight bot: dist=" + d.magnitude.ToString("F1") + " " + enemies.Describe(id) + " myHealth=" + player.health); }
        }

        void TickShots(float now)
        {
            if (shotsLeft <= 0) return;
            if (nextShot < 0f) { nextShot = now + 30f; return; }
            if (now < nextShot) return;
            nextShot = now + 5f;
            shotsLeft--;
            string dir = Path.Combine(Paths.BepInExRootPath, "coop-shots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, local.Name + "_" + (shotIndex++) + ".png");
            ScreenCapture.CaptureScreenshot(file);
            string where = "";
            foreach (Puppet p in puppets.Values) where += " " + p.Describe();
            Log.LogInfo("Screenshot: " + file + where);
        }

        void OnDestroy()
        {
            Log.LogWarning("Plugin object destroyed (scene change killed the BepInEx manager?)");
        }

        void OnApplicationQuit()
        {
            if (net != null) net.Dispose();
            StopLanResponder();
        }
    }
}
