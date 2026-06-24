using System;

namespace PieceSolver
{
    // A Transient: a value DERIVED from Real state, cached with a freshness flag. Fresh = the cache is valid;
    // Stale = a parent changed and it must be refreshed before use. Two flavours, by AVAILABILITY to the reader:
    //   GROWN    — constructed with a grow func; it produces its own value ON READ. `.Value` grows it if stale
    //              (total — always has a value).
    //   SUPPLIED — constructed without one; it must be produced in advance by a producer that calls `Supply`.
    //              `.Value` can't make one, so readers `Peek` (partial — a Maybe; might not be ready yet).
    // Never aliases Real — it is computed-FROM it. See docs/specs/DOC-SPEC.md (the dependency-graph design) and
    // AGENTS.md (Real / Transient / Ephemeral).
    // NOTE: the wider graph (parent/child edges, the rot cascade, the `.Value`-throws-on-stale-Supplied
    // contract) is DESIGNED, not built here yet — this is just the single node.
    sealed class Transient<T>
    {
        readonly Func<T> _grow;    // null = SUPPLIED (producer-fed); non-null = GROWN (self-growing)
        T _value;
        public bool IsFresh { get; private set; }
        public bool IsStale => !IsFresh;

        public Transient(Func<T> grow = null) { _grow = grow; }

        // GROWN: grow if stale and we know how, then return. SUPPLIED (no grow func) returns the last Supplied
        // value — Peek first if "never supplied / cleared" matters. (Designed: a stale SUPPLIED read should throw.)
        public T Value
        {
            get { if (IsStale && _grow != null) { _value = _grow(); IsFresh = true; } return _value; }
        }

        // PEEK the cache as-is and report its condition (IsFresh) — an INTENTIONAL leak in the abstraction:
        // it does NOT trigger a grow. Use when you explicitly want the current cache, to handle a SUPPLIED that
        // may not be ready, or to avoid a circular/expensive grow (a producer reading mid-build). A GROWN
        // transient peeked while stale hands back stale data — the bool is how you'd notice.
        public bool Peek(out T value) { value = _value; return IsFresh; }

        // SUPPLIED: a producer feeds the value. Named for the abstraction's role, not its body — today a naked
        // setter, later the seat for invalidation / handlers. (Designed: also rot children.)
        public void Supply(T value) { _value = value; IsFresh = true; }

        public void Rot() { IsFresh = false; }                         // a parent changed -> go stale, refresh on next need
        public void Clear() { _value = default; IsFresh = false; }     // drop the cache (compact save / free memory)
    }
}
