using System.Collections.Generic;
using System.Linq;
using BannerlordMP.Core.Protocol;
using BannerlordMP.Core.Sync;
using Xunit;

namespace BannerlordMP.Core.Tests
{
    public class CatchUpBufferTests
    {
        private static WorldSnapshotMessage Snapshot(double hours, bool full, params (string id, float x)[] parties)
        {
            return new WorldSnapshotMessage
            {
                HostHours = hours,
                IsFull = full,
                Parties = parties.Select(p => new PartyPosition(p.id, p.x, 0, true)).ToList(),
            };
        }

        [Fact]
        public void ReleasesUpdatesAsLocalTimePassesThem()
        {
            var buffer = new CatchUpBuffer();
            buffer.Add(Snapshot(1, true, ("a", 1), ("b", 1)));
            buffer.Add(Snapshot(2, false, ("a", 2)));
            buffer.Add(Snapshot(3, false, ("b", 3)));

            var (first, _) = buffer.DrainUntil(1.5);
            Assert.Equal(1, first.HostHours);

            var (second, _) = buffer.DrainUntil(2.5);
            Assert.Equal(2, second.HostHours);
            Assert.Single(second.Parties);

            Assert.Null(buffer.DrainUntil(2.9).Snapshot);
            Assert.Equal(3, buffer.DrainUntil(3).Snapshot.HostHours);
            Assert.True(buffer.IsEmpty);
        }

        [Fact]
        public void MergesDueSnapshotsKeepingNewestPositions()
        {
            var buffer = new CatchUpBuffer();
            buffer.Add(Snapshot(1, true, ("a", 1), ("b", 1)));
            buffer.Add(Snapshot(2, false, ("a", 2)));

            var (merged, _) = buffer.DrainUntil(5);
            Assert.True(merged.IsFull);
            Assert.Equal(2, merged.HostHours);
            Assert.Equal(2f, merged.Parties.Single(p => p.PartyId == "a").X);
            Assert.Equal(1f, merged.Parties.Single(p => p.PartyId == "b").X);
        }

        [Fact]
        public void FullSnapshotReplacesOlderState()
        {
            var merged = CatchUpBuffer.Merge(Snapshot(1, true, ("gone", 1)), Snapshot(2, true, ("a", 2)));
            Assert.Equal(new[] { "a" }, merged.Parties.Select(p => p.PartyId));
        }

        [Fact]
        public void OutOfOrderUpdatesAreSorted()
        {
            var buffer = new CatchUpBuffer();
            buffer.Add(Snapshot(3, false, ("a", 3)));
            buffer.Add(Snapshot(1, false, ("a", 1)));
            Assert.Equal(1, buffer.DrainUntil(1).Snapshot.Parties[0].X);
        }

        [Fact]
        public void StaysBoundedWithoutLosingInformation()
        {
            var buffer = new CatchUpBuffer(maxSnapshots: 4);
            for (var i = 0; i < 100; i++)
                buffer.Add(Snapshot(i, false, ("p" + i, i)));

            Assert.True(buffer.SnapshotCount <= 4);
            var (merged, _) = buffer.DrainAll();
            Assert.Equal(100, merged.Parties.Count);
            Assert.Equal(99, merged.HostHours);
        }

        [Fact]
        public void WorldUpdatesKeepOrder()
        {
            var buffer = new CatchUpBuffer();
            buffer.Add(2, new PartyDestroyedMessage { HostHours = 2, PartyId = "b" });
            buffer.Add(1, new PartySpawnedMessage { HostHours = 1, PartyId = "b" });
            buffer.Add(5, new WorldEventMessage { HostHours = 5, Kind = WorldEventKind.War, A = "x", B = "y" });

            var (_, events) = buffer.DrainUntil(3);
            Assert.IsType<PartySpawnedMessage>(events[0]);
            Assert.IsType<PartyDestroyedMessage>(events[1]);
            Assert.Equal(2, events.Count);
            Assert.Equal(5, buffer.LatestHostHours);
            Assert.IsType<WorldEventMessage>(buffer.DrainAll().Events.Single());
        }
    }
}
