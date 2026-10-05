using System;
using BannerlordMP.Core.Protocol;
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
            CampaignEvents.MobilePartyCreated.AddNonSerializedListener(this, party => Forward(s => s.OnLocalPartyCreated(party)));
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, (settlement, openToClaim, newOwner, oldOwner, capturer, detail) =>
                World(WorldEventKind.SettlementOwner, settlement?.StringId, newOwner?.StringId));
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, (a, b, detail) => World(WorldEventKind.War, a?.StringId, b?.StringId));
            CampaignEvents.MakePeace.AddNonSerializedListener(this, (a, b, detail) => World(WorldEventKind.Peace, a?.StringId, b?.StringId));
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, (clan, oldKingdom, newKingdom, detail, notify) =>
                World(WorldEventKind.ClanKingdom, clan?.StringId, newKingdom?.StringId ?? string.Empty));
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, (victim, killer, detail, notify) =>
                World(WorldEventKind.HeroKilled, victim?.StringId, killer?.StringId ?? string.Empty));
        }

        private static void World(WorldEventKind kind, string a, string b)
        {
            if (string.IsNullOrEmpty(a))
                return;
            Forward(s => s.OnLocalWorldEvent(kind, a, b ?? string.Empty));
        }

        private static void Forward(Action<MpSession> action)
        {
            var session = MpSession.Current;
            if (session == null)
                return;
            try
            {
                action(session);
            }
            catch (Exception e)
            {
                Log.Error("Failed to forward a campaign event", e);
            }
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
