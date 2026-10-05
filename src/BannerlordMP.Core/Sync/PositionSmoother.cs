using System;
using System.Collections.Generic;

namespace BannerlordMP.Core.Sync
{
    /// <summary>
    /// Positions arrive a few times a second; drawing them as they arrive makes parties jump. This blends each
    /// party from where it was drawn towards its latest reported position over one update interval.
    /// </summary>
    public sealed class PositionSmoother
    {
        private struct Track
        {
            public float FromX, FromY, ToX, ToY;
            public bool IsOnLand;
            public double StartedAt;
        }

        private readonly Dictionary<string, Track> _tracks = new Dictionary<string, Track>();
        private readonly double _interval;
        private readonly float _teleportDistance;

        /// <param name="interval">Seconds between position updates.</param>
        /// <param name="teleportDistance">Jumps longer than this are applied at once instead of blended.</param>
        public PositionSmoother(double interval, float teleportDistance = 5f)
        {
            _interval = Math.Max(0.01, interval);
            _teleportDistance = teleportDistance;
        }

        public int Count => _tracks.Count;

        /// <param name="currentX">Where the party is drawn right now (the blend starts there).</param>
        public void SetTarget(string id, float currentX, float currentY, float x, float y, bool isOnLand, double now)
        {
            var dx = x - currentX;
            var dy = y - currentY;
            var far = dx * dx + dy * dy > _teleportDistance * _teleportDistance;
            _tracks[id] = new Track
            {
                FromX = far ? x : currentX,
                FromY = far ? y : currentY,
                ToX = x,
                ToY = y,
                IsOnLand = isOnLand,
                StartedAt = now,
            };
        }

        public void Remove(string id) => _tracks.Remove(id);

        public void Clear() => _tracks.Clear();

        /// <summary>Visits every party still moving with its position for this frame; finished tracks are dropped after their last step.</summary>
        public void Step(double now, Action<string, float, float, bool> apply)
        {
            List<string> finished = null;
            foreach (var pair in _tracks)
            {
                var t = pair.Value;
                var k = (float)Math.Min(1.0, Math.Max(0.0, (now - t.StartedAt) / _interval));
                apply(pair.Key, t.FromX + (t.ToX - t.FromX) * k, t.FromY + (t.ToY - t.FromY) * k, t.IsOnLand);
                if (k >= 1f)
                    (finished ?? (finished = new List<string>())).Add(pair.Key);
            }
            if (finished != null)
            {
                foreach (var id in finished)
                    _tracks.Remove(id);
            }
        }
    }
}
