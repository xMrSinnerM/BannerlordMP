using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordMP.Game;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// On a joined client, the campaign's periodic simulation does not run: no AI decisions, no spawning,
    /// no economy, no diplomacy, no daily or hourly changes. All of that happens on the host and arrives as
    /// messages; the player's own party gets the host's wages, food and healing through the ledger.
    /// Per-frame logic (movement of the player's party, encounters, menus) still runs normally.
    /// </summary>
    [HarmonyPatch]
    internal static class WorldSimulationPatches
    {
        private static bool RunLocally() => !WorldAuthority.ClientMirroring;

        [HarmonyPatch(typeof(Campaign), "QuarterHourlyTick")]
        [HarmonyPrefix]
        private static bool QuarterHourly() => RunLocally();

        [HarmonyPatch(typeof(Campaign), "HourlyTick")]
        [HarmonyPrefix]
        private static bool Hourly() => RunLocally();

        [HarmonyPatch(typeof(Campaign), "DailyTick")]
        [HarmonyPrefix]
        private static bool Daily() => RunLocally();

        [HarmonyPatch(typeof(Campaign), "DailyTickSettlement")]
        [HarmonyPrefix]
        private static bool DailySettlement() => RunLocally();

        [HarmonyPatch(typeof(Campaign), "OnWeeklyTick")]
        [HarmonyPrefix]
        private static bool Weekly() => RunLocally();

        [HarmonyPatch(typeof(Campaign), nameof(Campaign.LateAITick))]
        [HarmonyPrefix]
        private static bool LateAi() => RunLocally();
    }

    /// <summary>
    /// The campaign's second scheduler: it gives every party, settlement, clan and hero its hourly and daily
    /// turn, spread over many frames, and fires the periodic campaign events. Villagers, caravans and bandits
    /// spawn from here, so on a client it must stay off as well (the host spawns, clients mirror).
    /// </summary>
    [HarmonyPatch]
    internal static class PeriodicEventManagerPatches
    {
        private static readonly string[] Methods =
        {
            "TickPeriodicEvents", "PeriodicQuarterDailyTick", "MobilePartyHourlyTick", "TickPartialHourlyAi",
            "PeriodicHourlyTick", "PeriodicDailyTick", "SignalPeriodicEvents",
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignPeriodicEventManager");
            if (type == null)
            {
                Log.Error("CampaignPeriodicEventManager not found; clients will keep simulating parts of the world");
                return Enumerable.Empty<MethodBase>();
            }
            var found = Methods.Select(name => (MethodBase)AccessTools.Method(type, name)).Where(m => m != null).ToList();
            if (found.Count != Methods.Length)
                Log.Error($"Only {found.Count} of {Methods.Length} periodic event methods found on this game version");
            return found;
        }

        private static bool Prefix() => !WorldAuthority.ClientMirroring;
    }
}
