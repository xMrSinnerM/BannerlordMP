using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// On a dedicated server nobody plays the host's hero: it stays parked, so map clicks do not move it and it
    /// never enters settlements (which would open menus on the server).
    /// </summary>
    internal static class DedicatedHostPatches
    {
        private static bool IsDedicatedHost => MpSession.Current is HostSession host && host.IsDedicated;

        [HarmonyPatch(typeof(MapState), nameof(MapState.ProcessTravel))]
        internal static class ProcessTravelPatch
        {
            private static bool Prefix() => !IsDedicatedHost;
        }

        [HarmonyPatch(typeof(EncounterManager), nameof(EncounterManager.StartSettlementEncounter))]
        internal static class StartSettlementEncounterPatch
        {
            private static bool Prefix(MobileParty attackerParty, Settlement settlement) =>
                !(IsDedicatedHost && attackerParty == MobileParty.MainParty);
        }
    }
}
