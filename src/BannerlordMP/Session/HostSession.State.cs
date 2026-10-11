using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Game;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;

namespace BannerlordMP.Session
{
    /// <summary>
    /// Host side of <see cref="StateSync"/>: which player may change what, and the server's own changes to players'
    /// fiefs (buildings finished, loyalty, food), kingdom policies and marriages, sent to everyone.
    /// </summary>
    internal sealed partial class HostSession
    {
        private const float StateRelayInterval = 2f;
        private float _stateRelayTimer;
        private bool _stateBaselined;
        private readonly Dictionary<string, string> _relayedState = new Dictionary<string, string>();
        private readonly List<(Hero, Hero)> _marriages = new List<(Hero, Hero)>();

        /// <summary>A player changes garrisons and dungeons of their own faction, and only their own fiefs, policies and party.</summary>
        private bool MayChangeState(int playerId, WorldEventMessage proposal)
        {
            var hero = GameBridge.FindHero(Players[playerId].HeroId);
            var clan = hero?.Clan;
            string problem = null;
            switch (proposal.Kind)
            {
                case WorldEventKind.SettlementRoster:
                {
                    var settlement = WorldBridge.Find<Settlement>(proposal.A);
                    if (settlement == null || clan == null || settlement.MapFaction != clan.MapFaction)
                        problem = "not their faction's settlement";
                    break;
                }
                case WorldEventKind.FiefState:
                    if (clan == null || WorldBridge.Find<Settlement>(proposal.A)?.OwnerClan != clan)
                        problem = "not their fief";
                    break;
                case WorldEventKind.KingdomPolicies:
                    if (clan == null || WorldBridge.Find<Kingdom>(proposal.A)?.RulingClan != clan)
                        problem = "not their kingdom";
                    break;
                case WorldEventKind.PartyRoles:
                    if (proposal.A != Players[playerId].PartyId)
                        problem = "not their party";
                    break;
                case WorldEventKind.HeroState:
                {
                    var companion = GameBridge.FindHero(proposal.A);
                    if (companion == null || clan == null || companion.Clan != clan || IsPlayedHero(companion))
                        problem = "not their companion";
                    else if (companion.PartyBelongedTo == null || companion.PartyBelongedTo.StringId != Players[playerId].PartyId)
                        problem = BringIntoParty(playerId, companion);
                    break;
                }
                case WorldEventKind.Pregnant:
                {
                    var mother = GameBridge.FindHero(proposal.A);
                    if (mother == null || clan == null || (mother.Clan != clan && mother.Spouse != hero))
                        problem = "not their clan's hero";
                    break;
                }
                case WorldEventKind.GaveBirth:
                    problem = "births happen on the server";
                    break;
                case WorldEventKind.Workshop:
                {
                    var fields = (proposal.B ?? string.Empty).Split(WorldEventMessage.FieldSeparator);
                    var workshop = StateSync.FindWorkshop(WorldBridge.Find<Settlement>(proposal.A), fields[0]);
                    var newOwner = fields.Length > 1 ? GameBridge.FindHero(fields[1]) : null;
                    if (workshop == null || (newOwner != hero && workshop.Owner != hero))
                        problem = "not their workshop";
                    break;
                }
                case WorldEventKind.ArmyPartyJoined:
                case WorldEventKind.ArmyPartyAttached:
                case WorldEventKind.ArmyPartyLeft:
                {
                    // Their party joining or leaving any army, or any party joining or leaving their own army.
                    var own = Players[playerId].PartyId;
                    var party = GameBridge.FindParty(proposal.A);
                    if (proposal.A != own && proposal.B != own)
                        problem = "not their party or army";
                    else if (proposal.A == own && proposal.Kind != WorldEventKind.ArmyPartyLeft && party?.MapFaction != GameBridge.FindParty(proposal.B)?.MapFaction)
                        problem = "not their faction's army";
                    break;
                }
                case WorldEventKind.Relation:
                case WorldEventKind.Married:
                {
                    var first = GameBridge.FindHero(proposal.A);
                    var second = proposal.Kind == WorldEventKind.Married
                        ? GameBridge.FindHero(proposal.B)
                        : GameBridge.FindHero((proposal.B ?? string.Empty).Split(WorldEventMessage.FieldSeparator)[0]);
                    bool Theirs(Hero h) => h != null && (h == hero || (clan != null && h.Clan == clan));
                    if (!Theirs(first) && !Theirs(second))
                        problem = "none of their heroes";
                    break;
                }
            }
            if (problem == null)
                return true;
            Log.Info($"Ignoring {NameOf(playerId)}'s {proposal.Kind} for {proposal.A}: {problem}");
            return false;
        }

        public override void OnLocalMarriage(Hero hero, Hero other) => _marriages.Add((hero, other));

        /// <summary>
        /// A clan member is with the player in their game but not in their party here: their move into it never
        /// arrived (a new spouse joins the party before the marriage reaches the server, so that move was refused).
        /// Without this, the gear the player gives them was refused too and vanished with the items. Put them in the
        /// party unless the server has them busy (leading a party, governing, captive).
        /// </summary>
        private string BringIntoParty(int playerId, Hero companion)
        {
            var party = PlayerParty(playerId);
            if (party == null)
                return "their party is not here";
            if (!companion.IsAlive || companion.IsPrisoner)
                return "dead or captive here";
            if (companion.PartyBelongedTo?.LeaderHero == companion)
                return "leading a party here";
            if (companion.GovernorOf != null)
                return "governing " + companion.GovernorOf.Name + " here";
            WorldBridge.Remote(() => AddHeroToPartyAction.Apply(companion, party, false));
            if (companion.PartyBelongedTo != party)
                return "could not be moved into their party here";
            Log.Info($"Moved {companion.StringId} into {NameOf(playerId)}'s party: they are with them in their game");
            return null;
        }

        /// <summary>
        /// Another player's party fought beside this player in their battle: take its losses from it here. Its owner's
        /// game gets the new troop counts with its next party update, and a note saying why.
        /// </summary>
        private void ApplyAllyLosses(int playerId, WorldEventMessage proposal)
        {
            var ally = GameBridge.FindParty(proposal.A);
            var own = PlayerParty(playerId);
            var owner = Players.Values.FirstOrDefault(p => p.PartyId == proposal.A);
            if (ally == null || own == null || owner == null || owner.Id == playerId
                || ally.Position.ToVec2().DistanceSquared(own.Position.ToVec2()) > 10f * 10f)
            {
                Log.Info($"Ignoring {NameOf(playerId)}'s ally losses for {proposal.A}: not another player's party close to them");
                return;
            }
            var before = ally.MemberRoster.TotalManCount;
            WorldBridge.Remote(() => StateSync.ApplyRosterChange(ally.MemberRoster, proposal.B));
            var lost = before - ally.MemberRoster.TotalManCount;
            Log.Info($"{owner.Name}'s party fought in {NameOf(playerId)}'s battle: {proposal.B} ({lost} fewer men)");
            var note = $"Your troops fought beside {NameOf(playerId)}" + (lost > 0 ? $" and lost {lost} men." : ".");
            if (owner.Id == HostPlayerId)
            {
                Log.Notify(note);
                return;
            }
            foreach (var pair in _peers.Where(p => p.Value.PlayerId == owner.Id))
                Net.Send(pair.Key, new ChatMessage { PlayerId = HostPlayerId, Text = note });
        }

        private void TickStateRelay(float dt)
        {
            _stateRelayTimer += dt;
            if (_stateRelayTimer < StateRelayInterval)
                return;
            _stateRelayTimer = 0;
            try
            {
                foreach (var (hero, other) in _marriages.Where(m => m.Item1.Spouse == m.Item2))
                    OnLocalWorldEvent(WorldEventKind.Married, hero.StringId, other.StringId);
                _marriages.Clear();

                // The first pass learns how things are; players loaded the same world.
                var send = _stateBaselined;
                _stateBaselined = true;
                foreach (var settlement in Settlement.All.Where(s => s.Town != null && (IsPlayerClan(s.OwnerClan) || s.OwnerClan == Clan.PlayerClan)))
                    Relay(send, WorldEventKind.FiefState, settlement.StringId, StateSync.DescribeFief(settlement.Town, true));
                foreach (var kingdom in Kingdom.All)
                    Relay(send, WorldEventKind.KingdomPolicies, kingdom.StringId, StateSync.DescribePolicies(kingdom));
                RelayCompanions(send);
            }
            catch (Exception e)
            {
                Log.Error("Relaying fiefs and policies failed", e);
            }
        }

        /// <summary>
        /// What happens to players' companions in the server's world (healing, and for those leading or riding in a
        /// clan party, battles and levels) goes to everyone as changes. Changes a player's game sends for companions
        /// with them are applied and remembered first (<see cref="RememberCompanion"/>), so they are not sent back.
        /// </summary>
        private void RelayCompanions(bool send)
        {
            var watched = new HashSet<string>();
            foreach (var hero in Hero.AllAliveHeroes.Where(h => h.CompanionOf != null && IsPlayerClan(h.Clan) && !IsPlayedHero(h)))
            {
                var key = (int)WorldEventKind.HeroState + ":" + hero.StringId;
                watched.Add(key);
                var now = StateSync.DescribeCompanion(hero);
                if (_relayedState.TryGetValue(key, out var before) && before != now && send && Players.Count > 0)
                    SendToPlayers(new WorldEventMessage { HostHours = GameBridge.NowHours, Kind = WorldEventKind.HeroState, A = hero.StringId, B = StateSync.CounterChange(before, now) });
                _relayedState[key] = now;
            }
            var prefix = (int)WorldEventKind.HeroState + ":";
            foreach (var key in _relayedState.Keys.Where(k => k.StartsWith(prefix) && !watched.Contains(k)).ToList())
                _relayedState.Remove(key);
        }

        private void RememberCompanion(string heroId)
        {
            var key = (int)WorldEventKind.HeroState + ":" + heroId;
            if (_relayedState.ContainsKey(key))
                _relayedState[key] = StateSync.DescribeCompanion(GameBridge.FindHero(heroId));
        }

        private void Relay(bool send, WorldEventKind kind, string id, string description)
        {
            if (description == null)
                return;
            var key = (int)kind + ":" + id;
            if (_relayedState.TryGetValue(key, out var known) && known == description)
                return;
            _relayedState[key] = description;
            if (send && Players.Count > 0)
                SendToPlayers(new WorldEventMessage { HostHours = GameBridge.NowHours, Kind = kind, A = id, B = description });
        }
    }
}
