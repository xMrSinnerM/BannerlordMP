using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// Host side: an army led by a joined player looks like an AI lord's army here, and its hourly turn would run
    /// the AI's rules on it (boost cohesion with the player's influence, break it up for inactivity, send its leader
    /// to a gathering point). The player's own game runs that turn for its army, with single player's rules for the
    /// player's army, and sends the outcome. Gathering and attaching the called parties still happens here.
    /// </summary>
    [HarmonyPatch(typeof(Army), "HourlyTick")]
    internal static class ArmyPatches
    {
        private static bool Prefix(Army __instance)
        {
            var leader = __instance?.LeaderParty;
            return !(MpSession.Current is HostSession host && leader != null && leader != TaleWorlds.CampaignSystem.Party.MobileParty.MainParty && host.IsPlayerParty(leader));
        }
    }
}
