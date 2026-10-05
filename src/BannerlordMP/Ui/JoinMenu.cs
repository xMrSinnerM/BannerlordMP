using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Game;
using BannerlordMP.Net;
using BannerlordMP.Session;
using BannerlordMP.Steam;

namespace BannerlordMP.Ui
{
    internal sealed class PendingResume
    {
        public ConnectTarget Target;
        public string Token;
        public string HeroId;
    }

    /// <summary>
    /// Main menu → "Join Co-op Campaign": server browser (Steam friends, Steam public, LAN, direct IP), server
    /// password, hero slot (claim with its password or create a new one), world download, then load.
    /// </summary>
    internal static class JoinMenu
    {
        private const string JoinSaveName = "BannerlordMP_Join";
        private const double SearchSeconds = 2.0;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static LanDiscovery _lan;
        private static List<SteamLobbyEntry> _steamLobbies;
        private static double _searchUntil = -1;
        private static JoinConnection _connection;
        private static ConnectTarget _pendingInvite;
        private static readonly SlotInfo CreateNewHero = new SlotInfo { SlotId = -1 };

        /// <summary>Set once the world is downloaded; the session starts when that world reaches the map.</summary>
        public static PendingResume PendingResume { get; set; }

        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            SteamService.JoinRequested += (hostSteamId, name) =>
            {
                _pendingInvite = ConnectTarget.Steam(hostSteamId);
                if (!GameBridge.AtMainMenu)
                    Log.Notify($"Invite to '{name}' accepted. Return to the main menu to join.");
            };
        }

        public static void Open()
        {
            CloseSearch();
            _lan = new LanDiscovery();
            _lan.Start(MpConfig.Load().Port);
            _steamLobbies = null;
            SteamService.RequestLobbies(list => _steamLobbies = list);
            _searchUntil = Clock.Elapsed.TotalSeconds + SearchSeconds;
            Log.Notify("Searching for servers...");
        }

        public static void Tick()
        {
            _lan?.Poll();
            _connection?.Poll();

            if (_searchUntil >= 0 && Clock.Elapsed.TotalSeconds >= _searchUntil)
            {
                _searchUntil = -1;
                ShowBrowser();
            }

            if (_pendingInvite != null && GameBridge.AtMainMenu && _connection == null)
            {
                var target = _pendingInvite;
                _pendingInvite = null;
                ConnectTo(target);
            }
        }

        /// <summary>Joins a server directly (console command, or an address typed in the browser).</summary>
        public static void ConnectTo(ConnectTarget target)
        {
            CloseSearch();
            _connection?.Dispose();
            Log.Notify($"Connecting to {target}...");
            try
            {
                _connection = new JoinConnection(target, SteamService.PersonaName ?? MpConfig.Load().PlayerName);
            }
            catch (Exception e)
            {
                Log.Error("Could not connect", e);
                Dialogs.Message("Could not join", e.Message);
                return;
            }
            _connection.PasswordNeeded = serverName => Dialogs.Text(serverName, "This server needs a password:",
                password => _connection?.SendHello(password), Cancel, password: true);
            _connection.SlotsReceived = ShowSlots;
            _connection.Progress = Log.Notify;
            _connection.WorldReceived = OnWorldReceived;
            _connection.Failed = reason =>
            {
                _connection = null;
                Dialogs.Message("Could not join", reason);
            };
        }

        public static bool TryParseAddress(string text, int defaultPort, out ConnectTarget target)
        {
            target = null;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
                return false;
            var port = defaultPort;
            var colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon)
            {
                if (!int.TryParse(text.Substring(colon + 1), out port) || port <= 0 || port > 65535)
                    return false;
                text = text.Substring(0, colon);
            }
            target = ConnectTarget.Ip(text, port);
            return true;
        }

        private static void ShowBrowser()
        {
            var choices = new List<Choice<Action>>();
            var seenHosts = new HashSet<ulong>();

            foreach (var lobby in SteamService.ReadFriendLobbies().Concat(_steamLobbies ?? new List<SteamLobbyEntry>()))
            {
                if (!seenHosts.Add(lobby.HostSteamId))
                    continue;
                var target = ConnectTarget.Steam(lobby.HostSteamId);
                choices.Add(new Choice<Action>(() => ConnectTo(target),
                    $"{(lobby.IsFriend ? "[Friend]" : "[Steam]")} {Describe(lobby.Name, lobby.PasswordRequired, lobby.PlayersOnline, lobby.UsedSlots, lobby.MaxSlots)}",
                    hint: "Over Steam's relay"));
            }

            foreach (var server in _lan?.Found ?? new List<LanServer>())
            {
                var info = server.Info;
                var target = ConnectTarget.Ip(server.Address, info.Port);
                var compatible = info.ProtocolVersion == MessageCodec.ProtocolVersion;
                choices.Add(new Choice<Action>(() => ConnectTo(target),
                    $"[LAN] {Describe(info.ServerName, info.PasswordRequired, info.PlayersOnline, info.UsedSlots, info.MaxSlots)}",
                    compatible, compatible ? $"{server.Address}:{info.Port}" : "Different mod version"));
            }

            choices.Add(new Choice<Action>(AskAddress, "Direct connect (IP address)..."));
            choices.Add(new Choice<Action>(Open, "Refresh"));

            var text = choices.Count > 2 ? "Choose a server:" : "No servers found. Ask the host for a Steam invite or their IP address.";
            Dialogs.Choose("Join Co-op Campaign", text, choices, action => action(), CloseSearch, "Join");
        }

        private static string Describe(string name, bool password, int online, int used, int max)
        {
            return $"{(string.IsNullOrEmpty(name) ? "Unnamed server" : name)}{(password ? " (password)" : "")} — {online} online, {used}/{max} heroes";
        }

        private static void AskAddress()
        {
            var port = MpConfig.Load().Port;
            Dialogs.Text("Direct connect", $"Server address, e.g. 203.0.113.5 or myhost.example.com:{port}",
                text =>
                {
                    if (TryParseAddress(text, port, out var target))
                        ConnectTo(target);
                },
                ShowBrowser,
                validate: text => TryParseAddress(text, port, out _) ? null : "Enter an address, optionally with :port.");
        }

        private static void ShowSlots(SlotListMessage list)
        {
            var choices = list.Slots.Select(s => new Choice<SlotInfo>(s,
                $"{s.HeroName} ({s.CultureName}){(s.InUse ? " — being played" : "")}",
                !s.InUse, "Your hero: you will need its password")).ToList();
            var free = list.MaxSlots - list.Slots.Count;
            if (free > 0)
                choices.Add(new Choice<SlotInfo>(CreateNewHero, $"Create a new hero ({free} of {list.MaxSlots} slots free)"));
            var text = choices.Count == 0
                ? "This server has no free hero slots."
                : "Pick your hero, or create a new one.";
            Dialogs.Choose(_connection?.ServerName ?? "Server", text, choices,
                slot =>
                {
                    if (slot == CreateNewHero)
                        AskNewHeroName(list);
                    else
                        Dialogs.Text(slot.HeroName, $"Password for {slot.HeroName}:", password => _connection?.ClaimSlot(slot, password),
                            () => ShowSlots(list), password: true);
                },
                Cancel);
        }

        private static void AskNewHeroName(SlotListMessage list)
        {
            Dialogs.Text("New hero", "Your hero's name:",
                name => AskCulture(list, name.Trim()),
                () => ShowSlots(list),
                validate: name =>
                {
                    name = name.Trim();
                    if (name.Length < 2 || name.Length > 32)
                        return "2 to 32 characters.";
                    if (name.IndexOf('|') >= 0)
                        return "No | characters.";
                    return list.Slots.Any(s => string.Equals(s.HeroName, name, StringComparison.OrdinalIgnoreCase)) ? "That name is taken." : null;
                });
        }

        private static void AskCulture(SlotListMessage list, string name)
        {
            var choices = list.Cultures.Select(c => new Choice<string>(c.Id, c.Name)).ToList();
            Dialogs.Choose("New hero", "Culture (your hero starts at one of its towns):", choices,
                culture => AskGender(list, name, culture),
                () => AskNewHeroName(list));
        }

        private static void AskGender(SlotListMessage list, string name, string culture)
        {
            var choices = new List<Choice<bool>> { new Choice<bool>(false, "Male"), new Choice<bool>(true, "Female") };
            Dialogs.Choose("New hero", "", choices,
                female => AskHeroPassword(list, name, culture, female),
                () => AskCulture(list, name));
        }

        private static void AskHeroPassword(SlotListMessage list, string name, string culture, bool female)
        {
            Dialogs.Text("Hero password", $"Choose a password for {name}. Anyone who wants to play this hero needs it.",
                password => Dialogs.Text("Hero password", "Type it again:",
                    confirm => _connection?.CreateHero(name, culture, female, password),
                    () => AskHeroPassword(list, name, culture, female),
                    password: true,
                    validate: confirm => confirm == password ? null : "Passwords do not match."),
                () => AskGender(list, name, culture),
                password: true,
                validate: password => password.Length < 4 ? "At least 4 characters." : null);
        }

        private static void OnWorldReceived(byte[] save, JoinAcceptedMessage accepted)
        {
            var target = _connection?.Target;
            _connection = null;
            try
            {
                GameBridge.WriteSaveFile(JoinSaveName, save);
            }
            catch (Exception e)
            {
                Log.Error("Could not write the downloaded world", e);
                Dialogs.Message("Could not join", "Could not write the downloaded world to your save folder: " + e.Message);
                return;
            }

            PendingResume = new PendingResume { Target = target, Token = accepted.ResumeToken, HeroId = accepted.HeroId };
            Log.Notify($"World downloaded. Loading as {accepted.HeroName}...");
            if (!GameBridge.LoadSave(JoinSaveName, () => PendingResume = null))
            {
                PendingResume = null;
                Dialogs.Message("Could not join", "The downloaded world could not be opened.");
            }
        }

        private static void Cancel()
        {
            _connection?.Dispose();
            _connection = null;
        }

        private static void CloseSearch()
        {
            _searchUntil = -1;
            _lan?.Dispose();
            _lan = null;
        }
    }
}
