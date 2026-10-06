using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Game;
using TaleWorlds.CampaignSystem;
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
        private float _worldTimer;

        public override void OnLocalWorldEvent(WorldEventKind kind, string a, string b)
        {
            // Whatever caused it (the AI, the host player, or a client's proposal we just applied), it is now true in
            // the real world, so everyone mirrors it. The proposing client receives it too and finds nothing to do.
            var message = new WorldEventMessage { HostHours = GameBridge.NowHours, Kind = kind, A = a, B = b };
            Log.Info("World changed on the host, relaying: " + message);
            SendToPlayers(message);
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

            _worldTimer += dt;
            if (_worldTimer < WorldSyncInterval)
                return;
            _worldTimer = 0;

            foreach (var pair in _peers)
            {
                var playerId = pair.Value.PlayerId;
                if (playerId < 0 || !Players.TryGetValue(playerId, out var player))
                    continue;
                var party = Parties.Find(player.PartyId, RealSeconds);
                if (party == null)
                    continue;
                SendLedgerState(pair.Key, playerId, party);
                SendNearbyRosters(pair.Key, playerId, party);
            }
        }

        private bool HandleWorldMessage(int peer, int playerId, INetMessage message)
        {
            switch (message)
            {
                case LedgerDeltaMessage delta:
                {
                    var party = Parties.Find(Players[playerId].PartyId, RealSeconds);
                    EnforcePlayerGold(party?.LeaderHero);
                    WorldBridge.Remote(() => WorldBridge.ApplyLedgerDelta(party, delta.Delta, onHost: true));
                    RememberPlayerGold(party?.LeaderHero);
                    _ledgerAck[playerId] = delta.Seq;
                    return true;
                }

                case WorldEventMessage proposal:
                    // A player's own action changed the world on their machine (took a castle, killed a lord, joined a
                    // kingdom). Friends trust each other: apply it; OnLocalWorldEvent then relays it to everyone.
                    Log.Info($"{NameOf(playerId)} changed the world: {proposal}");
                    WorldBridge.ApplyWorldEvent(proposal);
                    return true;

                case DecisionVoteMessage vote:
                    HandleVote(playerId, vote);
                    return true;

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

        /// <summary>
        /// A player's money is theirs: their own game computes wages and income exactly as single player does.
        /// The host sees their clan as an AI clan and would apply AI finances (and AI gold top-ups) to it, so any
        /// gold change that did not come from the player is undone here.
        /// </summary>
        private void EnforcePlayerGold(Hero hero)
        {
            if (hero == null || !_playerGold.TryGetValue(hero.StringId, out var gold) || hero.Gold == gold)
                return;
            Log.Info($"Undid {hero.Gold - gold:+#;-#;0} gold the host's AI gave player hero {hero.StringId}");
            hero.Gold = gold;
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
            EnforcePlayerGold(party.LeaderHero);
            RememberPlayerGold(party.LeaderHero);
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
            EnforcePlayerGold(party.LeaderHero);
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
                if (party.Position.ToVec2().DistanceSquared(center) > RosterInterestRadius * RosterInterestRadius)
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
            var peer = _peers.FirstOrDefault(p => p.Value.PlayerId == player.Id).Key;
            Net.Send(peer, new EncounterRequestMessage { AttackerPartyId = attacker.StringId });
        }
    }
}
