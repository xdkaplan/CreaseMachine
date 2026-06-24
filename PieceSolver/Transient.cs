using System;

namespace PieceSolver
{
    // A Transient: a value DERIVED from Real state, cached with a freshness flag and (optionally) its own
    // regen. Fresh = the cache is valid; Stale = a dependency changed and it must be rebuilt. Two flavours:
    //   PULL  — constructed with a regen func; `Value` lazily rebuilds when Stale.
    //   PUSH  — constructed without one; an external producer (e.g. an async bake) calls `Set`; readers gate
    //           on `IsFresh` / `TryGet`.
    // Never aliases Real — it is computed-FROM it. `Clear` drops the cached value (compact save / free memory);
    // it rebuilds (pull) or is re-Set (push) on next need. See AGENTS.md (Real / Transient / Ephemeral).
    sealed class Transient<T>
    {
        readonly Func<T> _regen;   // null = PUSH (externally produced); non-null = PULL (self-regenerating)
        T _value;
        public bool IsFresh { get; private set; }
        public bool IsStale => !IsFresh;

        public Transient(Func<T> regen = null) { _regen = regen; }

        // PULL: rebuild if stale and we know how, then return. PUSH (no regen) returns the last Set value —
        // gate on IsFresh / TryGet first if "never set / cleared" matters.
        public T Value
        {
            get { if (IsStale && _regen != null) { _value = _regen(); IsFresh = true; } return _value; }
        }

        // PEEK the cache as-is and report its condition (IsFresh) — an INTENTIONAL leak in the abstraction:
        // it does NOT trigger a pull regen. Use when you explicitly want the current cache, or to avoid a
        // circular/expensive regen (e.g. a producer reading mid-build). A pull transient peeked while stale
        // hands back stale data — the bool is how you'd notice; ignore it only when you mean to.
        public bool Peek(out T value) { value = _value; return IsFresh; }

        public void Set(T value) { _value = value; IsFresh = true; }   // PUSH: an external producer fills it
        public void MarkStale() { IsFresh = false; }                   // a dependency changed -> rebuild on next need
        public void Clear() { _value = default; IsFresh = false; }     // drop the cache (compact save / free memory)
    }
}
