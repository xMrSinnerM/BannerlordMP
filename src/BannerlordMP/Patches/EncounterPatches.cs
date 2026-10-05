using BannerlordMP.Session;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Patches
{
    /// <summary>Keeps each battle on exactly one machine. See <see cref="MpSession.AllowEncounter"/>.</summary>
    [HarmonyPatch(typeof(EncounterManager), nameof(EncounterManager.StartPartyEncounter))]
    internal static class StartPartyEncounterPatch
    {
        private static bool Prefix(PartyBase attackerParty, PartyBase defenderParty)
        {
            var session = MpSession.Current;
            return session == null || session.AllowEncounter(attackerParty?.MobileParty, defenderParty?.MobileParty);
        }
    }
}
