using BannerlordMP.Core.Time;
using Xunit;

namespace BannerlordMP.Core.Tests
{
    public class TimeSyncControllerTests
    {
        private static TimeSyncController Create() => new TimeSyncController(new TimeSyncSettings
        {
            CatchUpThresholdHours = 0.5,
            ToleranceHours = 0.05,
            AheadThresholdHours = 0.5,
            CatchUpMultiplier = 16f,
        });

        [Fact]
        public void WaitsUntilHostTimeKnown()
        {
            var sync = Create();
            Assert.Equal(SyncAction.WaitForHost, sync.Update(10, 0).Action);
        }

        [Fact]
        public void FollowsHostSpeedWhenInSync()
        {
            var sync = Create();
            sync.OnHostTime(100, TimeSpeed.Play, 0);
            var decision = sync.Update(100.01, 0);
            Assert.Equal(SyncAction.FollowHost, decision.Action);
            Assert.Equal(TimeSpeed.Play, decision.Speed);
        }

        [Fact]
        public void FastForwardsAfterBattleUntilCaughtUp()
        {
            var sync = Create();
            // The player spent a battle away; the world moved on 6 hours.
            sync.OnHostTime(106, TimeSpeed.Paused, 0);

            var decision = sync.Update(100, 0);
            Assert.Equal(SyncAction.CatchUp, decision.Action);
            Assert.Equal(TimeSpeed.FastForward, decision.Speed);
            Assert.Equal(16f, decision.SpeedUpMultiplier);
            Assert.Equal(6, decision.HoursBehind, 3);

            // Still behind, but inside the catch-up threshold: keep going (hysteresis).
            Assert.Equal(SyncAction.CatchUp, sync.Update(105.8, 1).Action);

            // Caught up.
            var done = sync.Update(105.98, 2);
            Assert.Equal(SyncAction.FollowHost, done.Action);
            Assert.Equal(TimeSpeed.Paused, done.Speed);
        }

        [Fact]
        public void PausesWhenAheadOfHost()
        {
            var sync = Create();
            sync.OnHostTime(100, TimeSpeed.Play, 0);
            Assert.Equal(SyncAction.WaitForHost, sync.Update(101, 0).Action);
            Assert.Equal(SyncAction.WaitForHost, sync.Update(101, 0).Action);

            sync.OnHostTime(101, TimeSpeed.Play, 1);
            Assert.Equal(SyncAction.FollowHost, sync.Update(101, 1).Action);
        }

        [Fact]
        public void ExtrapolatesRunningHostClock()
        {
            var sync = Create();
            sync.OnHostTime(100, TimeSpeed.Play, 0);
            sync.OnHostTime(101, TimeSpeed.Play, 1); // 1 campaign hour per real second

            Assert.Equal(101.5, sync.EstimateHostHours(1.5), 3);
            // A local clock running at the same rate stays in sync instead of being flagged as behind.
            Assert.Equal(SyncAction.FollowHost, sync.Update(101.5, 1.5).Action);
        }

        [Fact]
        public void ExtrapolationIsCappedWhenUpdatesStop()
        {
            var sync = Create();
            sync.OnHostTime(100, TimeSpeed.Play, 0);
            sync.OnHostTime(101, TimeSpeed.Play, 1);
            Assert.Equal(103, sync.EstimateHostHours(60), 3);
        }

        [Fact]
        public void PausedHostIsNotExtrapolated()
        {
            var sync = Create();
            sync.OnHostTime(100, TimeSpeed.Play, 0);
            sync.OnHostTime(101, TimeSpeed.Play, 1);
            sync.OnHostTime(101, TimeSpeed.Paused, 2);
            Assert.Equal(101, sync.EstimateHostHours(5), 3);
        }
    }
}
