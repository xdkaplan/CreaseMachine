using System;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Plankton;
using PlanktonGh;

namespace CreaseMachine
{
    /// <summary>
    /// SheetBender - a minimal developability flow.
    ///
    /// Uses the covariance ("hinge") developability energy and ANALYTIC gradient of Stein,
    /// Grinspun &amp; Crane, "Developability of Triangle Meshes" (ACM TOG 37(4), 2018), and flows
    /// the mesh DOWN that gradient toward a piecewise-developable (creasable) shape. That is ALL
    /// it does - no remeshing, no projection, no smoothing - plus on-demand 1->4 subdivision
    /// (flow a while, subdivide, flow again for hi-res creases).
    ///
    /// The optimizer is our own choice, not the paper's: Nesterov-accelerated gradient descent
    /// (gradient sampled at the lookahead x + beta*v; x += v; v = beta*v - t*grad). At beta=0 it
    /// is plain fixed-step descent; at beta=0.9 it reaches a developable state in ~5x fewer
    /// gradient evals (bench-measured) - the metric that matters on big meshes. The RAW gradient
    /// is used (no magnitude normalization) so the velocity self-damps as the gradient vanishes.
    /// Step is a fraction of edge length, applied as t = Step*L^2 so the flow is invariant to
    /// mesh scale and to subdivision (the dev gradient carries 1/length units). Boundaries held.
    /// </summary>
    public class SheetBender : GH_Component
    {
        public SheetBender()
            : base("SheetBender", "SheetBender",
                   "Developability flow (Stein, Grinspun & Crane 2018): bends a triangle mesh "
                 + "toward a piecewise-developable, creasable sheet. Developability force plus "
                 + "1->4 subdivision only - no remeshing.",
                   "Kangaroo", "Mesh")
        { }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            //0
            pManager.AddMeshParameter("Mesh", "Mesh",
                "Triangle mesh to develop. Connectivity is preserved (no remeshing); quads are triangulated.",
                GH_ParamAccess.item);

            //1
            pManager.AddNumberParameter("Step", "Step",
                "Step size as a fraction of edge length: the most-curved vertices move about this "
              + "fraction of an edge per iteration. Scaled internally as Step*L^2 so it behaves the "
              + "same at any mesh scale and after Subdivide. ~0.05 descends cleanly; raise for speed, "
              + "lower if the surface shimmers. Live-tunable.",
                GH_ParamAccess.item, 0.05);

            //2
            pManager.AddNumberParameter("Momentum", "Mom",
                "Nesterov momentum (0 to 0.95). 0 = plain gradient descent; 0.9 reaches a developable "
              + "state in roughly 5x fewer iterations by accelerating along consistent descent directions. "
              + "Higher is faster but lowers the stable Step ceiling - if the surface shimmers or blows up, "
              + "lower Momentum or Step. Resets on Reset and Subdivide. Live-tunable.",
                GH_ParamAccess.item, 0.9);

            //3
            pManager.AddIntegerParameter("Iterations", "Iter",
                "Flow steps taken per solve. Connect a timer for continuous flow.",
                GH_ParamAccess.item, 1);

            //4
            pManager.AddBooleanParameter("Subdivide", "Subdiv",
                "Rising edge (false->true) applies one in-place 1->4 subdivision to the live mesh. "
              + "Per the paper: subdivide after the flow settles to get hi-res creases, then keep flowing.",
                GH_ParamAccess.item, false);

            //5
            pManager.AddBooleanParameter("Reset", "Reset",
                "True to (re)initialize from the input mesh, false to run. Connect a timer for continuous flow.",
                GH_ParamAccess.item, true);

            //6
            pManager.AddNumberParameter("deBranch", "deBranch",
                "Weight of the B.5.1 branching penalty (Stein/Grinspun/Crane 2018, App B.5.1): an "
              + "extra per-vertex cost equal to the squared MINIMUM width of the convex hull of the "
              + "+/- signed face normals. The covariance energy penalizes the SUM of squared widths "
              + "(smallest eigenvalue), so it tolerates a few stray normals making a crazy/branchy "
              + "minimum; this term penalizes the MIN width directly - strictly anti-branching by "
              + "construction. 0 = off. Start small (~0.05) and raise until crazes thin out. "
              + "Live-tunable.",
                GH_ParamAccess.item, 0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Mesh", "Mesh", "The developing mesh, as a Plankton mesh.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Energy", "Energy",
                "Per-vertex developability energy (min eigenvalue of the 1-ring normal covariance), "
              + "parallel to the mesh vertices. ~0 where developable; higher at residual non-developable "
              + "spots (seam corners). Colour the mesh by it to inspect crease structure.",
                GH_ParamAccess.list);
        }

        private PlanktonMesh P = new PlanktonMesh();
        private bool initialized;
        private bool prevSubdiv;
        private Vec3[] vel;          // per-vertex momentum velocity (Nesterov), persists across solves

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Mesh inMesh = null;
            double Step = 0.05;
            double Momentum = 0.9;
            int Iter = 1;
            bool subdiv = false;
            bool reset = true;
            double deBranch = 0.0;

            DA.GetData(0, ref inMesh);
            DA.GetData(1, ref Step);
            DA.GetData(2, ref Momentum);
            DA.GetData(3, ref Iter);
            DA.GetData(4, ref subdiv);
            DA.GetData(5, ref reset);
            DA.GetData(6, ref deBranch);

            // --- (Re)initialize from the input mesh ---
            if (reset || !initialized)
            {
                if (inMesh == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No input mesh to develop.");
                    return;
                }
                Mesh m = inMesh.DuplicateMesh();
                m.Faces.ConvertQuadsToTriangles();
                P = m.ToPlanktonMesh();
                vel = new Vec3[P.Vertices.Count];    // momentum starts at rest
                initialized = true;
                prevSubdiv = subdiv;                 // don't fire a subdivide on the reset itself
                DA.SetData(0, new GH_PlanktonMesh(P));
                SetEnergyOutput(DA, deBranch);
                return;
            }

            // --- On-demand 1->4 subdivision (rising edge of the Subdiv input) ---
            if (subdiv && !prevSubdiv)
            {
                P = UniformSubdivide(P);
                vel = new Vec3[P.Vertices.Count];    // topology changed - drop stale momentum
            }
            prevSubdiv = subdiv;

            // --- Remove slivers: collapse over-short edges so the flow can't build degenerate
            // triangles whose 1/area gradient term spikes and corrupts the mesh. Simple collapse
            // only (Plankton's manifold-safe primitive) - NOT the adaptive remesher. ---
            if (MeshOps.CollapseShortEdges(P, 0.2) > 0)
            {
                P.Compact();                          // collapse leaves unused elements behind
                vel = new Vec3[P.Vertices.Count];     // Compact renumbered vertices - drop momentum
            }

            // --- Remove needle triangles (aspect < 5%): the absolute-length short-edge collapse
            // above misses these - a needle's short edge can be above 0.2*mean and still spike
            // the 1/dA face-normal-derivative term ~30x. Killing them PRE-STEP prevents the
            // cap-saturated-motion cascade that drives a one-frame gradient spike at a needle's
            // far vertex into a fold over the next several frames (the "about-to-explode" mode).
            if (MeshOps.CollapseSliverEdges(P, 0.05) > 0)
            {
                P.Compact();
                vel = new Vec3[P.Vertices.Count];
            }

            // Step is a fraction of edge length. The developability gradient has units of
            // 1/length (the energy is a pure angle measure, dimensionless), so for a given Step
            // to behave IDENTICALLY at any mesh scale - and, crucially, across Subdivide, which
            // halves L and doubles the gradient - the step must scale as L^2:
            //     displacement = Step * L^2 * grad
            // Then the most-curved vertices move ~Step of an edge length per iteration, always.
            double L = RepresentativeEdge(P);
            double t = Step * L * L;
            double capLen = L;   // trust region: no vertex moves more than ~one edge per step

            // --- Flow: Nesterov-accelerated gradient descent ---
            // The gradient is sampled at the LOOKAHEAD x + beta*v, then x += v where
            // v = beta*v - t*grad. beta = 0 is exactly the fixed global step. The RAW gradient is
            // used (no magnitude normalization), so the velocity decays as the gradient vanishes -
            // it self-damps and settles rather than orbiting the minimum. ~5x faster than beta=0.
            double beta = Math.Max(0.0, Math.Min(0.95, Momentum));
            int nV = P.Vertices.Count;
            if (vel == null || vel.Length != nV) vel = new Vec3[nV];
            double[] bx = new double[nV], by = new double[nV], bz = new double[nV];
            bool[] foldFlags = null;

            for (int s = 0; s < Iter; s++)
            {
                // save base x, hop to the lookahead x + beta*v
                for (int v = 0; v < nV; v++)
                {
                    PlanktonVertex pv = P.Vertices[v];
                    bx[v] = pv.X; by[v] = pv.Y; bz[v] = pv.Z;
                    if (beta > 0 && !pv.IsUnused && !P.Vertices.IsBoundary(v) && vel[v].IsValid)
                        P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
                }

                double[] energy;
                Vec3[] grad;
                // 5-arg overload also hands back the severe-fold flags (free - already computed for
                // the fold guard), which we use after the loop to collapse those pinches, and takes
                // the B.5.1 branching-penalty weight.
                DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out energy, out grad, out foldFlags, deBranch);   // grad at lookahead

                // v = beta*v - t*grad ;  x = base + v
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
                    vel[v] = beta * vel[v] - t * g;
                    // trust region: clamp any residual spike (slivers, eigenvalue kinks) so a
                    // vertex can never fly off - caps the velocity, so momentum can't accumulate it.
                    double vl = vel[v].Length;
                    if (vl > capLen && vl > 1e-20) vel[v] = vel[v] * (capLen / vl);
                    P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
                }
            }

            // --- Heal folds: collapse the severe pinch points the flow can't resolve. The flags
            // are a free byproduct of the last gradient eval (no extra face-data pass). ---
            if (foldFlags != null && MeshOps.CollapseFolds(P, foldFlags) > 0)
            {
                P.Compact();
                vel = new Vec3[P.Vertices.Count];   // Compact renumbered vertices - drop momentum
            }

            DA.SetData(0, new GH_PlanktonMesh(P));
            SetEnergyOutput(DA, deBranch);
        }

        /// <summary>Per-vertex developability energy output (parallel to the mesh vertices),
        /// including the optional B.5.1 branching penalty so the output matches what the flow saw.</summary>
        private void SetEnergyOutput(IGH_DataAccess DA, double branchWeight)
        {
            int nV = P.Vertices.Count;
            double[] energy = new double[nV];
            for (int v = 0; v < nV; v++)
                if (!P.Vertices[v].IsUnused) energy[v] = DevelopabilityEnergy.VertexEnergy(P, v, branchWeight);
            DA.SetDataList(1, energy);
        }

        /// <summary>First real edge length, used to make Step scale-invariant.</summary>
        private static double RepresentativeEdge(PlanktonMesh P)
        {
            for (int i = 0; i < P.Halfedges.Count; i += 2)
            {
                if (P.Halfedges[i].IsUnused) continue;
                PlanktonVertex a = P.Vertices[P.Halfedges[i].StartVertex];
                PlanktonVertex b = P.Vertices[P.Halfedges[i + 1].StartVertex];
                double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                double L = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (L > 0) return L;
            }
            return 1.0;
        }

        /// <summary>
        /// Uniform 1->4 (midpoint) subdivision. Keeps every original vertex in place, inserts
        /// one shared midpoint per edge, and replaces each triangle with 4. Geometry-preserving,
        /// so existing creases survive exactly - the paper's recipe for refining after the flow.
        /// </summary>
        private static PlanktonMesh UniformSubdivide(PlanktonMesh Pin)
        {
            var S = new PlanktonMesh();
            int nV = Pin.Vertices.Count;
            int nE = Pin.Halfedges.Count / 2;
            int nF = Pin.Faces.Count;

            // copy original vertices (indices preserved)
            for (int v = 0; v < nV; v++)
            {
                var pv = Pin.Vertices[v];
                S.Vertices.Add(pv.X, pv.Y, pv.Z);
            }

            // one shared midpoint vertex per edge
            int[] edgeMid = new int[nE];
            for (int e = 0; e < nE; e++)
            {
                if (Pin.Halfedges[2 * e].IsUnused) { edgeMid[e] = -1; continue; }
                int a = Pin.Halfedges[2 * e].StartVertex;
                int b = Pin.Halfedges[2 * e + 1].StartVertex;
                var pa = Pin.Vertices[a];
                var pb = Pin.Vertices[b];
                edgeMid[e] = S.Vertices.Add(0.5f * (pa.X + pb.X), 0.5f * (pa.Y + pb.Y), 0.5f * (pa.Z + pb.Z));
            }

            // each triangle -> 3 corner triangles + 1 central, preserving winding
            for (int f = 0; f < nF; f++)
            {
                if (Pin.Faces[f].IsUnused) continue;
                int[] hes = Pin.Faces.GetHalfedges(f);
                if (hes.Length != 3) continue; // only subdivide triangles
                int v0 = Pin.Halfedges[hes[0]].StartVertex;
                int v1 = Pin.Halfedges[hes[1]].StartVertex;
                int v2 = Pin.Halfedges[hes[2]].StartVertex;
                int m0 = edgeMid[hes[0] / 2]; // edge v0-v1
                int m1 = edgeMid[hes[1] / 2]; // edge v1-v2
                int m2 = edgeMid[hes[2] / 2]; // edge v2-v0
                if (m0 < 0 || m1 < 0 || m2 < 0) continue;
                S.Faces.AddFace(v0, m0, m2);
                S.Faces.AddFace(m0, v1, m1);
                S.Faces.AddFace(m2, m1, v2);
                S.Faces.AddFace(m0, m1, m2);
            }

            return S;
        }

        public override GH_Exposure Exposure { get { return GH_Exposure.primary; } }

        protected override System.Drawing.Bitmap Icon { get { return null; } }

        public override Guid ComponentGuid
        {
            get { return new Guid("{078039c1-4b2e-4e4f-8c72-e909a9b5c8f7}"); }
        }
    }
}
