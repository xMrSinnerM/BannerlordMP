using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.CampaignBehaviors.AiBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

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
            return party != null && MpSession.Current is HostSession host && host.IsPlayerHero(Game.WorldBridge.PartyHero(party));
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
                    // The steps PartyConsumeFood takes, in case the game reaches them another way (its frame tick).
                    AccessTools.Method(typeof(FoodConsumptionBehavior), "MakeFoodConsumption"),
                    AccessTools.Method(typeof(FoodConsumptionBehavior), "SlaughterLivestock"),
                    AccessTools.Method(typeof(DesertionCampaignBehavior), "DailyTickParty"),
                }.Where(method => method != null);
            }

            private static bool Prefix(MobileParty __0)
            {
                // Joined player: the server feeds their party and sends the result through the ledger. Eating here
                // as well counted every meal twice (the local drop went to the server as the player's own change).
                if (MpSession.Current is ClientSession && __0 != null && __0 == MobileParty.MainParty)
                    return false;
                return !(MpSession.Current is HostSession host && host.IsPlayerPartyAway(__0));
            }
        }

        /// <summary>
        /// A party's hourly AI planning (where to go, whom to chase, which settlement to visit). Players' parties
        /// are steered over the network, so the host's AI never plans for them. And one party's planning failing
        /// (a NullReferenceException in the game's settlement-visit check took a server down after a battle) must
        /// not end the game: that party just skips planning this hour.
        /// </summary>
        [HarmonyPatch(typeof(AiPartyThinkBehavior), "PartyHourlyAiTick")]
        internal static class HourlyAiPlanning
        {
            private static readonly HashSet<string> Reported = new HashSet<string>();

            private static bool Prefix(MobileParty mobileParty) => !IsPlayerParty(mobileParty);

            private static Exception Finalizer(Exception __exception, MobileParty mobileParty)
            {
                if (__exception == null)
                    return null;
                var key = (mobileParty?.StringId ?? "?") + __exception.GetType().Name;
                if (Reported.Add(key))
                    Log.Error($"AI planning failed for party {mobileParty?.StringId} (leader {mobileParty?.LeaderHero?.StringId}, faction {mobileParty?.MapFaction?.StringId}); skipped", __exception);
                return null;
            }
        }

        /// <summary>
        /// A player's party never enters or assaults a settlement on the host: that happens on the player's machine
        /// and arrives as events. Here the party besieging for its player was sent in by the game's siege AI, which
        /// fought the assault itself (the player lost hundreds of troops to it) and took the town or castle while the
        /// player was still preparing; that half-finished the player's own siege, so the attack menu wouldn't close.
        /// </summary>
        [HarmonyPatch(typeof(EncounterManager), nameof(EncounterManager.StartSettlementEncounter))]
        internal static class NoSettlementEncounters
        {
            private static readonly HashSet<string> Reported = new HashSet<string>();

            private static bool Prefix(MobileParty attackerParty, Settlement settlement)
            {
                if (attackerParty != null && attackerParty != MobileParty.MainParty && !IsPlayerParty(attackerParty))
                {
                    // An AI army with a player riding in it enters, raids or assaults: the player isn't taken along
                    // here (their game decides). They stay in the army and catch up with it afterwards.
                    if (MpSession.Current is HostSession host)
                    {
                        MobileParty rider;
                        while ((rider = host.OnlinePlayerRidingWith(attackerParty)) != null)
                        {
                            rider.AttachedTo = null;
                            Log.Info($"Detached player party {rider.StringId} from {attackerParty.StringId}'s army before it went into {settlement?.StringId}");
                        }
                    }
                    return true;
                }
                if (attackerParty == MobileParty.MainParty || !IsPlayerParty(attackerParty))
                    return true;
                if (Reported.Add(attackerParty.StringId + ">" + settlement?.StringId))
                    Log.Info($"Kept player party {attackerParty.StringId} from entering or assaulting {settlement?.StringId} on the host (their own game does that)");
                return false;
            }
        }

        /// <summary>
        /// The host's AI lords call parties into their armies; a player's party only joins an army when its player
        /// does it (the change arrives from their game).
        /// </summary>
        [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.Army), MethodType.Setter)]
        internal static class NoArmyCallsForPlayers
        {
            private static bool Prefix(MobileParty __instance, Army value)
            {
                if (value == null || value.LeaderParty == __instance || Game.WorldBridge.ApplyingRemote || !(MpSession.Current is HostSession) || !IsPlayerParty(__instance))
                    return true;
                Log.Info($"Kept the AI from calling player party {__instance.StringId} into {value.LeaderParty?.StringId}'s army");
                return false;
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
