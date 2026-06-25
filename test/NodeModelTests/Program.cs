using System;
using PieceSolver;

namespace NodeModelTests
{
    // A test-local Real (CreaseOverlay is slated for deletion, so don't lean on it).
    sealed class FakeReal : Real
    {
        public override string Name => "Fake";
    }

    static class Program
    {
        static void Assert(bool cond, string msg)
        {
            if (!cond) throw new Exception("FAIL: " + msg);
        }

        static int Main()
        {
            try
            {
                GrownGrowsOnReadCachesRegrows();
                SupplyCascadesDownstream();
                RealInvalidateCascades();
                IdempotentShortCircuitAndDiamondTerminates();

                Console.WriteLine("ALL PASS");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return 1;
            }
        }

        // Grown grows-on-read + caches + regrows after Rot.
        static void GrownGrowsOnReadCachesRegrows()
        {
            var grows = 0;
            var t = new Transient<int>(() => { grows++; return 7; });

            Assert(t.Value == 7 && grows == 1, "grown grows once on first read -> 7");
            Assert(t.Value == 7 && grows == 1, "grown caches: second read does not regrow");

            t.Rot();
            Assert(t.Value == 7 && grows == 2, "grown regrows after Rot");
        }

        // Supply cascades a rot downstream; the supplier itself ends fresh.
        static void SupplyCascadesDownstream()
        {
            var up = new Transient<int>();             // SUPPLIED
            var down = new Transient<int>(() => 0);     // GROWN
            up.AddDownstream(down);

            _ = down.Value;                              // down now fresh
            Assert(down.IsFresh, "precondition: down is fresh after read");

            up.Supply(5);
            Assert(down.IsStale, "Supply rotted the downstream");
            Assert(up.IsFresh, "Supply left the supplier fresh");
        }

        // Real.Invalidate flows a rot to its downstreams.
        static void RealInvalidateCascades()
        {
            var r = new FakeReal();
            var d = new Transient<int>(() => 0);
            r.AddDownstream(d);

            _ = d.Value;                                // d now fresh
            Assert(d.IsFresh, "precondition: d is fresh after read");

            r.Invalidate();
            Assert(d.IsStale, "Real.Invalidate rotted the downstream");
        }

        // Idempotent short-circuit + a DAG diamond terminates (no re-flood / infinite loop).
        //   root(Real) -> A, root -> B, A -> D, B -> D
        static void IdempotentShortCircuitAndDiamondTerminates()
        {
            var root = new FakeReal();
            var a = new Transient<int>(() => 1);
            var b = new Transient<int>(() => 2);
            var d = new Transient<int>(() => 3);

            root.AddDownstream(a);
            root.AddDownstream(b);
            a.AddDownstream(d);
            b.AddDownstream(d);

            // Freshen the whole sub-DAG by reading.
            _ = a.Value; _ = b.Value; _ = d.Value;
            Assert(a.IsFresh && b.IsFresh && d.IsFresh, "precondition: diamond all fresh");

            root.Invalidate();                          // floods A, B, and D (twice via the diamond)
            Assert(a.IsStale && b.IsStale, "diamond: A and B rotted");
            Assert(d.IsStale, "diamond: D rotted (reached via both arms, terminates)");

            // Re-rotting an already-stale node is a harmless no-op.
            d.Rot();
            Assert(d.IsStale, "Rot on an already-stale node is a no-op");
        }
    }
}
