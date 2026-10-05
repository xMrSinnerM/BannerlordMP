using System;
using BannerlordMP.Session;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Game
{
    /// <summary>Forwards the campaign events the session cares about. Stores nothing in the save.</summary>
    internal sealed class MpCampaignBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.OnPlayerBattleEndEvent.AddNonSerializedListener(this, OnPlayerBattleEnd);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnMobilePartyDestroyed);
        }

        public override void SyncData(IDataStore dataStore)
        {
        }

        private static void OnPlayerBattleEnd(MapEvent mapEvent)
        {
            try
            {
                MpSession.Current?.OnLocalBattleEnded(mapEvent);
            }
            catch (Exception e)
            {
                Log.Error("Failed to report battle result", e);
            }
        }

        private static void OnMobilePartyDestroyed(MobileParty party, PartyBase destroyer)
        {
            if (GameBridge.ApplyingRemoteDestroy)
                return;
            try
            {
                MpSession.Current?.OnLocalPartyDestroyed(party);
            }
            catch (Exception e)
            {
                Log.Error("Failed to report destroyed party", e);
            }
        }
    }
}
