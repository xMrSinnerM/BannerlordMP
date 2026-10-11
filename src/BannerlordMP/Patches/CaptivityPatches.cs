using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordMP.Game;
using BannerlordMP.Session;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// A player's hero taken prisoner leaves their party behind without a leader, as single player keeps the main
    /// party while the player is a captive (see <see cref="WorldBridge.CapturePlayerHero"/>). To the game it looks like
    /// a lord's party whose lord was captured, which it disbands. These patches keep it for its hero, and leave the
    /// captivity itself (escape) to the player's own game.
    /// </summary>
    internal static class CaptivityPatches
    {
        /// <summary>A leaderless party kept for a played hero who is a prisoner.</summary>
        private static bool IsKeptPlayerParty(MobileParty party)
        {
            var owner = WorldBridge.KeptPartyOwner(party);
            return owner != null && MpSession.Current?.IsPlayedHero(owner) == true;
        }

        /// <summary>The game starts disbanding a lord's party when the lord is taken prisoner; not a player's.</summary>
        [HarmonyPatch]
        internal static class NoDisbandOnCapture
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                return new[] { AccessTools.Method(typeof(DisbandPartyCampaignBehavior), "OnHeroPrisonerTaken") }.Where(m => m != null);
            }

            private static bool Prefix(Hero __1) => MpSession.Current?.IsPlayedHero(__1) != true;
        }

        /// <summary>The disbanding behavior's hourly and daily turns, and any other start of a disband, skip kept parties.</summary>
        [HarmonyPatch]
        internal static class NoDisband
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                return new[]
                {
                    AccessTools.Method(typeof(DisbandPartyCampaignBehavior), "HourlyTickParty"),
                    AccessTools.Method(typeof(DisbandPartyCampaignBehavior), "DailyTickParty"),
                    AccessTools.Method(typeof(DisbandPartyAction), nameof(DisbandPartyAction.StartDisband)),
                }.Where(m => m != null);
            }

            private static bool Prefix(MobileParty __0) => !IsKeptPlayerParty(__0);
        }

        /// <summary>
        /// Host: an online player escapes through their own game, with single player's chances. The host's daily
        /// escape chance for prisoners would give them a second one. An offline player's hero can still escape here.
        /// </summary>
        [HarmonyPatch]
        internal static class NoHostEscapeForOnlinePlayers
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                return new[] { AccessTools.Method(typeof(PrisonerReleaseCampaignBehavior), "DailyHeroTick") }.Where(m => m != null);
            }

            private static bool Prefix(Hero __0) => !(MpSession.Current is HostSession host && host.IsOnlinePlayerHero(__0));
        }

        /// <summary>
        /// Host: a freed lord without a party gets a new one from the game. A player's hero gets their own back
        /// instead, so the party their game controls keeps its id.
        /// </summary>
        [HarmonyPatch]
        internal static class OwnPartyBack
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                return new[] { AccessTools.Method(typeof(MobilePartyHelper), "SpawnLordPartyAux") }.Where(m => m != null);
            }

            private static bool Prefix(Hero __0, CampaignVec2 __1, ref MobileParty __result)
            {
                if (!(MpSession.Current is HostSession host) || !host.IsPlayerHero(__0))
                    return true;
                MobileParty party = null;
                WorldBridge.Remote(() => party = WorldBridge.RestoreKeptParty(__0, __1));
                if (party == null)
                    return true;
                Log.Info($"The game wanted a new party for {__0.StringId}; gave them back {party.StringId}");
                __result = party;
                return false;
            }
        }
    }
}
