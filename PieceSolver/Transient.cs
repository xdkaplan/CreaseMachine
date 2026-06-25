using System;
using System.Collections.Generic;

namespace PieceSolver
{
    // A node in the REFRESH GRAPH — the Real/Transient dependency DAG (docs/specs/DOC-SPEC.md §3, §5). It holds
    // its DOWNSTREAM edges (the Transients that derive from it) and flows a rot to them. Both Real (a source) and
    // Transient (a derived node) are Nodes; only Transients go stale, so downstreams are always Transients.
    // Upstream / Downstream is the DAG axis (Parent / Child belongs to the Real ownership tree).
    abstract class Node
    {
        readonly List<Transient> _downstream = new List<Transient>();

        // Register `d` as deriving FROM this node — an upstream→downstream edge. Idempotent.
        public void AddDownstream(Transient d) { if (d != null && !_downstream.Contains(d)) _downstream.Add(d); }

        // Flow a rot to everything downstream; each d.Rot() recurses, so one call floods the reachable sub-DAG.
        // This is the cascade ORIGIN for a Real mutation (a Real has no value to stale — it only propagates); a
        // Transient reaches the same call from Rot() / Supply().
        protected void RotDownstream() { for (int i = 0; i < _downstream.Count; i++) _downstream[i].Rot(); }
    }

    // The non-generic Transient face: freshness + the rot cascade. Generic value access lives in Transient<T>.
    abstract class Transient : Node
    {
        public bool IsFresh { get; protected set; }
        public bool IsStale => !IsFresh;

        // An upstream changed → go stale and flow the rot downstream. Idempotent: if already stale, the rot that
        // staled this node already flooded its sub-DAG, so stop (cheap; also avoids re-flooding DAG diamonds).
        public void Rot() { if (!IsFresh) return; IsFresh = false; RotDownstream(); }
    }

    // A Transient<T>: a value DERIVED from Real state, cached with a freshness flag. Fresh = the cache is valid;
    // Stale = an upstream changed and it must be refreshed before use. Two flavours, by AVAILABILITY to the reader:
    //   GROWN    — constructed with a grow func; it produces its own value ON READ. `.Value` grows it if stale
    //              (total — always has a value).
    //   SUPPLIED — constructed without one; it must be produced in advance by a producer that calls `Supply`.
    //              `.Value` can't make one, so readers `Peek` (partial — a Maybe; might not be ready yet).
    // Never aliases Real — it is computed-FROM it. See docs/specs/DOC-SPEC.md and AGENTS.md (Real / Transient).
    sealed class Transient<T> : Transient
    {
        readonly Func<T> _grow;    // null = SUPPLIED (producer-fed); non-null = GROWN (self-growing)
        T _value;

        public Transient(Func<T> grow = null) { _grow = grow; }

        // GROWN: grow if stale and we know how, then return. SUPPLIED (no grow func) returns the last Supplied
        // value — Peek first if "never supplied / cleared" matters. (Designed: a stale SUPPLIED read should throw.)
        // Growing does NOT rot downstreams: they were already rotted when THIS was rotted (DOC-SPEC §5).
        public T Value
        {
            get { if (IsStale && _grow != null) { _value = _grow(); IsFresh = true; } return _value; }
        }

        // PEEK the cache as-is and report its condition (IsFresh) — an INTENTIONAL leak in the abstraction:
        // it does NOT trigger a grow. Use when you explicitly want the current cache, to handle a SUPPLIED that
        // may not be ready, or to avoid a circular/expensive grow (a producer reading mid-build). A GROWN
        // transient peeked while stale hands back stale data — the bool is how you'd notice.
        public bool Peek(out T value) { value = _value; return IsFresh; }

        // SUPPLIED: a producer feeds the value → fresh self, then rot downstreams (the push rot-origin). Named
        // for the abstraction's role, not its body — the seat for invalidation / handlers.
        public void Supply(T value) { _value = value; IsFresh = true; RotDownstream(); }

        public void Clear() { _value = default; IsFresh = false; }     // drop the cache (compact save / free memory)
    }
}
