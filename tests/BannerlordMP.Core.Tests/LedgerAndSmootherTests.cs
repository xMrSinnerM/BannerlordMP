using System.Collections.Generic;
using BannerlordMP.Core.Sync;
using Xunit;

namespace BannerlordMP.Core.Tests
{
    public class LedgerAndSmootherTests
    {
        private static Dictionary<string, int> D(params (string k, int v)[] entries)
        {
            var d = new Dictionary<string, int>();
            foreach (var (k, v) in entries)
                d[k] = v;
            return d;
        }

        [Fact]
        public void DiffAndAddAreInverse()
        {
            var a = D(("g", 100), ("m:recruit", 10), ("i:grain", 3));
            var b = D(("g", 80), ("m:recruit", 12), ("m:archer", 2));
            var delta = Ledger.Diff(a, b);
            Assert.Equal(D(("g", -20), ("m:recruit", 2), ("m:archer", 2), ("i:grain", -3)), delta);
            Assert.Equal(b, Ledger.Add(a, delta));
        }

        [Fact]
        public void NothingSentBeforeHostStateOrWithoutChanges()
        {
            var ledger = new ClientLedger();
            Assert.Null(ledger.Capture(D(("g", 5))));

            ledger.Reconcile(0, D(("g", 5)));
            Assert.Null(ledger.Capture(D(("g", 5))));
        }

        [Fact]
        public void LocalChangeSurvivesAnOlderHostState()
        {
            var ledger = new ClientLedger();
            ledger.Reconcile(0, D(("g", 1000), ("m:recruit", 10)));

            // The player buys something for 200 gold.
            var sent = ledger.Capture(D(("g", 800), ("m:recruit", 10)));
            Assert.Equal(1, sent.Value.Seq);
            Assert.Equal(D(("g", -200)), sent.Value.Delta);

            // The host has not seen it yet but paid wages (-50): we must not lose the purchase.
            var predicted = ledger.Reconcile(0, D(("g", 950), ("m:recruit", 10)));
            Assert.Equal(750, predicted["g"]);
            Assert.Equal(1, ledger.UnackedCount);

            // Now the host acknowledges it.
            predicted = ledger.Reconcile(1, D(("g", 750), ("m:recruit", 10)));
            Assert.Equal(750, predicted["g"]);
            Assert.Equal(0, ledger.UnackedCount);
        }

        [Fact]
        public void HostOnlyChangesFlowToClient()
        {
            var ledger = new ClientLedger();
            ledger.Reconcile(0, D(("m:recruit", 10)));
            // A recruit died of... desertion on the host; the client had no pending changes.
            Assert.Equal(D(("m:recruit", 9)), ledger.Reconcile(0, D(("m:recruit", 9))));
            Assert.Null(ledger.Capture(D(("m:recruit", 9))));
        }

        [Fact]
        public void SmootherBlendsThenStops()
        {
            var smoother = new PositionSmoother(interval: 1.0, teleportDistance: 50);
            smoother.SetTarget("p", 0, 0, 10, 0, true, now: 0);
            var seen = new List<float>();
            smoother.Step(0.5, (id, x, y, land) => seen.Add(x));
            smoother.Step(1.0, (id, x, y, land) => seen.Add(x));
            smoother.Step(2.0, (id, x, y, land) => seen.Add(x));
            Assert.Equal(5f, seen[0]);
            Assert.Equal(10f, seen[1]);
            Assert.Equal(2, seen.Count); // finished after reaching the target
        }

        [Fact]
        public void SmootherTeleportsLongJumps()
        {
            var smoother = new PositionSmoother(interval: 1.0, teleportDistance: 5);
            smoother.SetTarget("p", 0, 0, 100, 0, true, now: 0);
            float x = -1;
            smoother.Step(0.1, (id, px, py, land) => x = px);
            Assert.Equal(100f, x);
        }
    }
}
