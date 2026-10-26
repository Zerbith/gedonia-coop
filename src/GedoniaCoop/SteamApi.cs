using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace GedoniaCoop
{
    // The few Steam features co-op needs. RealSteam calls the game's own
    // Steamworks.NET (12.0.0, SDK 1.42); MockSteam fakes them between local
    // test copies, following the same rules. All events fire from Tick() on
    // the main thread.
    public class FriendInfo
    {
        public ulong Id;
        public string Name;
        public bool InGedonia;
        public ulong Lobby; // the co-op lobby the friend is in (0 = none)
    }

    public interface ISteamApi
    {
        bool Ready { get; }
        ulong MyId { get; }
        string MyName { get; }

        void CreateFriendsLobby(int maxMembers);
        void JoinLobby(ulong lobby);
        void LeaveLobby(ulong lobby);
        ulong LobbyOwner(ulong lobby);
        List<ulong> LobbyMembers(ulong lobby);
        void SetLobbyData(ulong lobby, string key, string value);
        string GetLobbyData(ulong lobby, string key);
        void SetRichPresence(string key, string value);
        void OpenInviteDialog(ulong lobby);
        // Online immediate friends, those playing Gedonia first.
        List<FriendInfo> OnlineFriends();
        // A normal Steam lobby invite; works without the overlay.
        bool InviteToLobby(ulong lobby, ulong friend);

        bool Send(ulong to, byte[] data, bool reliable);
        bool TryRead(out ulong from, out byte[] data);
        void AcceptSession(ulong user);
        void CloseSession(ulong user);

        event Action<ulong, bool> LobbyCreated;      // lobby, ok
        event Action<ulong, bool> LobbyEntered;      // lobby, ok
        event Action<ulong, ulong> JoinRequested;    // lobby, friend (accepted an invite / Join Game)
        event Action<ulong, ulong, bool> MemberChanged; // lobby, user, joined (false = left)
        event Action<ulong> SessionRequested;        // first packet from an unknown user
        event Action<ulong, ulong> InviteReceived;   // lobby, from (a friend invited us; not yet accepted)

        void Tick();
    }

    public class RealSteam : ISteamApi
    {
        Callback<LobbyCreated_t> cbCreated;
        Callback<LobbyEnter_t> cbEnter;
        Callback<GameLobbyJoinRequested_t> cbJoinReq;
        Callback<LobbyChatUpdate_t> cbChat;
        Callback<P2PSessionRequest_t> cbSession;
        Callback<P2PSessionConnectFail_t> cbFail;
        Callback<GameOverlayActivated_t> cbOverlay;
        Callback<LobbyInvite_t> cbInvite;
        byte[] buffer = new byte[4096];
        bool hooked;

        public event Action<ulong, bool> LobbyCreated;
        public event Action<ulong, bool> LobbyEntered;
        public event Action<ulong, ulong> JoinRequested;
        public event Action<ulong, ulong, bool> MemberChanged;
        public event Action<ulong> SessionRequested;
        public event Action<ulong, ulong> InviteReceived;

        public bool Ready { get { return SteamManager.Initialized; } }
        public ulong MyId { get { return SteamUser.GetSteamID().m_SteamID; } }
        public string MyName { get { return SteamFriends.GetPersonaName(); } }

        // Callbacks need Steam initialised; the game's SteamManager does that
        // and pumps SteamAPI.RunCallbacks every frame.
        void Hook()
        {
            if (hooked || !Ready) return;
            hooked = true;
            SteamNetworking.AllowP2PPacketRelay(true);
            // LobbyCreated also arrives as a call result on real Steam; the
            // broadcast callback is enough, and the transport is idempotent.
            cbCreated = Callback<LobbyCreated_t>.Create(r =>
            {
                if (LobbyCreated != null) LobbyCreated(r.m_ulSteamIDLobby, r.m_eResult == EResult.k_EResultOK);
            });
            cbEnter = Callback<LobbyEnter_t>.Create(r =>
            {
                if (LobbyEntered != null) LobbyEntered(r.m_ulSteamIDLobby, r.m_EChatRoomEnterResponse == 1);
            });
            cbJoinReq = Callback<GameLobbyJoinRequested_t>.Create(r =>
            {
                if (JoinRequested != null) JoinRequested(r.m_steamIDLobby.m_SteamID, r.m_steamIDFriend.m_SteamID);
            });
            cbChat = Callback<LobbyChatUpdate_t>.Create(r =>
            {
                // 0x1 entered; 0x2 left, 0x4 disconnected, 0x8 kicked, 0x10 banned
                bool joined = (r.m_rgfChatMemberStateChange & 0x1) != 0;
                if (MemberChanged != null) MemberChanged(r.m_ulSteamIDLobby, r.m_ulSteamIDUserChanged, joined);
            });
            cbSession = Callback<P2PSessionRequest_t>.Create(r =>
            {
                if (SessionRequested != null) SessionRequested(r.m_steamIDRemote.m_SteamID);
            });
            cbFail = Callback<P2PSessionConnectFail_t>.Create(r =>
            {
                Plugin.Log.LogWarning("Steam P2P session with " + r.m_steamIDRemote.m_SteamID + " failed (error " + r.m_eP2PSessionError + ")");
            });
            cbOverlay = Callback<GameOverlayActivated_t>.Create(r =>
            {
                Plugin.Log.LogInfo(Plugin.Stamp() + " Steam overlay " + (r.m_bActive != 0 ? "opened" : "closed"));
            });
            cbInvite = Callback<LobbyInvite_t>.Create(r =>
            {
                // Only invites to this game (the low 24 bits of a game id are the AppID).
                uint app = (uint)(r.m_ulGameID & 0xFFFFFF);
                if (app != SteamUtils.GetAppID().m_AppId) return;
                if (InviteReceived != null) InviteReceived(r.m_ulSteamIDLobby, r.m_ulSteamIDUser);
            });
        }

        public List<FriendInfo> OnlineFriends()
        {
            List<FriendInfo> list = new List<FriendInfo>();
            if (!Ready) return list;
            uint myApp = SteamUtils.GetAppID().m_AppId;
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                CSteamID f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendPersonaState(f) == EPersonaState.k_EPersonaStateOffline) continue;
                FriendGameInfo_t game;
                bool inGedonia = SteamFriends.GetFriendGamePlayed(f, out game) && game.m_gameID.AppID().m_AppId == myApp;
                list.Add(new FriendInfo { Id = f.m_SteamID, Name = SteamFriends.GetFriendPersonaName(f), InGedonia = inGedonia, Lobby = inGedonia ? game.m_steamIDLobby.m_SteamID : 0 });
            }
            list.Sort((a, b) => a.InGedonia != b.InGedonia ? (a.InGedonia ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public bool InviteToLobby(ulong lobby, ulong friend)
        {
            return SteamMatchmaking.InviteUserToLobby(new CSteamID(lobby), new CSteamID(friend));
        }

        // Read back from Steam what the host set up, for the real-Steam self-test.
        public string Report(ulong lobby)
        {
            CSteamID id = new CSteamID(lobby);
            CSteamID me = SteamUser.GetSteamID();
            return "appId=" + SteamUtils.GetAppID().m_AppId +
                " lobbyOwnerIsMe=" + (SteamMatchmaking.GetLobbyOwner(id) == me) +
                " members=" + SteamMatchmaking.GetNumLobbyMembers(id) +
                " tag='" + SteamMatchmaking.GetLobbyData(id, SteamTransport.LobbyKey) + "'" +
                " richPresence.connect='" + SteamFriends.GetFriendRichPresence(me, "connect") + "'" +
                " richPresence.status='" + SteamFriends.GetFriendRichPresence(me, "status") + "'" +
                " overlayEnabled=" + SteamUtils.IsOverlayEnabled();
        }

        public void Tick() { Hook(); }

        public void CreateFriendsLobby(int maxMembers) { Hook(); SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxMembers); }
        public void JoinLobby(ulong lobby) { Hook(); SteamMatchmaking.JoinLobby(new CSteamID(lobby)); }
        public void LeaveLobby(ulong lobby) { SteamMatchmaking.LeaveLobby(new CSteamID(lobby)); }
        public ulong LobbyOwner(ulong lobby) { return SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID; }

        public List<ulong> LobbyMembers(ulong lobby)
        {
            List<ulong> list = new List<ulong>();
            CSteamID id = new CSteamID(lobby);
            int n = SteamMatchmaking.GetNumLobbyMembers(id);
            for (int i = 0; i < n; i++) list.Add(SteamMatchmaking.GetLobbyMemberByIndex(id, i).m_SteamID);
            return list;
        }

        public void SetLobbyData(ulong lobby, string key, string value) { SteamMatchmaking.SetLobbyData(new CSteamID(lobby), key, value); }
        public string GetLobbyData(ulong lobby, string key) { return SteamMatchmaking.GetLobbyData(new CSteamID(lobby), key); }
        public void SetRichPresence(string key, string value) { SteamFriends.SetRichPresence(key, value); }
        public void OpenInviteDialog(ulong lobby) { SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(lobby)); }

        public bool Send(ulong to, byte[] data, bool reliable)
        {
            return SteamNetworking.SendP2PPacket(new CSteamID(to), data, (uint)data.Length,
                reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay, 0);
        }

        public bool TryRead(out ulong from, out byte[] data)
        {
            from = 0;
            data = null;
            uint size;
            if (!SteamNetworking.IsP2PPacketAvailable(out size, 0)) return false;
            if (size > buffer.Length) buffer = new byte[size];
            CSteamID remote;
            if (!SteamNetworking.ReadP2PPacket(buffer, size, out size, out remote, 0)) return false;
            data = new byte[size];
            Buffer.BlockCopy(buffer, 0, data, 0, (int)size);
            from = remote.m_SteamID;
            return true;
        }

        public void AcceptSession(ulong user) { SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(user)); }
        public void CloseSession(ulong user) { SteamNetworking.CloseP2PSessionWithUser(new CSteamID(user)); }
    }

    // Test-only stand-in for Steam between copies on one PC (one PC can only
    // run one Steam account). Player n is SteamID 900000000+n on UDP port
    // 47000+n; the lobby of player n is 0x0186000000000000+n. Like Steam it
    // holds a stranger's packets until AcceptSession, joins a lobby by asking
    // its owner, and fires LobbyCreated/LobbyEntered/MemberChanged.
    public class MockSteam : ISteamApi
    {
        const ulong IdBase = 900000000;
        const ulong LobbyBase = 0x0186000000000000;
        const int PortBase = 47000;
        const byte KData = 0, KJoin = 1, KJoinReply = 2, KLeave = 3, KInvite = 4;

        readonly int n;
        readonly UdpClient udp;
        readonly Queue<Action> events = new Queue<Action>();
        readonly Queue<KeyValuePair<ulong, byte[]>> inbox = new Queue<KeyValuePair<ulong, byte[]>>();
        readonly HashSet<ulong> accepted = new HashSet<ulong>();
        readonly Dictionary<ulong, List<byte[]>> held = new Dictionary<ulong, List<byte[]>>();
        // Lobbies this copy owns or has joined: members and data.
        readonly Dictionary<ulong, List<ulong>> members = new Dictionary<ulong, List<ulong>>();
        readonly Dictionary<ulong, Dictionary<string, string>> lobbyData = new Dictionary<ulong, Dictionary<string, string>>();
        readonly float loss;
        readonly Random rng = new Random();
        float inviteAt = -1f;
        ulong inviteLobby;
        readonly List<int> friends;

        public event Action<ulong, bool> LobbyCreated;
        public event Action<ulong, bool> LobbyEntered;
        public event Action<ulong, ulong> JoinRequested;
        public event Action<ulong, ulong, bool> MemberChanged;
        public event Action<ulong> SessionRequested;
        public event Action<ulong, ulong> InviteReceived;

        // friendNumbers: the mock players listed as this player's online friends.
        public MockSteam(int playerNumber, float unreliableLoss, List<int> friendNumbers)
        {
            n = playerNumber;
            loss = unreliableLoss;
            friends = friendNumbers;
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, PortBase + n));
            try { udp.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
            udp.Client.Blocking = false;
            Plugin.Log.LogWarning("MOCK STEAM: player " + n + " = SteamID " + MyId + ", UDP 127.0.0.1:" + (PortBase + n) +
                (loss > 0f ? ", unreliable loss " + (loss * 100f).ToString("F0") + "%" : ""));
        }

        public static ulong LobbyOf(int playerNumber) { return LobbyBase + (ulong)playerNumber; }

        // Fakes "friend accepted an invite in Steam" after a delay.
        public void ScheduleInvite(ulong lobby, float at) { inviteLobby = lobby; inviteAt = at; }

        public bool Ready { get { return true; } }
        public ulong MyId { get { return IdBase + (ulong)n; } }
        public string MyName { get { return "MockPlayer" + n; } }

        static IPEndPoint PortOf(ulong steamId) { return new IPEndPoint(IPAddress.Loopback, PortBase + (int)(steamId - IdBase)); }

        void Raw(ulong to, byte kind, byte[] payload)
        {
            byte[] pkt = new byte[9 + payload.Length];
            pkt[0] = kind;
            BitConverter.GetBytes(MyId).CopyTo(pkt, 1);
            payload.CopyTo(pkt, 9);
            try { udp.Send(pkt, pkt.Length, PortOf(to)); } catch (SocketException) { }
        }

        public void CreateFriendsLobby(int maxMembers)
        {
            ulong lobby = LobbyOf(n);
            members[lobby] = new List<ulong> { MyId };
            lobbyData[lobby] = new Dictionary<string, string>();
            // Real Steam delivers LobbyCreated twice (call result + broadcast).
            events.Enqueue(() => { if (LobbyCreated != null) LobbyCreated(lobby, true); });
            events.Enqueue(() => { if (LobbyCreated != null) LobbyCreated(lobby, true); });
            events.Enqueue(() => { if (LobbyEntered != null) LobbyEntered(lobby, true); });
        }

        public void JoinLobby(ulong lobby)
        {
            ulong owner = IdBase + (lobby - LobbyBase);
            Raw(owner, KJoin, BitConverter.GetBytes(lobby));
        }

        public void LeaveLobby(ulong lobby)
        {
            List<ulong> m;
            if (members.TryGetValue(lobby, out m))
                foreach (ulong u in m) if (u != MyId) Raw(u, KLeave, BitConverter.GetBytes(lobby));
            members.Remove(lobby);
        }

        public ulong LobbyOwner(ulong lobby) { return IdBase + (lobby - LobbyBase); }

        public List<ulong> LobbyMembers(ulong lobby)
        {
            List<ulong> m;
            return members.TryGetValue(lobby, out m) ? new List<ulong>(m) : new List<ulong>();
        }

        public void SetLobbyData(ulong lobby, string key, string value)
        {
            Dictionary<string, string> d;
            if (lobbyData.TryGetValue(lobby, out d)) d[key] = value;
        }

        public string GetLobbyData(ulong lobby, string key)
        {
            Dictionary<string, string> d;
            string v;
            return lobbyData.TryGetValue(lobby, out d) && d.TryGetValue(key, out v) ? v : "";
        }

        public void SetRichPresence(string key, string value) { Plugin.Log.LogInfo("MOCK STEAM: rich presence " + key + "=" + value); }
        public void OpenInviteDialog(ulong lobby) { Plugin.Log.LogInfo("MOCK STEAM: invite dialog for lobby " + lobby); }

        public List<FriendInfo> OnlineFriends()
        {
            List<FriendInfo> list = new List<FriendInfo>();
            foreach (int f in friends)
                list.Add(new FriendInfo { Id = IdBase + (ulong)f, Name = "MockPlayer" + f, InGedonia = f % 2 == 0, Lobby = LobbyOf(f) }); // mock: anyone may be hosting
            return list;
        }

        // Steam delivers the invite to the friend's client, which tells their
        // running game (LobbyInvite_t); the mock sends it straight there.
        public bool InviteToLobby(ulong lobby, ulong friend)
        {
            if (!friends.Contains((int)(friend - IdBase))) return false;
            Raw(friend, KInvite, BitConverter.GetBytes(lobby));
            return true;
        }

        public bool Send(ulong to, byte[] data, bool reliable)
        {
            if (data.Length > (reliable ? 1024 * 1024 : 1200)) return false; // Steam's P2P limits
            if (!reliable && loss > 0f && rng.NextDouble() < loss) return true; // sent, then lost
            Raw(to, KData, data);
            return true;
        }

        public bool TryRead(out ulong from, out byte[] data)
        {
            if (inbox.Count == 0) { from = 0; data = null; return false; }
            KeyValuePair<ulong, byte[]> p = inbox.Dequeue();
            from = p.Key;
            data = p.Value;
            return true;
        }

        public void AcceptSession(ulong user)
        {
            if (!accepted.Add(user)) return;
            List<byte[]> h;
            if (held.TryGetValue(user, out h))
            {
                foreach (byte[] d in h) inbox.Enqueue(new KeyValuePair<ulong, byte[]>(user, d));
                held.Remove(user);
            }
        }

        public void CloseSession(ulong user) { accepted.Remove(user); }

        public void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (inviteAt > 0f && now >= inviteAt)
            {
                inviteAt = -1f;
                ulong lobby = inviteLobby;
                Plugin.Log.LogInfo("MOCK STEAM: simulating an accepted invite to lobby " + lobby);
                if (JoinRequested != null) JoinRequested(lobby, LobbyOwner(lobby));
            }

            byte[] buf = new byte[65536];
            while (true)
            {
                int got;
                EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                try { got = udp.Client.ReceiveFrom(buf, ref ep); }
                catch (SocketException) { break; } // WouldBlock: nothing more
                if (got < 9) continue;
                byte kind = buf[0];
                ulong from = BitConverter.ToUInt64(buf, 1);
                byte[] payload = new byte[got - 9];
                Buffer.BlockCopy(buf, 9, payload, 0, payload.Length);
                Handle(kind, from, payload);
            }

            while (events.Count > 0) events.Dequeue()();
        }

        void Handle(byte kind, ulong from, byte[] payload)
        {
            if (kind == KData)
            {
                if (accepted.Contains(from)) { inbox.Enqueue(new KeyValuePair<ulong, byte[]>(from, payload)); return; }
                List<byte[]> h;
                if (!held.TryGetValue(from, out h))
                {
                    held[from] = h = new List<byte[]>();
                    ulong who = from;
                    events.Enqueue(() => { if (SessionRequested != null) SessionRequested(who); });
                }
                if (h.Count < 256) h.Add(payload);
                return;
            }
            ulong lobby = BitConverter.ToUInt64(payload, 0);
            if (kind == KInvite)
            {
                ulong who = from;
                events.Enqueue(() => { if (InviteReceived != null) InviteReceived(lobby, who); });
                return;
            }
            if (kind == KJoin)
            {
                List<ulong> m;
                bool ok = members.TryGetValue(lobby, out m) && LobbyOwner(lobby) == MyId;
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(lobby); w.Write(ok);
                    if (ok)
                    {
                        if (!m.Contains(from)) m.Add(from);
                        w.Write(m.Count); foreach (ulong u in m) w.Write(u);
                        Dictionary<string, string> d = lobbyData[lobby];
                        w.Write(d.Count); foreach (KeyValuePair<string, string> kv in d) { w.Write(kv.Key); w.Write(kv.Value); }
                        ulong who = from;
                        events.Enqueue(() => { if (MemberChanged != null) MemberChanged(lobby, who, true); });
                    }
                    w.Flush();
                    Raw(from, KJoinReply, ms.ToArray());
                }
            }
            else if (kind == KJoinReply)
            {
                using (BinaryReader r = new BinaryReader(new MemoryStream(payload)))
                {
                    r.ReadUInt64();
                    bool ok = r.ReadBoolean();
                    if (ok)
                    {
                        List<ulong> m = new List<ulong>();
                        int count = r.ReadInt32();
                        for (int i = 0; i < count; i++) m.Add(r.ReadUInt64());
                        Dictionary<string, string> d = new Dictionary<string, string>();
                        count = r.ReadInt32();
                        for (int i = 0; i < count; i++) d[r.ReadString()] = r.ReadString();
                        members[lobby] = m;
                        lobbyData[lobby] = d;
                    }
                    events.Enqueue(() => { if (LobbyEntered != null) LobbyEntered(lobby, ok); });
                }
            }
            else if (kind == KLeave)
            {
                List<ulong> m;
                if (members.TryGetValue(lobby, out m)) m.Remove(from);
                ulong who = from;
                events.Enqueue(() => { if (MemberChanged != null) MemberChanged(lobby, who, false); });
            }
        }
    }
}
