using System;
using System.Collections.Generic;
using System.Linq;

namespace BannerlordMP.Core.Sync
{
    /// <summary>
    /// A player's own party and hero, flattened into named integer counters (gold, troops by type, items,
    /// skill xp...). Both sides change it: the host through the world simulation (wages, food, healing), the
    /// client through what the player does (buying, recruiting, looting). Counters make both kinds of change
    /// mergeable as plain deltas.
    /// </summary>
    public static class Ledger
    {
        /// <summary>to − from, without zero entries.</summary>
        public static Dictionary<string, int> Diff(IReadOnlyDictionary<string, int> from, IReadOnlyDictionary<string, int> to)
        {
            var delta = new Dictionary<string, int>();
            foreach (var pair in to)
            {
                from.TryGetValue(pair.Key, out var before);
                if (pair.Value != before)
                    delta[pair.Key] = pair.Value - before;
            }
            foreach (var pair in from)
            {
                if (!to.ContainsKey(pair.Key) && pair.Value != 0)
                    delta[pair.Key] = -pair.Value;
            }
            return delta;
        }

        /// <summary>state + delta, without zero entries.</summary>
        public static Dictionary<string, int> Add(IReadOnlyDictionary<string, int> state, IReadOnlyDictionary<string, int> delta)
        {
            var result = state.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => p.Value);
            foreach (var pair in delta)
            {
                result.TryGetValue(pair.Key, out var value);
                value += pair.Value;
                if (value == 0)
                    result.Remove(pair.Key);
                else
                    result[pair.Key] = value;
            }
            return result;
        }
    }

    /// <summary>
    /// Client side of the ledger: sends local changes as numbered deltas and, when the host's authoritative
    /// state arrives, re-applies whatever the host has not acknowledged yet (so the player never sees their own
    /// action undone and then redone).
    /// </summary>
    public sealed class ClientLedger
    {
        private readonly List<(int Seq, Dictionary<string, int> Delta)> _unacked = new List<(int, Dictionary<string, int>)>();
        private Dictionary<string, int> _baseline;
        private int _nextSeq = 1;

        public bool HasBaseline => _baseline != null;
        public int UnackedCount => _unacked.Count;

        /// <summary>
        /// Compares the party as it is now with the last known state.
        /// Returns the change to send, or null if nothing changed (or no host state has arrived yet).
        /// </summary>
        public (int Seq, Dictionary<string, int> Delta)? Capture(IReadOnlyDictionary<string, int> local)
        {
            if (_baseline == null)
                return null;
            var delta = Ledger.Diff(_baseline, local);
            if (delta.Count == 0)
                return null;
            var seq = _nextSeq++;
            _unacked.Add((seq, delta));
            _baseline = local.ToDictionary(p => p.Key, p => p.Value);
            return (seq, delta);
        }

        /// <summary>
        /// After applying a reconciled state, the game may not reproduce it exactly (an item that no longer
        /// exists, a capped value). Rebasing on what the party really looks like stops those leftovers from
        /// being sent back as if the player had made them.
        /// </summary>
        public void Rebase(IReadOnlyDictionary<string, int> actual)
        {
            _baseline = actual.ToDictionary(p => p.Key, p => p.Value);
        }

        /// <summary>
        /// Host state that includes our deltas up to <paramref name="ackSeq"/>. Returns what the local party
        /// should now look like: that state plus our not-yet-acknowledged changes.
        /// </summary>
        public Dictionary<string, int> Reconcile(int ackSeq, IReadOnlyDictionary<string, int> authoritative)
        {
            _unacked.RemoveAll(u => u.Seq <= ackSeq);
            var predicted = authoritative.ToDictionary(p => p.Key, p => p.Value);
            foreach (var pending in _unacked)
                predicted = Ledger.Add(predicted, pending.Delta);
            _baseline = predicted;
            return predicted.ToDictionary(p => p.Key, p => p.Value);
        }
    }
}
