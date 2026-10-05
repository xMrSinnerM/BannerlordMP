using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Sync;
using BannerlordMP.Core.Time;
using BannerlordMP.Game;
using BannerlordMP.Net;
using BannerlordMP.Steam;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Session
{
    /// <summary>
    /// A joined player. The client runs its own copy of the campaign but treats the host as the truth:
    /// its clock follows the host's, other parties are moved to where the host says they are, and only
    /// the client's own party and its own battles are decided locally.
    /// </summary>
    /// <remarks>
    /// When the client is in a battle (or any other mission) its campaign stops, while the host's world keeps
    /// running. World updates received meanwhile go into a <see cref="CatchUpBuffer"/>. Back on the map, the
    /// client's clock is behind, so <see cref="TimeSyncController"/> fast-forwards it and the buffered
    /// updates are replayed as the local clock passes them.
    /// </remarks>
    internal sealed class ClientSession : MpSession
    {
        private const float PartyStateInterval = 0.1f;

        private readonly string _heroId;
        private readonly string _resumeToken;
        private readonly TimeSyncController _sync;
        private readonly CatchUpBuffer _buffer = new CatchUpBuffer();
        private readonly HashSet<string> _frozenRemoteParties = new HashSet<string>();

        private int _playerId = -1;
        private bool _welcomed;
        private PlayerActivity _activity = PlayerActivity.Map;
        private bool _detached;
        private bool _hadBattle;
        private bool _hostDetached;
        private SyncAction _lastAction = SyncAction.FollowHost;
        private float _partyStateTimer;

        /// <param name="resumeToken">One-time token from the join that downloaded this world.</param>
        /// <param name="heroId">The player's hero in that world.</param>
        public ClientSession(MpConfig config, ConnectTarget target, string resumeToken, string heroId) : base(config)
        {
            _heroId = heroId;
            _resumeToken = resumeToken;
            _sync = new TimeSyncController(config.ToSyncSettings());

            Net = target.CreateClientTransport();
            Net.PeerDisconnected += (peer, reason) =>
            {
                Log.Notify($"Disconnected from host ({reason}).");
                RequestStop();
            };
            Net.MessageReceived += OnMessage;
            Log.Notify($"Entering the world on {target}...");
        }

        public override bool IsHost => false;

        private bool Buffering => _detached || _sync.State == SyncAction.CatchUp;

        protected override TimeSpeed CurrentSharedSpeed() => _sync.HostSpeed;

        protected override void OnSpeedRequested(TimeSpeed speed)
        {
            if (_welcomed)
                Net.SendToAll(new TimeRequestMessage { Speed = speed });
        }

        protected override void OnTick(float dt)
        {
            if (!_welcomed)
                return;

            UpdateActivity();
            if (_detached)
                return; // In a mission: the local campaign is not ticking, the world goes on without us.

            var decision = _sync.Update(GameBridge.NowHours, RealSeconds);
            GameBridge.SetLocalTime(decision.Speed, decision.SpeedUpMultiplier, unstoppable: decision.Action == SyncAction.CatchUp);
            ReportSyncChange(decision);

            ReplayBuffered(GameBridge.NowHours);

            _partyStateTimer += dt;
            if (_partyStateTimer >= PartyStateInterval && MobileParty.MainParty != null)
            {
                _partyStateTimer = 0;
                var position = GameBridge.GetPosition(MobileParty.MainParty);
                Net.SendToAll(new PartyStateMessage { X = position.X, Y = position.Y, IsOnLand = position.IsOnLand }, reliable: false);
            }
        }

        public override bool AllowEncounter(MobileParty attacker, MobileParty defender)
        {
            // Battles between AI parties happen on the host and arrive as results. Locally we only fight our own
            // battles, and never against (or alongside) another player's party.
            var main = MobileParty.MainParty;
            if (attacker != main && defender != main)
                return false;
            var other = attacker == main ? defender : attacker;
            return !IsPlayerParty(other);
        }

        public override void OnLocalBattleEnded(MapEvent mapEvent)
        {
            if (!_welcomed || mapEvent == null)
                return;

            var result = new BattleResultMessage { WinningSide = (int)mapEvent.WinningSide };
            foreach (var partyBase in mapEvent.InvolvedParties)
            {
                var party = partyBase?.MobileParty;
                if (party == null || (IsPlayerParty(party) && party != MobileParty.MainParty))
                    continue;
                result.Parties.Add(CaptureOutcome(party));
            }
            Net.SendToAll(result);
        }

        public override void OnLocalPartyDestroyed(MobileParty party)
        {
            // Only the host decides which parties stop existing; local destructions come from our own battles,
            // which the host learns about through the battle result.
        }

        public override void SendChat(string text)
        {
            Net.SendToAll(new ChatMessage { Text = text });
        }

        public override IEnumerable<string> Describe()
        {
            if (!_welcomed)
            {
                yield return "Connecting to host...";
                yield break;
            }
            var behind = _sync.EstimateHostHours(RealSeconds) - GameBridge.NowHours;
            yield return $"Joined as player #{_playerId}, host time {_sync.HostSpeed}{(_hostDetached ? " (host in a mission)" : "")}, {_sync.State}, {behind:0.00}h behind, {_buffer.SnapshotCount} buffered updates";
            foreach (var p in Players.Values.OrderBy(p => p.Id))
                yield return $"  #{p.Id} {p.Name} hero={p.HeroId} party={p.PartyId} {p.Activity} wants={p.RequestedSpeed}";
        }

        public override void Dispose()
        {
            foreach (var id in _frozenRemoteParties)
                GameBridge.Unfreeze(GameBridge.FindParty(id), enableAi: true);
            base.Dispose();
        }

        private void OnMessage(int peer, INetMessage message)
        {
            switch (message)
            {
                case AuthChallengeMessage _:
                    // We already passed the server password when downloading the world; the token stands in for it.
                    Net.SendToAll(new HelloMessage
                    {
                        ModVersion = typeof(ClientSession).Assembly.GetName().Version.ToString(),
                        PlayerName = SteamService.PersonaName ?? Config.PlayerName,
                        ResumeToken = _resumeToken,
                        CampaignId = GameBridge.CampaignId,
                        LocalHours = GameBridge.NowHours,
                    });
                    break;

                case WelcomeMessage welcome:
                    _playerId = welcome.PlayerId;
                    _welcomed = true;
                    var hero = GameBridge.FindHero(_heroId);
                    if (hero == null)
                    {
                        Log.Notify("Your hero is missing from the downloaded world.");
                        RequestStop();
                        break;
                    }
                    GameBridge.TakeControlOf(hero);
                    GameBridge.ReleaseOwnParty();
                    Log.Notify($"Joined! You are {hero.Name}.");
                    break;

                case RejectMessage reject:
                    Log.Notify("Host refused: " + reject.Reason);
                    RequestStop();
                    break;

                case PlayerListMessage list:
                    UpdatePlayers(list);
                    break;

                case TimeStateMessage time:
                    if (time.HostDetached != _hostDetached)
                        Log.Notify(time.HostDetached ? "The host is in a mission; the world is paused." : "The host is back; the world resumes.");
                    _hostDetached = time.HostDetached;
                    _sync.OnHostTime(time.HostHours, time.EffectiveSpeed, RealSeconds);
                    break;

                case WorldSnapshotMessage snapshot:
                    if (Buffering)
                        _buffer.Add(snapshot);
                    else
                        ApplySnapshot(snapshot);
                    break;

                case PartyDestroyedMessage destroyed:
                    if (Buffering || destroyed.HostHours > GameBridge.NowHours)
                        _buffer.Add(destroyed);
                    else
                        GameBridge.DestroyFromHost(Parties.Find(destroyed.PartyId, RealSeconds));
                    break;

                case ChatMessage chat:
                    if (chat.PlayerId != _playerId)
                        Log.Notify($"{NameOf(chat.PlayerId)}: {chat.Text}");
                    break;
            }
        }

        private void UpdateActivity()
        {
            var activity = GameBridge.DetectActivity();
            if (activity == _activity)
                return;

            var wasDetached = _detached;
            _activity = activity;
            _detached = activity.IsDetached(Config.DetachDuringConversations);
            Net.SendToAll(new ActivityChangedMessage { Activity = activity });

            if (!wasDetached && _detached && activity == PlayerActivity.Mission && MapEvent.PlayerMapEvent != null)
            {
                // Tell the host which parties are tied up in our battle so it freezes them in the real world.
                var started = new BattleStartedMessage();
                foreach (var party in MapEvent.PlayerMapEvent.InvolvedParties)
                {
                    if (party?.MobileParty?.StringId != null)
                        started.PartyIds.Add(party.MobileParty.StringId);
                }
                Net.SendToAll(started);
                _hadBattle = true;
            }

            if (wasDetached && !_detached && _hadBattle && activity == PlayerActivity.Map)
            {
                // Loot, prisoners and recruits are settled in the screens after the battle, so send our party once more.
                _hadBattle = false;
                Net.SendToAll(new BattleResultMessage { Parties = { CaptureOutcome(MobileParty.MainParty) } });
            }
        }

        private void ReportSyncChange(SyncDecision decision)
        {
            if (decision.Action == _lastAction)
                return;
            if (decision.Action == SyncAction.CatchUp)
                Log.Notify($"Catching up with the world ({decision.HoursBehind:0.0} hours)...");
            else if (_lastAction == SyncAction.CatchUp)
                Log.Notify("Caught up.");
            _lastAction = decision.Action;
        }

        private void ReplayBuffered(double localHours)
        {
            if (_buffer.IsEmpty)
                return;
            // Once caught up, anything still buffered is due.
            var (snapshot, destroyed) = _sync.State == SyncAction.CatchUp ? _buffer.DrainUntil(localHours) : _buffer.DrainAll();
            if (snapshot != null)
                ApplySnapshot(snapshot);
            foreach (var d in destroyed)
                GameBridge.DestroyFromHost(Parties.Find(d.PartyId, RealSeconds));
        }

        private void ApplySnapshot(WorldSnapshotMessage snapshot)
        {
            var ownId = MobileParty.MainParty?.StringId;
            foreach (var position in snapshot.Parties)
            {
                if (position.PartyId == ownId)
                    continue;
                GameBridge.SetPosition(Parties.Find(position.PartyId, RealSeconds), position.X, position.Y, position.IsOnLand);
            }
        }

        private void UpdatePlayers(PlayerListMessage list)
        {
            Players.Clear();
            foreach (var p in list.Players)
                Players[p.Id] = p;

            // Other players' parties are puppets on this machine: moved by snapshots, never by local AI.
            var remote = new HashSet<string>(list.Players.Where(p => p.Id != _playerId).Select(p => p.PartyId));
            // Players who log off stay frozen too: their hero waits where they left it, as it does on the host.
            foreach (var id in remote.Where(id => !_frozenRemoteParties.Contains(id)))
                GameBridge.Freeze(GameBridge.FindParty(id));
            _frozenRemoteParties.UnionWith(remote);
        }

        private static PartyOutcome CaptureOutcome(MobileParty party)
        {
            return new PartyOutcome
            {
                PartyId = party.StringId,
                Destroyed = !party.IsActive || party.MemberRoster.TotalHealthyCount == 0,
                LeaderGold = party == MobileParty.MainParty && party.LeaderHero != null ? party.LeaderHero.Gold : -1,
                Members = GameBridge.CaptureRoster(party.MemberRoster),
                Prisoners = GameBridge.CaptureRoster(party.PrisonRoster),
            };
        }
    }
}
