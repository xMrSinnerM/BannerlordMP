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
    /// up to the local time matters. Other world updates (parties spawning or being removed, owners changing,
    /// wars...) are kept in order and never dropped.
    /// </remarks>
    public sealed class CatchUpBuffer
    {
        private readonly int _maxSnapshots;
        private readonly LinkedList<WorldSnapshotMessage> _snapshots = new LinkedList<WorldSnapshotMessage>();
        private readonly LinkedList<(double HostHours, INetMessage Message)> _events = new LinkedList<(double, INetMessage)>();

        public CatchUpBuffer(int maxSnapshots = 512)
        {
            if (maxSnapshots < 2)
                throw new ArgumentOutOfRangeException(nameof(maxSnapshots));
            _maxSnapshots = maxSnapshots;
        }

        public int SnapshotCount => _snapshots.Count;
        public int EventCount => _events.Count;
        public bool IsEmpty => _snapshots.Count == 0 && _events.Count == 0;

        /// <summary>Host time of the newest buffered update, or null if empty.</summary>
        public double? LatestHostHours
        {
            get
            {
                double? latest = null;
                if (_snapshots.Count > 0)
                    latest = _snapshots.Last.Value.HostHours;
                if (_events.Count > 0 && (latest == null || _events.Last.Value.HostHours > latest))
                    latest = _events.Last.Value.HostHours;
                return latest;
            }
        }

        public void Add(WorldSnapshotMessage snapshot)
        {
            InsertOrdered(_snapshots, snapshot, s => s.HostHours);
            while (_snapshots.Count > _maxSnapshots)
                MergeOldestPair();
        }

        /// <summary>Queues a world update that happened at <paramref name="hostHours"/> on the host.</summary>
        public void Add(double hostHours, INetMessage message) => InsertOrdered(_events, (HostHours: hostHours, Message: message), e => e.HostHours);

        /// <summary>
        /// Removes and returns everything that happened at or before <paramref name="localHours"/>.
        /// All due snapshots are merged into one (null if none were due); other updates keep their order.
        /// </summary>
        public (WorldSnapshotMessage Snapshot, List<INetMessage> Events) DrainUntil(double localHours)
        {
            WorldSnapshotMessage merged = null;
            while (_snapshots.Count > 0 && _snapshots.First.Value.HostHours <= localHours)
            {
                var next = _snapshots.First.Value;
                _snapshots.RemoveFirst();
                merged = merged == null ? next : Merge(merged, next);
            }

            var events = new List<INetMessage>();
            while (_events.Count > 0 && _events.First.Value.HostHours <= localHours)
            {
                events.Add(_events.First.Value.Message);
                _events.RemoveFirst();
            }

            return (merged, events);
        }

        public (WorldSnapshotMessage Snapshot, List<INetMessage> Events) DrainAll() => DrainUntil(double.MaxValue);

        public void Clear()
        {
            _snapshots.Clear();
            _events.Clear();
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
