using Plankton;

namespace PieceSolver
{
    // The developed geometry of ONE piece (Free-float), keyed by its stable piece id — the per-Piece form of
    // the Dev chain (docs/specs/SOLVEDPIECE.md, realized by docs/specs/INCREMENTAL-SOLVE.md). A LEAF Transient:
    // a Pattern delta rots exactly the touched pieces (Pattern.Apply/Invert), so only changed pieces re-develop
    // on the next Solve. NOT registered downstream of Pattern — that wholesale cascade would rot every piece;
    // here the delta targets ids precisely.
    //
    // Rot() (inherited) marks stale but KEEPS the cached mesh + its input hash, so a stale piece whose develop
    // input is unchanged can REVALIDATE for free (tier-2, the input-hash escape hatch) instead of re-baking
    // (tier-3). A freshly-constructed SolvedPiece is born stale (IsFresh=false) with no bake — same observable
    // state as "rotted", so a new piece id always develops the first time.
    sealed class SolvedPiece : Transient
    {
        PlanktonMesh _mesh;                 // last successful bake's developed geometry; survives Rot (tier-2 reuse)
        public long BakedHash;              // input hash that produced _mesh; 0 until the first bake
        public double SlotX = double.NaN;   // layout slot (flat-panel x offset); NaN = unplaced. Held per id so an unchanged panel doesn't jump.

        public bool HasBake => _mesh != null;
        public bool Peek(out PlanktonMesh mesh) { mesh = _mesh; return IsFresh; }

        // Tier 3 — re-bake: store the freshly-developed mesh + the input hash that produced it, mark fresh.
        public void Bake(PlanktonMesh mesh, long hash) { _mesh = mesh; BakedHash = hash; IsFresh = true; }

        // Tier 2 — revalidate: the current input hash matched BakedHash, so _mesh is still valid; just mark fresh.
        public void Revalidate() { IsFresh = true; }
    }
}
