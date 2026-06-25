namespace PieceSolver
{
    // The authored node (DOC-SPEC: a Real lives in the ownership TREE, never stale, undo via its Store).
    // A Real optionally owns a geometry Transient the View pulls + stages (null => nothing to draw — the
    // View just no-ops). Adopters so far: the crease overlay (I1) and the partition Pattern (I2a — its Pieces
    // SPLIT buffer). Still ahead: the ownership tree (Parent/Children), the rot cascade (I3), per-piece
    // identity + Crease-with-identity (I4) — see docs/specs/NODE-MODEL-IMPL.md.
    abstract class Real
    {
        public abstract string Name { get; }

        // Optional viewable geometry — a Transient the View pulls (Peek) and stages via OnRender. null =>
        // nothing to draw. (Parent/Children come with I2, when the tree has more than one node.)
        public virtual Transient<RenderData> Geometry => null;
    }

    // I1 adopter: the proposed-crease overlay as the first Real the View pulls. Taxonomy note — this overlay
    // geometry is *derived* (true Crease-with-identity Reals are the I4 gateway); it's used here to prove the
    // spine — Real.Geometry (Supplied) -> View Peeks -> stages MeshView.SetCreases — on an always-on,
    // non-occluding node that needs no DisplaySource change and touches no frozen layer.
    sealed class CreaseOverlay : Real
    {
        public override string Name => "Creases";
        readonly Transient<RenderData> _geo = new Transient<RenderData>();   // Supplied by MainWindow.SetCreasePts
        public override Transient<RenderData> Geometry => _geo;
    }
}
