using Plankton;

namespace PieceSolver
{
    // The developed result of ONE piece (Free-float), keyed by its stable piece id — the per-Piece form of the
    // Dev chain (docs/specs/SOLVEDPIECE.md, realized by docs/specs/INCREMENTAL-SOLVE.md). A LEAF Transient: a
    // Pattern delta rots exactly the touched pieces (Pattern.Apply/Invert), so only changed pieces re-develop on
    // the next Solve. NOT registered downstream of Pattern — that wholesale cascade would rot every piece.
    //
    // Caches the expensive per-panel work (BFF flatten + LM develop + Dev2PQ): the developed 3D mesh, the 2D flat
    // panel, the input hash that produced them, and the panel strain. Rot() (inherited) marks stale but KEEPS the
    // cache, so a stale piece whose develop input is unchanged REVALIDATES for free (tier 2, the input-hash
    // escape hatch) instead of re-baking (tier 3). Born stale with no bake -> a new piece id always develops.
    sealed class SolvedPiece : Transient
    {
        PlanktonMesh _mesh;                 // last bake's developed 3D result (may be a Dev2PQ remesh); survives Rot
        PlanktonMesh _flat;                 // last bake's 2D flat panel (BFF), for the laid-out pattern
        public long BakedHash;              // input hash (face-set + Solve-param salt) that produced the cache; 0 until first bake
        public double Strain;               // panel worst strain % at last bake (for the summary on reuse)
        public double SlotX = double.NaN;   // layout slot (flat-panel x offset); NaN = unplaced. Held per id (panel stability — not yet wired)

        public bool HasBake => _mesh != null;
        public bool Peek(out PlanktonMesh mesh, out PlanktonMesh flat) { mesh = _mesh; flat = _flat; return IsFresh; }

        // Tier 3 — re-bake: store the freshly-developed 3D + flat + the input hash + strain, mark fresh.
        public void Bake(PlanktonMesh mesh, PlanktonMesh flat, long hash, double strain)
        { _mesh = mesh; _flat = flat; BakedHash = hash; Strain = strain; IsFresh = true; }

        // Tier 2 — revalidate: the current input hash matched BakedHash, so the cache is still valid; mark fresh.
        public void Revalidate() { IsFresh = true; }
    }
}
