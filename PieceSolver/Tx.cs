using System.Collections.Generic;

namespace PieceSolver
{
    // The transaction layer. A reversible change is an IDelta — opaque to the Doc (which only holds and
    // forwards it), concrete to the Store (which applies/inverts it). A delta is a list of Ops, each an
    // invertible atom. A Store wears ITxAble so the Doc can drive it. See docs/DOC-TX-REFACTOR.md.

    // Opaque to the Doc.
    interface IDelta { }

    // One invertible atom: face's piece label went From -> To.
    readonly struct Op
    {
        public readonly int Face, From, To;
        public Op(int face, int from, int to) { Face = face; From = from; To = to; }
    }

    // Pattern's delta: the per-face label changes. Concrete to the piece Store.
    sealed class PieceDelta : IDelta
    {
        public readonly List<Op> Ops;
        public PieceDelta(List<Op> ops) { Ops = ops; }
        public bool Empty => Ops == null || Ops.Count == 0;
    }

    // The contract a Store wears to take part in transactions. Driven by the Doc; the Doc is not ITxAble.
    interface ITxAble
    {
        void Apply(IDelta d);    // mutate Real forward, then regen Transient
        void Invert(IDelta d);   // mutate Real back,    then regen Transient
    }
}
