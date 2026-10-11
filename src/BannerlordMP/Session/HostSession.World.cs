using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Game;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Session
{
    /// <summary>World replication: the host is the single source of truth and mirrors its changes to every client.</summary>
    internal sealed partial class HostSession
    {
        private const float WorldSyncInterval = 1f;
        private const float RosterInterestRadius = 30f;
        private const double EncounterRequestCooldownSeconds = 5;

        private readonly List<MobileParty> _spawnQueue = new List<MobileParty>();
        private readonly Dictionary<int, Dictionary<string, int>> _rostersSent = new Dictionary<int, Dictionary<string, int>>();
        private readonly Dictionary<int, int> _ledgerAck = new Dictionary<int, int>();
        private readonly Dictionary<string, double> _encounterRequested = new Dictionary<string, double>();
        /// <summary>Player heroes' gold as their owners' games last set it. Nothing on the host may change it.</summary>
        private readonly Dictionary<string, int> _playerGold = new Dictionary<string, int>();
        /// <summary>Players' heroes held prisoner here, and who holds them (party id), as last seen.</summary>
        private readonly Dictionary<string, string> _heldBy = new Dictionary<string, string>();
        /// <summary>Where each captive player's hero was last held: where their party comes back when they are freed.</summary>
        private readonly Dictionary<string, CampaignVec2> _heldAt = new Dictionary<string, CampaignVec2>();
        private float _worldTimer;
        private float _pursuitTimer;
        /// <summary>How close (map units) a chasing enemy must get before the player's game starts the battle.</summary>
        private const float CatchDistance = 0.6f;
        private const float PursuitCheckInterval = 0.25f;

        public override void OnLocalWorldEvent(WorldEventKind kind, string a, string b)
        {
            // Whatever caused it (the AI, the host player, or a client's proposal we just applied), it is now true in
            // the real world, so everyone mirrors it. The proposing client receives it too and finds nothing to do.
            var message = new WorldEventMessage { HostHours = GameBridge.NowHours, Kind = kind, A = a, B = b };
            Log.Info("World changed on the host, relaying: " + message);
            SendToPlayers(message);
        }

        public override void OnLocalArmyEvent(WorldEventKind kind, MobileParty leader, MobileParty party)
        {
            // Only joined players' own armies are mirrored; every other army is just parties the snapshots move.
            if (leader == MobileParty.MainParty || !IsPlayerParty(leader))
                return;
            var message = DescribeArmyEvent(kind, leader, party);
            Log.Info("Player army changed on the host, relaying: " + message);
            SendToPlayers(message);
        }

        public override bool IsPlayerCharacter(Hero hero) => base.IsPlayerCharacter(hero) || IsPlayerHero(hero);

        public override void OnLocalHeroJoinedParty(Hero hero, MobileParty party)
        {
            // Only players' companions are news; lords forming parties are just parties the snapshots show.
            if (IsPlayerCharacter(hero) || party.StringId == null || !(IsPlayerClan(party.ActualClan) || party.ActualClan == Clan.PlayerClan))
                return;
            OnLocalWorldEvent(WorldEventKind.HeroJoinedParty, hero.StringId, party.StringId);
        }

        public override void OnLocalPartyCreated(MobileParty party)
        {
            // Parties are filled in (troops, position) after the creation event; describe them next frame.
            _spawnQueue.Add(party);
        }

        private void TickWorld(float dt)
        {
            TickDecisions(dt);
            if (_spawnQueue.Count > 0)
            {
                foreach (var party in _spawnQueue)
                {
                    if (party != null && party.IsActive && party.StringId != null)
                        SendToPlayers(WorldBridge.DescribeParty(party, GameBridge.NowHours));
                }
                _spawnQueue.Clear();
            }

            _pursuitTimer += dt;
            if (_pursuitTimer >= PursuitCheckInterval)
            {
                _pursuitTimer = 0;
                CheckPursuits();
            }

            _worldTimer += dt;
            if (_worldTimer < WorldSyncInterval)
                return;
            _worldTimer = 0;
            TickCaptivity();

            foreach (var pair in _peers)
            {
                var playerId = pair.Value.PlayerId;
                if (playerId < 0 || !Players.ContainsKey(playerId))
                    continue;
                var party = PlayerParty(playerId);
                if (party == null)
                    continue;
                SendLedgerState(pair.Key, playerId, party);
                if (party.IsActive)
                    SendNearbyRosters(pair.Key, playerId, party);
            }
        }

        private bool HandleWorldMessage(int peer, int playerId, INetMessage message)
        {
            switch (message)
            {
                case LedgerDeltaMessage delta:
                {
                    var party = PlayerParty(playerId);
                    EnforcePlayerGold(WorldBridge.PartyHero(party));
                    WorldBridge.Remote(() => WorldBridge.ApplyLedgerDelta(party, delta.Delta));
                    RememberPlayerGold(WorldBridge.PartyHero(party));
                    _ledgerAck[playerId] = delta.Seq;
                    return true;
                }

                case WorldEventMessage proposal:
                    if (!MayChangeHero(playerId, proposal))
                        return true;
                    // A player's own action changed the world on their machine (took a castle, killed a lord, joined a
                    // kingdom). Friends trust each other: apply it; OnLocalWorldEvent then relays it to everyone.
                    Log.Info($"{NameOf(playerId)} changed the world: {proposal}");
                    WorldBridge.ApplyWorldEvent(proposal);
                    return true;

                case DecisionVoteMessage vote:
                    HandleVote(playerId, vote);
                    return true;

                case MarketRequestMessage request:
                {
                    var settlement = WorldBridge.Find<Settlement>(request.SettlementId);
                    if (WorldBridge.HasMarket(settlement))
                        Net.Send(peer, WorldBridge.CaptureMarket(settlement));
                    return true;
                }

                case MarketChangeMessage change:
                {
                    // Friends trust each other, as with world events; the change is kept within what the place has.
                    var settlement = WorldBridge.Find<Settlement>(change.SettlementId);
                    if (WorldBridge.HasMarket(settlement))
                    {
                        Log.Info($"{NameOf(playerId)} traded in {settlement.StringId}: {change.GoldChange:+#;-#;0} gold, {change.Items.Count} item types");
                        WorldBridge.Remote(() => WorldBridge.ApplyMarketChange(settlement, change));
                    }
                    return true;
                }

                case PartySpawnedMessage created:
                {
                    // The player made a new party for one of their clan's heroes (clan screen).
                    var clan = Parties.Find(Players[playerId].PartyId, RealSeconds)?.LeaderHero?.Clan;
                    var problem = WorldBridge.CreatePlayerClanParty(clan, created);
                    if (problem == null)
                    {
                        Log.Info($"{NameOf(playerId)} created party {created.PartyId} led by {created.LeaderHeroId}");
                    }
                    else
                    {
                        Log.Info($"Could not create {NameOf(playerId)}'s party {created.PartyId} led by {created.LeaderHeroId}: {problem}");
                        Net.Send(peer, new ChatMessage { PlayerId = HostPlayerId, Text = $"The server could not create your new party: {problem}." });
                    }
                    return true;
                }

                case PartyInfoRequestMessage request:
                    foreach (var id in request.PartyIds.Take(200))
                    {
                        var party = Parties.Find(id, RealSeconds);
                        if (party != null && party.IsActive)
                            Net.Send(peer, WorldBridge.DescribeParty(party, GameBridge.NowHours));
                    }
                    return true;
            }
            return false;
        }

        /// <summary>The player's party, also while it waits for its captive hero (inactive, so the party cache skips it).</summary>
        private MobileParty PlayerParty(int playerId)
        {
            if (!Players.TryGetValue(playerId, out var player))
                return null;
            return Parties.Find(player.PartyId, RealSeconds) ?? GameBridge.FindParty(player.PartyId);
        }

        /// <summary>
        /// Only a player's own game captures or frees their hero (it fought the battle, it runs the captivity). A
        /// proposal about somebody else's played hero is ignored.
        /// </summary>
        private bool MayChangeHero(int playerId, WorldEventMessage proposal)
        {
            if (proposal.Kind == WorldEventKind.CompanionJoined || proposal.Kind == WorldEventKind.CompanionLeft || proposal.Kind == WorldEventKind.HeroJoinedParty)
                return MayChangeCompanion(playerId, proposal);
            if (proposal.Kind != WorldEventKind.HeroCaptured && proposal.Kind != WorldEventKind.HeroReleased)
                return true;
            var hero = GameBridge.FindHero(proposal.A);
            if (hero == null || !IsPlayedHero(hero) || hero.StringId == Players[playerId].HeroId)
                return true;
            Log.Info($"Ignoring {NameOf(playerId)}'s {proposal}: that hero is played by someone else");
            return false;
        }

        /// <summary>A player hires, dismisses and places only their own clan's companions.</summary>
        private bool MayChangeCompanion(int playerId, WorldEventMessage proposal)
        {
            var clan = GameBridge.FindHero(Players[playerId].HeroId)?.Clan;
            var hero = GameBridge.FindHero(proposal.A);
            string problem = null;
            if (clan == null || hero == null)
                problem = "unknown hero or clan";
            else if (IsPlayerCharacter(hero))
                problem = "that hero is a player";
            else if (proposal.Kind == WorldEventKind.CompanionJoined && proposal.B != clan.StringId)
                problem = "not their clan";
            else if (proposal.Kind == WorldEventKind.CompanionLeft && hero.Clan != clan)
                problem = "not their companion";
            else if (proposal.Kind == WorldEventKind.HeroJoinedParty && ((hero.Clan != null && hero.Clan != clan) || GameBridge.FindParty(proposal.B)?.ActualClan != clan))
                problem = "not their clan's hero or party";
            if (problem == null)
                return true;
            Log.Info($"Ignoring {NameOf(playerId)}'s {proposal}: {problem}");
            return false;
        }

        /// <summary>An online joined player's hero (not the host's own).</summary>
        public bool IsOnlinePlayerHero(Hero hero) => hero != null && Players.Values.Any(p => p.Id != HostPlayerId && p.HeroId == hero.StringId);

        /// <summary>
        /// Players' heroes in captivity, checked every second. The host's world moves prisoners on its own: a captor
        /// takes them to a dungeon, another party takes them, a peace or a fallen castle frees them. The player's game
        /// learns where it is held, and a freed hero gets their party back (set up again for its player if online).
        /// </summary>
        private void TickCaptivity()
        {
            foreach (var slot in _slots.Slots)
            {
                var hero = GameBridge.FindHero(slot.HeroId);
                if (hero == null || !hero.IsAlive)
                {
                    _heldBy.Remove(slot.HeroId);
                    continue;
                }
                if (hero.IsPrisoner)
                {
                    var captor = hero.PartyBelongedToAsPrisoner;
                    if (captor == null)
                        continue;
                    _heldAt[hero.StringId] = WorldBridge.PlaceOf(captor);
                    // Taken by the host's own game (it never fights players' parties, but just in case): hide the party too.
                    var kept = WorldBridge.FindKeptParty(hero);
                    if (kept != null && kept.IsActive)
                        WorldBridge.Remote(() => WorldBridge.HideKeptParty(kept));
                    if (_heldBy.TryGetValue(hero.StringId, out var previous) && previous != captor.Id)
                    {
                        var where = captor.IsSettlement ? captor.Settlement.StringId : captor.MobileParty?.StringId;
                        Log.Info($"{hero.StringId} is now held by {where} (was {previous}); telling the players");
                        if (!string.IsNullOrEmpty(where))
                            SendToPlayers(new WorldEventMessage { HostHours = GameBridge.NowHours, Kind = WorldEventKind.HeroCaptured, A = hero.StringId, B = where });
                    }
                    _heldBy[hero.StringId] = captor.Id;
                    continue;
                }

                var wasHeld = _heldBy.Remove(hero.StringId);
                MobileParty party = null;
                if (hero.PartyBelongedTo == null)
                {
                    var at = _heldAt.TryGetValue(hero.StringId, out var place) ? place : (CampaignVec2?)null;
                    WorldBridge.Remote(() => party = WorldBridge.RestoreKeptParty(hero, at));
                }
                else if (wasHeld && hero.PartyBelongedTo.LeaderHero == hero)
                {
                    party = hero.PartyBelongedTo;
                }
                _heldAt.Remove(hero.StringId);
                if (party == null)
                    continue;
                if (IsOnlinePlayerHero(hero))
                {
                    PrepareOnlinePlayerParty(party);
                    Log.Info($"{hero.StringId} is free; their party {party.StringId} follows its player again");
                }
                else
                {
                    GameBridge.Freeze(party);
                    Log.Info($"{hero.StringId} is free; their party {party.StringId} waits for its player");
                }
            }
        }

        /// <summary>
        /// A player's money is theirs: their own game computes wages and income exactly as single player does.
        /// The host sees their clan as an AI clan and would apply AI finances (and AI gold top-ups) to it, so any
        /// gold change that did not come from the player is undone here.
        /// </summary>
        private void EnforcePlayerGold(Hero hero)
        {
            if (hero == null || !_playerGold.TryGetValue(hero.StringId, out var gold) || hero.Gold == gold)
                return;
            Log.Info($"Undid a {hero.Gold - gold:+#;-#;0} gold change the host's AI made to player hero {hero.StringId} (the player's own game handles their money)");
            hero.Gold = gold;
        }

        /// <summary>
        /// A player hero's party whose player is not on the map right now: in a battle or conversation, or
        /// offline. Its food and morale wait for them (a battle takes no time in single player).
        /// </summary>
        public override bool IsPlayedHero(Hero hero) => base.IsPlayedHero(hero) || IsPlayerHero(hero);

        public bool IsPlayerPartyAway(MobileParty party)
        {
            if (party?.LeaderHero == null || !IsPlayerHero(party.LeaderHero))
                return false;
            var player = Players.Values.FirstOrDefault(p => p.PartyId == party.StringId);
            return player == null || _arbiter.IsDetached(player.Id);
        }

        private void RememberPlayerGold(Hero hero)
        {
            if (hero != null)
                _playerGold[hero.StringId] = hero.Gold;
        }

        private void RememberSlotHeroesGold()
        {
            foreach (var slot in _slots.Slots)
            {
                var hero = GameBridge.FindHero(slot.HeroId);
                if (hero != null && !_playerGold.ContainsKey(hero.StringId))
                    RememberPlayerGold(hero);
            }
        }

        private void OnPlayerEnteredWorld(int peer, int playerId, MobileParty party)
        {
            // Restores what the hero had when its owner last played (or when the server started).
            EnforcePlayerGold(WorldBridge.PartyHero(party));
            WorldBridge.RepairUnspentPoints(WorldBridge.PartyHero(party));
            RememberPlayerGold(WorldBridge.PartyHero(party));
            _rostersSent[playerId] = new Dictionary<string, int>();
            _ledgerAck[playerId] = 0;
            SendLedgerState(peer, playerId, party);
        }

        private void OnPlayerLeftWorld(int playerId)
        {
            _rostersSent.Remove(playerId);
            _ledgerAck.Remove(playerId);
        }

        private void SendLedgerState(int peer, int playerId, MobileParty party)
        {
            EnforcePlayerGold(WorldBridge.PartyHero(party));
            _ledgerAck.TryGetValue(playerId, out var ack);
            Net.Send(peer, new LedgerStateMessage { AckSeq = ack, State = WorldBridge.CaptureLedger(party) });
        }

        /// <summary>Keeps the troops of parties near a player accurate on their machine, so encounters there fight the real army.</summary>
        private void SendNearbyRosters(int peer, int playerId, MobileParty playerParty)
        {
            if (!_rostersSent.TryGetValue(playerId, out var sent))
                _rostersSent[playerId] = sent = new Dictionary<string, int>();
            var center = playerParty.Position.ToVec2();
            foreach (var party in MobileParty.All)
            {
                if (party == null || !party.IsActive || party == playerParty || party.StringId == null)
                    continue;
                // Their own clan's parties wherever they are, so the clan screen shows what those parties really have.
                var ownClan = party.ActualClan != null && party.ActualClan == playerParty.ActualClan && party.LeaderHero != null;
                if (!ownClan && party.Position.ToVec2().DistanceSquared(center) > RosterInterestRadius * RosterInterestRadius)
                    continue;
                var hash = WorldBridge.RosterHash(party);
                if (sent.TryGetValue(party.StringId, out var previous) && previous == hash)
                    continue;
                sent[party.StringId] = hash;
                Net.Send(peer, new PartyRosterMessage
                {
                    PartyId = party.StringId,
                    Members = GameBridge.CaptureRoster(party.MemberRoster),
                    Prisoners = GameBridge.CaptureRoster(party.PrisonRoster),
                });
            }
        }

        /// <summary>
        /// Enemies chasing a player. The host's game never starts a battle with a player's party itself (it is fought
        /// on the player's machine), and on a server the chaser does not always "arrive" in the game's sense. So
        /// any hostile party that targets a player and gets within reach asks that player's game to start it.
        /// </summary>
        private void CheckPursuits()
        {
            foreach (var player in Players.Values)
            {
                if (player.Id == HostPlayerId || _arbiter.IsDetached(player.Id))
                    continue;
                var target = Parties.Find(player.PartyId, RealSeconds);
                if (target == null || !target.IsActive || target.CurrentSettlement != null || target.MapEvent != null || target.MapFaction == null
                    || target.BesiegedSettlement != null || target.SiegeEvent != null)
                    continue; // In a town, a battle or a siege of their own: nobody catches them on the map.
                var center = target.Position.ToVec2();
                foreach (var chaser in MobileParty.All)
                {
                    if (chaser == null || chaser == target || !chaser.IsActive || chaser.MapEvent != null || chaser.CurrentSettlement != null
                        || chaser.MapFaction == null || IsRemotePlayerParty(chaser) || IsBattleFrozen(chaser) || chaser.ShouldBeIgnored
                        || chaser.BesiegedSettlement != null || chaser.MemberRoster.TotalHealthyCount <= 0)
                        continue;
                    if (chaser.TargetParty != target && chaser.ShortTermTargetParty != target)
                        continue;
                    if (!FactionManager.IsAtWarAgainstFaction(chaser.MapFaction, target.MapFaction))
                        continue;
                    if (chaser.Position.ToVec2().DistanceSquared(center) > CatchDistance * CatchDistance)
                        continue;
                    RequestEncounter(chaser, target);
                }
            }
        }

        /// <summary>
        /// An AI party on the host caught an online player's party. The battle belongs on that player's machine,
        /// so ask it to start the encounter there.
        /// </summary>
        private void RequestEncounter(MobileParty attacker, MobileParty playerParty)
        {
            var player = Players.Values.FirstOrDefault(p => p.Id != HostPlayerId && p.PartyId == playerParty.StringId);
            if (player == null || _arbiter.IsDetached(player.Id) || attacker?.StringId == null)
                return;
            var key = attacker.StringId + ">" + player.Id;
            if (_encounterRequested.TryGetValue(key, out var at) && RealSeconds - at < EncounterRequestCooldownSeconds)
                return;
            _encounterRequested[key] = RealSeconds;
            Log.Info($"{attacker.StringId} ({attacker.MapFaction?.StringId}) caught {player.Name}; asking their game to start the battle");
            var peer = _peers.FirstOrDefault(p => p.Value.PlayerId == player.Id).Key;
            Net.Send(peer, new EncounterRequestMessage { AttackerPartyId = attacker.StringId });
        }
    }
}
