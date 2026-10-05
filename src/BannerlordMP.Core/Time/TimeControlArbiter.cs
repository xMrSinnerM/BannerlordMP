using System;
using System.Collections.Generic;
using System.Linq;

namespace BannerlordMP.Core.Time
{
    /// <summary>
    /// Host-side authority for the shared campaign clock. Collects speed requests and activities from every
    /// player and decides the single effective speed the world runs at.
    /// </summary>
    /// <remarks>
    /// Detached players (in a battle or other mission) have no say: the world keeps running without them and
    /// they are fast-forwarded when they return. The host is the only machine that simulates the world, so if
    /// the host itself is detached the world has to stop for everyone.
    /// </remarks>
    public sealed class TimeControlArbiter
    {
        private sealed class PlayerTimeState
        {
            public TimeSpeed Requested = TimeSpeed.Paused;
            public PlayerActivity Activity = PlayerActivity.Map;
        }

        private readonly Dictionary<int, PlayerTimeState> _players = new Dictionary<int, PlayerTimeState>();
        private TimeSpeed _lastRequest = TimeSpeed.Paused;

        public TimeControlArbiter(int hostPlayerId, TimeArbitrationMode mode, bool detachDuringConversations)
        {
            HostPlayerId = hostPlayerId;
            Mode = mode;
            DetachDuringConversations = detachDuringConversations;
            _players[hostPlayerId] = new PlayerTimeState();
            Effective = Compute();
        }

        public int HostPlayerId { get; }
        public TimeArbitrationMode Mode { get; }
        public bool DetachDuringConversations { get; }
        public TimeSpeed Effective { get; private set; }

        public bool HostDetached => IsDetached(HostPlayerId);

        public IEnumerable<int> Players => _players.Keys;

        public bool IsDetached(int playerId)
        {
            return _players.TryGetValue(playerId, out var state) && state.Activity.IsDetached(DetachDuringConversations);
        }

        public PlayerActivity GetActivity(int playerId)
        {
            return _players.TryGetValue(playerId, out var state) ? state.Activity : PlayerActivity.Map;
        }

        public TimeSpeed GetRequested(int playerId)
        {
            return _players.TryGetValue(playerId, out var state) ? state.Requested : TimeSpeed.Paused;
        }

        /// <returns>True if the effective speed changed.</returns>
        public bool AddPlayer(int playerId)
        {
            if (!_players.ContainsKey(playerId))
            {
                // A new player joins paused in consensus mode (it acts like a ready check), and is neutral otherwise.
                _players[playerId] = new PlayerTimeState { Requested = Mode == TimeArbitrationMode.Consensus ? TimeSpeed.Paused : _lastRequest };
            }
            return Recompute();
        }

        /// <returns>True if the effective speed changed.</returns>
        public bool RemovePlayer(int playerId)
        {
            if (playerId == HostPlayerId)
                throw new InvalidOperationException("The host cannot be removed from its own session.");
            _players.Remove(playerId);
            return Recompute();
        }

        /// <returns>True if the effective speed changed.</returns>
        public bool Request(int playerId, TimeSpeed speed)
        {
            if (!_players.TryGetValue(playerId, out var state))
                return false;
            if (state.Activity.IsDetached(DetachDuringConversations))
                return false; // Players in a battle cannot drive the world clock.

            state.Requested = speed;
            _lastRequest = speed;
            if (Mode == TimeArbitrationMode.LastRequestWins)
            {
                foreach (var other in _players.Values)
                    other.Requested = speed;
            }
            return Recompute();
        }

        /// <returns>True if the effective speed changed.</returns>
        public bool SetActivity(int playerId, PlayerActivity activity)
        {
            if (!_players.TryGetValue(playerId, out var state))
                return false;
            state.Activity = activity;
            return Recompute();
        }

        private bool Recompute()
        {
            var previous = Effective;
            Effective = Compute();
            return previous != Effective;
        }

        private TimeSpeed Compute()
        {
            if (HostDetached)
                return TimeSpeed.Paused;

            if (Mode == TimeArbitrationMode.LastRequestWins)
                return _lastRequest;

            var attached = _players.Values.Where(p => !p.Activity.IsDetached(DetachDuringConversations)).ToList();
            if (attached.Count == 0)
                return TimeSpeed.Paused;
            return attached.Min(p => p.Requested);
        }
    }
}
