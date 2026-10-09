using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// Host side: a joined player's party looks like an AI lord's party to the host's game, which manages it the
    /// way it manages lords: it auto-upgrades troops, buys food and horses, sells loot and prisoners, recruits,
    /// and makes troops desert once the wages pass the lord's payment limit (even under the party size limit).
    /// A player manages their own party on their own machine, so none of that may run on it here. Desertion
    /// from low morale or an oversized party still happens, as in single player.
    /// </summary>
    internal static class PlayerPartyPatches
    {
        public static bool IsPlayerParty(MobileParty party)
        {
            return party != null && MpSession.Current is HostSession host && host.IsPlayerHero(party.LeaderHero);
        }

        /// <summary>No wage limit for a player's party (a single-player main party has none).</summary>
        [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.PaymentLimit), MethodType.Getter)]
        internal static class PaymentLimit
        {
            private static void Postfix(MobileParty __instance, ref int __result)
            {
                if (IsPlayerParty(__instance))
                    __result = Campaign.Current?.Models?.PartyWageModel?.MaxWagePaymentLimit ?? __result;
            }
        }

        /// <summary>
        /// While its player is in a battle or offline, a party neither eats nor loses troops to low morale; the
        /// player skips that time when they come back (see ClientSession.SkipAheadIfBehind).
        /// </summary>
        [HarmonyPatch]
        internal static class AwayUpkeep
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                // A method missing from this game version is skipped rather than failing every patch.
                return new[]
                {
                    AccessTools.Method(typeof(FoodConsumptionBehavior), nameof(FoodConsumptionBehavior.DailyTickParty)),
                    AccessTools.Method(typeof(FoodConsumptionBehavior), "PartyConsumeFood"),
                    AccessTools.Method(typeof(DesertionCampaignBehavior), "DailyTickParty"),
                }.Where(method => method != null);
            }

            private static bool Prefix(MobileParty __0)
            {
                return !(MpSession.Current is HostSession host && host.IsPlayerPartyAway(__0));
            }
        }

        /// <summary>The AI's party management, skipped for players' parties.</summary>
        [HarmonyPatch]
        internal static class AiPartyManagement
        {
            private static readonly Type[] Behaviors =
            {
                typeof(PartyUpgraderCampaignBehavior),
                typeof(PartiesBuyFoodCampaignBehavior),
                typeof(PartiesBuyHorseCampaignBehavior),
                typeof(PartiesSellLootCampaignBehavior),
                typeof(PartiesSellPrisonerCampaignBehavior),
                typeof(RecruitmentCampaignBehavior),
            };

            private static IEnumerable<MethodBase> TargetMethods()
            {
                // Every void method of these behaviors that acts on one party (its first parameter).
                return Behaviors.SelectMany(type => AccessTools.GetDeclaredMethods(type))
                    .Where(method => !method.IsAbstract && !method.IsGenericMethod && !method.Name.StartsWith("<") && method.ReturnType == typeof(void))
                    .Where(method =>
                    {
                        var parameters = method.GetParameters();
                        return parameters.Length > 0 && (parameters[0].ParameterType == typeof(MobileParty) || parameters[0].ParameterType == typeof(PartyBase));
                    });
            }

            private static bool Prefix(object __0)
            {
                var party = __0 as MobileParty ?? (__0 as PartyBase)?.MobileParty;
                return !IsPlayerParty(party);
            }
        }
    }
}
