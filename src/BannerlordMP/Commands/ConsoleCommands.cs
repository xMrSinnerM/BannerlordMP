using System;
using System.Collections.Generic;
using BannerlordMP.Game;
using BannerlordMP.Session;
using BannerlordMP.Steam;
using BannerlordMP.Ui;
using TaleWorlds.Library;

namespace BannerlordMP.Commands
{
    /// <summary>
    /// Developer console commands (open the console with Alt + ~), all in the "mp" group, e.g. mp.host.
    /// The main menu buttons cover the same ground; these are for hosting a campaign that is already loaded,
    /// and for server admin.
    /// </summary>
    internal static class ConsoleCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("host", "mp")]
        public static string Host(List<string> args) => StartHost(args, dedicated: null);

        /// <summary>Runs this game as a dedicated world host: nobody plays here.</summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("server", "mp")]
        public static string Server(List<string> args) => StartHost(args, dedicated: true);

        private static string StartHost(List<string> args, bool? dedicated)
        {
            if (!GameBridge.CampaignRunning)
                return "Load a campaign first, or use \"Host Co-op Campaign\" in the main menu.";
            var config = MpConfig.Load();
            if (dedicated.HasValue)
                config.DedicatedHost = dedicated.Value;
            if (args.Count > 0 && int.TryParse(args[0], out var port))
                config.Port = port;
            try
            {
                MpSession.Start(new HostSession(config));
                return $"{(config.DedicatedHost ? "Dedicated server" : "Hosting")} '{config.ServerName}' on port {config.Port}. " +
                       "Settings come from config.ini. Players join from the main menu (Join Co-op Campaign).";
            }
            catch (Exception e)
            {
                MpSession.Stop();
                Log.Error("Could not host", e);
                return "Could not host: " + e.Message;
            }
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("join", "mp")]
        public static string Join(List<string> args)
        {
            if (!GameBridge.AtMainMenu)
                return "Join from the main menu.";
            if (args.Count < 1 || !JoinMenu.TryParseAddress(args[0], MpConfig.Load().Port, out var target))
                return "Usage: mp.join <address[:port]>   (or use \"Join Co-op Campaign\" in the main menu)";
            JoinMenu.ConnectTo(target);
            return "Connecting to " + target + "...";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("invite", "mp")]
        public static string Invite(List<string> args)
        {
            if (!(MpSession.Current is HostSession))
                return "Only the host can invite.";
            return SteamService.OpenInviteDialog() ? "Steam invite dialog opened." : "No Steam lobby (Steam unavailable or SteamVisibility=Off).";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("save", "mp")]
        public static string Save(List<string> args)
        {
            if (!(MpSession.Current is HostSession host))
                return "Only the host saves the world.";
            host.SaveSoon();
            return "Saving the world as BannerlordMP_Autosave...";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("slots", "mp")]
        public static string Slots(List<string> args)
        {
            return MpSession.Current is HostSession host ? string.Join("\n", host.DescribeSlots()) : "Only the host has slots.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("removeslot", "mp")]
        public static string RemoveSlot(List<string> args)
        {
            if (!(MpSession.Current is HostSession host))
                return "Only the host can remove slots.";
            if (args.Count < 1 || !int.TryParse(args[0], out var slotId))
                return "Usage: mp.removeslot <slot number>   (see mp.slots)";
            return host.RemoveSlot(slotId);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("leave", "mp")]
        public static string Leave(List<string> args)
        {
            if (!MpSession.Active)
                return "Not in a session.";
            MpSession.Stop();
            return "Left the session.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("status", "mp")]
        public static string Status(List<string> args)
        {
            var session = MpSession.Current;
            return session == null ? "Not in a session." : string.Join("\n", session.Describe());
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("say", "mp")]
        public static string Say(List<string> args)
        {
            var session = MpSession.Current;
            if (session == null)
                return "Not in a session.";
            session.SendChat(string.Join(" ", args));
            return string.Empty;
        }
    }
}
