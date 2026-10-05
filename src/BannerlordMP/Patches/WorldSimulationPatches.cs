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
}
