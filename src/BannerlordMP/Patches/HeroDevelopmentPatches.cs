using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace BannerlordMP.Patches
{
    /// <summary>
    /// Host side: a player's hero is an AI hero to the host's game, and the game spends an AI hero's focus and
    /// attribute points (and picks perks) for it when it levels up. Those points belong to the player, who spends
    /// them on their own machine; the host's random picks came back through the ledger and broke the player's
    /// counts (focus points even went negative).
    /// </summary>
    [HarmonyPatch]
    internal static class HeroDevelopmentPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return new[]
            {
                AccessTools.Method(typeof(HeroDeveloper), "DistributeUnspentFocusPoints"),
                AccessTools.Method(typeof(HeroDeveloper), "DistributeUnspentAttributePoints"),
                AccessTools.Method(typeof(HeroDeveloper), "SelectPerks"),
                AccessTools.Method(typeof(HeroDeveloper), nameof(HeroDeveloper.DevelopCharacterStats)),
            }.Where(method => method != null);
        }

        private static bool Prefix(HeroDeveloper __instance, MethodBase __originalMethod)
        {
            if (!(MpSession.Current is HostSession host) || !host.IsPlayerHero(__instance.Hero))
                return true;
            Log.Info($"Kept the host from spending {__instance.Hero.StringId}'s points ({__originalMethod.Name})");
            return false;
        }
    }
}
