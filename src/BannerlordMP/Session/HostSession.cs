using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Security;
using BannerlordMP.Core.Slots;
using BannerlordMP.Core.Time;
using BannerlordMP.Core.Transfer;
using BannerlordMP.Game;
using BannerlordMP.Net;
using BannerlordMP.Steam;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordMP.Session
{
    /// <summary>
    /// The host owns the real world: its campaign is the only one whose AI, economy and battles between AI
    /// parties count. It also runs the "lobby": server password, player hero slots, and sending the current
    /// world to joining players.
    /// </summary>
    /// <remarks>
    /// Joining happens in two connections. From the main menu a player authenticates, picks or creates their
    /// hero and downloads the world. After loading it they reconnect with a one-time resume token and enter
    /// the game.
    /// </remarks>
    internal sealed partial class HostSession : MpSession
    {
        private const int FullSnapshotEvery = 20;
        private const float TimeStateInterval = 0.5f;
        private const double BattleResultTimeoutSeconds = 30;
        private const float MinMoveToSend = 0.01f;
        private const int ChunksPerFrame = 8;

        private sealed class PendingBattle
        {
            public readonly List<string> PartyIds = new List<string>();
            public double? ExpiresAt;
        }

        /// <summary>A connected peer, from the challenge until it is either in the game or gone.</summary>
        private sealed class PeerState
        {
            public byte[] Nonce;
            public bool Authenticated;
            public int SlotId;
            public bool WaitingForSave;
            public SaveSender Sender;
            public int PlayerId = -1;
        }

        private readonly TimeControlArbiter _arbiter;
        private readonly Dictionary<int, PeerState> _peers = new Dictionary<int, PeerState>();
        private readonly Dictionary<int, PendingBattle> _battles = new Dictionary<int, PendingBattle>();
        private readonly HashSet<string> _battleFrozen = new HashSet<string>();
        private readonly Dictionary<string, PartyPosition> _lastSent = new Dictionary<string, PartyPosition>();
        private readonly TokenStore _tokens = new TokenStore();
        private readonly SlotRegistry _slots;
        private readonly string _slotFile;
        private readonly byte[] _serverSalt;
        private readonly byte[] _serverKey;

        private int _nextPlayerId = 1;
        private bool _saving;
        private float _snapshotTimer;
        private float _timeStateTimer;
        private int _snapshotCount;

        public HostSession(MpConfig config) : base(config)
        {
            _arbiter = new TimeControlArbiter(HostPlayerId, config.TimeArbitration, config.DetachDuringConversations, hostIsPlayer: !config.DedicatedHost);
            Players[HostPlayerId] = new PlayerInfo
            {
                Id = HostPlayerId,
                Name = config.DedicatedHost ? "Server" : SteamService.PersonaName ?? config.PlayerName,
                HeroId = Hero.MainHero.StringId,
                PartyId = MobileParty.MainParty.StringId,
                Activity = GameBridge.DetectActivity(),
            };

            _serverSalt = PasswordProof.NewSalt();
            _serverKey = string.IsNullOrEmpty(config.ServerPassword) ? null : PasswordProof.DeriveKey(config.ServerPassword, _serverSalt);

            _slotFile = SlotFilePath(GameBridge.CampaignId);
            _slots = LoadSlots(_slotFile, config.MaxSlots);
            // Heroes of players who are not online stay where they logged off, untouchable.
            foreach (var slot in _slots.Slots)
                GameBridge.Freeze(GameBridge.FindHero(slot.HeroId)?.PartyBelongedTo);

            Net = CreateTransport(config);
            Net.PeerConnected += OnPeerConnected;
            Net.PeerDisconnected += OnPeerDisconnected;
            Net.MessageReceived += OnMessage;
            CampaignEvents.OnSaveOverEvent.AddNonSerializedListener(this, OnSaveOver);

            if (config.DedicatedHost)
                GameBridge.ParkMainParty();

            Log.Notify($"{(config.DedicatedHost ? "Dedicated server" : "Hosting")} '{config.ServerName}' on UDP port {config.Port}" +
                       $"{(SteamService.IsHostingLobby || config.SteamVisibility != LobbyVisibility.Off && SteamService.Available ? " and Steam" : "")}. " +
                       $"{_slots.Slots.Count}/{_slots.MaxSlots} hero slots used.");
        }

        public override bool IsHost => true;

        public SlotRegistry Slots => _slots;

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

            PumpSaveTransfers();

            // While the host is in a mission its campaign does not tick at all, so there is nothing to enforce or send.
            if (_arbiter.HostDetached)
                return;

            if (!_saving && _peers.Values.Any(p => p.WaitingForSave))
                StartSave();

            GameBridge.SetLocalTime(_arbiter.Effective, 0f, unstoppable: false);
            ExpireStaleBattles();
            TickWorld(dt);

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
            if (IsBattleFrozen(attacker) || IsBattleFrozen(defender))
                return false; // Mid-battle on someone's machine.
            // An AI party caught an online player: that battle is fought on the player's machine.
            if (IsOnlinePlayerParty(defender) && !IsRemotePlayerParty(attacker))
                RequestEncounter(attacker, defender);
            else if (IsOnlinePlayerParty(attacker) && !IsRemotePlayerParty(defender))
                RequestEncounter(defender, attacker);
            // Player heroes (online or not) never fight on the host itself.
            return !IsRemotePlayerParty(attacker) && !IsRemotePlayerParty(defender);
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
            SendToPlayers(new PartyDestroyedMessage { HostHours = GameBridge.NowHours, PartyId = party.StringId });
        }

        public override void SendChat(string text)
        {
            SendToPlayers(new ChatMessage { PlayerId = HostPlayerId, Text = text });
            Log.Notify($"{Players[HostPlayerId].Name}: {text}");
        }

        public override IEnumerable<string> Describe()
        {
            yield return $"{(Config.DedicatedHost ? "Dedicated server" : "Hosting")} '{Config.ServerName}' on port {Config.Port}{(SteamService.IsHostingLobby ? " + Steam lobby" : "")}, " +
                         $"time {_arbiter.Effective} ({Config.TimeArbitration}), world at {GameBridge.NowHours:0.00}h, slots {_slots.Slots.Count}/{_slots.MaxSlots}";
            foreach (var p in Players.Values.OrderBy(p => p.Id))
                yield return $"  #{p.Id} {p.Name} hero={p.HeroId} party={p.PartyId} {p.Activity} wants={p.RequestedSpeed}";
        }

        public IEnumerable<string> DescribeSlots()
        {
            if (_slots.Slots.Count == 0)
                yield return "No player heroes yet.";
            foreach (var slot in _slots.Slots)
            {
                var online = Players.Values.Any(p => p.HeroId == slot.HeroId);
                yield return $"  slot {slot.SlotId}: {slot.HeroName} ({slot.CultureName}) hero={slot.HeroId}{(online ? " [online]" : "")}";
            }
        }

        /// <summary>Frees a slot. The hero stays in the world as an ordinary AI lord.</summary>
        public string RemoveSlot(int slotId)
        {
            var slot = _slots.Find(slotId);
            if (slot == null)
                return $"No slot {slotId}.";
            if (Players.Values.Any(p => p.HeroId == slot.HeroId))
                return $"{slot.HeroName} is online; they must leave first.";
            _slots.Remove(slotId);
            SaveSlots();
            GameBridge.Unfreeze(GameBridge.FindHero(slot.HeroId)?.PartyBelongedTo, enableAi: true);
            UpdateLobby();
            return $"Slot {slotId} ({slot.HeroName}) freed; the hero is now an AI lord.";
        }

        public override void Dispose()
        {
            CampaignEvents.OnSaveOverEvent.ClearListeners(this);
            SteamService.LeaveHostedLobby();
            foreach (var id in _battleFrozen)
                GameBridge.Unfreeze(GameBridge.FindParty(id), enableAi: true);
            if (Config.DedicatedHost)
                GameBridge.UnparkMainParty();
            base.Dispose();
        }

        private ITransport CreateTransport(MpConfig config)
        {
            var composite = new CompositeTransport();
            var udp = new NetTransport { ServerInfoProvider = BuildServerInfo };
            udp.StartServer(config.Port);
            composite.Add(udp);

            if (config.SteamVisibility != LobbyVisibility.Off && SteamService.Available)
            {
                try
                {
                    composite.Add(SteamTransport.Listen());
                    SteamService.CreateLobby(config.SteamVisibility, config.MaxSlots + 1, ok =>
                    {
                        if (!ok)
                        {
                            Log.Notify("Could not create the Steam lobby; friends can still join by IP.");
                            return;
                        }
                        UpdateLobby();
                        Log.Notify("Steam lobby is up. Invite friends with mp.invite (or Shift+Tab → invite).");
                    });
                }
                catch (Exception e)
                {
                    Log.Error("Steam hosting unavailable", e);
                    Log.Notify("Steam relay unavailable; friends can join by IP only.");
                }
            }
            return composite;
        }

        private ServerInfoMessage BuildServerInfo()
        {
            return new ServerInfoMessage
            {
                ServerName = Config.ServerName,
                PasswordRequired = _serverKey != null,
                PlayersOnline = Players.Count - (Config.DedicatedHost ? 1 : 0),
                UsedSlots = _slots.Slots.Count,
                MaxSlots = _slots.MaxSlots,
                Port = Config.Port,
            };
        }

        private void UpdateLobby()
        {
            var info = BuildServerInfo();
            SteamService.UpdateLobby(info.ServerName, info.PasswordRequired, info.PlayersOnline, info.UsedSlots, info.MaxSlots);
        }

        private void OnPeerConnected(int peer)
        {
            var state = new PeerState { Nonce = PasswordProof.NewNonce() };
            _peers[peer] = state;
            Net.Send(peer, new AuthChallengeMessage
            {
                ServerName = Config.ServerName,
                PasswordRequired = _serverKey != null,
                ServerSalt = _serverSalt,
                Nonce = state.Nonce,
            });
        }

        private void OnMessage(int peer, INetMessage message)
        {
            if (!_peers.TryGetValue(peer, out var state))
                return;

            if (state.PlayerId < 0)
            {
                HandleLobbyMessage(peer, state, message);
                return;
            }

            var playerId = state.PlayerId;
            if (HandleWorldMessage(peer, playerId, message))
                return;
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

                case PartyStateMessage partyState:
                    if (!_arbiter.IsDetached(playerId))
                        GameBridge.SetPosition(Parties.Find(Players[playerId].PartyId, RealSeconds), partyState.X, partyState.Y, partyState.IsOnLand);
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
                    SendToPlayers(chat);
                    break;
            }
        }

        private void HandleLobbyMessage(int peer, PeerState state, INetMessage message)
        {
            switch (message)
            {
                case HelloMessage hello:
                    if (hello.ProtocolVersion != MessageCodec.ProtocolVersion)
                    {
                        Reject(peer, $"Version mismatch (server protocol {MessageCodec.ProtocolVersion}, yours {hello.ProtocolVersion}). Use the same mod version.");
                        return;
                    }
                    if (!string.IsNullOrEmpty(hello.ResumeToken))
                    {
                        HandleResume(peer, state, hello);
                        return;
                    }
                    if (_serverKey != null && !PasswordProof.Verify(_serverKey, state.Nonce, hello.ServerProof))
                    {
                        Reject(peer, "Wrong server password.");
                        return;
                    }
                    state.Authenticated = true;
                    Net.Send(peer, BuildSlotList());
                    break;

                case ClaimSlotMessage claim when state.Authenticated:
                {
                    var slot = _slots.Find(claim.SlotId);
                    if (slot == null)
                        Reject(peer, "That hero no longer exists on this server.");
                    else if (IsSlotInUse(slot, peer))
                        Reject(peer, $"{slot.HeroName} is already being played.");
                    else if (!_slots.VerifyClaim(slot.SlotId, state.Nonce, claim.Proof))
                        Reject(peer, $"Wrong password for {slot.HeroName}.");
                    else if (GameBridge.FindHero(slot.HeroId)?.IsAlive != true)
                        Reject(peer, $"{slot.HeroName} has died. Ask the host to free the slot (mp.removeslot {slot.SlotId}) and create a new hero.");
                    else
                        QueueJoin(state, slot);
                    break;
                }

                case CreateHeroMessage create when state.Authenticated:
                    HandleCreateHero(peer, state, create);
                    break;
            }
        }

        private void HandleCreateHero(int peer, PeerState state, CreateHeroMessage create)
        {
            var name = (create.HeroName ?? string.Empty).Trim();
            if (!_slots.CanCreate)
            {
                Reject(peer, "All hero slots on this server are taken.");
                return;
            }
            if (name.Length < 2 || name.Length > 32 || name.IndexOfAny(new[] { '\n', '\r', '|' }) >= 0)
            {
                Reject(peer, "Hero names must be 2 to 32 characters.");
                return;
            }
            if (_slots.IsNameTaken(name))
            {
                Reject(peer, $"There is already a player hero called {name}.");
                return;
            }
            if (create.Key == null || create.Key.Length != PasswordProof.KeySize || create.Salt == null || create.Salt.Length != PasswordProof.SaltSize)
            {
                Reject(peer, "Invalid hero password.");
                return;
            }
            if (_arbiter.HostDetached || !GameBridge.OnCampaignMap)
            {
                Reject(peer, "The server is busy (host in a battle or menu). Try again in a moment.");
                return;
            }

            Hero hero;
            try
            {
                hero = GameBridge.CreatePlayerHero(name, create.CultureId, create.IsFemale);
            }
            catch (Exception e)
            {
                Log.Error("Hero creation failed", e);
                Reject(peer, "The server could not create your hero: " + e.Message);
                return;
            }

            GameBridge.Freeze(hero.PartyBelongedTo);
            var slot = _slots.Add(hero.StringId, name, GameBridge.CultureName(hero), create.Salt, create.Key);
            SaveSlots();
            UpdateLobby();
            Log.Notify($"New player hero {name} created (slot {slot.SlotId}).");
            QueueJoin(state, slot);
        }

        private void QueueJoin(PeerState state, PlayerSlot slot)
        {
            state.SlotId = slot.SlotId;
            state.WaitingForSave = true;
        }

        private void StartSave()
        {
            // One save serves everyone currently waiting; it captures the world as it is right now.
            _saving = true;
            try
            {
                GameBridge.SaveWorld(GameBridge.ServerSaveName);
            }
            catch (Exception e)
            {
                _saving = false;
                Log.Error("Could not save the world for joining players", e);
                foreach (var pair in _peers.Where(p => p.Value.WaitingForSave).ToList())
                    Reject(pair.Key, "The server could not save the world: " + e.Message);
            }
        }

        private void OnSaveOver(bool success, string saveName)
        {
            if (!_saving)
                return;
            _saving = false;

            byte[] data = null;
            string error = success ? null : "saving failed";
            if (success)
            {
                try
                {
                    data = GameBridge.ReadSaveFile(GameBridge.ServerSaveName);
                }
                catch (Exception e)
                {
                    Log.Error("Could not read the server save", e);
                    error = e.Message;
                }
            }

            foreach (var pair in _peers.Where(p => p.Value.WaitingForSave).ToList())
            {
                var state = pair.Value;
                state.WaitingForSave = false;
                if (data == null)
                {
                    Reject(pair.Key, "The server could not save the world: " + error);
                    continue;
                }
                var slot = _slots.Find(state.SlotId);
                state.Sender = new SaveSender(data);
                Net.Send(pair.Key, new JoinAcceptedMessage
                {
                    HeroId = slot.HeroId,
                    HeroName = slot.HeroName,
                    ResumeToken = _tokens.Issue(slot.SlotId, RealSeconds),
                    SaveSize = state.Sender.Size,
                    SaveHash = state.Sender.Hash,
                });
                Log.Info($"Sending world ({data.Length / 1024} KB) to peer {pair.Key} for {slot.HeroName}");
            }
        }

        private void PumpSaveTransfers()
        {
            foreach (var pair in _peers)
            {
                var sender = pair.Value.Sender;
                if (sender == null)
                    continue;
                for (var i = 0; i < ChunksPerFrame && !sender.Done; i++)
                {
                    var (offset, data) = sender.Peek();
                    if (!Net.Send(pair.Key, new SaveChunkMessage { Offset = offset, Data = data }))
                        break; // Congested; continue next frame.
                    sender.Advance(data.Length);
                }
                if (sender.Done)
                    pair.Value.Sender = null; // The client disconnects once it has everything.
            }
        }

        private void HandleResume(int peer, PeerState state, HelloMessage hello)
        {
            var slotId = _tokens.Redeem(hello.ResumeToken, RealSeconds);
            var slot = slotId.HasValue ? _slots.Find(slotId.Value) : null;
            var hero = slot == null ? null : GameBridge.FindHero(slot.HeroId);

            string reason = null;
            if (slot == null)
                reason = "Your join expired. Join again from the main menu.";
            else if (hello.CampaignId != GameBridge.CampaignId)
                reason = "You loaded a different world than the server's.";
            else if (IsSlotInUse(slot, peer))
                reason = $"{slot.HeroName} is already being played.";
            else if (hero == null || !hero.IsAlive)
                reason = $"{slot.HeroName} no longer exists or has died.";
            else if (hero.PartyBelongedTo == null || hero.PartyBelongedTo.LeaderHero != hero)
                reason = $"{hero.Name} is not leading a party (captured or defeated?). This is not handled yet.";
            if (reason != null)
            {
                Reject(peer, reason);
                return;
            }
            Log.Info($"Host: {hello.PlayerName} entering the world as {hero.StringId} (party {hero.PartyBelongedTo?.StringId})");

            var playerId = _nextPlayerId++;
            var party = hero.PartyBelongedTo;
            state.PlayerId = playerId;
            state.SlotId = slot.SlotId;
            Players[playerId] = new PlayerInfo
            {
                Id = playerId,
                Name = string.IsNullOrWhiteSpace(hello.PlayerName) ? slot.HeroName : hello.PlayerName,
                HeroId = hero.StringId,
                PartyId = party.StringId,
                Activity = PlayerActivity.Map,
            };
            _arbiter.AddPlayer(playerId);
            SyncRequestedSpeeds();
            // Moved by its owner over the network, but AI on the host may still hunt it.
            GameBridge.Freeze(party, ignoredByOthers: false);

            Net.Send(peer, new WelcomeMessage
            {
                PlayerId = playerId,
                HostPlayerId = HostPlayerId,
                ArbitrationMode = Config.TimeArbitration,
                DetachDuringConversations = Config.DetachDuringConversations,
            });
            _snapshotCount = 0; // Next snapshot is a full one so the newcomer gets everything.
            OnPlayerEnteredWorld(peer, playerId, party);
            BroadcastPlayerList();
            BroadcastTimeState();
            UpdateLobby();
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
                if (party == null || IsPlayerParty(party) || IsSlotParty(party))
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
                // A client reports only on the AI parties it fought. Its own party travels through the ledger, and
                // other players' parties are never its business.
                if (party == playerParty || IsPlayerParty(party) || IsSlotParty(party))
                    continue;

                try
                {
                    GameBridge.ApplyRoster(party.MemberRoster, outcome.Members);
                    GameBridge.ApplyRoster(party.PrisonRoster, outcome.Prisoners);
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
            if (!_peers.TryGetValue(peer, out var state))
                return;
            _peers.Remove(peer);
            if (state.PlayerId < 0)
                return; // Left the lobby stage (usually: finished downloading the world).

            var playerId = state.PlayerId;
            ReleaseBattle(playerId);
            var player = Players[playerId];
            Players.Remove(playerId);
            OnPlayerLeftWorld(playerId);
            // The hero stays where it is, frozen and untouchable, until its owner comes back.
            GameBridge.Freeze(GameBridge.FindParty(player.PartyId));
            if (_arbiter.RemovePlayer(playerId))
                BroadcastTimeState();
            BroadcastPlayerList();
            UpdateLobby();
            Log.Notify($"{player.Name} left ({reason}).");
        }

        private void Reject(int peer, string reason)
        {
            Log.Info($"Rejecting peer {peer}: {reason}");
            Net.Send(peer, new RejectMessage { Reason = reason });
            if (_peers.TryGetValue(peer, out var state))
            {
                state.WaitingForSave = false;
                state.Sender = null;
            }
        }

        private SlotListMessage BuildSlotList()
        {
            return new SlotListMessage
            {
                MaxSlots = _slots.MaxSlots,
                Slots = _slots.Slots.Select(s => new SlotInfo
                {
                    SlotId = s.SlotId,
                    HeroName = s.HeroName,
                    CultureName = s.CultureName,
                    InUse = IsSlotInUse(s, -1),
                    Salt = s.Salt,
                }).ToList(),
                Cultures = GameBridge.PlayableCultures(),
            };
        }

        private bool IsSlotInUse(PlayerSlot slot, int exceptPeer)
        {
            return Players.Values.Any(p => p.HeroId == slot.HeroId)
                || _peers.Any(p => p.Key != exceptPeer && p.Value.SlotId == slot.SlotId && (p.Value.WaitingForSave || p.Value.Sender != null));
        }

        private bool IsRemotePlayerParty(MobileParty party) =>
            party != null && party != MobileParty.MainParty && (IsPlayerParty(party) || IsSlotParty(party));

        private bool IsOnlinePlayerParty(MobileParty party) =>
            party != null && party != MobileParty.MainParty && IsPlayerParty(party);

        private bool IsSlotParty(MobileParty party)
        {
            var leader = party?.LeaderHero;
            return leader != null && _slots.FindByHero(leader.StringId) != null;
        }

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

        /// <summary>Sends to peers that are in the game (not those still in the lobby or downloading).</summary>
        private void SendToPlayers(INetMessage message, bool reliable = true)
        {
            foreach (var pair in _peers)
            {
                if (pair.Value.PlayerId >= 0)
                    Net.Send(pair.Key, message, reliable);
            }
        }

        private void BroadcastTimeState()
        {
            SendToPlayers(new TimeStateMessage
            {
                EffectiveSpeed = _arbiter.Effective,
                HostHours = GameBridge.NowHours,
                HostDetached = _arbiter.HostDetached,
            });
        }

        private void BroadcastPlayerList()
        {
            SendToPlayers(new PlayerListMessage { Players = Players.Values.OrderBy(p => p.Id).ToList() });
        }

        private void BroadcastSnapshot()
        {
            if (!_peers.Values.Any(p => p.PlayerId >= 0))
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
            SendToPlayers(snapshot);
        }

        private void SaveSlots()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_slotFile));
                File.WriteAllText(_slotFile, _slots.Serialize());
            }
            catch (Exception e)
            {
                Log.Error("Could not save the slot file " + _slotFile, e);
                Log.Notify("Warning: could not save player slots to disk. See BannerlordMP.log.");
            }
        }

        private static SlotRegistry LoadSlots(string path, int maxSlots)
        {
            try
            {
                var registry = File.Exists(path) ? SlotRegistry.Deserialize(File.ReadAllText(path), maxSlots) : new SlotRegistry(maxSlots);
                registry.MaxSlots = maxSlots;
                return registry;
            }
            catch (Exception e)
            {
                Log.Error("Could not read slot file " + path, e);
                throw new InvalidOperationException("The player slot file is unreadable: " + path, e);
            }
        }

        /// <summary>Slots live next to the mod, one file per campaign, so they never travel inside the save sent to players.</summary>
        private static string SlotFilePath(string campaignId)
        {
            var safe = new string((campaignId ?? "campaign").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            return Path.Combine(MpConfig.ModuleDirectory, "Servers", safe + ".slots");
        }
    }
}
