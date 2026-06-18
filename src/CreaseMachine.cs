using System;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Plankton;
using PlanktonGh;

namespace CreaseMachine
{
    /// <summary>
    /// CreaseMachine - a minimal developability flow.
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
    public class CreaseMachine : GH_Component
    {
        public CreaseMachine()
            : base("CreaseMachine", "CreaseMachine",
                   "Developability flow (Stein, Grinspun & Crane 2018): bends a triangle mesh "
                 + "toward a piecewise-developable, creasable sheet. Developability force plus "
                 + "1->4 subdivision only - no remeshing. "
                 + "Build: " + BuildInfo.Date + " (" + BuildInfo.Hash + ")",
                   "Mesh", "CreaseMachine")
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

            //7
            pManager.AddNumberParameter("deConsolidate", "deConsolidate",
                "Weight of the B.2 combinatorial (consolidation) penalty (Stein/Grinspun/Crane 2018, "
              + "App B.2): per vertex i, the within-cluster pair-sum of face normals "
              + "Sum_{(s,t) in same cluster} |N_s - N_t|^2 over the BEST connected 2-partition of "
              + "St(i). The covariance lambda reads zero for ANY hinge - true clean creases and "
              + "smeared piecewise hinges alike - because both keep normals on a great circle; this "
              + "term penalises within-cluster SPREAD, so a piecewise-flat hinge reads 0 but a "
              + "subdivision-smeared hinge reads > 0. The flow descending it pulls within-patch "
              + "normals together while leaving between-cluster (real seam) differences untouched - "
              + "merges piecewise developability into global. 0 = off. Live-tunable.",
                GH_ParamAccess.item, 0.0);

            //8
            pManager.AddBooleanParameter("useMaxCov", "useMaxCov",
                "REPLACE the default sum-covariance (Eq 5, smaller eigenvalue of Sum_f theta N N^T) "
              + "with the B.4 MAX covariance lambda^max := min_{|u|=1} max_{ijk in St(i)} <u, N>^2. "
              + "Sum lets the flow spread small angular error in every direction, so rulings can "
              + "branch into V's along seams; MAX penalises the worst projection, forcing every "
              + "normal into a single 1D arc -> straight rulings. deBranch and deConsolidate remain "
              + "independent and still apply. Live-tunable.",
                GH_ParamAccess.item, false);

            //9
            pManager.AddNumberParameter("Sharpness", "Sharpness",
                "Corner-preservation exponent: per-vertex energy and gradient are multiplied by "
              + "w(d) = 1 / (1 + (d / (pi/4))^Sharpness), where d is the Gauss-Bonnet angle defect "
              + "(2*pi - sum of corner angles). At 0 the falloff is OFF and corners get pulled to "
              + "developable like everywhere else. Higher values preserve sharper junctions: at "
              + "Sharpness=2 a cube corner (d ~ pi/2) gets ~20% weight, at 4 (default) ~6%, at 6 "
              + "~1.5%, at 8 ~0.4%. Lower values reward smaller junctions more. Live-tunable.",
                GH_ParamAccess.item, 4.0);

            //10
            pManager.AddNumberParameter("deCraze", "deCraze",
                "Weight of the L1 dihedral sparsity penalty (Tibshirani 1996 lasso-style; mesh "
              + "adaptation per He & Schaefer 2013 'Mesh denoising via L0 minimization'): adds "
              + "|phi_e| * deCraze per interior edge (phi_e = unsigned dihedral). Unlike L2 forms "
              + "that smear small dihedrals everywhere, L1 is sparse-promoting - within-patch "
              + "edges drop their dihedral to zero (so adjacent patches merge into one) while "
              + "real seams keep theirs. Useful when piecewise-developable meshes craze into too "
              + "many sub-pieces. Corner-weighted by the Sharpness falloff so sharp junctions are "
              + "still preserved. 0 = off. Live-tunable.",
                GH_ParamAccess.item, 0.0);

            //11
            pManager.AddBooleanParameter("Running", "Running",
                "True = decouple compute from GH's solve cycle: the flow runs on a background "
              + "thread as fast as the engine can deliver, independent of how often the GH timer "
              + "ticks. Each SolveInstance just snapshots the current mesh + energy for display. "
              + "All other inputs (Step, weights, etc.) remain live-tunable - the worker reads "
              + "them on every iteration. False = legacy behavior: one flow step per GH solve.",
                GH_ParamAccess.item, false);

            //12
            pManager.AddNumberParameter("DetMix", "DetMix",
                "Continuous blend in [0, 1] between the paper-faithful lambda_min(M) energy and "
              + "the symmetric det(M_tangent) = lambda_min * lambda_max energy. "
              + "0 = pure lambda_min (Stein/Grinspun/Crane Eq 5); non-smooth at degenerate vertices "
              + "(icosahedron tips, symmetric quads) so the gradient there is direction-arbitrary "
              + "and the flow can twist. "
              + "1 = pure det; gradient combines both tangent eigenvectors so degenerate vertices "
              + "are basis-invariant and symmetry is preserved, but the energy is non-zero anywhere "
              + "there's any curvature so the flow pulls harder. "
              + "Intermediate values blend smoothly: small amounts add just enough symmetry to "
              + "kill twist on icosahedra / quads without changing the rest of the flow's character. "
              + "Try 0.05 - 0.2 as a starting point. Live-tunable.",
                GH_ParamAccess.item, 0.0);

            //13
            pManager.AddIntegerParameter("MomFix", "MomFix",
                "Momentum restart mode — controls how accumulated velocity is suppressed at "
              + "near-isotropic vertices where the gradient direction is direction-arbitrary:\n"
              + "1 = none (baseline paper behaviour — racks on geodesic spheres at iter ~27)\n"
              + "2 = DegenZeroMom: zero vel at vertices where eigenvalue separation sep<0.1 "
              + "(flags the isotropic poles; reduces collapses but racking onset ~unchanged)\n"
              + "3 = GradRestart: zero vel when dot(grad,vel)>0 AND detMix<0.5 — velocity is "
              + "heading uphill; delayed racking onset to ~61 on the geodesic sphere\n"
              + "4 = Combined: 2+3 (default)",
                GH_ParamAccess.item, 4);

            //14
            pManager.AddNumberParameter("CrazeBand", "CrazeBand",
                "deCraze smoothing band, in RADIANS (Huber). The deCraze L1 penalty is |phi| on the "
              + "unsigned dihedral, whose force holds CONSTANT magnitude as phi->0 and flips direction "
              + "across the flat (phi=0) cusp - that non-vanishing, reversing force is what makes "
              + "deCraze vibrate/jitter under momentum instead of flattening cleanly. CrazeBand "
              + "replaces |phi| with a quadratic below the band so the force tapers smoothly to 0 at "
              + "flat (near-flat edges SETTLE) while edges above the band keep the full L1 pull (real "
              + "creases are untouched). ~0.1 rad (~5.7 deg, default) calms the jitter while preserving "
              + "creases; raise toward 0.2-0.3 if it still buzzes, lower if real creases soften. "
              + "0 = off (original pure-L1 behaviour). Only active when deCraze > 0. Live-tunable.",
                GH_ParamAccess.item, 0.1);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Mesh", "Mesh", "The developing mesh, as a Plankton mesh.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Energy", "Energy",
                "Per-vertex developability energy (min eigenvalue of the 1-ring normal covariance), "
              + "parallel to the mesh vertices. ~0 where developable; higher at residual non-developable "
              + "spots (seam corners). Colour the mesh by it to inspect crease structure.",
                GH_ParamAccess.list);
            pManager.AddNumberParameter("BrushWeights", "Brush",
                "Per-vertex brush boost accumulated by Ctrl+LMB drag-paint, parallel to the mesh "
              + "vertices. 0 where untouched; up to the brush cap (~2.0) where painted. Colour the "
              + "mesh by it to see what you've painted in real time.",
                GH_ParamAccess.list);
        }

        private PlanktonMesh P = new PlanktonMesh();
        private bool initialized;
        private bool prevSubdiv;
        private Vec3[] vel;          // per-vertex momentum velocity (Nesterov), persists across solves

        // Background worker for Running=true: thread, lifecycle flag, mesh guard, shared params
        // for live-tuning while the worker iterates. meshLock guards both P and vel; sharedLock
        // guards every field in `shared`. SolveInstance updates shared on every tick, worker
        // reads it (under sharedLock) at the start of each iteration. solvePending throttles the
        // worker -> GH redraw signal: at most one solve is in flight at a time, so a fast worker
        // can't flood the UI thread with queued recomputes.
        // brushCallback is active while Running=true: Ctrl+Left-Click in any Rhino viewport
        // perturbs the nearest vertex by 0.5*L along its vertex normal - proof-of-concept for the
        // real brush UX, before adding radius / falloff / weight maps.
        private System.Threading.Thread workerThread;
        private volatile bool runningFlag;
        private int solvePending;   // Interlocked-managed 0/1
        private readonly object meshLock = new object();
        private readonly object sharedLock = new object();
        private readonly SharedParams shared = new SharedParams();
        private BrushMouseCallback brushCallback;
        // Per-vertex brush boost - additive deCraze contribution painted by the user. Cleared on
        // Reset and reallocated after subdivide / collapse (which renumber vertices). Each Ctrl+
        // LMB drag deposit adds to it; saturates at BRUSH_WEIGHT_CAP per vertex.
        private double[] brushWeights;
        private bool ctrlLeftDown;   // true while the user holds Ctrl+LMB for drag-paint
        private const double BRUSH_RADIUS_EDGES = 8.0;
        private const double BRUSH_DEPOSIT      = 0.25;
        private const double BRUSH_WEIGHT_CAP   = 2.0;

        private sealed class SharedParams
        {
            public double Step;
            public double Momentum;
            public int Iter;
            public double deBranch;
            public double deConsolidate;
            public bool useMaxCov;
            public double sharpness;
            public double deCraze;
            public double detMix;
            public int momFix;
            public double crazeBand;
            public bool subdivRequest;   // edge-triggered: SolveInstance sets, worker consumes
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Mesh inMesh = null;
            double Step = 0.05;
            double Momentum = 0.9;
            int Iter = 1;
            bool subdiv = false;
            bool reset = true;
            double deBranch = 0.0;
            double deConsolidate = 0.0;
            bool useMaxCov = false;
            double sharpness = 4.0;
            double deCraze = 0.0;
            bool running = false;
            double detMix = 0.0;
            int momFix = 4;
            double crazeBand = 0.1;

            DA.GetData(0, ref inMesh);
            DA.GetData(1, ref Step);
            DA.GetData(2, ref Momentum);
            DA.GetData(3, ref Iter);
            DA.GetData(4, ref subdiv);
            DA.GetData(5, ref reset);
            DA.GetData(6, ref deBranch);
            DA.GetData(7, ref deConsolidate);
            DA.GetData(8, ref useMaxCov);
            DA.GetData(9, ref sharpness);
            DA.GetData(10, ref deCraze);
            DA.GetData(11, ref running);
            DA.GetData(12, ref detMix);
            DA.GetData(13, ref momFix);
            DA.GetData(14, ref crazeBand);
            if (detMix < 0) detMix = 0; else if (detMix > 1) detMix = 1;
            if (momFix < 1) momFix = 1; else if (momFix > 4) momFix = 4;
            if (crazeBand < 0) crazeBand = 0; else if (crazeBand > Math.PI) crazeBand = Math.PI;
            // Static config read by both the flow's analytic gradient and EmitSnapshot's energy.
            DevelopabilityEnergy.CrazeBand = crazeBand;

            // --- (Re)initialize from the input mesh ---
            if (reset || !initialized)
            {
                StopWorker();   // worker can't survive a topology rebuild
                if (inMesh == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No input mesh to develop.");
                    return;
                }
                Mesh m = inMesh.DuplicateMesh();
                m.Faces.ConvertQuadsToTriangles();
                lock (meshLock)
                {
                    P = m.ToPlanktonMesh();
                    vel = new Vec3[P.Vertices.Count];    // momentum starts at rest
                    brushWeights = new double[P.Vertices.Count];   // brush state starts blank
                }
                initialized = true;
                prevSubdiv = subdiv;                 // don't fire a subdivide on the reset itself
                EmitSnapshot(DA, deBranch, deConsolidate, useMaxCov, sharpness, deCraze);
                return;
            }

            // Update shared params so the worker (or this tick's inline flow) sees current values.
            lock (sharedLock)
            {
                shared.Step = Step;
                shared.Momentum = Momentum;
                shared.Iter = Iter;
                shared.deBranch = deBranch;
                shared.deConsolidate = deConsolidate;
                shared.useMaxCov = useMaxCov;
                shared.sharpness = sharpness;
                shared.deCraze = deCraze;
                shared.detMix = detMix;
                shared.momFix = momFix;
                shared.crazeBand = crazeBand;
                if (subdiv && !prevSubdiv) shared.subdivRequest = true;
            }
            prevSubdiv = subdiv;

            // Worker lifecycle: start on Running edge-true, stop on Running edge-false. While
            // running=true we DO NOT run the flow inline - the worker owns the mesh under meshLock.
            if (running && !runningFlag) StartWorker();
            else if (!running && runningFlag) StopWorker();

            if (!running)
            {
                // Legacy single-step path: SolveInstance runs one flow step under meshLock.
                lock (meshLock) { DoFlowStep(); }
            }

            EmitSnapshot(DA, deBranch, deConsolidate, useMaxCov, sharpness, deCraze);
        }

        /// <summary>One full Nesterov flow step: optional subdivision, sliver collapses, Iter
        /// inner steps of (save base, hop to lookahead, CHA, apply v + cap, write back), then
        /// fold heal. Called under meshLock from either SolveInstance (Running=false) or the
        /// background worker (Running=true). Reads parameters from `shared` so live-tuning while
        /// the worker iterates Just Works.</summary>
        private void DoFlowStep()
        {
            // Snapshot current params under sharedLock to avoid holding it through the whole step.
            double Step, Momentum, deBranch, deConsolidate, sharpness, deCraze, detMix, crazeBand;
            int Iter, momFix;
            bool useMaxCov, subdivRequest;
            lock (sharedLock)
            {
                Step = shared.Step;
                Momentum = shared.Momentum;
                Iter = shared.Iter;
                deBranch = shared.deBranch;
                deConsolidate = shared.deConsolidate;
                useMaxCov = shared.useMaxCov;
                sharpness = shared.sharpness;
                deCraze = shared.deCraze;
                detMix = shared.detMix;
                momFix = shared.momFix;
                crazeBand = shared.crazeBand;
                subdivRequest = shared.subdivRequest;
                shared.subdivRequest = false;
            }
            // Worker thread reads CrazeBand via this static; refresh it from the snapshot each step
            // so live-tuning the slider while Running=true takes effect on the next iteration.
            DevelopabilityEnergy.CrazeBand = crazeBand;

            if (subdivRequest)
            {
                P = UniformSubdivide(P);
                vel = new Vec3[P.Vertices.Count];
                brushWeights = new double[P.Vertices.Count];   // indices renumbered - blank paint
            }

            if (MeshOps.CollapseShortEdges(P, 0.2) > 0)
            {
                P.Compact();
                vel = new Vec3[P.Vertices.Count];
                brushWeights = new double[P.Vertices.Count];
            }
            if (MeshOps.CollapseSliverEdges(P, 0.05) > 0)
            {
                P.Compact();
                vel = new Vec3[P.Vertices.Count];
                brushWeights = new double[P.Vertices.Count];
            }

            double L = RepresentativeEdge(P);
            double t = Step * L * L;
            double capLen = L;

            double beta = Math.Max(0.0, Math.Min(0.95, Momentum));
            int nV = P.Vertices.Count;
            if (vel == null || vel.Length != nV) vel = new Vec3[nV];
            double[] bx = new double[nV], by = new double[nV], bz = new double[nV];
            bool[] foldFlags = null;

            for (int s = 0; s < Iter; s++)
            {
                for (int v = 0; v < nV; v++)
                {
                    PlanktonVertex pv = P.Vertices[v];
                    bx[v] = pv.X; by[v] = pv.Y; bz[v] = pv.Z;
                    if (beta > 0 && !pv.IsUnused && !P.Vertices.IsBoundary(v) && vel[v].IsValid)
                        P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
                }

                double[] energy;
                Vec3[] grad;
                // brushWeights = per-vertex additive deCraze boost. Null skips the brush term in CHA.
                bool[] degenVerts;
                DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out energy, out grad, out foldFlags, out degenVerts,
                    deBranch, deConsolidate, useMaxCov, sharpness, deCraze, true, brushWeights, detMix);

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
                    if ((momFix == 2 || momFix == 4) && beta > 0 && degenVerts[v]) vel[v] = Vec3.Zero;
                    if ((momFix == 3 || momFix == 4) && beta > 0 && detMix < 0.5 && (g * vel[v]) > 0.0) vel[v] = Vec3.Zero;
                    vel[v] = beta * vel[v] - t * g;
                    double vl = vel[v].Length;
                    if (vl > capLen && vl > 1e-20) vel[v] = vel[v] * (capLen / vl);
                    P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
                }
            }

            if (foldFlags != null && MeshOps.CollapseFolds(P, foldFlags) > 0)
            {
                P.Compact();
                vel = new Vec3[P.Vertices.Count];
                brushWeights = new double[P.Vertices.Count];
            }
        }

        /// <summary>Deep-copy the live mesh + brush state under meshLock, then compute energy on
        /// the snapshot (outside the lock) so the worker can keep iterating while GH formats the
        /// output. Brush snapshot is shallow (just the double[]) - cheap.</summary>
        private void EmitSnapshot(IGH_DataAccess DA, double branchWeight, double consolidateWeight, bool useMaxCov, double sharpness, double crazeWeight)
        {
            PlanktonMesh snapshot;
            double[] brushSnap;
            lock (meshLock)
            {
                snapshot = new PlanktonMesh(P);
                int nV = snapshot.Vertices.Count;
                brushSnap = new double[nV];
                if (brushWeights != null && brushWeights.Length == nV)
                    System.Array.Copy(brushWeights, brushSnap, nV);
            }
            DA.SetData(0, new GH_PlanktonMesh(snapshot));
            double[] energy;
            bool[] foldDiscard;
            DevelopabilityEnergy.ComputeHingeEnergy(snapshot, out energy, out foldDiscard,
                branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight);
            DA.SetDataList(1, energy);
            DA.SetDataList(2, brushSnap);
        }

        private void StartWorker()
        {
            runningFlag = true;
            if (brushCallback == null)
            {
                brushCallback = new BrushMouseCallback(this);
                brushCallback.Enabled = true;
            }
            workerThread = new System.Threading.Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "CreaseMachine.Worker",
            };
            workerThread.Start();
        }

        private void StopWorker()
        {
            runningFlag = false;
            if (brushCallback != null)
            {
                brushCallback.Enabled = false;
                brushCallback = null;
            }
            if (workerThread != null)
            {
                workerThread.Join(2000);   // bounded wait - one CHA call is ~80ms worst-case
                workerThread = null;
            }
        }

        /// <summary>Drag-paint brush: deposit deCraze boost into brushWeights[] within a 3D radius
        /// of the camera ray, with cubic falloff (smooth at the edge). Runs on the UI thread;
        /// brushWeights is read by the worker on its next CHA call, so a fast drag deposits across
        /// several iterations of the flow. Painted regions get edgeCraze = global + brush, so the
        /// flow normal-smooths there much harder than the rest of the mesh.</summary>
        private void HandleBrushPaint(Rhino.Geometry.Line cameraRay)
        {
            lock (meshLock)
            {
                int nV = P.Vertices.Count;
                if (nV == 0) return;
                if (brushWeights == null || brushWeights.Length != nV) brushWeights = new double[nV];

                double L = RepresentativeEdge(P);
                double radius = BRUSH_RADIUS_EDGES * L;
                double radius2 = radius * radius;

                int painted = 0;
                for (int v = 0; v < nV; v++)
                {
                    PlanktonVertex pv = P.Vertices[v];
                    if (pv.IsUnused) continue;
                    Rhino.Geometry.Point3d pt = new Rhino.Geometry.Point3d(pv.X, pv.Y, pv.Z);
                    Rhino.Geometry.Point3d cp = cameraRay.ClosestPoint(pt, false);
                    double dx = cp.X - pt.X, dy = cp.Y - pt.Y, dz = cp.Z - pt.Z;
                    double d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 >= radius2) continue;
                    // Cubic-smooth falloff: w(d) = (1 - (d/r)^2)^2, smooth at the boundary so the
                    // brush edge doesn't create a discontinuity in the painted deCraze field.
                    double t = 1.0 - d2 / radius2;
                    double w = t * t;
                    brushWeights[v] += BRUSH_DEPOSIT * w;
                    if (brushWeights[v] > BRUSH_WEIGHT_CAP) brushWeights[v] = BRUSH_WEIGHT_CAP;
                    painted++;
                }
                if (painted > 0)
                {
                    Rhino.RhinoApp.WriteLine("CreaseMachine brush: " + painted +
                        " verts painted (radius=" + radius.ToString("F3") + ")");
                }
            }
        }

        /// <summary>Captures Ctrl+Left-drag in any Rhino viewport while the worker is running and
        /// paints brushWeights for the deCraze boost. MouseDown starts the stroke, MouseMove
        /// continues it (gated on owner.ctrlLeftDown so a drag without Ctrl modifier doesn't paint),
        /// MouseUp ends it. Other clicks pass through unmodified so navigation keeps working.</summary>
        private sealed class BrushMouseCallback : Rhino.UI.MouseCallback
        {
            private readonly CreaseMachine owner;
            public BrushMouseCallback(CreaseMachine o) { owner = o; }

            protected override void OnMouseDown(Rhino.UI.MouseCallbackEventArgs e)
            {
                if (e.MouseButton != Rhino.UI.MouseButton.Left) return;
                if ((System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Control) == 0) return;
                if (e.View == null) return;
                Rhino.Geometry.Line ray;
                if (!TryGetRay(e, out ray)) return;
                owner.ctrlLeftDown = true;
                owner.HandleBrushPaint(ray);
                e.Cancel = true;
            }

            protected override void OnMouseMove(Rhino.UI.MouseCallbackEventArgs e)
            {
                if (!owner.ctrlLeftDown) return;
                // If user lifted Ctrl mid-drag without releasing the button, stop painting.
                if ((System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Control) == 0) return;
                if (e.View == null) return;
                Rhino.Geometry.Line ray;
                if (!TryGetRay(e, out ray)) return;
                owner.HandleBrushPaint(ray);
                e.Cancel = true;
            }

            protected override void OnMouseUp(Rhino.UI.MouseCallbackEventArgs e)
            {
                if (e.MouseButton != Rhino.UI.MouseButton.Left) return;
                if (!owner.ctrlLeftDown) return;
                owner.ctrlLeftDown = false;
                Rhino.RhinoApp.WriteLine("CreaseMachine brush: stroke ended");
            }

            private static bool TryGetRay(Rhino.UI.MouseCallbackEventArgs e, out Rhino.Geometry.Line ray)
            {
                ray = default(Rhino.Geometry.Line);
                Rhino.Display.RhinoViewport vp = e.View.ActiveViewport;
                if (vp == null) return false;
                return vp.GetFrustumLine(e.ViewportPoint.X, e.ViewportPoint.Y, out ray);
            }
        }

        private void WorkerLoop()
        {
            try
            {
                while (runningFlag)
                {
                    lock (meshLock) { DoFlowStep(); }
                    // Ask GH to re-run SolveInstance on this component so the output mesh and
                    // energy update in the viewport. Without this nudge, no Timer = no recompute
                    // = the user sees the input mesh forever even though the worker is iterating.
                    RequestRedraw();
                }
            }
            catch
            {
                // Swallow worker exceptions - we don't want a transient mesh issue to kill the
                // thread silently AND keep runningFlag asserted. Clearing it lets SolveInstance
                // restart the worker on the next Running edge.
                runningFlag = false;
            }
        }

        /// <summary>Ask GH to re-solve this component (from the worker thread). Coalesces: if a
        /// solve is already queued, this is a no-op - so a worker faster than the UI thread can
        /// process won't pile up an unbounded redraw queue. The flag clears inside the callback,
        /// which only runs after GH has actually picked up the request.</summary>
        private void RequestRedraw()
        {
            if (System.Threading.Interlocked.Exchange(ref solvePending, 1) != 0) return;
            var doc = OnPingDocument();
            if (doc == null)
            {
                System.Threading.Interlocked.Exchange(ref solvePending, 0);
                return;
            }
            doc.ScheduleSolution(1, d =>
            {
                System.Threading.Interlocked.Exchange(ref solvePending, 0);
                ExpireSolution(false);
            });
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            StopWorker();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close || context == GH_DocumentContext.Unloaded)
                StopWorker();
            base.DocumentContextChanged(document, context);
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
