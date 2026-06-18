using System;
using Plankton;

namespace CreaseMachine
{
    /// <summary>
    /// Tunable inputs for one developability flow step. Plain value bag so the GH component,
    /// the headless CLI, and the GUI all feed the SAME flow with their own parameter sources
    /// (live sliders / ramps / fields).
    /// </summary>
    public struct FlowParams
    {
        public double Step;          // step size as a fraction of edge length (applied as Step*L^2)
        public double Momentum;      // Nesterov beta, clamped to [0, 0.95]
        public double deBranch;      // B.5.1 branching penalty weight
        public double deConsolidate; // B.2 consolidation penalty weight
        public double Sharpness;     // corner-preservation exponent
        public double deCraze;       // L1 dihedral sparsity weight
        public double CrazeBand;     // deCraze Huber flat-band, radians
        public double DetMix;        // lambda_min <-> det energy blend
        public bool   UseMaxCov;     // B.4 max-covariance toggle
        public int    MomFix;        // momentum-restart mode (1..4)
    }

    /// <summary>
    /// The ONE Rhino-free developability-flow implementation. Holds the live mesh + Nesterov
    /// velocity (+ optional per-vertex brush/freeze fields) and exposes the canonical flow step.
    /// The GH component (CreaseMachine.DoFlowStep), the CLI, and the GUI all drive this, so the
    /// intricate Nesterov+CHA+momentum-restart logic exists once and cannot drift between them.
    ///
    /// Collapse cadence is left to the caller (GH collapses once per multi-iter solve; the CLI
    /// collapses every step) because that is a legitimate per-host choice, not shared math.
    /// </summary>
    public sealed class FlowSession
    {
        public PlanktonMesh Mesh;
        public Vec3[] Vel;            // Nesterov velocity, persists across steps
        public double[] BrushWeights; // per-vertex additive deCraze boost; null = none (future: freeze field)

        // reused per-step scratch for the look-ahead base positions (resized on topology change)
        private double[] _bx, _by, _bz;

        public FlowSession() { }
        public FlowSession(PlanktonMesh mesh) { Load(mesh); }

        public void Load(PlanktonMesh mesh) { Mesh = mesh; Vel = new Vec3[mesh.Vertices.Count]; }
        public void ZeroMomentum() { Vel = new Vec3[Mesh.Vertices.Count]; }

        // Vel (and any brush field) are parallel to the vertex list; after a topology change
        // (collapse / subdivide / compact) they must be reset to the new vertex count.
        private void OnTopologyChanged()
        {
            int n = Mesh.Vertices.Count;
            Vel = new Vec3[n];
            if (BrushWeights != null) BrushWeights = new double[n];
        }

        /// <summary>Heal short edges; returns true if topology changed.</summary>
        public bool CollapseShort(double frac = 0.2)
        {
            if (MeshOps.CollapseShortEdges(Mesh, frac) > 0) { Mesh.Compact(); OnTopologyChanged(); return true; }
            return false;
        }

        /// <summary>Heal sliver edges; returns true if topology changed.</summary>
        public bool CollapseSliver(double aspectThresh = 0.05)
        {
            if (MeshOps.CollapseSliverEdges(Mesh, aspectThresh) > 0) { Mesh.Compact(); OnTopologyChanged(); return true; }
            return false;
        }

        /// <summary>Heal folded faces flagged by the last NesterovStep; returns true if topology changed.</summary>
        public bool HealFolds(bool[] foldFlags)
        {
            if (foldFlags != null && MeshOps.CollapseFolds(Mesh, foldFlags) > 0) { Mesh.Compact(); OnTopologyChanged(); return true; }
            return false;
        }

        /// <summary>
        /// One Nesterov-accelerated developability flow step on the current mesh: hop to the
        /// look-ahead x + beta*v, evaluate the analytic gradient (CHA) there, then apply
        /// v = beta*v - t*g with the optional per-vertex momentum restarts (MomFix) and the
        /// trust-region velocity cap. Boundaries/unused vertices are held fixed. Returns the
        /// max |grad| seen this step (diagnostic); `foldFlags` reports folded faces for HealFolds.
        ///
        /// This is the exact inner step the GH component used to inline in DoFlowStep.
        /// </summary>
        public double NesterovStep(FlowParams p, out bool[] foldFlags)
        {
            DevelopabilityEnergy.CrazeBand = p.CrazeBand;

            PlanktonMesh P = Mesh;
            int nV = P.Vertices.Count;
            if (Vel == null || Vel.Length != nV) Vel = new Vec3[nV];
            Vec3[] vel = Vel;
            if (_bx == null || _bx.Length != nV) { _bx = new double[nV]; _by = new double[nV]; _bz = new double[nV]; }
            double[] bx = _bx, by = _by, bz = _bz;

            double L = RepEdge(P);
            double t = p.Step * L * L;
            double capLen = L;
            double beta = Math.Max(0.0, Math.Min(0.95, p.Momentum));

            // save base x, hop to look-ahead x + beta*v (Nesterov)
            for (int v = 0; v < nV; v++)
            {
                PlanktonVertex pv = P.Vertices[v];
                bx[v] = pv.X; by[v] = pv.Y; bz[v] = pv.Z;
                if (beta > 0 && !pv.IsUnused && !P.Vertices.IsBoundary(v) && vel[v].IsValid)
                    P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
            }

            double[] energy; Vec3[] grad; bool[] degenVerts;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out energy, out grad, out foldFlags, out degenVerts,
                p.deBranch, p.deConsolidate, p.UseMaxCov, p.Sharpness, p.deCraze, true, BrushWeights, p.DetMix);

            double maxG = 0.0;
            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v))
                {
                    P.Vertices.SetVertex(v, bx[v], by[v], bz[v]);
                    continue;
                }
                Vec3 g = grad[v];
                if (!g.IsValid)
                {
                    vel[v] = Vec3.Zero;
                    P.Vertices.SetVertex(v, bx[v], by[v], bz[v]);
                    continue;
                }
                if ((p.MomFix == 2 || p.MomFix == 4) && beta > 0 && degenVerts[v]) vel[v] = Vec3.Zero;
                if ((p.MomFix == 3 || p.MomFix == 4) && beta > 0 && p.DetMix < 0.5 && (g * vel[v]) > 0.0) vel[v] = Vec3.Zero;
                vel[v] = beta * vel[v] - t * g;
                double vl = vel[v].Length;
                if (vl > capLen && vl > 1e-20) vel[v] = vel[v] * (capLen / vl);
                P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
                double gl = g.Length; if (gl > maxG) maxG = gl;
            }
            return maxG;
        }

        // First non-degenerate edge length; the scale that makes Step*L^2 scale/subdivision-invariant.
        private static double RepEdge(PlanktonMesh P)
        {
            for (int i = 0; i < P.Halfedges.Count; i += 2)
            {
                if (P.Halfedges[i].IsUnused) continue;
                PlanktonVertex a = P.Vertices[P.Halfedges[i].StartVertex];
                PlanktonVertex b = P.Vertices[P.Halfedges[i + 1].StartVertex];
                double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len > 0) return len;
            }
            return 1.0;
        }
    }
}
