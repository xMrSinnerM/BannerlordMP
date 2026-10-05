using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Time;
using BannerlordMP.Game;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Session
{
    /// <summary>
    /// The host owns the real world: its campaign is the only one whose AI, economy and battles between AI
    /// parties count. Clients send it their own party's movement and the results of the battles they fight.
    /// </summary>
    internal sealed class HostSession : MpSession
    {
        private const int FullSnapshotEvery = 20;
        private const float TimeStateInterval = 0.5f;
        private const double BattleResultTimeoutSeconds = 30;
        private const float MinMoveToSend = 0.01f;

        private sealed class PendingBattle
        {
            public readonly List<string> PartyIds = new List<string>();
            public double? ExpiresAt;
        }

        private readonly TimeControlArbiter _arbiter;
        private readonly Dictionary<int, int> _peerToPlayer = new Dictionary<int, int>();
        private readonly Dictionary<int, PendingBattle> _battles = new Dictionary<int, PendingBattle>();
        private readonly HashSet<string> _battleFrozen = new HashSet<string>();
        private readonly Dictionary<string, PartyPosition> _lastSent = new Dictionary<string, PartyPosition>();

        private float _snapshotTimer;
        private float _timeStateTimer;
        private int _snapshotCount;

        public HostSession(MpConfig config) : base(config)
        {
            _arbiter = new TimeControlArbiter(HostPlayerId, config.TimeArbitration, config.DetachDuringConversations, hostIsPlayer: !config.DedicatedHost);
            Players[HostPlayerId] = new PlayerInfo
            {
                Id = HostPlayerId,
                Name = config.DedicatedHost ? "Server" : config.PlayerName,
                HeroId = Hero.MainHero.StringId,
                PartyId = MobileParty.MainParty.StringId,
                Activity = GameBridge.DetectActivity(),
            };

            Net.PeerConnected += peer => Log.Info($"Peer {peer} connected, waiting for hello");
            Net.PeerDisconnected += OnPeerDisconnected;
            Net.MessageReceived += OnMessage;
            Net.StartServer(config.Port);
            if (config.DedicatedHost)
                GameBridge.ParkMainParty();
            Log.Notify($"{(config.DedicatedHost ? "Dedicated server" : "Hosting")} on UDP port {config.Port}. Time control: {config.TimeArbitration}.");
        }

        public override bool IsHost => true;

        protected override TimeSpeed CurrentSharedSpeed() => _arbiter.Effective;

        protected override void OnSpeedRequested(TimeSpeed speed)
        {
            if (_arbiter.Request(HostPlayerId, speed))
                BroadcastTimeState();
            SyncRequestedSpeeds();
        }

        protected override void OnTick(float dt)
        {
            var activity = GameBridge.DetectActivity();
            if (activity != Players[HostPlayerId].Activity)
            {
                Players[HostPlayerId].Activity = activity;
                if (_arbiter.SetActivity(HostPlayerId, activity) && _arbiter.HostDetached)
                    Log.Notify("You are in a mission: the world is paused for everyone until you return.");
                BroadcastTimeState();
                BroadcastPlayerList();
            }

            // While the host is in a mission its campaign does not tick at all, so there is nothing to enforce or send.
            if (_arbiter.HostDetached)
                return;

            GameBridge.SetLocalTime(_arbiter.Effective, 0f, unstoppable: false);
            ExpireStaleBattles();

            _timeStateTimer += dt;
            if (_timeStateTimer >= TimeStateInterval)
            {
                _timeStateTimer = 0;
                BroadcastTimeState();
            }

            _snapshotTimer += dt;
            if (_snapshotTimer >= 1f / Config.SnapshotRateHz)
            {
                _snapshotTimer = 0;
                BroadcastSnapshot();
            }
        }

        public override bool AllowEncounter(MobileParty attacker, MobileParty defender)
        {
            // A dedicated server's own party never fights: any battle on this machine would stop the world.
            if (Config.DedicatedHost && (attacker == MobileParty.MainParty || defender == MobileParty.MainParty))
                return false;
            // Encounters involving other players are decided on their machines; frozen parties are mid-battle elsewhere.
            return !IsRemotePlayerParty(attacker) && !IsRemotePlayerParty(defender)
                && !IsBattleFrozen(attacker) && !IsBattleFrozen(defender);
        }

        public override void OnLocalBattleEnded(MapEvent mapEvent)
        {
            // The host's own battles happen while the world is paused; their results are already in the real world.
        }

        public override void OnLocalPartyDestroyed(MobileParty party)
        {
            if (party?.StringId == null)
                return;
            _lastSent.Remove(party.StringId);
            Net.SendToAll(new PartyDestroyedMessage { HostHours = GameBridge.NowHours, PartyId = party.StringId });
        }

        public override void SendChat(string text)
        {
            Net.SendToAll(new ChatMessage { PlayerId = HostPlayerId, Text = text });
            Log.Notify($"{Config.PlayerName}: {text}");
        }

        public override IEnumerable<string> Describe()
        {
            yield return $"{(Config.DedicatedHost ? "Dedicated server" : "Hosting")} on port {Config.Port}, time {_arbiter.Effective} ({Config.TimeArbitration}), world at {GameBridge.NowHours:0.00}h";
            foreach (var p in Players.Values.OrderBy(p => p.Id))
                yield return $"  #{p.Id} {p.Name} hero={p.HeroId} party={p.PartyId} {p.Activity} wants={p.RequestedSpeed}";
        }

        public override void Dispose()
        {
            foreach (var player in Players.Values.Where(p => p.Id != HostPlayerId))
                GameBridge.Unfreeze(GameBridge.FindParty(player.PartyId), enableAi: true);
            foreach (var id in _battleFrozen)
                GameBridge.Unfreeze(GameBridge.FindParty(id), enableAi: true);
            if (Config.DedicatedHost)
                GameBridge.UnparkMainParty();
            base.Dispose();
        }

        private void OnMessage(int peer, INetMessage message)
        {
            if (message is HelloMessage hello)
            {
                HandleHello(peer, hello);
                return;
            }

            if (!_peerToPlayer.TryGetValue(peer, out var playerId))
                return; // Ignore anything before a successful hello.

            switch (message)
            {
                case TimeRequestMessage request:
                    if (_arbiter.Request(playerId, request.Speed))
                        BroadcastTimeState();
                    SyncRequestedSpeeds();
                    break;

                case ActivityChangedMessage activity:
                    HandleActivity(playerId, activity.Activity);
                    break;

                case PartyStateMessage state:
                    if (!_arbiter.IsDetached(playerId))
                        GameBridge.SetPosition(Parties.Find(Players[playerId].PartyId, RealSeconds), state.X, state.Y, state.IsOnLand);
                    break;

                case BattleStartedMessage started:
                    HandleBattleStarted(playerId, started);
                    break;

                case BattleResultMessage result:
                    HandleBattleResult(playerId, result);
                    break;

                case ChatMessage chat:
                    chat.PlayerId = playerId;
                    Log.Notify($"{NameOf(playerId)}: {chat.Text}");
                    Net.SendToAll(chat);
                    break;
            }
        }

        private void HandleHello(int peer, HelloMessage hello)
        {
            string reason = null;
            var hero = GameBridge.FindHero(hello.HeroId);
            if (hello.ProtocolVersion != MessageCodec.ProtocolVersion)
                reason = $"Protocol mismatch (host {MessageCodec.ProtocolVersion}, you {hello.ProtocolVersion}). Use the same mod version.";
            else if (hello.CampaignId != GameBridge.CampaignId)
                reason = "You loaded a different campaign. Load the save the host is running.";
            else if (hello.LocalHours > GameBridge.NowHours + Config.CatchUpThresholdHours)
                reason = "Your save is newer than the host's world. Load the host's latest save.";
            else if (hero == null || !hero.IsAlive)
                reason = $"Hero '{hello.HeroId}' does not exist or is dead.";
            else if (hero == Hero.MainHero)
                reason = "That hero is the host's character.";
            else if (hero.PartyBelongedTo == null || hero.PartyBelongedTo.LeaderHero != hero)
                reason = $"{hero.Name} is not leading a party. The host must give them a party first (clan screen).";
            else if (Players.Values.Any(p => p.HeroId == hero.StringId))
                reason = $"{hero.Name} is already controlled by another player.";

            if (reason != null)
            {
                Log.Info($"Rejecting peer {peer}: {reason}");
                Net.Send(peer, new RejectMessage { Reason = reason });
                return;
            }

            var playerId = peer + 1;
            var party = hero.PartyBelongedTo;
            _peerToPlayer[peer] = playerId;
            Players[playerId] = new PlayerInfo
            {
                Id = playerId,
                Name = string.IsNullOrWhiteSpace(hello.PlayerName) ? "Player " + playerId : hello.PlayerName,
                HeroId = hero.StringId,
                PartyId = party.StringId,
                Activity = PlayerActivity.Map,
            };
            _arbiter.AddPlayer(playerId);
            SyncRequestedSpeeds();
            GameBridge.Freeze(party);

            Net.Send(peer, new WelcomeMessage
            {
                PlayerId = playerId,
                HostPlayerId = HostPlayerId,
                ArbitrationMode = Config.TimeArbitration,
                DetachDuringConversations = Config.DetachDuringConversations,
            });
            _snapshotCount = 0; // Next snapshot is a full one so the newcomer gets everything.
            BroadcastPlayerList();
            BroadcastTimeState();
            Log.Notify($"{Players[playerId].Name} joined as {hero.Name}.");
        }

        private void HandleActivity(int playerId, PlayerActivity activity)
        {
            var wasDetached = _arbiter.IsDetached(playerId);
            Players[playerId].Activity = activity;
            var changed = _arbiter.SetActivity(playerId, activity);
            var isDetached = _arbiter.IsDetached(playerId);

            if (!wasDetached && isDetached)
                Log.Notify($"{NameOf(playerId)} entered {(activity == PlayerActivity.Mission ? "a battle/scene" : "a conversation")}; the world keeps going.");
            else if (wasDetached && !isDetached)
            {
                Log.Notify($"{NameOf(playerId)} is back on the map and catching up.");
                if (_battles.TryGetValue(playerId, out var battle))
                    battle.ExpiresAt = RealSeconds + BattleResultTimeoutSeconds;
            }

            if (changed)
                BroadcastTimeState();
            BroadcastPlayerList();
        }

        private void HandleBattleStarted(int playerId, BattleStartedMessage started)
        {
            var battle = new PendingBattle();
            foreach (var id in started.PartyIds)
            {
                var party = GameBridge.FindParty(id);
                if (party == null || IsPlayerParty(party))
                    continue;
                GameBridge.Freeze(party);
                _battleFrozen.Add(id);
                battle.PartyIds.Add(id);
            }
            ReleaseBattle(playerId);
            _battles[playerId] = battle;
        }

        private void HandleBattleResult(int playerId, BattleResultMessage result)
        {
            var playerParty = GameBridge.FindParty(Players[playerId].PartyId);
            foreach (var outcome in result.Parties)
            {
                var party = GameBridge.FindParty(outcome.PartyId);
                if (party == null)
                    continue;
                // A client only reports on its own party and the AI parties it fought, never on other players.
                if (IsPlayerParty(party) && party != playerParty)
                    continue;

                try
                {
                    GameBridge.ApplyRoster(party.MemberRoster, outcome.Members);
                    GameBridge.ApplyRoster(party.PrisonRoster, outcome.Prisoners);
                    if (party == playerParty && outcome.LeaderGold >= 0 && party.LeaderHero != null)
                        party.LeaderHero.Gold = outcome.LeaderGold;
                }
                catch (Exception e)
                {
                    Log.Error($"Could not apply battle result for {party.StringId}", e);
                }

                if (outcome.Destroyed && party != playerParty)
                {
                    _battleFrozen.Remove(party.StringId);
                    GameBridge.ApplyBattleDestroy(party, playerParty);
                }
            }
            ReleaseBattle(playerId);
        }

        private void ReleaseBattle(int playerId)
        {
            if (!_battles.TryGetValue(playerId, out var battle))
                return;
            _battles.Remove(playerId);
            foreach (var id in battle.PartyIds)
            {
                if (_battleFrozen.Remove(id))
                    GameBridge.Unfreeze(GameBridge.FindParty(id), enableAi: true);
            }
        }

        private void ExpireStaleBattles()
        {
            foreach (var pair in _battles.Where(b => b.Value.ExpiresAt < RealSeconds).ToList())
            {
                Log.Info($"No battle result from {NameOf(pair.Key)}; releasing frozen parties.");
                ReleaseBattle(pair.Key);
            }
        }

        private void OnPeerDisconnected(int peer, string reason)
        {
            if (!_peerToPlayer.TryGetValue(peer, out var playerId))
                return;
            _peerToPlayer.Remove(peer);
            ReleaseBattle(playerId);
            var player = Players[playerId];
            Players.Remove(playerId);
            // Their party goes back to being a normal AI-controlled clan party.
            GameBridge.Unfreeze(GameBridge.FindParty(player.PartyId), enableAi: true);
            if (_arbiter.RemovePlayer(playerId))
                BroadcastTimeState();
            BroadcastPlayerList();
            Log.Notify($"{player.Name} left ({reason}).");
        }

        private bool IsRemotePlayerParty(MobileParty party) => party != null && party != MobileParty.MainParty && IsPlayerParty(party);

        private bool IsBattleFrozen(MobileParty party) => party?.StringId != null && _battleFrozen.Contains(party.StringId);

        private void SyncRequestedSpeeds()
        {
            foreach (var id in _arbiter.Players)
            {
                if (Players.TryGetValue(id, out var info))
                    info.RequestedSpeed = _arbiter.GetRequested(id);
            }
            BroadcastPlayerList();
        }

        private void BroadcastTimeState()
        {
            Net.SendToAll(new TimeStateMessage
            {
                EffectiveSpeed = _arbiter.Effective,
                HostHours = GameBridge.NowHours,
                HostDetached = _arbiter.HostDetached,
            });
        }

        private void BroadcastPlayerList()
        {
            Net.SendToAll(new PlayerListMessage { Players = Players.Values.OrderBy(p => p.Id).ToList() });
        }

        private void BroadcastSnapshot()
        {
            if (Net.ConnectedPeers == 0)
                return;

            var full = _snapshotCount++ % FullSnapshotEvery == 0;
            var snapshot = new WorldSnapshotMessage { HostHours = GameBridge.NowHours, IsFull = full };
            foreach (var party in MobileParty.All)
            {
                if (party == null || !party.IsActive || party.StringId == null)
                    continue;
                var position = GameBridge.GetPosition(party);
                if (!full && _lastSent.TryGetValue(party.StringId, out var last)
                    && Math.Abs(last.X - position.X) < MinMoveToSend && Math.Abs(last.Y - position.Y) < MinMoveToSend)
                    continue;
                _lastSent[party.StringId] = position;
                snapshot.Parties.Add(position);
            }
            Net.SendToAll(snapshot);
        }
    }
}
