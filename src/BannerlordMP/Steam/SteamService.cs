using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;

namespace BannerlordMP.Steam
{
    public enum LobbyVisibility : byte
    {
        /// <summary>No Steam lobby; players join by IP or LAN.</summary>
        Off = 0,
        /// <summary>Only people you invite through the Steam overlay.</summary>
        InviteOnly = 1,
        /// <summary>Your Steam friends see the server in their browser and can join.</summary>
        FriendsOnly = 2,
        /// <summary>Anyone with the mod sees the server in the browser.</summary>
        Public = 3,
    }

    internal sealed class SteamLobbyEntry
    {
        public ulong LobbyId;
        public ulong HostSteamId;
        public string Name;
        public bool PasswordRequired;
        public int PlayersOnline;
        public int UsedSlots;
        public int MaxSlots;
        public bool IsFriend;
    }

    /// <summary>
    /// Steam lobbies are only used as a signpost: they carry the host's Steam id and server details for the
    /// browser and the invite overlay. The game itself runs over <see cref="Net.SteamTransport"/>.
    /// </summary>
    internal static class SteamService
    {
        public const string LobbyTag = "bannerlordmp";
        private const string KeyTag = "bmp";
        private const string KeyProtocol = "bmp_protocol";

        private static bool? _available;
        private static CallResult<LobbyCreated_t> _lobbyCreated;
        private static CallResult<LobbyMatchList_t> _lobbyList;
        private static CallResult<LobbyEnter_t> _lobbyEntered;
        private static Callback<GameLobbyJoinRequested_t> _joinRequested;
        private static CSteamID _hostedLobby = CSteamID.Nil;

        /// <summary>Raised when the player accepts a Steam invite (or clicks "Join game"). Arguments: host Steam id, server name.</summary>
        public static event Action<ulong, string> JoinRequested;

        public static bool Available
        {
            get
            {
                if (_available == null)
                {
                    try
                    {
                        _available = SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid();
                    }
                    catch (Exception e)
                    {
                        // Not the Steam version of the game, or Steam not initialized.
                        Log.Info("Steam not available: " + e.Message);
                        _available = false;
                    }
                }
                return _available.Value;
            }
        }

        public static ulong MySteamId => Available ? SteamUser.GetSteamID().m_SteamID : 0;

        public static string PersonaName => Available ? SteamFriends.GetPersonaName() : null;

        public static bool IsHostingLobby => _hostedLobby != CSteamID.Nil;

        /// <summary>Starts listening for invites. Safe to call when Steam is unavailable.</summary>
        public static void Initialize()
        {
            if (!Available || _joinRequested != null)
                return;
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);

            // Steam launches the game with "+connect_lobby <id>" when an invite is accepted while it was closed.
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "+connect_lobby");
            if (index >= 0 && index + 1 < args.Length && ulong.TryParse(args[index + 1], out var lobby))
                EnterLobbyForJoin(new CSteamID(lobby));
        }

        public static void CreateLobby(LobbyVisibility visibility, int maxMembers, Action<bool> done)
        {
            if (!Available || visibility == LobbyVisibility.Off)
            {
                done(false);
                return;
            }
            LeaveHostedLobby();
            var type = visibility == LobbyVisibility.Public ? ELobbyType.k_ELobbyTypePublic
                : visibility == LobbyVisibility.FriendsOnly ? ELobbyType.k_ELobbyTypeFriendsOnly
                : ELobbyType.k_ELobbyTypePrivate;
            _lobbyCreated = CallResult<LobbyCreated_t>.Create((result, failed) =>
            {
                if (failed || result.m_eResult != EResult.k_EResultOK)
                {
                    Log.Error("Steam lobby creation failed: " + result.m_eResult);
                    done(false);
                    return;
                }
                _hostedLobby = new CSteamID(result.m_ulSteamIDLobby);
                SteamMatchmaking.SetLobbyData(_hostedLobby, KeyTag, LobbyTag);
                SteamMatchmaking.SetLobbyData(_hostedLobby, KeyProtocol, Core.Protocol.MessageCodec.ProtocolVersion.ToString());
                SteamMatchmaking.SetLobbyData(_hostedLobby, "host", MySteamId.ToString());
                done(true);
            });
            _lobbyCreated.Set(SteamMatchmaking.CreateLobby(type, Math.Max(2, maxMembers)));
        }

        public static void UpdateLobby(string name, bool passwordRequired, int playersOnline, int usedSlots, int maxSlots)
        {
            if (!IsHostingLobby)
                return;
            SteamMatchmaking.SetLobbyData(_hostedLobby, "name", name ?? string.Empty);
            SteamMatchmaking.SetLobbyData(_hostedLobby, "password", passwordRequired ? "1" : "0");
            SteamMatchmaking.SetLobbyData(_hostedLobby, "players", playersOnline.ToString());
            SteamMatchmaking.SetLobbyData(_hostedLobby, "used", usedSlots.ToString());
            SteamMatchmaking.SetLobbyData(_hostedLobby, "slots", maxSlots.ToString());
        }

        public static void LeaveHostedLobby()
        {
            if (!IsHostingLobby)
                return;
            SteamMatchmaking.LeaveLobby(_hostedLobby);
            _hostedLobby = CSteamID.Nil;
        }

        public static bool OpenInviteDialog()
        {
            if (!IsHostingLobby)
                return false;
            SteamFriends.ActivateGameOverlayInviteDialog(_hostedLobby);
            return true;
        }

        /// <summary>
        /// Starts a lobby search: public servers worldwide plus lobbies friends are hosting. Results come in through
        /// <paramref name="done"/>; friends' lobby details may still arrive afterwards, so call <see cref="ReadFriendLobbies"/> again.
        /// </summary>
        public static void RequestLobbies(Action<List<SteamLobbyEntry>> done)
        {
            if (!Available)
            {
                done(new List<SteamLobbyEntry>());
                return;
            }

            foreach (var lobby in FriendLobbies())
                SteamMatchmaking.RequestLobbyData(lobby);

            SteamMatchmaking.AddRequestLobbyListStringFilter(KeyTag, LobbyTag, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            _lobbyList = CallResult<LobbyMatchList_t>.Create((result, failed) =>
            {
                var entries = new List<SteamLobbyEntry>();
                if (!failed)
                {
                    for (var i = 0; i < result.m_nLobbiesMatching; i++)
                    {
                        var entry = ReadLobby(SteamMatchmaking.GetLobbyByIndex(i), isFriend: false);
                        if (entry != null)
                            entries.Add(entry);
                    }
                }
                done(entries);
            });
            _lobbyList.Set(SteamMatchmaking.RequestLobbyList());
        }

        /// <summary>Lobbies hosted by friends (including friends-only ones, which the public search does not return).</summary>
        public static List<SteamLobbyEntry> ReadFriendLobbies()
        {
            return FriendLobbies().Select(l => ReadLobby(l, isFriend: true)).Where(e => e != null).ToList();
        }

        private static IEnumerable<CSteamID> FriendLobbies()
        {
            if (!Available)
                yield break;
            var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (var i = 0; i < count; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendGamePlayed(friend, out var game) && game.m_steamIDLobby.IsValid())
                    yield return game.m_steamIDLobby;
            }
        }

        private static SteamLobbyEntry ReadLobby(CSteamID lobby, bool isFriend)
        {
            if (SteamMatchmaking.GetLobbyData(lobby, KeyTag) != LobbyTag)
                return null;
            if (!ulong.TryParse(SteamMatchmaking.GetLobbyData(lobby, "host"), out var host))
                return null;
            int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "players"), out var players);
            int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "used"), out var used);
            int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "slots"), out var slots);
            return new SteamLobbyEntry
            {
                LobbyId = lobby.m_SteamID,
                HostSteamId = host,
                Name = SteamMatchmaking.GetLobbyData(lobby, "name"),
                PasswordRequired = SteamMatchmaking.GetLobbyData(lobby, "password") == "1",
                PlayersOnline = players,
                UsedSlots = used,
                MaxSlots = slots,
                IsFriend = isFriend,
            };
        }

        private static void OnJoinRequested(GameLobbyJoinRequested_t request) => EnterLobbyForJoin(request.m_steamIDLobby);

        private static void EnterLobbyForJoin(CSteamID lobby)
        {
            // Joining the lobby is how we read its data (the host's id) for a private lobby; we leave right away.
            _lobbyEntered = CallResult<LobbyEnter_t>.Create((entered, failed) =>
            {
                var id = new CSteamID(entered.m_ulSteamIDLobby);
                var entry = failed ? null : ReadLobby(id, isFriend: true);
                SteamMatchmaking.LeaveLobby(id);
                if (entry == null)
                {
                    Log.Notify("That Steam invite is not for a BannerlordMP server (or it has closed).");
                    return;
                }
                JoinRequested?.Invoke(entry.HostSteamId, entry.Name);
            });
            _lobbyEntered.Set(SteamMatchmaking.JoinLobby(lobby));
        }
    }
}
