using System;
using System.Collections.Generic;
using System.IO;

namespace GedoniaCoop
{
    // Co-op over Steam: a friends-only lobby finds the host, and packets go by
    // SteamID through Steam P2P, which falls back to Valve's relay when a direct
    // connection fails. No IP addresses and no port forwarding.
    //
    // Host: creates the lobby, tags it, sets rich presence so friends can
    // "Join Game", and only accepts P2P sessions from lobby members.
    // Client: joins a lobby (by id, by accepted invite or by +connect_lobby),
    // checks the tag, and only accepts the lobby owner.
    public class SteamTransport : ITransport
    {
        public const string LobbyKey = "gedonia_coop";
        public const string LobbyTag = "1"; // bump when the protocol changes incompatibly

        readonly ISteamApi api;
        readonly bool isHost;
        ulong lobby;          // 0 until created / entered
        ulong pendingJoin;    // lobby to join once Steam is ready
        object host;          // boxed SteamID of the lobby owner (client)
        bool started;
        string status = "starting Steam";
        readonly HashSet<ulong> accepted = new HashSet<ulong>();
        readonly Dictionary<ulong, float> pending = new Dictionary<ulong, float>(); // session requests from not-yet-members

        public event Action<ulong> LobbyReady; // host: lobby id, once

        // Client: the latest invite not yet answered (0 = none).
        public ulong InviteLobby;
        public ulong InviteFrom;
        public string InviteFromName = "";
        public readonly HashSet<ulong> Invited = new HashSet<ulong>(); // host: friends invited this session

        public SteamTransport(ISteamApi api, bool isHost, ulong joinLobby)
        {
            this.api = api;
            this.isHost = isHost;
            pendingJoin = joinLobby;
            api.LobbyCreated += OnLobbyCreated;
            api.LobbyEntered += OnLobbyEntered;
            api.JoinRequested += OnJoinRequested;
            api.MemberChanged += OnMemberChanged;
            api.SessionRequested += OnSessionRequested;
            api.InviteReceived += OnInviteReceived;
        }

        public ISteamApi Api { get { return api; } }

        // Host: invite a friend from the in-game list.
        public bool InviteFriend(FriendInfo f)
        {
            if (!isHost || lobby == 0) return false;
            bool ok = api.InviteToLobby(lobby, f.Id);
            Plugin.Log.LogInfo(Plugin.Stamp() + " Invite to '" + f.Name + "' (" + f.Id + ") " + (ok ? "sent" : "FAILED"));
            if (ok) Invited.Add(f.Id);
            return ok;
        }

        void OnInviteReceived(ulong id, ulong from)
        {
            if (isHost || lobby == id) return;
            InviteLobby = id;
            InviteFrom = from;
            InviteFromName = from.ToString();
            foreach (FriendInfo f in api.OnlineFriends()) if (f.Id == from) InviteFromName = f.Name;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Invite received from '" + InviteFromName + "' to lobby " + id);
        }

        // Client: F9 on the in-game invite prompt.
        public void AcceptInvite()
        {
            if (InviteLobby == 0) return;
            ulong id = InviteLobby;
            Plugin.Log.LogInfo(Plugin.Stamp() + " Accepting invite from '" + InviteFromName + "'");
            InviteLobby = 0;
            OnJoinRequested(id, InviteFrom);
        }

        public void DeclineInvite()
        {
            Plugin.Log.LogInfo("Declined invite from '" + InviteFromName + "'");
            InviteLobby = 0;
        }

        public bool IsHost { get { return isHost; } }
        public object HostEndpoint { get { return host; } }
        public ulong Lobby { get { return lobby; } }
        public bool Joined { get { return host != null; } }
        public string Status { get { return status; } }

        public void Tick()
        {
            api.Tick();
            if (!started && api.Ready)
            {
                started = true;
                Plugin.Log.LogInfo("Steam ready: " + api.MyName + " (" + api.MyId + ")");
                if (isHost)
                {
                    status = "creating Steam lobby";
                    api.CreateFriendsLobby(4);
                }
                else if (pendingJoin != 0)
                {
                    Join(pendingJoin);
                }
                else
                {
                    status = "waiting for a Steam invite";
                }
            }
            // A friend's first packet can beat the lobby-join notice; re-check
            // their membership for a few seconds before giving up on them.
            if (pending.Count > 0)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                List<ulong> done = new List<ulong>();
                foreach (KeyValuePair<ulong, float> p in pending)
                {
                    if (IsMember(p.Key)) { Accept(p.Key); done.Add(p.Key); }
                    else if (now - p.Value > 10f)
                    {
                        Plugin.Log.LogWarning("Ignoring Steam user " + p.Key + ": not in the co-op lobby");
                        done.Add(p.Key);
                    }
                }
                foreach (ulong u in done) pending.Remove(u);
            }
        }

        void Join(ulong id)
        {
            status = "joining Steam lobby " + id;
            Plugin.Log.LogInfo("Joining Steam lobby " + id);
            api.JoinLobby(id);
        }

        void OnLobbyCreated(ulong id, bool ok)
        {
            if (!isHost) return;
            if (!ok) { status = "could not create a Steam lobby"; Plugin.Log.LogError(status); return; }
            if (lobby == id) return; // Steam sends this twice
            lobby = id;
            api.SetLobbyData(id, LobbyKey, LobbyTag);
            api.SetRichPresence("connect", "+connect_lobby " + id);
            api.SetRichPresence("status", "Hosting Gedonia co-op");
            status = "hosting Steam lobby " + id;
            Plugin.Log.LogInfo("Steam lobby created: " + id);
            if (LobbyReady != null) LobbyReady(id);
        }

        void OnLobbyEntered(ulong id, bool ok)
        {
            if (isHost) return; // the creator also "enters" its own lobby
            if (!ok) { status = "could not join Steam lobby " + id; Plugin.Log.LogError(status); return; }
            if (api.GetLobbyData(id, LobbyKey) != LobbyTag)
            {
                status = "lobby " + id + " is not a Gedonia co-op game (or a different mod version)";
                Plugin.Log.LogError(status);
                api.LeaveLobby(id);
                return;
            }
            lobby = id;
            ulong owner = api.LobbyOwner(id);
            host = owner;
            Accept(owner);
            api.SetRichPresence("connect", "+connect_lobby " + id);
            status = "in Steam lobby " + id + ", host " + owner;
            Plugin.Log.LogInfo("Joined Steam lobby " + id + "; host is " + owner);
        }

        void OnJoinRequested(ulong id, ulong friend)
        {
            Plugin.Log.LogInfo("Steam invite accepted: lobby " + id + " from " + friend);
            if (isHost) { Plugin.Log.LogWarning("Already hosting; ignoring the invite"); return; }
            if (InviteLobby == id) InviteLobby = 0; // answered in Steam instead of in game
            if (lobby == id) return;
            if (lobby != 0) { api.LeaveLobby(lobby); host = null; lobby = 0; }
            Join(id);
        }

        void OnMemberChanged(ulong id, ulong user, bool joined)
        {
            if (id != lobby || user == api.MyId) return;
            Plugin.Log.LogInfo("Steam lobby member " + user + (joined ? " joined" : " left"));
            if (!joined)
            {
                accepted.Remove(user);
                api.CloseSession(user);
                if (!isHost && host != null && (ulong)host == user)
                {
                    status = "host left the lobby";
                    host = null;
                }
            }
        }

        void OnSessionRequested(ulong user)
        {
            if (accepted.Contains(user)) return;
            if (isHost ? IsMember(user) : host != null && (ulong)host == user) Accept(user);
            else if (isHost) pending[user] = UnityEngine.Time.realtimeSinceStartup;
            else Plugin.Log.LogWarning("Ignoring Steam user " + user + ": not our host");
        }

        bool IsMember(ulong user)
        {
            return lobby != 0 && api.LobbyMembers(lobby).Contains(user);
        }

        void Accept(ulong user)
        {
            if (!accepted.Add(user)) return;
            api.AcceptSession(user);
            Plugin.Log.LogInfo("Accepted Steam P2P session with " + user);
        }

        public bool Send(object endpoint, byte[] data, bool reliable)
        {
            return api.Send((ulong)endpoint, data, reliable);
        }

        public void Receive(List<KeyValuePair<object, byte[]>> into)
        {
            ulong from;
            byte[] data;
            int guard = 0;
            while (guard++ < 512 && api.TryRead(out from, out data))
            {
                if (accepted.Contains(from)) into.Add(new KeyValuePair<object, byte[]>(from, data));
            }
        }

        public void Dispose()
        {
            if (lobby != 0) api.LeaveLobby(lobby);
            try { api.SetRichPresence("connect", ""); api.SetRichPresence("status", ""); } catch { }
            foreach (ulong u in accepted) api.CloseSession(u);
            Plugin.Log.LogInfo("Steam transport closed: left lobby " + lobby + ", cleared rich presence, closed " + accepted.Count + " session(s)");
        }
    }
}
