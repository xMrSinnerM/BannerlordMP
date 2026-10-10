using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Sync;
using BannerlordMP.Core.Time;
using BannerlordMP.Game;
using BannerlordMP.Net;
using BannerlordMP.Steam;
using BannerlordMP.Ui;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Session
{
    /// <summary>
    /// A joined player. The client's campaign is a mirror of the host's world: its world simulation (AI,
    /// economy, spawning, diplomacy) is switched off, parties are puppets moved by the host, and world changes
    /// arrive as messages. What the client decides itself is its own party's movement, what the player does
    /// (trading, recruiting, dialogue), and the battles it fights.
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
        private const float LedgerInterval = 1f;
        private const double PartyRequestCooldownSeconds = 10;
        private const int MissingSnapshotsBeforeRemoval = 2;

        private readonly string _heroId;
        private readonly string _resumeToken;
        private readonly TimeSyncController _sync;
        private readonly CatchUpBuffer _buffer = new CatchUpBuffer();
        private readonly ClientLedger _ledger = new ClientLedger();
        private readonly PositionSmoother _smoother;
        private readonly HashSet<string> _frozenRemoteParties = new HashSet<string>();
        private readonly Dictionary<string, int> _missingFromHost = new Dictionary<string, int>();
        private readonly Dictionary<string, double> _requestedParties = new Dictionary<string, double>();

        private int _playerId = -1;
        private bool _welcomed;
        private PlayerActivity _activity = PlayerActivity.Map;
        private bool _detached;
        /// <summary>Just back on the map from a battle or conversation: the time missed is skipped without upkeep.</summary>
        private bool _justReturned;
        /// <summary>Behind by this much for any reason, skip instead of fast-forwarding.</summary>
        private const double LargeGapHours = 6;
        private bool _hostDetached;
        private SyncAction _lastAction = SyncAction.FollowHost;
        private float _partyStateTimer;
        private float _ledgerTimer;
        private bool _loggedFirstSnapshot;
        private int _financeDay = -1;
        private readonly Queue<DecisionVoteRequestMessage> _pendingVotes = new Queue<DecisionVoteRequestMessage>();
        private bool _voteDialogOpen;

        /// <param name="resumeToken">One-time token from the join that downloaded this world.</param>
        /// <param name="heroId">The player's hero in that world.</param>
        public ClientSession(MpConfig config, ConnectTarget target, string resumeToken, string heroId) : base(config)
        {
            _heroId = heroId;
            _resumeToken = resumeToken;
            _sync = new TimeSyncController(config.ToSyncSettings());
            _smoother = new PositionSmoother(1.0 / config.SnapshotRateHz);

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

            SkipAheadIfBehind();
            TickEncounterGrace();
            var decision = _sync.Update(GameBridge.NowHours, RealSeconds);
            GameBridge.SetLocalTime(decision.Speed, decision.SpeedUpMultiplier);
            ReportSyncChange(decision);

            ReplayBuffered(GameBridge.NowHours);
            TickDailyFinances();
            ShowPendingVote();
            if (Config.ClientSmoothPositions)
                _smoother.Step(RealSeconds, (id, x, y, land) => GameBridge.SetPosition(Parties.Find(id, RealSeconds), x, y, land));

            _partyStateTimer += dt;
            if (_partyStateTimer >= PartyStateInterval && MobileParty.MainParty != null)
            {
                _partyStateTimer = 0;
                var position = GameBridge.GetPosition(MobileParty.MainParty);
                Net.SendToAll(new PartyStateMessage { X = position.X, Y = position.Y, IsOnLand = position.IsOnLand }, reliable: false);
            }

            _ledgerTimer += dt;
            if (Config.ClientSyncOwnParty && _ledgerTimer >= LedgerInterval)
            {
                _ledgerTimer = 0;
                SendLedgerChanges();
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

            // Our own party's losses, loot and prisoners travel through the ledger; report the AI parties we fought.
            var result = new BattleResultMessage { WinningSide = (int)mapEvent.WinningSide };
            foreach (var partyBase in mapEvent.InvolvedParties)
            {
                var party = partyBase?.MobileParty;
                if (party == null || party == MobileParty.MainParty || IsPlayerParty(party))
                    continue;
                result.Parties.Add(new PartyOutcome
                {
                    PartyId = party.StringId,
                    Destroyed = !party.IsActive || party.MemberRoster.TotalHealthyCount == 0,
                    Members = GameBridge.CaptureRoster(party.MemberRoster),
                    Prisoners = GameBridge.CaptureRoster(party.PrisonRoster),
                });
            }
            Net.SendToAll(result);
        }

        /// <summary>The market of the settlement we are in, as the host sent it: what our trading is measured against.</summary>
        private readonly Dictionary<string, MarketStateMessage> _markets = new Dictionary<string, MarketStateMessage>();

        public override void OnLocalSettlementEntered(Settlement settlement)
        {
            if (_welcomed && WorldBridge.HasMarket(settlement))
                Net.SendToAll(new MarketRequestMessage { SettlementId = settlement.StringId });
        }

        public override void OnLocalSettlementLeft(Settlement settlement)
        {
            if (settlement == null || !_markets.TryGetValue(settlement.StringId, out var before))
                return;
            _markets.Remove(settlement.StringId);
            var change = WorldBridge.DiffMarket(settlement, before);
            if (change == null)
                return;
            Log.Info($"Client: traded in {settlement.StringId}: {change.GoldChange:+#;-#;0} gold, {change.Items.Count} item types");
            Net.SendToAll(change);
        }

        private void ApplyMarket(MarketStateMessage market)
        {
            var settlement = WorldBridge.Find<Settlement>(market.SettlementId);
            if (!WorldBridge.HasMarket(settlement))
                return;
            WorldBridge.Remote(() => WorldBridge.ApplyMarketState(settlement, market));
            _markets[settlement.StringId] = WorldBridge.CaptureMarket(settlement);
        }

        /// <summary>After an encounter with a party ends (won, fled, or left), its "caught you" is ignored this long.</summary>
        private const double EncounterGraceSeconds = 45;
        private readonly Dictionary<string, double> _encounterGraceUntil = new Dictionary<string, double>();
        private string _encounterWith;

        private void HandleEncounterRequest(string attackerId)
        {
            if (_encounterGraceUntil.TryGetValue(attackerId, out var until) && RealSeconds < until)
                return;
            var attacker = Parties.Find(attackerId, RealSeconds);
            var reason = WorldBridge.WhyNoEncounter(attacker);
            if (reason == null && !GameBridge.IsOnMapWithoutMenu())
                reason = "a menu or encounter is open";
            if (reason == null)
            {
                try
                {
                    if (WorldBridge.StartEncounterWith(attacker))
                    {
                        _encounterWith = attackerId;
                        Log.Info($"Client: {attackerId} caught us; encounter started");
                        return;
                    }
                    reason = "the game refused";
                }
                catch (Exception e)
                {
                    Log.Error("Could not start the encounter with " + attackerId, e);
                    reason = "error";
                }
            }
            // Don't ask again for a while: the host keeps reporting the catch until its world agrees with ours.
            _encounterGraceUntil[attackerId] = RealSeconds + EncounterGraceSeconds / 3;
            Log.Info($"Client: {attackerId} caught us on the host; ignored ({reason})");
        }

        /// <summary>Starts the grace period once the encounter we started is over.</summary>
        private void TickEncounterGrace()
        {
            if (_encounterWith == null || TaleWorlds.CampaignSystem.Encounters.PlayerEncounter.Current != null || _detached)
                return;
            _encounterGraceUntil[_encounterWith] = RealSeconds + EncounterGraceSeconds;
            _encounterWith = null;
        }

        public override void OnLocalPartyDestroyed(MobileParty party)
        {
            // Only the host decides which parties stop existing; local destructions come from our own battles,
            // which the host learns about through the battle result.
        }

        /// <summary>A world change happened in our game. If the player caused it (not the network), tell the host.</summary>
        public override void OnLocalWorldEvent(WorldEventKind kind, string a, string b)
        {
            if (!_welcomed || WorldBridge.ApplyingRemote)
                return;
            var message = new WorldEventMessage { HostHours = GameBridge.NowHours, Kind = kind, A = a, B = b };
            Log.Info("Proposing to the host: " + message);
            Net.SendToAll(message);
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
            yield return $"Joined as player #{_playerId}, host time {_sync.HostSpeed}{(_hostDetached ? " (host in a mission)" : "")}, {_sync.State}, " +
                         $"{behind:0.00}h behind, {_buffer.SnapshotCount + _buffer.EventCount} buffered updates, {_ledger.UnackedCount} unconfirmed party changes";
            foreach (var p in Players.Values.OrderBy(p => p.Id))
                yield return $"  #{p.Id} {p.Name} hero={p.HeroId} party={p.PartyId} {p.Activity} wants={p.RequestedSpeed}";
        }

        public override void Dispose()
        {
            WorldAuthority.ClientMirroring = false;
            base.Dispose();
        }

        private void OnMessage(int peer, INetMessage message)
        {
            // A world update that fails to apply is logged and skipped; it must not end the session.
            try
            {
                HandleMessage(message);
            }
            catch (Exception e)
            {
                Log.Error($"Error applying {message.Type} from the host; continuing", e);
            }
        }

        private void HandleMessage(INetMessage message)
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
                    OnWelcome(welcome);
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
                        ApplySnapshot(snapshot, smooth: true);
                    break;

                case PartyDestroyedMessage destroyed:
                    QueueOrApply(destroyed.HostHours, destroyed);
                    break;

                case PartySpawnedMessage spawned:
                    _requestedParties.Remove(spawned.PartyId);
                    QueueOrApply(spawned.HostHours, spawned);
                    break;

                case WorldEventMessage worldEvent:
                    QueueOrApply(worldEvent.HostHours, worldEvent);
                    break;

                case MarketStateMessage market:
                    ApplyMarket(market);
                    break;

                case PartyRosterMessage roster when Config.ClientSyncRosters:
                    WorldBridge.ApplyRosters(Parties.Find(roster.PartyId, RealSeconds), roster.Members, roster.Prisoners);
                    break;

                case LedgerStateMessage state when Config.ClientSyncOwnParty:
                    ReconcileLedger(state);
                    break;

                case EncounterRequestMessage encounter when Config.ClientAcceptEncounterRequests:
                    // Also while catching up on time: the enemy is here now. Not in a battle or conversation.
                    if (_detached)
                        break;
                    HandleEncounterRequest(encounter.AttackerPartyId);
                    break;

                case DecisionVoteRequestMessage voteRequest:
                    _pendingVotes.Enqueue(voteRequest);
                    break;

                case ChatMessage chat:
                    if (chat.PlayerId != _playerId)
                        Log.Notify($"{NameOf(chat.PlayerId)}: {chat.Text}");
                    break;
            }
        }

        private void OnWelcome(WelcomeMessage welcome)
        {
            var hero = GameBridge.FindHero(_heroId);
            if (hero == null)
            {
                Log.Notify("Your hero is missing from the downloaded world.");
                RequestStop();
                return;
            }
            _playerId = welcome.PlayerId;
            Log.Info($"Client: welcomed as player {_playerId}; taking control of {hero.StringId} (main hero now {Hero.MainHero?.StringId})");
            GameBridge.TakeControlOf(hero);
            Log.Info($"Client: main hero is {Hero.MainHero?.StringId}, main party {MobileParty.MainParty?.StringId}, clan {Clan.PlayerClan?.StringId}");
            GameBridge.ReleaseOwnParty();

            // From here on the host runs the world; this campaign only mirrors it.
            Log.Info("Client features: " + Config.DescribeClientFeatures());
            WorldAuthority.ClientMirroring = Config.ClientMirrorWorld;
            var count = 0;
            if (Config.ClientPuppetParties)
            {
                foreach (var party in MobileParty.All.ToList())
                {
                    WorldBridge.MakePuppet(party);
                    count++;
                }
            }
            Parties.Rebuild(RealSeconds);
            _welcomed = true;
            Log.Info($"Client: {count} parties are now puppets; mirroring the host's world");
            Log.Notify($"Joined! You are {hero.Name}.");
        }

        private void QueueOrApply(double hostHours, INetMessage message)
        {
            if (Buffering || hostHours > GameBridge.NowHours)
                _buffer.Add(hostHours, message);
            else
                ApplyWorldUpdate(message);
        }

        private void ApplyWorldUpdate(INetMessage message)
        {
            switch (message)
            {
                case PartyDestroyedMessage destroyed:
                    _smoother.Remove(destroyed.PartyId);
                    WorldBridge.Remote(() => GameBridge.Retire(Parties.Find(destroyed.PartyId, RealSeconds)));
                    break;
                case PartySpawnedMessage spawned when Config.ClientMirrorSpawns:
                    var party = WorldBridge.CreateMirrorParty(spawned);
                    if (party != null)
                        Parties.Rebuild(RealSeconds);
                    break;
                case WorldEventMessage worldEvent:
                    WorldBridge.ApplyWorldEvent(worldEvent);
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

            if (!wasDetached && _detached)
                _smoother.Clear();
            if (wasDetached && !_detached)
                _justReturned = true;

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
            }
        }

        /// <summary>
        /// Instead of fast-forwarding through the hours the world moved on without us, jump to its time. After a
        /// battle those hours cost nothing: in single player a battle takes no time at all.
        /// </summary>
        private void SkipAheadIfBehind()
        {
            var returned = _justReturned;
            if (!Config.SkipTimeAfterBattles || !_sync.HasHostTime)
                return;
            _justReturned = false;
            var hostHours = _sync.EstimateHostHours(RealSeconds);
            var behind = hostHours - GameBridge.NowHours;
            // Only for time missed in a battle or conversation (or a big gap). Everyday drift of a few minutes is
            // evened out by the gentle catch-up; skipping it made the map jump every couple of seconds.
            if (!returned && behind < LargeGapHours)
                return;
            if (behind <= Config.CatchUpThresholdHours || !GameBridge.JumpToHours(hostHours))
                return; // Not behind, or the clock could not be moved: the normal fast-forward handles it.

            _smoother.Clear();
            if (returned)
                _financeDay = (int)Math.Floor(GameBridge.NowHours / 24);
            Log.Info($"Client: skipped {behind:0.00} hours to the world's time{(returned ? " (no upkeep, back from a battle)" : "")}");
            Log.Notify(behind >= 1 ? $"Skipped {behind:0} hours to rejoin the world." : "Rejoined the world.");
        }

        private void ReportSyncChange(SyncDecision decision)
        {
            if (decision.Action == _lastAction)
                return;
            if (decision.Action == SyncAction.CatchUp)
            {
                _smoother.Clear();
                Log.Notify($"Catching up with the world ({decision.HoursBehind:0.0} hours)...");
            }
            else if (_lastAction == SyncAction.CatchUp)
            {
                Log.Notify("Caught up.");
            }
            _lastAction = decision.Action;
        }

        private void ReplayBuffered(double localHours)
        {
            if (_buffer.IsEmpty)
                return;
            // Once caught up, anything still buffered is due.
            var (snapshot, events) = _sync.State == SyncAction.CatchUp ? _buffer.DrainUntil(localHours) : _buffer.DrainAll();
            foreach (var message in events)
                ApplyWorldUpdate(message);
            if (snapshot != null)
                ApplySnapshot(snapshot, smooth: _sync.State != SyncAction.CatchUp);
        }

        private void ApplySnapshot(WorldSnapshotMessage snapshot, bool smooth)
        {
            if (!_loggedFirstSnapshot)
            {
                _loggedFirstSnapshot = true;
                Log.Info($"Client: first snapshot, {snapshot.Parties.Count} parties (full: {snapshot.IsFull})");
            }
            var ownId = MobileParty.MainParty?.StringId;
            List<string> unknown = null;
            foreach (var position in snapshot.Parties)
            {
                if (position.PartyId == ownId)
                    continue;
                var party = Parties.Find(position.PartyId, RealSeconds);
                if (party == null)
                {
                    (unknown ?? (unknown = new List<string>())).Add(position.PartyId);
                    continue;
                }
                if (smooth && Config.ClientSmoothPositions)
                {
                    var current = GameBridge.GetPosition(party);
                    _smoother.SetTarget(position.PartyId, current.X, current.Y, position.X, position.Y, position.IsOnLand, RealSeconds);
                }
                else
                {
                    GameBridge.SetPosition(party, position.X, position.Y, position.IsOnLand);
                }
            }

            if (unknown != null && Config.ClientMirrorSpawns)
                RequestUnknownParties(unknown);
            if (snapshot.IsFull && Config.ClientRemoveMissingParties)
                RemovePartiesTheHostDoesNotHave(snapshot);
        }

        /// <summary>Parties the host spawned while we were loading (or whose spawn message we missed).</summary>
        private void RequestUnknownParties(List<string> ids)
        {
            var now = RealSeconds;
            var request = new PartyInfoRequestMessage();
            foreach (var id in ids)
            {
                if (_requestedParties.TryGetValue(id, out var at) && now - at < PartyRequestCooldownSeconds)
                    continue;
                _requestedParties[id] = now;
                request.PartyIds.Add(id);
            }
            if (request.PartyIds.Count > 0)
                Net.SendToAll(request);
        }

        /// <summary>
        /// A full snapshot lists every party on the host. Anything we have that it lacks (destroyed while we were
        /// loading) goes, after being missing from two snapshots in a row so a race cannot remove a fresh party.
        /// </summary>
        private void RemovePartiesTheHostDoesNotHave(WorldSnapshotMessage snapshot)
        {
            var onHost = new HashSet<string>(snapshot.Parties.Select(p => p.PartyId));
            var stillMissing = new HashSet<string>();
            foreach (var party in MobileParty.All.ToList())
            {
                if (party == null || !party.IsActive || party == MobileParty.MainParty || party.StringId == null || onHost.Contains(party.StringId))
                    continue;
                if (party.IsGarrison || party.IsMilitia || party.ActualClan == Clan.PlayerClan || IsPlayerParty(party))
                    continue;

                stillMissing.Add(party.StringId);
                _missingFromHost.TryGetValue(party.StringId, out var count);
                if (++count >= MissingSnapshotsBeforeRemoval)
                {
                    Log.Info("Retiring party the host does not have: " + party.StringId);
                    WorldBridge.Remote(() => GameBridge.Retire(party));
                    _missingFromHost.Remove(party.StringId);
                    stillMissing.Remove(party.StringId);
                }
                else
                {
                    _missingFromHost[party.StringId] = count;
                }
            }
            foreach (var id in _missingFromHost.Keys.Where(id => !stillMissing.Contains(id)).ToList())
                _missingFromHost.Remove(id);
        }

        /// <summary>Asks the player about the next pending kingdom decision, when they are free on the map.</summary>
        private void ShowPendingVote()
        {
            if (_voteDialogOpen || _pendingVotes.Count == 0 || !GameBridge.IsOnMapWithoutMenu()
                || TaleWorlds.Library.InformationManager.IsAnyInquiryActive())
                return;

            var request = _pendingVotes.Dequeue();
            _voteDialogOpen = true;
            var choices = request.Options.Select((o, i) => new Choice<int>(i, o.Title, hint: o.Description)).ToList();
            choices.Add(new Choice<int>(-1, "Abstain"));
            var text = $"{request.Description}\n\nDecided in {request.DaysLeft:0.#} days." +
                       (request.IsRuler ? " As ruler, you choose the outcome." : "");

            Dialogs.Choose($"{request.KingdomName}: {request.Title}", text, choices,
                option =>
                {
                    if (option < 0)
                        SendVote(request, -1, VoteWeight.Abstain);
                    else if (request.IsRuler)
                        SendVote(request, option, VoteWeight.Choose);
                    else
                        AskVoteWeight(request, option);
                },
                () => SendVote(request, -1, VoteWeight.Abstain),
                "Vote");
        }

        private void AskVoteWeight(DecisionVoteRequestMessage request, int option)
        {
            var influence = Clan.PlayerClan?.Influence ?? 0f;
            var weights = new[] { VoteWeight.SlightlyFavor, VoteWeight.StronglyFavor, VoteWeight.FullyPush };
            var names = new[] { "Slightly favor", "Strongly favor", "Fully push" };
            var choices = weights.Select((w, i) =>
            {
                var cost = i < request.WeightCosts.Count ? request.WeightCosts[i] : 0;
                return new Choice<VoteWeight>(w, $"{names[i]} ({cost} influence)", cost <= influence, cost <= influence ? null : "Not enough influence");
            }).ToList();
            choices.Insert(0, new Choice<VoteWeight>(VoteWeight.Abstain, "Stay neutral"));
            Dialogs.Choose(request.Options[option].Title, "How strongly does your clan back this?", choices,
                weight => SendVote(request, weight == VoteWeight.Abstain ? -1 : option, weight),
                () => SendVote(request, -1, VoteWeight.Abstain),
                "Vote");
        }

        private void SendVote(DecisionVoteRequestMessage request, int option, VoteWeight weight)
        {
            _voteDialogOpen = false;
            Net.SendToAll(new DecisionVoteMessage { DecisionId = request.DecisionId, OptionIndex = option, Weight = weight });
            Log.Notify(option < 0 ? $"You abstain on: {request.Title}" : $"You voted: {request.Options[option].Title}");
        }

        private void TickDailyFinances()
        {
            if (!Config.ClientSyncOwnParty)
                return;
            var day = (int)Math.Floor(GameBridge.NowHours / 24);
            if (_financeDay < 0)
            {
                _financeDay = day;
                return;
            }
            // Several days can pass during a catch-up; settle each one (bounded, in case of a huge jump).
            for (var guard = 0; _financeDay < day && guard < 30; guard++)
            {
                _financeDay++;
                try
                {
                    var change = GameBridge.ApplyDailyClanFinances();
                    Log.Info($"Client: daily clan finances for day {_financeDay}: {change:+#;-#;0} gold");
                }
                catch (Exception e)
                {
                    Log.Error("Daily clan finances failed", e);
                }
            }
            _financeDay = day;
        }

        private void SendLedgerChanges()
        {
            var change = _ledger.Capture(WorldBridge.CaptureLedger(MobileParty.MainParty));
            if (change.HasValue)
                Net.SendToAll(new LedgerDeltaMessage { Seq = change.Value.Seq, Delta = change.Value.Delta });
        }

        private void ReconcileLedger(LedgerStateMessage state)
        {
            var main = MobileParty.MainParty;
            // In a battle the party changes by the second (casualties) and the host's state predates them; wait
            // until we are back on the map, where the battle's outcome is sent first and then reconciled.
            if (main == null || _detached)
                return;

            // Send anything the player did since the last capture first, so the reconcile below cannot overwrite it.
            SendLedgerChanges();
            var predicted = _ledger.Reconcile(state.AckSeq, state.State);
            var correction = Ledger.Diff(WorldBridge.CaptureLedger(main), predicted);
            if (correction.Count > 0)
                WorldBridge.Remote(() => WorldBridge.ApplyLedgerDelta(main, correction));
            _ledger.Rebase(WorldBridge.CaptureLedger(main));
        }

        private void UpdatePlayers(PlayerListMessage list)
        {
            Players.Clear();
            foreach (var p in list.Players)
                Players[p.Id] = p;

            // Other players' parties are puppets like every other party here. Players who log off stay frozen too:
            // their hero waits where they left it, as it does on the host.
            var remote = new HashSet<string>(list.Players.Where(p => p.Id != _playerId).Select(p => p.PartyId));
            foreach (var id in remote.Where(id => !_frozenRemoteParties.Contains(id)))
                GameBridge.Freeze(GameBridge.FindParty(id));
            _frozenRemoteParties.UnionWith(remote);
        }
    }
}
