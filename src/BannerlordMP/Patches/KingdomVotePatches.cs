using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.Library;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// Host side: at election time, a player clan's support is the player's own vote (sent from their game),
    /// never the AI's. No vote means abstaining. A player who rules the kingdom picks the final outcome.
    /// </summary>
    internal static class KingdomVotePatches
    {
        [HarmonyPatch(typeof(KingdomDecision), nameof(KingdomDecision.DetermineSupportOption))]
        internal static class SupportOption
        {
            private static bool Prefix(KingdomDecision __instance, Supporter supporter, MBReadOnlyList<DecisionOutcome> possibleOutcomes,
                ref Supporter.SupportWeights supportWeightOfSelectedOutcome, ref DecisionOutcome __result)
            {
                if (!(MpSession.Current is HostSession host) || supporter?.Clan == null || !host.IsPlayerClan(supporter.Clan)
                    || possibleOutcomes == null || possibleOutcomes.Count == 0)
                    return true;

                if (host.TryGetVote(__instance, supporter.Clan, out var title, out var weight)
                    && title != null && weight != VoteWeight.Abstain && weight != VoteWeight.Choose)
                {
                    var outcome = possibleOutcomes.FirstOrDefault(o => o.GetDecisionTitle()?.ToString() == title);
                    if (outcome != null)
                    {
                        __result = outcome;
                        supportWeightOfSelectedOutcome = HostSession.ToGameWeight(weight);
                        Log.Info($"Clan {supporter.Clan.StringId} supports '{title}' ({weight}) as its player voted");
                        return false;
                    }
                }

                // No vote (or the voted option no longer exists): the player's clan stays neutral.
                __result = possibleOutcomes[0];
                supportWeightOfSelectedOutcome = Supporter.SupportWeights.StayNeutral;
                return false;
            }
        }

        [HarmonyPatch(typeof(KingdomElection), "GetAiChoice")]
        internal static class RulerChoice
        {
            private static void Postfix(KingdomElection __instance, MBReadOnlyList<DecisionOutcome> possibleOutcomes, ref DecisionOutcome __result)
            {
                if (!(MpSession.Current is HostSession host) || possibleOutcomes == null)
                    return;
                var chooser = Traverse.Create(__instance).Field("_chooser").GetValue<Clan>();
                if (chooser == null || !host.IsPlayerClan(chooser))
                    return;
                var decision = Traverse.Create(__instance).Field("_decision").GetValue<KingdomDecision>();
                if (!host.TryGetVote(decision, chooser, out var title, out _) || title == null)
                    return; // The ruler did not answer: the outcome with the most support wins, as the AI picked.
                var outcome = possibleOutcomes.FirstOrDefault(o => o.GetDecisionTitle()?.ToString() == title);
                if (outcome == null)
                    return;
                __result = outcome;
                Log.Info($"Ruling player clan {chooser.StringId} chose '{title}'");
            }
        }
    }
}
