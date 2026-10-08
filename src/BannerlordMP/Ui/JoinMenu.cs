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

        /// <summary>A hero made in the character creator, waiting to be sent to the server it was made for.</summary>
        private static HeroSheet _madeHero;
        private static string _madeHeroServer;
        /// <summary>Set right after the creator returns, so the slot screen goes straight to the made hero.</summary>
        private static bool _offerMadeHero;
        /// <summary>The server password to answer with automatically when reconnecting after the creator.</summary>
        private static string _autoPassword;

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
        public static void ConnectTo(ConnectTarget target) => ConnectTo(target, null);

        private static void ConnectTo(ConnectTarget target, string serverPassword)
        {
            _autoPassword = serverPassword;
            if (target.IsSteam && target.SteamId == SteamService.MySteamId)
            {
                // Same Steam account on this PC (two game windows): Steam cannot relay to yourself.
                target = ConnectTarget.Ip("127.0.0.1", MpConfig.Load().Port);
                Log.Notify("That server runs on your own Steam account; connecting locally instead.");
            }
            CloseSearch();
            _connection?.Dispose();
            Log.Notify($"Connecting to {target}...");
            try
            {
                _connection = new JoinConnection(target, SteamService.PersonaName ?? MpConfig.Load().PlayerName);
            }
            catch (Exception e)
            {
                Log.Error("Could not connect to " + target, e);
                Dialogs.Message("Could not join", e.Message + (target.IsSteam ? "\n\nTry the LAN entry or Direct connect (IP) instead." : ""));
                return;
            }
            _connection.PasswordNeeded = serverName =>
            {
                var remembered = _autoPassword;
                _autoPassword = null;
                if (remembered != null)
                    _connection?.SendHello(remembered);
                else
                    Dialogs.Text(serverName, "This server needs a password:", password => _connection?.SendHello(password), Cancel, password: true);
            };
            _connection.SlotsReceived = ShowSlots;
            _connection.Progress = Log.Notify;
            _connection.WorldReceived = OnWorldReceived;
            _connection.Failed = reason =>
            {
                _connection = null;
                if (_madeHero != null)
                    reason += $"\n\n{_madeHero.Name} from the character creator is kept: join again and pick them under \"Create a new hero\".";
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

            var lastServer = MenuMemory.Get("Join.LastServer");
            if (TryParseRemembered(MenuMemory.Get("Join.LastTarget"), out var lastTarget))
            {
                var hero = MenuMemory.Get("Join.Hero." + lastServer);
                choices.Add(new Choice<Action>(() => ConnectTo(lastTarget),
                    $">>  Rejoin {(string.IsNullOrEmpty(lastServer) ? lastTarget.ToString() : lastServer)}{(string.IsNullOrEmpty(hero) ? "" : " as " + hero)}  <<",
                    hint: "The server you played on last"));
            }

            foreach (var lobby in SteamService.ReadFriendLobbies().Concat(_steamLobbies ?? new List<SteamLobbyEntry>()))
            {
                if (!seenHosts.Add(lobby.HostSteamId))
                    continue;
                var target = ConnectTarget.Steam(lobby.HostSteamId);
                var tag = lobby.HostSteamId == SteamService.MySteamId ? "[You]" : lobby.IsFriend ? "[Friend]" : "[Steam]";
                choices.Add(new Choice<Action>(() => ConnectTo(target),
                    $"{tag} {Describe(lobby.Name, lobby.PasswordRequired, lobby.PlayersOnline, lobby.UsedSlots, lobby.MaxSlots)}",
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

            var found = choices.Count(c => c.Label.StartsWith("["));
            var text = found > 0 ? "Choose a server:" : "No servers found nearby. Ask the host for a Steam invite or their IP address.";
            Dialogs.Choose("Join Co-op Campaign", text, choices, action => action(), CloseSearch, "Join");
        }

        private static string Describe(string name, bool password, int online, int used, int max)
        {
            return $"{(string.IsNullOrEmpty(name) ? "Unnamed server" : name)}{(password ? "  [password]" : "")}   {online} online, {used}/{max} heroes";
        }

        private static string Remember(ConnectTarget target) => target.IsSteam ? "steam:" + target.SteamId : $"ip:{target.Address}:{target.Port}";

        private static bool TryParseRemembered(string text, out ConnectTarget target)
        {
            target = null;
            if (string.IsNullOrEmpty(text))
                return false;
            if (text.StartsWith("steam:") && ulong.TryParse(text.Substring(6), out var id))
            {
                target = ConnectTarget.Steam(id);
                return true;
            }
            return text.StartsWith("ip:") && TryParseAddress(text.Substring(3), MpConfig.Load().Port, out target);
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

        /// <summary>The character creator is done: reconnect to the server it was for and offer the new hero.</summary>
        public static void OnHeroMade(HeroSheet sheet, ConnectTarget target, string serverPassword)
        {
            _madeHero = sheet;
            _madeHeroServer = Remember(target);
            _offerMadeHero = true;
            ConnectTo(target, serverPassword);
        }

        private static HeroSheet MadeHeroForThisServer =>
            _madeHero != null && _connection != null && _madeHeroServer == Remember(_connection.Target) ? _madeHero : null;

        private static void ShowSlots(SlotListMessage list)
        {
            if (_offerMadeHero)
            {
                _offerMadeHero = false;
                if (MadeHeroForThisServer != null && list.MaxSlots > list.Slots.Count)
                {
                    ShowMadeHero(list, MadeHeroForThisServer);
                    return;
                }
            }
            var serverName = _connection?.ServerName ?? "Server";
            var lastHero = MenuMemory.Get("Join.Hero." + serverName);
            var choices = list.Slots
                .OrderByDescending(slot => slot.HeroName == lastHero)
                .Select(slot => new Choice<SlotInfo>(slot,
                    $"{slot.HeroName}  ({slot.CultureName}){(slot.HeroName == lastHero ? "   your last hero" : "")}{(slot.InUse ? "   - being played" : "")}",
                    !slot.InUse, "Needs this hero's password"))
                .ToList();
            var free = list.MaxSlots - list.Slots.Count;
            if (free > 0)
                choices.Add(new Choice<SlotInfo>(CreateNewHero, $"+  Create a new hero   ({free} of {list.MaxSlots} free)"));
            var text = choices.Count == 0 ? "This server has no free hero slots." : "Pick your hero, or create a new one.";
            Dialogs.Choose(serverName, text, choices,
                slot =>
                {
                    if (slot == CreateNewHero)
                        ShowCreateChoice(list);
                    else
                        Dialogs.Text(slot.HeroName, $"Password for {slot.HeroName}:", password => _connection?.ClaimSlot(slot, password),
                            () => ShowSlots(list), password: true);
                },
                Cancel, "Play");
        }

        private enum CreateWay
        {
            MadeHero,
            Creator,
            Quick,
        }

        private static void ShowCreateChoice(SlotListMessage list)
        {
            var made = MadeHeroForThisServer;
            var choices = new List<Choice<CreateWay>>();
            if (made != null)
                choices.Add(new Choice<CreateWay>(CreateWay.MadeHero, $">>  {made.Name}  <<   (made in the character creator)", hint: "Join as the hero you just made."));
            choices.Add(new Choice<CreateWay>(CreateWay.Creator, "Character creator (like single player)",
                hint: "Every creation screen from a new campaign: culture, face, background, skills, banner, clan name. You rejoin automatically afterwards."));
            choices.Add(new Choice<CreateWay>(CreateWay.Quick, "Quick create", hint: "Just a name, culture and gender. The server picks the rest."));
            Dialogs.Choose("New hero", "How do you want to make your hero?", choices, way =>
            {
                switch (way)
                {
                    case CreateWay.MadeHero:
                        ShowMadeHero(list, made);
                        break;
                    case CreateWay.Creator:
                        StartCreator(list);
                        break;
                    case CreateWay.Quick:
                        ShowNewHero(list, new NewHero { Culture = list.Cultures.FirstOrDefault().Id });
                        break;
                }
            }, () => ShowSlots(list), "Select");
        }

        private static void StartCreator(SlotListMessage list)
        {
            var connection = _connection;
            if (connection == null)
                return;
            var target = connection.Target;
            var serverPassword = connection.ServerPassword;
            // The server is left while the creator runs (that can take a while) and joined again afterwards.
            Cancel();
            if (!CharacterCreator.Begin(target, serverPassword))
                Dialogs.Message("Character creator", "Could not find the game's Sandbox new-game option, so the creator cannot start. Use Quick create instead.");
        }

        /// <summary>Sends the hero from the character creator, after a password (and a new name, if that one is taken).</summary>
        private static void ShowMadeHero(SlotListMessage list, HeroSheet sheet)
        {
            Action back = () => ShowSlots(list);
            if (list.Cultures.All(c => c.Id != sheet.CultureId))
            {
                Dialogs.Message("Character creator", $"This server has no '{sheet.CultureId}' culture (different modules?). Use Quick create instead.", back);
                return;
            }
            if (ValidateName(list, sheet.Name) != null)
            {
                Dialogs.Text("Name", $"The name {sheet.Name} cannot be used on this server. Pick another one for your hero:",
                    name =>
                    {
                        sheet.Name = name.Trim();
                        ShowMadeHero(list, sheet);
                    }, back, defaultText: sheet.Name, validate: name => ValidateName(list, name));
                return;
            }
            Dialogs.Text(sheet.Name, $"Choose a password for {sheet.Name} (at least 4 characters). Anyone who wants to play this hero needs it.",
                password => Dialogs.Text(sheet.Name, "Type it again:",
                    confirm => _connection?.CreateHero(sheet.Name, sheet.CultureId, sheet.IsFemale, password, sheet), back, password: true,
                    validate: confirm => confirm == password ? null : "Passwords do not match."),
                back, password: true,
                validate: password => password.Length < 4 ? "At least 4 characters." : null);
        }

        private static string ValidateName(SlotListMessage list, string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length < 2 || name.Length > 32)
                return "2 to 32 characters.";
            if (name.IndexOf('|') >= 0)
                return "No | characters.";
            return list.Slots.Any(slot => string.Equals(slot.HeroName, name, StringComparison.OrdinalIgnoreCase)) ? "That name is taken." : null;
        }

        private sealed class NewHero
        {
            public string Name = string.Empty;
            public string Culture;
            public bool Female;
            public string Password = string.Empty;
        }

        private enum HeroItem
        {
            Create,
            Name,
            Culture,
            Gender,
            Password,
        }

        /// <summary>New hero on one screen: click a line to change it, then "Create hero".</summary>
        private static void ShowNewHero(SlotListMessage list, NewHero hero)
        {
            var cultureName = list.Cultures.FirstOrDefault(c => c.Id == hero.Culture).Name ?? "?";
            var ready = hero.Name.Length >= 2 && hero.Password.Length >= 4 && hero.Culture != null;
            var choices = new List<Choice<HeroItem>>
            {
                new Choice<HeroItem>(HeroItem.Create, ">>  Create hero  <<", ready, ready ? "Joins the server as this hero." : "Give your hero a name and a password first."),
                new Choice<HeroItem>(HeroItem.Name, "Name:  " + (hero.Name.Length > 0 ? hero.Name : "(choose a name)")),
                new Choice<HeroItem>(HeroItem.Culture, "Culture:  " + cultureName, hint: "Your hero starts at one of its towns."),
                new Choice<HeroItem>(HeroItem.Gender, "Gender:  " + (hero.Female ? "Female" : "Male")),
                new Choice<HeroItem>(HeroItem.Password, "Password:  " + (hero.Password.Length > 0 ? new string('*', hero.Password.Length) : "(choose a password)"),
                    hint: "Anyone who wants to play this hero needs it."),
            };
            Action back = () => ShowNewHero(list, hero);
            Dialogs.Choose("New hero", "Click a line to change it.", choices, item =>
            {
                switch (item)
                {
                    case HeroItem.Create:
                        _connection?.CreateHero(hero.Name, hero.Culture, hero.Female, hero.Password);
                        break;
                    case HeroItem.Name:
                        Dialogs.Text("Name", "Your hero's name:", name => { hero.Name = name.Trim(); back(); }, back, defaultText: hero.Name,
                            validate: name => ValidateName(list, name));
                        break;
                    case HeroItem.Culture:
                        Dialogs.Choose("Culture", "Your hero starts at one of its towns.", list.Cultures.Select(c => new Choice<string>(c.Id, c.Name)).ToList(),
                            culture => { hero.Culture = culture; back(); }, back);
                        break;
                    case HeroItem.Gender:
                        hero.Female = !hero.Female;
                        back();
                        break;
                    case HeroItem.Password:
                        Dialogs.Text("Password", "Choose a password (at least 4 characters):",
                            password => Dialogs.Text("Password", "Type it again:",
                                confirm => { hero.Password = password; back(); }, back, password: true,
                                validate: confirm => confirm == password ? null : "Passwords do not match."),
                            back, password: true,
                            validate: password => password.Length < 4 ? "At least 4 characters." : null);
                        break;
                }
            }, () => ShowCreateChoice(list), "Select");
        }

        private static void OnWorldReceived(byte[] save, JoinAcceptedMessage accepted)
        {
            var serverNameForMemory = _connection?.ServerName;
            Log.Info($"Join: world received ({save.Length} bytes) for {accepted.HeroName} ({accepted.HeroId})");
            var target = _connection?.Target;
            _connection = null;
            _madeHero = null;
            _madeHeroServer = null;
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

            Log.Info("Join: world written as " + JoinSaveName + ", loading");
            var serverName = serverNameForMemory;
            if (target != null)
            {
                MenuMemory.Set("Join.LastTarget", Remember(target));
                MenuMemory.Set("Join.LastServer", serverName ?? string.Empty);
                MenuMemory.Set("Join.Hero." + (serverName ?? string.Empty), accepted.HeroName);
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
