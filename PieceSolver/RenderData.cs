namespace PieceSolver
{
    // A typed DISPATCH TOKEN naming which MeshView upload path to call — NOT a re-abstraction of vertex
    // data (MeshView keeps owning GL + its three shader programs). A Real's Geometry yields one of these on
    // the UI thread; the actual GL Set*/Upload call stays staged in OnRender (rendering is PUSH).
    //
    // Fields are added per-increment as each path is wired (the full 3-case shape is in
    // docs/specs/NODE-MODEL-IMPL.md): I1 wires only Lines; I2 adds the Pieces arrays
    // (pos/nrm/col/dist/edge); the Mesh path (PlanktonMesh + posOverride) lands with its adopter.
    enum RenderKind { Mesh, Pieces, Lines }

    sealed class RenderData
    {
        public RenderKind Kind;
        public float[] Segments;   // Lines -> MeshView.SetCreases / SetSeams
    }
}
