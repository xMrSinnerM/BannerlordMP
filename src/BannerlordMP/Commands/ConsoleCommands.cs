using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Game;
using BannerlordMP.Session;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace BannerlordMP.Commands
{
    /// <summary>Developer console commands (open the console with Alt + ~). All are in the "mp" group, e.g. mp.host.</summary>
    internal static class ConsoleCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("host", "mp")]
        public static string Host(List<string> args) => StartHost(args, dedicated: null);

        /// <summary>Runs this game as a dedicated world host: nobody plays here, every player joins with mp.join.</summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("server", "mp")]
        public static string Server(List<string> args) => StartHost(args, dedicated: true);

        private static string StartHost(List<string> args, bool? dedicated)
        {
            if (!GameBridge.CampaignRunning)
                return "Load a campaign first.";
            var config = MpConfig.Load();
            if (dedicated.HasValue)
                config.DedicatedHost = dedicated.Value;
            if (args.Count > 0 && int.TryParse(args[0], out var port))
                config.Port = port;
            try
            {
                MpSession.Start(new HostSession(config));
                return $"{(config.DedicatedHost ? "Dedicated server" : "Hosting")} on port {config.Port}. Players join with: mp.join <your-ip> <hero_id>  (see mp.heroes)";
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
            if (!GameBridge.CampaignRunning)
                return "Load the same save as the host first.";
            if (args.Count < 2)
                return "Usage: mp.join <host-address> <hero_id> [port]";

            var config = MpConfig.Load();
            if (args.Count > 2 && int.TryParse(args[2], out var port))
                config.Port = port;
            var hero = GameBridge.FindHero(args[1]);
            if (hero == null)
                return $"No hero with id '{args[1]}' in this save. Run mp.heroes on the host.";

            try
            {
                MpSession.Start(new ClientSession(config, args[0], config.Port, hero.StringId));
                return $"Connecting to {args[0]}:{config.Port} as {hero.Name}...";
            }
            catch (Exception e)
            {
                MpSession.Stop();
                Log.Error("Could not join", e);
                return "Could not join: " + e.Message;
            }
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

        /// <summary>Lists heroes a friend can take control of: party leaders in the player's clan.</summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("heroes", "mp")]
        public static string Heroes(List<string> args)
        {
            if (!GameBridge.CampaignRunning || Clan.PlayerClan == null)
                return "Load a campaign first.";
            var leaders = Clan.PlayerClan.Heroes
                .Where(h => h.IsAlive && h != Hero.MainHero && h.PartyBelongedTo != null && h.PartyBelongedTo.LeaderHero == h)
                .Select(h => $"  {h.StringId}  ({h.Name}, {h.PartyBelongedTo.MemberRoster.TotalManCount} troops)")
                .ToList();
            if (leaders.Count == 0)
                return "No clan member leads a party. Create one from the clan screen (Parties tab) for each friend, then save.";
            return "Heroes available to players:\n" + string.Join("\n", leaders);
        }
    }
}
