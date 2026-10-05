using System;
using System.Collections.Generic;
using BannerlordMP.Core.Protocol;

namespace BannerlordMP.Core.Sync
{
    /// <summary>
    /// Holds world updates a client received while it was not following the world clock (in a battle, or
    /// while fast-forwarding back), and hands them back in host-time order as the local clock passes them.
    /// That is what makes the fast-forward after a battle show the world moving rather than teleporting.
    /// </summary>
    /// <remarks>
    /// Position snapshots are merged rather than queued one by one: only the newest position of each party
    /// up to the local time matters. Party removals are kept in order and never dropped.
    /// </remarks>
    public sealed class CatchUpBuffer
    {
        private readonly int _maxSnapshots;
        private readonly LinkedList<WorldSnapshotMessage> _snapshots = new LinkedList<WorldSnapshotMessage>();
        private readonly LinkedList<PartyDestroyedMessage> _destroyed = new LinkedList<PartyDestroyedMessage>();

        public CatchUpBuffer(int maxSnapshots = 512)
        {
            if (maxSnapshots < 2)
                throw new ArgumentOutOfRangeException(nameof(maxSnapshots));
            _maxSnapshots = maxSnapshots;
        }

        public int SnapshotCount => _snapshots.Count;
        public int DestroyedCount => _destroyed.Count;
        public bool IsEmpty => _snapshots.Count == 0 && _destroyed.Count == 0;

        /// <summary>Host time of the newest buffered update, or null if empty.</summary>
        public double? LatestHostHours
        {
            get
            {
                double? latest = null;
                if (_snapshots.Count > 0)
                    latest = _snapshots.Last.Value.HostHours;
                if (_destroyed.Count > 0 && (latest == null || _destroyed.Last.Value.HostHours > latest))
                    latest = _destroyed.Last.Value.HostHours;
                return latest;
            }
        }

        public void Add(WorldSnapshotMessage snapshot)
        {
            InsertOrdered(_snapshots, snapshot, s => s.HostHours);
            while (_snapshots.Count > _maxSnapshots)
                MergeOldestPair();
        }

        public void Add(PartyDestroyedMessage destroyed) => InsertOrdered(_destroyed, destroyed, d => d.HostHours);

        /// <summary>
        /// Removes and returns everything that happened at or before <paramref name="localHours"/>.
        /// All due snapshots are merged into one (null if none were due); removals keep their order.
        /// </summary>
        public (WorldSnapshotMessage Snapshot, List<PartyDestroyedMessage> Destroyed) DrainUntil(double localHours)
        {
            WorldSnapshotMessage merged = null;
            while (_snapshots.Count > 0 && _snapshots.First.Value.HostHours <= localHours)
            {
                var next = _snapshots.First.Value;
                _snapshots.RemoveFirst();
                merged = merged == null ? next : Merge(merged, next);
            }

            var destroyed = new List<PartyDestroyedMessage>();
            while (_destroyed.Count > 0 && _destroyed.First.Value.HostHours <= localHours)
            {
                destroyed.Add(_destroyed.First.Value);
                _destroyed.RemoveFirst();
            }

            return (merged, destroyed);
        }

        public (WorldSnapshotMessage Snapshot, List<PartyDestroyedMessage> Destroyed) DrainAll() => DrainUntil(double.MaxValue);

        public void Clear()
        {
            _snapshots.Clear();
            _destroyed.Clear();
        }

        /// <summary>Combines an older and a newer snapshot: the newer position wins for every party in both.</summary>
        public static WorldSnapshotMessage Merge(WorldSnapshotMessage older, WorldSnapshotMessage newer)
        {
            if (newer.IsFull)
                return newer;

            var byId = new Dictionary<string, int>(older.Parties.Count + newer.Parties.Count);
            var parties = new List<PartyPosition>(older.Parties.Count + newer.Parties.Count);
            foreach (var p in older.Parties)
            {
                byId[p.PartyId] = parties.Count;
                parties.Add(p);
            }
            foreach (var p in newer.Parties)
            {
                if (byId.TryGetValue(p.PartyId, out var index))
                {
                    parties[index] = p;
                }
                else
                {
                    byId[p.PartyId] = parties.Count;
                    parties.Add(p);
                }
            }

            return new WorldSnapshotMessage { HostHours = newer.HostHours, IsFull = older.IsFull, Parties = parties };
        }

        private void MergeOldestPair()
        {
            var first = _snapshots.First.Value;
            _snapshots.RemoveFirst();
            _snapshots.First.Value = Merge(first, _snapshots.First.Value);
        }

        private static void InsertOrdered<T>(LinkedList<T> list, T item, Func<T, double> time)
        {
            // Updates almost always arrive in order, so walk from the back.
            var node = list.Last;
            while (node != null && time(node.Value) > time(item))
                node = node.Previous;
            if (node == null)
                list.AddFirst(item);
            else
                list.AddAfter(node, item);
        }
    }
}
