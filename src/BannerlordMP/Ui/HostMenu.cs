using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Game;
using BannerlordMP.Steam;
using TaleWorlds.MountAndBlade;

namespace BannerlordMP.Ui
{
    /// <summary>
    /// Main menu → "Host Co-op Campaign": one screen with every server setting. Click a line to change it,
    /// "Start server" to go. Choices are remembered for next time.
    /// </summary>
    internal static class HostMenu
    {
        private const string NewCampaign = "<new>";
        private static readonly int[] SlotChoices = { 2, 3, 4, 6, 8, 12, 16 };

        private enum Item
        {
            Start,
            World,
            Mode,
            Name,
            Password,
            Slots,
            Visibility,
        }

        /// <summary>Settings to start hosting with as soon as the chosen campaign reaches the map.</summary>
        public static MpConfig PendingHost { get; set; }

        public static void Open()
        {
            var config = MpConfig.Load();
            config.ServerName = MenuMemory.Get("Host.Name", config.ServerName);
            config.ServerPassword = MenuMemory.Get("Host.Password", config.ServerPassword);
            config.MaxSlots = MenuMemory.GetInt("Host.Slots", config.MaxSlots);
            config.DedicatedHost = MenuMemory.GetBool("Host.Dedicated", config.DedicatedHost);
            if (Enum.TryParse(MenuMemory.Get("Host.Visibility"), out LobbyVisibility visibility))
                config.SteamVisibility = visibility;
            if (!SteamService.Available)
                config.SteamVisibility = LobbyVisibility.Off;

            var saves = GameBridge.ListSaves().Select(s => s.Name).ToList();
            var world = MenuMemory.Get("Host.World");
            if (world != NewCampaign && !saves.Contains(world))
                world = saves.FirstOrDefault() ?? NewCampaign; // The server autosave sorts first.
            ShowHub(config, world);
        }

        private static void ShowHub(MpConfig config, string world)
        {
            var choices = new List<Choice<Item>>
            {
                new Choice<Item>(Item.Start, ">>  Start server  <<", hint: "Loads the world and opens the server."),
                new Choice<Item>(Item.World, "World:  " + DescribeWorld(world)),
                new Choice<Item>(Item.Mode, "Mode:  " + (config.DedicatedHost ? "Dedicated server (nobody plays here)" : "Play on this PC")),
                new Choice<Item>(Item.Name, "Server name:  " + config.ServerName),
                new Choice<Item>(Item.Password, "Password:  " + (string.IsNullOrEmpty(config.ServerPassword) ? "none (open server)" : new string('*', config.ServerPassword.Length))),
                new Choice<Item>(Item.Slots, $"Player heroes:  {config.MaxSlots}"),
                new Choice<Item>(Item.Visibility, "Who can find it:  " + DescribeVisibility(config)),
            };
            Dialogs.Choose("Host Co-op Campaign", "Click a line to change it.", choices,
                item => Edit(item, config, world), null, "Select");
        }

        private static void Edit(Item item, MpConfig config, string world)
        {
            Action back = () => ShowHub(config, world);
            switch (item)
            {
                case Item.Start:
                    Start(config, world);
                    break;

                case Item.World:
                {
                    var choices = new List<Choice<string>> { new Choice<string>(NewCampaign, DescribeWorld(NewCampaign), hint: "Create the host's character, then the server starts.") };
                    choices.AddRange(GameBridge.ListSaves().Select(s => new Choice<string>(s.Name, DescribeWorld(s.Name),
                        hint: IsServerSave(s.Name) ? "Has every player hero and their progress."
                            : "If players joined this world before, pick a server save instead, or their heroes will be missing.")));
                    Dialogs.Choose("World", "Which campaign should the server run?", choices, chosen => ShowHub(config, chosen), back);
                    break;
                }

                case Item.Mode:
                    Dialogs.Choose("Mode", "", new List<Choice<bool>>
                        {
                            new Choice<bool>(true, "Dedicated server (nobody plays here)", hint: "The world never pauses for battles. Join from another game window or PC."),
                            new Choice<bool>(false, "Play on this PC", hint: "You play the world's main hero. While you are in a battle, the world pauses for everyone."),
                        },
                        dedicated => { config.DedicatedHost = dedicated; back(); }, back);
                    break;

                case Item.Name:
                    Dialogs.Text("Server name", "Shown in the server browser:",
                        name => { config.ServerName = name.Trim(); back(); }, back, defaultText: config.ServerName,
                        validate: name => name.Trim().Length < 2 ? "At least 2 characters." : name.Length > 40 ? "At most 40 characters." : null);
                    break;

                case Item.Password:
                    Dialogs.Text("Password", "Players need this to join. Leave empty for an open server.",
                        password => { config.ServerPassword = password; back(); }, back, password: true);
                    break;

                case Item.Slots:
                    Dialogs.Choose("Player heroes", "How many heroes players can create on this server (each protected by its owner's password):",
                        SlotChoices.Select(n => new Choice<int>(n, $"{n} player heroes")).ToList(),
                        slots => { config.MaxSlots = slots; back(); }, back);
                    break;

                case Item.Visibility:
                {
                    if (!SteamService.Available)
                    {
                        Dialogs.Message("Who can find it", "Steam is not available (non-Steam version of the game?), so players join over LAN or by IP address.", back);
                        break;
                    }
                    var choices = new List<Choice<LobbyVisibility>>
                    {
                        new Choice<LobbyVisibility>(LobbyVisibility.FriendsOnly, "Steam friends", hint: "Through Steam's relay: no port forwarding needed."),
                        new Choice<LobbyVisibility>(LobbyVisibility.InviteOnly, "Only people I invite on Steam"),
                        new Choice<LobbyVisibility>(LobbyVisibility.Public, "Everyone with the mod (public)"),
                        new Choice<LobbyVisibility>(LobbyVisibility.Off, "LAN and IP address only", hint: "For internet play, forward UDP port " + config.Port + "."),
                    };
                    Dialogs.Choose("Who can find it", "LAN and IP address always work as well.", choices,
                        visibility => { config.SteamVisibility = visibility; back(); }, back);
                    break;
                }
            }
        }

        private static void Start(MpConfig config, string world)
        {
            MenuMemory.Set("Host.Name", config.ServerName);
            MenuMemory.Set("Host.Password", config.ServerPassword);
            MenuMemory.Set("Host.Slots", config.MaxSlots);
            MenuMemory.Set("Host.Dedicated", config.DedicatedHost);
            MenuMemory.Set("Host.Visibility", config.SteamVisibility);
            MenuMemory.Set("Host.World", world);

            PendingHost = config;
            if (world == NewCampaign)
            {
                StartNewSandbox();
            }
            else if (!GameBridge.LoadSave(world, () => PendingHost = null))
            {
                PendingHost = null;
                Dialogs.Message("Host Co-op Campaign", "Could not find that save.");
            }
        }

        private static bool IsServerSave(string name) => name == GameBridge.AutoSaveName || name == GameBridge.ServerSaveName;

        private static string DescribeWorld(string world)
        {
            if (world == NewCampaign)
                return "New sandbox campaign";
            if (world == GameBridge.AutoSaveName)
                return world + "  (latest server save)";
            if (world == GameBridge.ServerSaveName)
                return world + "  (server save from the last join)";
            return world;
        }

        private static string DescribeVisibility(MpConfig config)
        {
            switch (config.SteamVisibility)
            {
                case LobbyVisibility.FriendsOnly:
                    return "Steam friends, LAN, IP";
                case LobbyVisibility.InviteOnly:
                    return "Steam invites, LAN, IP";
                case LobbyVisibility.Public:
                    return "everyone on Steam, LAN, IP";
                default:
                    return "LAN and IP address only";
            }
        }

        private static void StartNewSandbox()
        {
            // Use the game's own "Sandbox" new-game button so character creation runs as normal.
            var option = Module.CurrentModule.GetInitialStateOptions().FirstOrDefault(o =>
                o.Id.IndexOf("sandbox", StringComparison.OrdinalIgnoreCase) >= 0 && o.Id.IndexOf("new", StringComparison.OrdinalIgnoreCase) >= 0);
            if (option == null)
            {
                PendingHost = null;
                Log.Info("Initial state options: " + string.Join(", ", Module.CurrentModule.GetInitialStateOptions().Select(o => o.Id)));
                Dialogs.Message("Host Co-op Campaign", "Could not find the Sandbox new game option. Start a new sandbox campaign normally, then type mp.host in the console (Alt + ~).");
                return;
            }
            option.DoAction();
        }
    }
}
