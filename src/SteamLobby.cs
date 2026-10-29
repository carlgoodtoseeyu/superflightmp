using System;
using System.Globalization;
using Steamworks;
using SuperflightCoop.Net;

namespace SuperflightCoop
{
    /// <summary>Friends-only Steam lobby around a session: the host creates one and invites through the overlay, a
    /// friend accepts (overlay while the game runs, or Steam starts the game with "+connect_lobby ID") and joins the
    /// lobby owner over <see cref="SteamTransport"/>. The lobby is only a meeting point: membership gates which
    /// SteamIDs the host accepts packets from, and a member leaving the lobby drops them from the session.</summary>
    public static class SteamLobby
    {
        public const string GameKey = "game", GameValue = "superflight-coop", ProtocolKey = "protocol";
        public const int MaxMembers = 8;

        public static CSteamID Lobby = CSteamID.Nil;
        public static CSteamID Owner = CSteamID.Nil;
        public static string Status = "";
        public static ulong RequestedLobby;        // from +connect_lobby or the overlay, joined once the game is ready
        static bool registered, creating;
        static CSteamID joining = CSteamID.Nil;
        static Callback<LobbyEnter_t> onEnter;
        static Callback<GameLobbyJoinRequested_t> onJoinRequested;
        static Callback<LobbyChatUpdate_t> onChat;
        static CallResult<LobbyCreated_t> onCreated;

        public static bool Ready
        {
            get { try { return SteamManager.Initialized; } catch { return false; } }
        }

        public static bool InLobby { get { return Lobby.m_SteamID != 0; } }
        public static bool Busy { get { return creating || joining.m_SteamID != 0; } }
        public static string LobbyCode { get { return InLobby ? Lobby.m_SteamID.ToString(CultureInfo.InvariantCulture) : ""; } }

        public static int MemberCount
        {
            get { try { return InLobby ? SteamMatchmaking.GetNumLobbyMembers(Lobby) : 0; } catch { return 0; } }
        }

        /// <summary>Call every frame from the main thread: registers the callbacks once Steam is up and starts a
        /// requested join once the game can take it.</summary>
        public static void Update()
        {
            if (!registered && Ready) Register();
            if (RequestedLobby != 0 && Ready && LocalGameManager.Singleton != null)
            {
                ulong id = RequestedLobby;
                RequestedLobby = 0;
                CoopManager.Instance.JoinSteam(id);
            }
        }

        static void Register()
        {
            registered = true;
            onEnter = Callback<LobbyEnter_t>.Create(OnEnter);
            onJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            onChat = Callback<LobbyChatUpdate_t>.Create(OnChat);
            onCreated = CallResult<LobbyCreated_t>.Create(OnCreated);
            Plugin.Log.LogInfo("steam: lobby callbacks registered");
        }

        public static bool Host()
        {
            if (!Ready) { Status = "Steam is not running"; return false; }
            if (!registered) Register();
            Leave();
            creating = true;
            Status = "creating Steam lobby...";
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxMembers);
            onCreated.Set(call, OnCreated);
            return true;
        }

        static void OnCreated(LobbyCreated_t r, bool ioFailure)
        {
            creating = false;
            if (ioFailure || r.m_eResult != EResult.k_EResultOK)
            {
                Status = "Steam lobby failed: " + (ioFailure ? "IO failure" : r.m_eResult.ToString());
                CoopManager.Instance.Session.LastError = Status;
                Plugin.Log.LogError(Status);
                return;
            }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            Owner = SteamUser.GetSteamID();
            SteamMatchmaking.SetLobbyData(Lobby, GameKey, GameValue);
            SteamMatchmaking.SetLobbyData(Lobby, ProtocolKey, Protocol.Version.ToString(CultureInfo.InvariantCulture));
            SteamMatchmaking.SetLobbyData(Lobby, "plugin", Plugin.Version);
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + LobbyCode);
            Status = "hosting Steam lobby";
            Plugin.Log.LogInfo("steam: lobby " + LobbyCode + " created (friends only, max " + MaxMembers + ")");
            CoopManager.Instance.OnLobbyHosted();
        }

        public static bool Join(ulong lobbyId)
        {
            if (!Ready) { Status = "Steam is not running"; return false; }
            if (lobbyId == 0) { Status = "no lobby id"; return false; }
            if (!registered) Register();
            Leave();
            joining = new CSteamID(lobbyId);
            Status = "joining Steam lobby...";
            Plugin.Log.LogInfo("steam: joining lobby " + lobbyId);
            SteamMatchmaking.JoinLobby(joining);
            return true;
        }

        static void OnEnter(LobbyEnter_t e)
        {
            if (joining.m_SteamID == 0 || e.m_ulSteamIDLobby != joining.m_SteamID) return; // our own creation, or stale
            var id = joining;
            joining = CSteamID.Nil;
            if (e.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Status = "could not enter the lobby (" + (EChatRoomEnterResponse)e.m_EChatRoomEnterResponse + ")";
                CoopManager.Instance.Session.LastError = Status;
                Plugin.Log.LogWarning(Status);
                return;
            }
            string game = SteamMatchmaking.GetLobbyData(id, GameKey);
            string proto = SteamMatchmaking.GetLobbyData(id, ProtocolKey);
            if (game != GameValue || proto != Protocol.Version.ToString(CultureInfo.InvariantCulture))
            {
                SteamMatchmaking.LeaveLobby(id);
                Status = game != GameValue ? "that lobby is not a Superflight co-op lobby" : "the host runs protocol " + proto + ", you have " + Protocol.Version;
                CoopManager.Instance.Session.LastError = Status;
                Plugin.Log.LogWarning(Status);
                return;
            }
            Lobby = id;
            Owner = SteamMatchmaking.GetLobbyOwner(id);
            Status = "in Steam lobby, connecting to the host";
            Plugin.Log.LogInfo("steam: entered lobby " + LobbyCode + ", owner " + Owner.m_SteamID);
            CoopManager.Instance.OnLobbyEntered(Owner);
        }

        static void OnJoinRequested(GameLobbyJoinRequested_t r)
        {
            Plugin.Log.LogInfo("steam: join requested through the overlay, lobby " + r.m_steamIDLobby.m_SteamID);
            RequestedLobby = r.m_steamIDLobby.m_SteamID;
        }

        static void OnChat(LobbyChatUpdate_t u)
        {
            if (!InLobby || u.m_ulSteamIDLobby != Lobby.m_SteamID) return;
            const uint gone = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                | EChatMemberStateChange.k_EChatMemberStateChangeKicked | EChatMemberStateChange.k_EChatMemberStateChangeBanned);
            if ((u.m_rgfChatMemberStateChange & gone) == 0) return;
            var session = CoopManager.Instance.Session;
            if (session.CurrentRole == Session.Role.Host)
                session.DropPeer(u.m_ulSteamIDUserChanged.ToString(CultureInfo.InvariantCulture), "left the lobby");
            else if (u.m_ulSteamIDUserChanged == Owner.m_SteamID)
            {
                session.LastError = "the host left the lobby";
                Plugin.Log.LogInfo("steam: " + session.LastError);
                CoopManager.Instance.Leave();
            }
        }

        /// <summary>Host side: only lobby members may open a P2P session with us.</summary>
        public static bool IsMember(CSteamID id)
        {
            if (!InLobby) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i).m_SteamID == id.m_SteamID) return true;
            return false;
        }

        public static bool Invite()
        {
            if (!InLobby) return false;
            SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
            return true;
        }

        public static void Leave()
        {
            joining = CSteamID.Nil;
            if (!InLobby) return;
            try
            {
                SteamMatchmaking.LeaveLobby(Lobby);
                SteamFriends.ClearRichPresence();
            }
            catch (Exception e) { Plugin.Log.LogWarning("steam: leave: " + e.Message); }
            Plugin.Log.LogInfo("steam: left lobby " + LobbyCode);
            Lobby = CSteamID.Nil;
            Owner = CSteamID.Nil;
            Status = "";
        }
    }
}
