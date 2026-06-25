namespace PieceSolver
{
    // A typed DISPATCH TOKEN naming which MeshView upload path to call — NOT a re-abstraction of vertex
    // data (MeshView keeps owning GL + its three shader programs). A Real's Geometry yields one of these on
    // the UI thread; the actual GL Set*/Upload call stays staged in OnRender (rendering is PUSH).
    //
    // Fields are added per-increment as each path is wired (full shape: docs/specs/NODE-MODEL-IMPL.md):
    // I1 wired Lines; I2a adds the Pieces arrays (pos/nrm/col/dist/edge); the Mesh path
    // (PlanktonMesh + posOverride) lands with its adopter.
    enum RenderKind { Mesh, Pieces, Lines }

    sealed class RenderData
    {
        public RenderKind Kind;
        public float[] Pos, Nrm, Col, Dist, Edge;   // Pieces -> MeshView.SetPieces (5 parallel arrays; Dist+Edge drive PIECE_FRAG)
        public float[] Segments;                     // Lines  -> MeshView.SetCreases / SetSeams
    }
}
