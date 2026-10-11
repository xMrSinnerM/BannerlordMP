using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Game;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Session
{
    /// <summary>
    /// What the player decides in their own game besides their party and hero (garrisons, dungeons, fiefs, kingdom
    /// policies, party roles, relations, marriages): watched here and proposed to the server when it changes, so the
    /// server's world has it too. What the server tells us about the same things becomes the new known state, so it
    /// is not sent back.
    /// </summary>
    internal sealed partial class ClientSession
    {
        private const float StateWatchInterval = 1f;
        private float _stateWatchTimer;
        /// <summary>Description of each watched thing as last sent to or heard from the server.</summary>
        private readonly Dictionary<string, string> _knownState = new Dictionary<string, string>();
        private readonly HashSet<(Hero, Hero)> _relationsChanged = new HashSet<(Hero, Hero)>();
        private readonly List<(Hero, Hero)> _marriages = new List<(Hero, Hero)>();
        private string _rosterSettlement;

        public override void OnLocalRelationChanged(Hero hero, Hero other)
        {
            if (_welcomed && (IsOurs(hero) || IsOurs(other)))
                _relationsChanged.Add((hero, other));
        }

        public override void OnLocalMarriage(Hero hero, Hero other)
        {
            if (_welcomed && (IsOurs(hero) || IsOurs(other)))
                _marriages.Add((hero, other));
        }

        private static bool IsOurs(Hero hero) => hero != null && (hero == Hero.MainHero || (hero.Clan != null && hero.Clan == Clan.PlayerClan));

        private void TickStateWatch(float dt)
        {
            _stateWatchTimer += dt;
            if (_stateWatchTimer < StateWatchInterval)
                return;
            _stateWatchTimer = 0;
            try
            {
                foreach (var (hero, other) in _relationsChanged)
                    OnLocalWorldEvent(WorldEventKind.Relation, hero.StringId, StateSync.DescribeRelation(hero, other));
                _relationsChanged.Clear();
                // The game announces a marriage before it happens; by now it has.
                foreach (var (hero, other) in _marriages.Where(m => m.Item1.Spouse == m.Item2))
                {
                    OnLocalWorldEvent(WorldEventKind.Married, hero.StringId, other.StringId);
                    // The spouse joined our party before the marriage was sent, when the server still saw them as
                    // someone else's clan member and refused the move; now it can take it.
                    var ourParty = MobileParty.MainParty;
                    foreach (var spouse in new[] { hero, other }.Where(h => h != Hero.MainHero && h.PartyBelongedTo == ourParty && ourParty?.StringId != null))
                        OnLocalWorldEvent(WorldEventKind.HeroJoinedParty, spouse.StringId, ourParty.StringId);
                }
                _marriages.Clear();

                WatchSettlementRosters();
                KeepRidingInArmy();
                var clan = Clan.PlayerClan;
                if (clan == null)
                    return;
                foreach (var settlement in clan.Settlements.Where(s => s?.Town != null).ToList())
                    Watch("f:" + settlement.StringId, StateSync.DescribeFief(settlement.Town, false),
                        (before, now) => OnLocalWorldEvent(WorldEventKind.FiefState, settlement.StringId, now));
                var kingdom = clan.Kingdom;
                if (kingdom != null && kingdom.RulingClan == clan)
                    Watch("k:" + kingdom.StringId, StateSync.DescribePolicies(kingdom),
                        (before, now) => OnLocalWorldEvent(WorldEventKind.KingdomPolicies, kingdom.StringId, now));
                var main = MobileParty.MainParty;
                if (main?.StringId != null)
                {
                    Watch("r:" + main.StringId, StateSync.DescribeRoles(main),
                        (before, now) => OnLocalWorldEvent(WorldEventKind.PartyRoles, main.StringId, now));
                    WatchCompanions(main);
                }
            }
            catch (Exception e)
            {
                Log.Error("Watching our clan's state failed", e);
            }
        }

        /// <summary>Troops left in a garrison and prisoners left in a dungeon, while we are in that town or castle.</summary>
        private void WatchSettlementRosters()
        {
            var settlement = MobileParty.MainParty?.CurrentSettlement;
            var id = settlement?.StringId;
            if (id != _rosterSettlement)
            {
                // Arriving: start from what the settlement has now.
                if (_rosterSettlement != null)
                {
                    _knownState.Remove(StateSync.Garrison + ":" + _rosterSettlement);
                    _knownState.Remove(StateSync.Prison + ":" + _rosterSettlement);
                }
                _rosterSettlement = id;
            }
            if (settlement == null || !(settlement.IsTown || settlement.IsCastle) || settlement.MapFaction != Clan.PlayerClan?.MapFaction)
                return;
            foreach (var which in new[] { StateSync.Garrison, StateSync.Prison })
            {
                Watch(which + ":" + id, StateSync.DescribeRoster(StateSync.Roster(settlement, which)),
                    (before, now) => OnLocalWorldEvent(WorldEventKind.SettlementRoster, id, StateSync.DescribeRosterChange(which, StateSync.RosterChange(before, now))));
            }
        }

        /// <summary>
        /// Companions with us fight our battles and wear what we give them here, so their gear, skills and level are
        /// ours to send. Those in other parties are the server's (it runs those parties).
        /// </summary>
        private void WatchCompanions(MobileParty main)
        {
            var withUs = new HashSet<string>();
            foreach (var element in main.MemberRoster.GetTroopRoster())
            {
                var hero = element.Character?.HeroObject;
                if (hero == null || hero == Hero.MainHero || hero.Clan != Clan.PlayerClan || !hero.IsAlive)
                    continue;
                withUs.Add("h:" + hero.StringId);
                Watch("h:" + hero.StringId, StateSync.DescribeCompanion(hero),
                    (before, now) => OnLocalWorldEvent(WorldEventKind.HeroState, hero.StringId, StateSync.CounterChange(before, now)));
            }
            foreach (var key in _knownState.Keys.Where(k => k.StartsWith("h:") && !withUs.Contains(k)).ToList())
                _knownState.Remove(key);
        }

        private double _armyRideSent;

        /// <summary>
        /// Riding in an AI lord's army: the server lets go of our party when that army enters a town or castle (our game
        /// decides about it). Telling it now and then that we are still with the army puts us back.
        /// </summary>
        private void KeepRidingInArmy()
        {
            var main = MobileParty.MainParty;
            var leader = main?.Army?.LeaderParty;
            if (leader == null || leader == main || main.AttachedTo != leader || RealSeconds - _armyRideSent < 30)
                return;
            _armyRideSent = RealSeconds;
            Net.SendToAll(DescribeArmyEvent(WorldEventKind.ArmyPartyAttached, leader, main));
        }

        private void SendCaravan(MobileParty caravan)
        {
            var description = StateSync.DescribeCaravan(caravan);
            if (description != null)
                OnLocalWorldEvent(WorldEventKind.CaravanCreated, caravan.StringId, description);
        }

        private void Watch(string key, string current, Action<string, string> send)
        {
            if (current == null)
                return;
            if (!_knownState.TryGetValue(key, out var known))
            {
                _knownState[key] = current;
                return;
            }
            if (known == current)
                return;
            _knownState[key] = current;
            send(known, current);
        }

        /// <summary>The server told us about something we watch: that is what it is now, not a change of ours.</summary>
        private void AfterRemoteState(WorldEventMessage message)
        {
            try
            {
                switch (message.Kind)
                {
                    case WorldEventKind.FiefState:
                        Remember("f:" + message.A, StateSync.DescribeFief(WorldBridge.Find<TaleWorlds.CampaignSystem.Settlements.Settlement>(message.A)?.Town, false));
                        break;
                    case WorldEventKind.KingdomPolicies:
                        Remember("k:" + message.A, StateSync.DescribePolicies(WorldBridge.Find<Kingdom>(message.A)));
                        break;
                    case WorldEventKind.HeroState:
                        if (_knownState.ContainsKey("h:" + message.A))
                            Remember("h:" + message.A, StateSync.DescribeCompanion(GameBridge.FindHero(message.A)));
                        break;
                    case WorldEventKind.PartyRoles:
                        Remember("r:" + message.A, StateSync.DescribeRoles(GameBridge.FindParty(message.A)));
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error("Remembering the server's state failed", e);
            }
        }

        /// <summary>The server sent our current settlement's garrison troops.</summary>
        private void AfterRemoteRoster(string partyId)
        {
            var settlement = MobileParty.MainParty?.CurrentSettlement;
            var garrison = settlement?.Town?.GarrisonParty;
            if (garrison != null && garrison.StringId == partyId)
                Remember(StateSync.Garrison + ":" + settlement.StringId, StateSync.DescribeRoster(garrison.MemberRoster));
        }

        private void Remember(string key, string value)
        {
            if (value != null)
                _knownState[key] = value;
        }
    }
}
