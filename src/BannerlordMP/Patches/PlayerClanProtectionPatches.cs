using BannerlordMP.Game;
using Helpers;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// Host side: the AI may not take decisions for players' clans. Only what a player does on their own machine
    /// (relayed to the host) changes their clan. Forced changes (a clan or kingdom being destroyed) still happen.
    /// </summary>
    internal static class PlayerClanProtectionPatches
    {
        [HarmonyPatch(typeof(ChangeKingdomAction), "ApplyInternal")]
        internal static class ChangeKingdom
        {
            private static bool Prefix(Clan clan, Kingdom newKingdom, ChangeKingdomAction.ChangeKingdomActionDetail detail)
            {
                if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByClanDestruction || detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByKingdomDestruction)
                    return true;
                if (!PlayerClanGuard.Protects(clan))
                    return true;
                PlayerClanGuard.Blocked($"{clan.StringId} {detail} {newKingdom?.StringId}");
                return false;
            }
        }

        [HarmonyPatch(typeof(MarriageAction), "ApplyInternal")]
        internal static class Marriage
        {
            private static bool Prefix(Hero firstHero, Hero secondHero)
            {
                if (!PlayerClanGuard.Protects(firstHero) && !PlayerClanGuard.Protects(secondHero))
                    return true;
                PlayerClanGuard.Blocked($"marriage {firstHero?.StringId} + {secondHero?.StringId}");
                return false;
            }
        }

        [HarmonyPatch(typeof(DeclareWarAction), "ApplyInternal")]
        internal static class DeclareWar
        {
            private static bool Prefix(IFaction faction1, IFaction faction2, DeclareWarAction.DeclareWarDetail declareWarDetail)
            {
                // Structural wars (rebellions, new kingdoms, throne claims) follow from events, not choices.
                if (declareWarDetail == DeclareWarAction.DeclareWarDetail.CausedByRebellion || declareWarDetail == DeclareWarAction.DeclareWarDetail.CausedByKingdomCreation
                    || declareWarDetail == DeclareWarAction.DeclareWarDetail.CausedByClaimOnThrone)
                    return true;
                // Others may declare war on a player; a player's own faction only declares war when they decide to.
                if (!PlayerClanGuard.Protects(faction1))
                    return true;
                PlayerClanGuard.Blocked($"war {faction1?.StringId} -> {faction2?.StringId} ({declareWarDetail})");
                return false;
            }
        }

        [HarmonyPatch(typeof(MakePeaceAction), "ApplyInternal")]
        internal static class MakePeace
        {
            private static bool Prefix(IFaction faction1, IFaction faction2)
            {
                // Peace binds both sides, so a player's faction is never signed into peace by the AI.
                if (!PlayerClanGuard.Protects(faction1) && !PlayerClanGuard.Protects(faction2))
                    return true;
                PlayerClanGuard.Blocked($"peace {faction1?.StringId} <-> {faction2?.StringId}");
                return false;
            }
        }

        [HarmonyPatch(typeof(Kingdom), nameof(Kingdom.AddDecision))]
        internal static class KingdomProposal
        {
            private static bool Prefix(KingdomDecision kingdomDecision)
            {
                if (!PlayerClanGuard.Protects(kingdomDecision?.ProposerClan))
                    return true;
                PlayerClanGuard.Blocked($"proposal by {kingdomDecision.ProposerClan.StringId}: {kingdomDecision.GetType().Name}");
                return false;
            }
        }

        [HarmonyPatch(typeof(ChangeClanLeaderAction), "ApplyInternal")]
        internal static class ClanLeader
        {
            private static bool Prefix(Clan clan)
            {
                // A dead leader must be replaced; otherwise the leader is the player's hero and stays so.
                if (clan?.Leader == null || !clan.Leader.IsAlive || !PlayerClanGuard.Protects(clan))
                    return true;
                PlayerClanGuard.Blocked($"leader change of {clan.StringId}");
                return false;
            }
        }

        /// <summary>
        /// The AI respawns lords' parties after a defeat. For a player's hero that is welcome (it lets them rejoin),
        /// but the new party must never be driven by the AI: freeze it like any player party on the host.
        /// Blocking the spawn instead would hand a null party back to game code that uses it.
        /// </summary>
        [HarmonyPatch(typeof(MobilePartyHelper), nameof(MobilePartyHelper.SpawnLordParty), typeof(Hero), typeof(Settlement))]
        internal static class SpawnLordAtSettlement
        {
            private static void Postfix(Hero hero, MobileParty __result) => FreezePlayerParty(hero, __result);
        }

        [HarmonyPatch(typeof(MobilePartyHelper), nameof(MobilePartyHelper.SpawnLordParty), typeof(Hero), typeof(CampaignVec2), typeof(float))]
        internal static class SpawnLordAtPosition
        {
            private static void Postfix(Hero hero, MobileParty __result) => FreezePlayerParty(hero, __result);
        }

        private static void FreezePlayerParty(Hero hero, MobileParty party)
        {
            if (party == null || !(Session.MpSession.Current is Session.HostSession host) || !host.IsPlayerHero(hero))
                return;
            Log.Info($"Player hero {hero.StringId} got a new party {party.StringId}; frozen until its owner plays");
            GameBridge.Freeze(party);
        }
    }
}
