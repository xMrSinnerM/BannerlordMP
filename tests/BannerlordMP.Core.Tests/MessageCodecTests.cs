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
            var m = RoundTrip(new HelloMessage { ModVersion = "0.1.0", PlayerName = "Ana", HeroId = "lord_1_1", CampaignId = "abc", LocalHours = 1234.5 });
            Assert.Equal(MessageCodec.ProtocolVersion, m.ProtocolVersion);
            Assert.Equal("Ana", m.PlayerName);
            Assert.Equal("lord_1_1", m.HeroId);
            Assert.Equal("abc", m.CampaignId);
            Assert.Equal(1234.5, m.LocalHours);
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
