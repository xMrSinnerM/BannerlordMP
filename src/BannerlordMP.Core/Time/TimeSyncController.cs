using System;

namespace BannerlordMP.Core.Time
{
    public enum SyncAction : byte
    {
        /// <summary>Clocks agree: run at the host's speed.</summary>
        FollowHost = 0,
        /// <summary>Local clock is behind the host (e.g. after a battle): fast-forward until caught up.</summary>
        CatchUp = 1,
        /// <summary>Local clock is ahead of the host: hold still until the host catches up.</summary>
        WaitForHost = 2,
    }

    public readonly struct SyncDecision
    {
        public SyncDecision(SyncAction action, TimeSpeed speed, float speedUpMultiplier, double hoursBehind)
        {
            Action = action;
            Speed = speed;
            SpeedUpMultiplier = speedUpMultiplier;
            HoursBehind = hoursBehind;
        }

        public SyncAction Action { get; }
        /// <summary>Speed the local campaign should run at.</summary>
        public TimeSpeed Speed { get; }
        /// <summary>Fast-forward multiplier to use when <see cref="Speed"/> is FastForward. 0 means "use the game default".</summary>
        public float SpeedUpMultiplier { get; }
        /// <summary>Positive when the local clock is behind the host.</summary>
        public double HoursBehind { get; }
    }

    public sealed class TimeSyncSettings
    {
        /// <summary>Start fast-forwarding when the local clock falls this many campaign hours behind.</summary>
        public double CatchUpThresholdHours = 0.5;
        /// <summary>Stop catching up / waiting once the clocks are within this many campaign hours.</summary>
        public double ToleranceHours = 0.05;
        /// <summary>Pause locally when the local clock is this many campaign hours ahead.</summary>
        public double AheadThresholdHours = 0.5;
        /// <summary>Fast-forward multiplier while catching up (the game's own fast-forward uses 4).</summary>
        public float CatchUpMultiplier = 16f;
    }

    /// <summary>
    /// Client-side controller that keeps the local campaign clock aligned with the host's. It is also what
    /// fast-forwards a player who comes back from a battle: their clock is far behind, so it catches up.
    /// </summary>
    /// <remarks>
    /// Host time is extrapolated between updates using the rate observed from consecutive host updates,
    /// so the controller does not oscillate while both clocks are running.
    /// </remarks>
    public sealed class TimeSyncController
    {
        private readonly TimeSyncSettings _settings;
        private double _hostHours;
        private double _hostReceivedAtSeconds;
        private double _hostRateHoursPerSecond;
        private TimeSpeed _hostSpeed = TimeSpeed.Paused;
        private bool _hasHostTime;

        public TimeSyncController(TimeSyncSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public SyncAction State { get; private set; } = SyncAction.FollowHost;
        public bool HasHostTime => _hasHostTime;
        public TimeSpeed HostSpeed => _hostSpeed;

        public void OnHostTime(double hostHours, TimeSpeed hostSpeed, double realTimeSeconds)
        {
            if (_hasHostTime && hostSpeed != TimeSpeed.Paused && _hostSpeed == hostSpeed)
            {
                var dt = realTimeSeconds - _hostReceivedAtSeconds;
                var dh = hostHours - _hostHours;
                if (dt > 0.05 && dh >= 0)
                    _hostRateHoursPerSecond = dh / dt;
            }
            else if (hostSpeed == TimeSpeed.Paused)
            {
                _hostRateHoursPerSecond = 0;
            }

            _hostHours = hostHours;
            _hostReceivedAtSeconds = realTimeSeconds;
            _hostSpeed = hostSpeed;
            _hasHostTime = true;
        }

        public double EstimateHostHours(double realTimeSeconds)
        {
            if (!_hasHostTime)
                return 0;
            if (_hostSpeed == TimeSpeed.Paused)
                return _hostHours;
            // Do not extrapolate further than a couple of seconds past the last update: if updates stop
            // arriving we would rather wait than run off on our own.
            var elapsed = Math.Min(Math.Max(0, realTimeSeconds - _hostReceivedAtSeconds), 2.0);
            return _hostHours + _hostRateHoursPerSecond * elapsed;
        }

        public SyncDecision Update(double localHours, double realTimeSeconds)
        {
            if (!_hasHostTime)
                return new SyncDecision(SyncAction.WaitForHost, TimeSpeed.Paused, 0f, 0);

            var behind = EstimateHostHours(realTimeSeconds) - localHours;

            switch (State)
            {
                case SyncAction.CatchUp:
                    if (behind <= _settings.ToleranceHours)
                        State = SyncAction.FollowHost;
                    break;
                case SyncAction.WaitForHost:
                    if (-behind <= _settings.ToleranceHours)
                        State = SyncAction.FollowHost;
                    break;
            }

            if (State == SyncAction.FollowHost)
            {
                if (behind > _settings.CatchUpThresholdHours)
                    State = SyncAction.CatchUp;
                else if (-behind > _settings.AheadThresholdHours)
                    State = SyncAction.WaitForHost;
            }

            switch (State)
            {
                case SyncAction.CatchUp:
                    return new SyncDecision(SyncAction.CatchUp, TimeSpeed.FastForward, _settings.CatchUpMultiplier, behind);
                case SyncAction.WaitForHost:
                    return new SyncDecision(SyncAction.WaitForHost, TimeSpeed.Paused, 0f, behind);
                default:
                    return new SyncDecision(SyncAction.FollowHost, _hostSpeed, 0f, behind);
            }
        }

        public void Reset()
        {
            State = SyncAction.FollowHost;
            _hasHostTime = false;
            _hostRateHoursPerSecond = 0;
        }
    }
}
