using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Game;
using BannerlordMP.Steam;
using TaleWorlds.MountAndBlade;

namespace BannerlordMP.Ui
{
    /// <summary>Main menu → "Host Co-op Campaign": server settings, then pick the world to run.</summary>
    internal static class HostMenu
    {
        private const string NewCampaign = "<new>";
        private static readonly int[] SlotChoices = { 2, 3, 4, 6, 8, 12, 16 };

        /// <summary>Settings to start hosting with as soon as the chosen campaign reaches the map.</summary>
        public static MpConfig PendingHost { get; set; }

        public static void Open()
        {
            var config = MpConfig.Load();
            Dialogs.Text("Host Co-op Campaign", "Server name (shown in the server browser):",
                name =>
                {
                    config.ServerName = name.Trim();
                    AskPassword(config);
                },
                defaultText: config.ServerName,
                validate: name => name.Trim().Length < 2 ? "At least 2 characters." : name.Length > 40 ? "At most 40 characters." : null);
        }

        private static void AskPassword(MpConfig config)
        {
            Dialogs.Text("Server password", "Players need this to join. Leave empty for an open server.",
                password =>
                {
                    config.ServerPassword = password;
                    AskSlots(config);
                },
                Open, password: true);
        }

        private static void AskSlots(MpConfig config)
        {
            var choices = SlotChoices.Select(n => new Choice<int>(n, $"{n} player heroes")).ToList();
            Dialogs.Choose("Player slots", "How many heroes players can create on this server (each protected by its owner's password):",
                choices,
                slots =>
                {
                    config.MaxSlots = slots;
                    AskVisibility(config);
                },
                () => AskPassword(config));
        }

        private static void AskVisibility(MpConfig config)
        {
            var choices = new List<Choice<LobbyVisibility>>();
            if (SteamService.Available)
            {
                choices.Add(new Choice<LobbyVisibility>(LobbyVisibility.FriendsOnly, "Steam: friends can see it and join", hint: "Through Steam's relay: no port forwarding needed."));
                choices.Add(new Choice<LobbyVisibility>(LobbyVisibility.InviteOnly, "Steam: invite only"));
                choices.Add(new Choice<LobbyVisibility>(LobbyVisibility.Public, "Steam: public (anyone with the mod)"));
            }
            choices.Add(new Choice<LobbyVisibility>(LobbyVisibility.Off, "LAN and direct IP only", hint: "For internet play, forward UDP port " + config.Port + "."));
            var text = SteamService.Available
                ? "Who can find this server? LAN and direct IP always work as well."
                : "Steam is not available (non-Steam version of the game?), so players join over LAN or by IP.";
            Dialogs.Choose("Visibility", text, choices,
                visibility =>
                {
                    config.SteamVisibility = visibility;
                    AskMode(config);
                },
                () => AskSlots(config));
        }

        private static void AskMode(MpConfig config)
        {
            var choices = new List<Choice<bool>>
            {
                new Choice<bool>(false, "Play on this PC", hint: "You play the campaign's main hero. While you are in a battle, the world pauses for everyone."),
                new Choice<bool>(true, "Dedicated server (nobody plays here)", hint: "The world never pauses for battles. Join from another game window or PC."),
            };
            Dialogs.Choose("Host mode", "", choices,
                dedicated =>
                {
                    config.DedicatedHost = dedicated;
                    AskWorld(config);
                },
                () => AskVisibility(config));
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

        private static void AskWorld(MpConfig config)
        {
            var choices = new List<Choice<string>> { new Choice<string>(NewCampaign, "New sandbox campaign", hint: "Create the host's character, then the server starts.") };
            choices.AddRange(GameBridge.ListSaves().Select(s => new Choice<string>(s.Name,
                s.Name == GameBridge.AutoSaveName ? s.Name + "  (server autosave: continue here)"
                : s.Name == GameBridge.ServerSaveName ? s.Name + "  (server save from the last join)"
                : s.Name,
                hint: s.Name == GameBridge.AutoSaveName || s.Name == GameBridge.ServerSaveName
                    ? "Has all player heroes and their progress."
                    : "If players joined this world before, load a server save instead, or their heroes will be missing.")));
            Dialogs.Choose("World", "Which campaign should the server run?", choices,
                world =>
                {
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
                },
                () => AskMode(config));
        }
    }
}
