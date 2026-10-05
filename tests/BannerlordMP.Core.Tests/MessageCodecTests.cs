using System.Collections.Generic;
using System.IO;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Time;
using Xunit;

namespace BannerlordMP.Core.Tests
{
    public class MessageCodecTests
    {
        private static T RoundTrip<T>(T message) where T : INetMessage
        {
            var decoded = MessageCodec.Decode(MessageCodec.Encode(message));
            return Assert.IsType<T>(decoded);
        }

        [Fact]
        public void Hello()
        {
            var m = RoundTrip(new HelloMessage { ModVersion = "0.2.0", PlayerName = "Ana", ServerProof = new byte[] { 1, 2, 3 }, ResumeToken = "tok", CampaignId = "abc", LocalHours = 1234.5 });
            Assert.Equal(MessageCodec.ProtocolVersion, m.ProtocolVersion);
            Assert.Equal("Ana", m.PlayerName);
            Assert.Equal(new byte[] { 1, 2, 3 }, m.ServerProof);
            Assert.Equal("tok", m.ResumeToken);
            Assert.Equal("abc", m.CampaignId);
            Assert.Equal(1234.5, m.LocalHours);
        }

        [Fact]
        public void LoginAndSlotMessages()
        {
            var challenge = RoundTrip(new AuthChallengeMessage { ServerName = "Calradia", PasswordRequired = true, ServerSalt = new byte[] { 9 }, Nonce = new byte[] { 7, 7 } });
            Assert.Equal("Calradia", challenge.ServerName);
            Assert.True(challenge.PasswordRequired);
            Assert.Equal(new byte[] { 7, 7 }, challenge.Nonce);

            var list = RoundTrip(new SlotListMessage
            {
                MaxSlots = 4,
                Slots = new List<SlotInfo> { new SlotInfo { SlotId = 1, HeroName = "Ana", CultureName = "Vlandia", InUse = true, Salt = new byte[] { 1 } } },
                Cultures = new List<CultureChoice> { new CultureChoice("vlandia", "Vlandia") },
            });
            Assert.Equal(4, list.MaxSlots);
            Assert.True(list.Slots[0].InUse);
            Assert.Equal("vlandia", list.Cultures[0].Id);

            var claim = RoundTrip(new ClaimSlotMessage { SlotId = 3, Proof = new byte[] { 5 } });
            Assert.Equal(3, claim.SlotId);

            var create = RoundTrip(new CreateHeroMessage { HeroName = "Bo", CultureId = "sturgia", IsFemale = true, Salt = new byte[] { 1 }, Key = new byte[] { 2 } });
            Assert.Equal("sturgia", create.CultureId);
            Assert.True(create.IsFemale);

            var accepted = RoundTrip(new JoinAcceptedMessage { HeroId = "h", HeroName = "Bo", ResumeToken = "t", SaveSize = 123, SaveHash = new byte[] { 4 } });
            Assert.Equal(123, accepted.SaveSize);

            var chunk = RoundTrip(new SaveChunkMessage { Offset = 10, Data = new byte[] { 1, 2 } });
            Assert.Equal(10, chunk.Offset);
            Assert.Equal(new byte[] { 1, 2 }, chunk.Data);

            var info = RoundTrip(new ServerInfoMessage { ServerName = "S", PasswordRequired = true, PlayersOnline = 2, UsedSlots = 3, MaxSlots = 6, Port = 7777 });
            Assert.Equal(6, info.MaxSlots);
            Assert.Equal(7777, info.Port);
        }

        [Fact]
        public void WelcomeAndReject()
        {
            var w = RoundTrip(new WelcomeMessage { PlayerId = 3, HostPlayerId = 0, ArbitrationMode = TimeArbitrationMode.Consensus, DetachDuringConversations = true });
            Assert.Equal(3, w.PlayerId);
            Assert.Equal(TimeArbitrationMode.Consensus, w.ArbitrationMode);
            Assert.True(w.DetachDuringConversations);

            Assert.Equal("nope", RoundTrip(new RejectMessage { Reason = "nope" }).Reason);
        }

        [Fact]
        public void PlayerList()
        {
            var m = RoundTrip(new PlayerListMessage
            {
                Players = new List<PlayerInfo>
                {
                    new PlayerInfo { Id = 0, Name = "Host", HeroId = "main_hero", PartyId = "player_party", Activity = PlayerActivity.Map, RequestedSpeed = TimeSpeed.Play },
                    new PlayerInfo { Id = 1, Name = "Guest", HeroId = "h2", PartyId = "p2", Activity = PlayerActivity.Mission, RequestedSpeed = TimeSpeed.Paused },
                },
            });
            Assert.Equal(2, m.Players.Count);
            Assert.Equal(PlayerActivity.Mission, m.Players[1].Activity);
            Assert.Equal("p2", m.Players[1].PartyId);
        }

        [Fact]
        public void TimeMessages()
        {
            Assert.Equal(TimeSpeed.FastForward, RoundTrip(new TimeRequestMessage { Speed = TimeSpeed.FastForward }).Speed);
            Assert.Equal(PlayerActivity.Conversation, RoundTrip(new ActivityChangedMessage { Activity = PlayerActivity.Conversation }).Activity);

            var t = RoundTrip(new TimeStateMessage { EffectiveSpeed = TimeSpeed.Play, HostHours = 77.25, HostDetached = true });
            Assert.Equal(TimeSpeed.Play, t.EffectiveSpeed);
            Assert.Equal(77.25, t.HostHours);
            Assert.True(t.HostDetached);
        }

        [Fact]
        public void WorldSnapshotAndPartyState()
        {
            var s = RoundTrip(new WorldSnapshotMessage
            {
                HostHours = 12,
                IsFull = true,
                Parties = new List<PartyPosition> { new PartyPosition("a", 1.5f, 2.5f, true), new PartyPosition("ship", 3, 4, false) },
            });
            Assert.True(s.IsFull);
            Assert.Equal(2.5f, s.Parties[0].Y);
            Assert.False(s.Parties[1].IsOnLand);

            var p = RoundTrip(new PartyStateMessage { X = 9, Y = 8, IsOnLand = true });
            Assert.Equal(9, p.X);
        }

        [Fact]
        public void BattleMessages()
        {
            var started = RoundTrip(new BattleStartedMessage { PartyIds = new List<string> { "a", "b" } });
            Assert.Equal(new List<string> { "a", "b" }, started.PartyIds);

            var result = RoundTrip(new BattleResultMessage
            {
                WinningSide = 1,
                Parties = new List<PartyOutcome>
                {
                    new PartyOutcome
                    {
                        PartyId = "a",
                        LeaderGold = 500,
                        Members = new List<TroopCount> { new TroopCount("imperial_recruit", 10, 2) },
                        Prisoners = new List<TroopCount> { new TroopCount("looter", 3, 0) },
                    },
                    new PartyOutcome { PartyId = "b", Destroyed = true },
                },
            });
            Assert.Equal(1, result.WinningSide);
            Assert.Equal(2, result.Parties[0].Members[0].Wounded);
            Assert.Equal("looter", result.Parties[0].Prisoners[0].CharacterId);
            Assert.True(result.Parties[1].Destroyed);
            Assert.Equal(-1, result.Parties[1].LeaderGold);
        }

        [Fact]
        public void DestroyedAndChat()
        {
            var d = RoundTrip(new PartyDestroyedMessage { HostHours = 4, PartyId = "x" });
            Assert.Equal("x", d.PartyId);
            var c = RoundTrip(new ChatMessage { PlayerId = 2, Text = "hello ⚔" });
            Assert.Equal("hello ⚔", c.Text);
        }

        [Fact]
        public void WorldReplicationMessages()
        {
            var spawned = RoundTrip(new PartySpawnedMessage
            {
                HostHours = 3, PartyId = "bandit_1", Name = "Looters", ClanId = "looters", LeaderHeroId = "", HomeSettlementId = "hideout_1",
                IsLordParty = false, X = 1, Y = 2, IsOnLand = true,
                Members = new List<TroopCount> { new TroopCount("looter", 12, 1) },
            });
            Assert.Equal("Looters", spawned.Name);
            Assert.Equal(12, spawned.Members[0].Count);

            var roster = RoundTrip(new PartyRosterMessage { PartyId = "p", Members = new List<TroopCount> { new TroopCount("a", 1, 0) } });
            Assert.Equal("p", roster.PartyId);

            var world = RoundTrip(new WorldEventMessage { HostHours = 9, Kind = WorldEventKind.SettlementOwner, A = "town_V1", B = "lord_1" });
            Assert.Equal(WorldEventKind.SettlementOwner, world.Kind);
            Assert.Equal("lord_1", world.B);

            Assert.Equal("x", RoundTrip(new EncounterRequestMessage { AttackerPartyId = "x" }).AttackerPartyId);

            var delta = RoundTrip(new LedgerDeltaMessage { Seq = 4, Delta = new Dictionary<string, int> { { "g", -5 } } });
            Assert.Equal(4, delta.Seq);
            Assert.Equal(-5, delta.Delta["g"]);

            var state = RoundTrip(new LedgerStateMessage { AckSeq = 4, State = new Dictionary<string, int> { { "m:recruit", 7 } } });
            Assert.Equal(7, state.State["m:recruit"]);

            Assert.Equal(new List<string> { "a" }, RoundTrip(new PartyInfoRequestMessage { PartyIds = new List<string> { "a" } }).PartyIds);
        }

        [Fact]
        public void UnknownTypeIsRejected()
        {
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(new byte[] { 250 }));
        }

        [Fact]
        public void AbsurdListLengthIsRejected()
        {
            var data = new byte[] { (byte)MessageType.BattleStarted, 0xFF, 0xFF, 0xFF, 0x7F };
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(data));
        }
    }
}
