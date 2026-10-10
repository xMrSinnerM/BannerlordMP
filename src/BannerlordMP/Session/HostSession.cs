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
        private bool _autoSaving;
        private double _nextAutoSaveAt;
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
            AdoptBestSlotFile(config.MaxSlots);
            WarnAboutTheLoadedWorld();
            // Heroes of players who are not online stay where they logged off, untouchable.
            foreach (var slot in _slots.Slots)
                GameBridge.Freeze(GameBridge.FindHero(slot.HeroId)?.PartyBelongedTo);
            RememberSlotHeroesGold();

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

        public bool IsDedicated => Config.DedicatedHost;

        /// <summary>A clan led or joined by a player hero (any slot, online or not).</summary>
        public bool IsPlayerClan(Clan clan)
        {
            if (clan == null)
                return false;
            foreach (var slot in _slots.Slots)
            {
                if (GameBridge.FindHero(slot.HeroId)?.Clan == clan)
                    return true;
            }
            return false;
        }

        public bool IsPlayerHero(Hero hero) => hero != null && _slots.FindByHero(hero.StringId) != null;

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

            if (!_saving && !_autoSaving && _peers.Values.Any(p => p.WaitingForSave))
                StartSave();
            TickAutoSave();

            GameBridge.SetLocalTime(_arbiter.Effective, 0f);
            if (Config.DedicatedHost)
                GameBridge.KeepMainPartyParked();
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
            // One bad message (a game call throwing on odd data) must not shut the server down for everyone.
            try
            {
                HandleMessage(peer, message);
            }
            catch (Exception e)
            {
                Log.Error($"Error handling {message.Type} from {NameOfPeer(peer)}; continuing", e);
            }
        }

        private string NameOfPeer(int peer) =>
            _peers.TryGetValue(peer, out var state) && state.PlayerId >= 0 ? NameOf(state.PlayerId) : "peer " + peer;

        private void HandleMessage(int peer, INetMessage message)
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
                    else if (GameBridge.FindHero(slot.HeroId)?.IsAlive != true && !RecreateHero(peer, slot))
                        return;
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

            // A hero from the single-player character creator: keep it within what creation can give.
            var sheet = create.Sheet;
            if (sheet != null)
            {
                sheet.Name = name;
                sheet.CultureId = create.CultureId;
                sheet.IsFemale = create.IsFemale;
                var changes = HeroSheetRules.Clamp(sheet);
                if (changes.Count > 0)
                    Log.Info($"Character {name} (peer {peer}) adjusted: {string.Join(", ", changes)}");
            }

            Hero hero;
            try
            {
                hero = GameBridge.CreatePlayerHero(name, create.CultureId, create.IsFemale, sheet);
            }
            catch (Exception e)
            {
                Log.Error("Hero creation failed", e);
                Reject(peer, "The server could not create your hero: " + e.Message);
                return;
            }

            GameBridge.Freeze(hero.PartyBelongedTo);
            RememberPlayerGold(hero);
            var slot = _slots.Add(hero.StringId, name, GameBridge.CultureName(hero), create.Salt, create.Key, create.CultureId, create.IsFemale, sheet?.ToBytes());
            SaveSlots();
            UpdateLobby();
            Log.Notify($"New player hero {name} created (slot {slot.SlotId}).");
            QueueJoin(state, slot);
        }

        private static HeroSheet ReadSheet(PlayerSlot slot)
        {
            if (slot.Sheet == null || slot.Sheet.Length == 0)
                return null;
            try
            {
                var sheet = HeroSheet.FromBytes(slot.Sheet);
                sheet.Name = slot.HeroName;
                HeroSheetRules.Clamp(sheet);
                return sheet;
            }
            catch (Exception e)
            {
                Log.Error($"Slot {slot.SlotId}: stored character unreadable, recreating without it", e);
                return null;
            }
        }

        /// <summary>
        /// The slot's hero is not in this world (an older save was loaded) or has died. Rather than making the
        /// player start over, give them a fresh hero with the same name, culture and gender in the same slot.
        /// </summary>
        private bool RecreateHero(int peer, PlayerSlot slot)
        {
            if (_arbiter.HostDetached || !GameBridge.OnCampaignMap)
            {
                Reject(peer, "The server is busy (host in a battle or menu). Try again in a moment.");
                return false;
            }
            var cultureId = string.IsNullOrEmpty(slot.CultureId)
                ? GameBridge.PlayableCultures().FirstOrDefault(c => c.Name == slot.CultureName).Id
                : slot.CultureId;
            try
            {
                // Made in the character creator: rebuild the same face, background and starting gear.
                var hero = GameBridge.CreatePlayerHero(slot.HeroName, cultureId ?? GameBridge.PlayableCultures().First().Id, slot.IsFemale, ReadSheet(slot));
                GameBridge.Freeze(hero.PartyBelongedTo);
                RememberPlayerGold(hero);
                Log.Notify($"{slot.HeroName} was not in this world; recreated them (slot {slot.SlotId}).");
                _slots.ReplaceHero(slot.SlotId, hero.StringId);
                SaveSlots();
                return true;
            }
            catch (Exception e)
            {
                Log.Error("Could not recreate hero for slot " + slot.SlotId, e);
                Reject(peer, $"{slot.HeroName} is missing from this world and could not be recreated: {e.Message}");
                return false;
            }
        }

        private bool _exitAfterSave;

        /// <summary>
        /// The host chose to leave the campaign. Saves the world first, then leaves (the game's own exit is held
        /// back until then). False when there is nothing to wait for: the exit goes ahead at once.
        /// </summary>
        public bool SaveBeforeExit()
        {
            if (_exitAfterSave || _saving || Campaign.Current == null)
                return false;
            _exitAfterSave = true;
            Log.Notify("Saving the world before leaving... (exit again to leave without waiting)");
            if (_autoSaving)
                return true; // An autosave is already being written; leave when it is done.
            _autoSaving = true;
            try
            {
                GameBridge.SaveWorld(GameBridge.AutoSaveName);
                return true;
            }
            catch (Exception e)
            {
                _autoSaving = false;
                Log.Error("Could not save before leaving", e);
                return false; // Leave anyway; the last autosave stays.
            }
        }

        /// <summary>Saves the world now (host command or a player leaving).</summary>
        public void SaveSoon() => _nextAutoSaveAt = RealSeconds;

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

        private void TickAutoSave()
        {
            var interval = Config.AutoSaveMinutes > 0 ? Config.AutoSaveMinutes * 60 : double.MaxValue / 4;
            if (_nextAutoSaveAt <= 0)
            {
                _nextAutoSaveAt = RealSeconds + interval;
                return;
            }
            // Only from the map, and never on top of a save someone is waiting for.
            if (RealSeconds < _nextAutoSaveAt || _saving || _autoSaving || !GameBridge.OnCampaignMap || _arbiter.HostDetached)
                return;
            _nextAutoSaveAt = RealSeconds + interval;
            _autoSaving = true;
            try
            {
                Log.Info("Autosaving the world as " + GameBridge.AutoSaveName);
                GameBridge.SaveWorld(GameBridge.AutoSaveName);
            }
            catch (Exception e)
            {
                _autoSaving = false;
                Log.Error("Autosave failed", e);
            }
        }

        private void OnSaveOver(bool success, string saveName)
        {
            if (_autoSaving)
            {
                _autoSaving = false;
                if (success)
                {
                    Log.Notify($"World saved as {GameBridge.AutoSaveName}.");
                    // The world now lives on in this save: host it next time, not the save the server started from
                    // (which the menu would otherwise remember, making progress look lost).
                    Ui.MenuMemory.Set("Host.World", GameBridge.AutoSaveName);
                }
                else
                {
                    Log.Notify("Autosave failed. See BannerlordMP.log.");
                }
                if (_exitAfterSave)
                    TaleWorlds.MountAndBlade.MBGameManager.EndGame(); // Now leave, as the host asked.
                return;
            }
            if (!_saving)
                return;
            _saving = false;
            if (success)
                Ui.MenuMemory.Set("Host.World", GameBridge.ServerSaveName);

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
            // Moved by its owner over the network, but AI on the host may still hunt it: its AI stays on (the
            // game's AI does not chase parties whose AI is off) but it makes no decisions and holds still here.
            GameBridge.Freeze(party, ignoredByOthers: false);
            try
            {
                party.Ai.EnableAi();
                party.Ai.SetDoNotMakeNewDecisions(true);
                party.SetMoveModeHold();
            }
            catch (Exception e)
            {
                Log.Error("Could not make " + party.StringId + " a target for the AI", e);
            }
            // A lord's wage limit makes troops desert; a player's party has none (see PlayerPartyPatches).
            try
            {
                party.SetWagePaymentLimit(Campaign.Current.Models.PartyWageModel.MaxWagePaymentLimit);
            }
            catch (Exception e)
            {
                Log.Error("Could not lift the wage limit of " + party.StringId, e);
            }

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
            // The hero stays where it is, frozen and untouchable, until its owner comes back. Their army breaks up
            // first: the lords in it would otherwise wait, frozen with it, until they return.
            var leftParty = GameBridge.FindParty(player.PartyId);
            if (leftParty?.Army != null && leftParty.Army.LeaderParty == leftParty)
                WorldBridge.Remote(() => TaleWorlds.CampaignSystem.Actions.DisbandArmyAction.ApplyByUnknownReason(leftParty.Army));
            GameBridge.Freeze(leftParty);
            if (_arbiter.RemovePlayer(playerId))
                BroadcastTimeState();
            BroadcastPlayerList();
            UpdateLobby();
            Log.Notify($"{player.Name} left ({reason}).");
            SaveSoon(); // Keep their progress even if the server is closed right after.
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

        /// <summary>
        /// The slot file is named after the campaign id, but saves made along the way can carry a different id.
        /// If this campaign's file has no heroes in the loaded world, use the slot file whose heroes are.
        /// </summary>
        private void AdoptBestSlotFile(int maxSlots)
        {
            int Present(SlotRegistry registry) => registry.Slots.Count(slot => GameBridge.FindHero(slot.HeroId) != null);
            if (_slots.Slots.Count > 0 && Present(_slots) == _slots.Slots.Count)
                return;
            try
            {
                var directory = Path.GetDirectoryName(_slotFile);
                if (!Directory.Exists(directory))
                    return;
                var best = Directory.GetFiles(directory, "*.slots")
                    .Select(path => { try { return (Path: path, Registry: SlotRegistry.Deserialize(File.ReadAllText(path), maxSlots)); } catch { return (Path: path, Registry: (SlotRegistry)null); } })
                    .Where(c => c.Registry != null)
                    .Select(c => (c.Path, c.Registry, Count: Present(c.Registry)))
                    .OrderByDescending(c => c.Count)
                    .FirstOrDefault();
                if (best.Registry == null || best.Count <= Present(_slots))
                    return;
                Log.Info($"Using player slots from {best.Path}: {best.Count} of their heroes are in this world");
                _slots.MaxSlots = maxSlots;
                foreach (var slot in best.Registry.Slots)
                {
                    if (_slots.Find(slot.SlotId) == null && _slots.FindByHero(slot.HeroId) == null && _slots.CanCreate)
                        _slots.Add(slot.HeroId, slot.HeroName, slot.CultureName, slot.Salt, slot.Key, slot.CultureId, slot.IsFemale);
                }
                SaveSlots();
            }
            catch (Exception e)
            {
                Log.Error("Could not look for other slot files", e);
            }
        }

        private void WarnAboutTheLoadedWorld()
        {
            if (_slots.FindByHero(Hero.MainHero?.StringId) != null)
                Log.Notify("Warning: this save was made by a player's game, not the server (its main hero is a player's hero). " +
                           "Load BannerlordMP_Autosave or BannerlordMP_Server instead.");
            var missing = _slots.Slots.Where(slot => GameBridge.FindHero(slot.HeroId) == null).Select(slot => slot.HeroName).ToList();
            if (missing.Count > 0)
                Log.Notify($"{missing.Count} player hero(es) are not in this save ({string.Join(", ", missing)}): an older save was loaded. " +
                           "Load BannerlordMP_Autosave to keep their progress; otherwise they are recreated when they join.");
        }

        /// <summary>Slots live next to the mod, one file per campaign, so they never travel inside the save sent to players.</summary>
        private static string SlotFilePath(string campaignId)
        {
            var safe = new string((campaignId ?? "campaign").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
            return Path.Combine(MpConfig.ModuleDirectory, "Servers", safe + ".slots");
        }
    }
}
