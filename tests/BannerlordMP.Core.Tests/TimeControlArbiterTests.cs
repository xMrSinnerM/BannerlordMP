using BannerlordMP.Core.Time;
using Xunit;

namespace BannerlordMP.Core.Tests
{
    public class TimeControlArbiterTests
    {
        private const int Host = 0;

        [Fact]
        public void StartsPaused()
        {
            var arbiter = new TimeControlArbiter(Host, TimeArbitrationMode.LastRequestWins, false);
            Assert.Equal(TimeSpeed.Paused, arbiter.Effective);
        }

        [Fact]
        public void LastRequestWins_AnyPlayerControlsTheClock()
        {
            var arbiter = new TimeControlArbiter(Host, TimeArbitrationMode.LastRequestWins, false);
            arbiter.AddPlayer(1);

            Assert.True(arbiter.Request(1, TimeSpeed.FastForward));
            Assert.Equal(TimeSpeed.FastForward, arbiter.Effective);

            Assert.True(arbiter.Request(Host, TimeSpeed.Paused));
            Assert.Equal(TimeSpeed.Paused, arbiter.Effective);
        }

        [Fact]
        public void Consensus_RunsAtSlowestRequest()
        {
            var arbiter = new TimeControlArbiter(Host, TimeArbitrationMode.Consensus, false);
            arbiter.AddPlayer(1);

            arbiter.Request(Host, TimeSpeed.FastForward);
            Assert.Equal(TimeSpeed.Paused, arbiter.Effective); // player 1 has not readied up

            arbiter.Request(1, TimeSpeed.Play);
            Assert.Equal(TimeSpeed.Play, arbiter.Effective);

            arbiter.Request(1, TimeSpeed.FastForward);
            Assert.Equal(TimeSpeed.FastForward, arbiter.Effective);
        }

        [Fact]
        public void PlayerInBattle_DoesNotHoldUpTheWorld()
        {
            var arbiter = new TimeControlArbiter(Host, TimeArbitrationMode.Consensus, false);
            arbiter.AddPlayer(1);
            arbiter.Request(Host, TimeSpeed.Play);
            arbiter.Request(1, TimeSpeed.Paused);
            Assert.Equal(TimeSpeed.Paused, arbiter.Effective);

            arbiter.SetActivity(1, PlayerActivity.Mission);
            Assert.True(arbiter.IsDetached(1));
            Assert.Equal(TimeSpeed.Play, arbiter.Effective);
        }

        [Fact]
        public void PlayerInBattle_CannotChangeSpeed()
        {
            var arbiter = new TimeControlArbiter(Host, TimeArbitrationMode.LastRequestWins, false);
            arbiter.AddPlayer(1);
            arbiter.Request(Host, TimeSpeed.Play);
            arbiter.SetActivity(1, PlayerActivity.Mission);

            Assert.False(arbiter.Request(1, TimeSpeed.Paused));
            Assert.Equal(TimeSpeed.Play, arbiter.Effective);
        }

        [Fact]
        public void HostInMission_StopsTheWorld()
        {
            var arbiter = new TimeControlArbiter(Host, TimeArbitrationMode.LastRequestWins, false);
            arbiter.AddPlayer(1);
            arbiter.Request(1, TimeSpeed.FastForward);

            arbiter.SetActivity(Host, PlayerActivity.Mission);
            Assert.True(arbiter.HostDetached);
            Assert.Equal(TimeSpeed.Paused, arbiter.Effective);

            arbiter.SetActivity(Host, PlayerActivity.Map);
            Assert.Equal(TimeSpeed.FastForward, arbiter.Effective);
        }

        [Fact]
        public void Conversations_DetachOnlyWhenConfigured()
        {
            var attached = new TimeControlArbiter(Host, TimeArbitrationMode.Consensus, detachDuringConversations: false);
            attached.SetActivity(Host, PlayerActivity.Conversation);
            Assert.False(attached.HostDetached);

            var detached = new TimeControlArbiter(Host, TimeArbitrationMode.Consensus, detachDuringConversations: true);
            detached.SetActivity(Host, PlayerActivity.Conversation);
            Assert.True(detached.HostDetached);
        }

        [Fact]
        public void Menus_NeverDetach()
        {
            var arbiter = new TimeControlArbiter(Host, TimeArbitrationMode.Consensus, true);
            arbiter.SetActivity(Host, PlayerActivity.Menu);
            Assert.False(arbiter.HostDetached);
        }

        [Fact]
        public void RemovingAPausedPlayer_ResumesConsensus()
        {
            var arbiter = new TimeControlArbiter(Host, TimeArbitrationMode.Consensus, false);
            arbiter.AddPlayer(1);
            arbiter.Request(Host, TimeSpeed.Play);
            Assert.Equal(TimeSpeed.Paused, arbiter.Effective);

            Assert.True(arbiter.RemovePlayer(1));
            Assert.Equal(TimeSpeed.Play, arbiter.Effective);
        }
    }
}
