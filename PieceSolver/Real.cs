namespace PieceSolver
{
    // The authored node (DOC-SPEC: a Real lives in the ownership TREE, never stale, undo via its Store).
    // A Real optionally owns a geometry Transient the View pulls + stages (null => nothing to draw — the
    // View just no-ops). Adopter: the partition Pattern (its Pieces SPLIT buffer + the Grown CreaseLines wire
    // overlay derived from CreaseMap). Still ahead: the ownership tree (Parent/Children), per-piece identity +
    // Crease-with-identity (I4) — see docs/specs/NODE-MODEL-IMPL.md.
    abstract class Real : Node   // a Real is a refresh-graph node: it can own downstream Transients + originate a rot
    {
        public abstract string Name { get; }

        // Optional viewable geometry — a Transient the View pulls (Peek) and stages via OnRender. null =>
        // nothing to draw. (Parent/Children come with I2, when the tree has more than one node.)
        public virtual Transient<RenderData> Geometry => null;

        // The rule-1 cascade origin: a Real mutated its authored state -> rot everything downstream. Provided
        // once on the base so no Store coins its own hook (see docs/specs/DOC-SPEC.md §5). Symmetric with the
        // other two rot-origins, Transient.Rot() and Transient.Supply().
        public void Invalidate() => RotDownstream();
    }
}
