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
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, siege =>
                World(WorldEventKind.SiegeStarted, siege?.BesiegedSettlement?.StringId, siege?.BesiegerCamp?.LeaderParty?.StringId));
            CampaignEvents.OnSiegeEventEndedEvent.AddNonSerializedListener(this, siege =>
                World(WorldEventKind.SiegeEnded, siege?.BesiegedSettlement?.StringId, string.Empty));
            CampaignEvents.OnCharacterCreationIsOverEvent.AddNonSerializedListener(this, Ui.CharacterCreator.OnCreationOver);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, (captor, prisoner) =>
                World(WorldEventKind.HeroCaptured, prisoner?.StringId, captor?.IsMobile == true ? captor.MobileParty?.StringId : captor?.Settlement?.StringId));
            CampaignEvents.HeroPrisonerReleased.AddNonSerializedListener(this, (prisoner, captor, faction, detail, notify) =>
                World(WorldEventKind.HeroReleased, prisoner?.StringId, string.Empty));
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, (party, settlement, hero) =>
            {
                if (party != null && party == MobileParty.MainParty)
                    Forward(s => s.OnLocalSettlementEntered(settlement));
            });
            CampaignEvents.OnSettlementLeftEvent.AddNonSerializedListener(this, (party, settlement) =>
            {
                if (party != null && party == MobileParty.MainParty)
                    Forward(s => s.OnLocalSettlementLeft(settlement));
            });
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
