using System;
using System.Collections.Generic;
using Plankton;

namespace CreaseMachine
{
    // Hinge (covariance) developability energy and its analytic gradient,
    // following Stein, Grinspun & Crane 2018. Rhino-free: uses only Plankton
    // for connectivity and the local Vec3 for math, so it can be unit-tested
    // (e.g. finite-difference gradient checks) against just Plankton.dll.
    // Per-call tick accumulators populated by the CHA blocks when CHAStats.Enabled is true.
    // Read by the test bench (PerfBench) to see where time actually goes inside CHA.
    // Stopwatch.Frequency tick units; convert to ms via ticks / Stopwatch.Frequency * 1000.
    public static class CHAStats
    {
        public static bool Enabled;
        public static long FacePrecomputeTicks;
        public static long VertNormalsTicks;
        public static long PerVertexLoopTicks;
        public static long L1Ticks;
        public static long GetVertexFacesTicks;   // cost of P.Vertices.GetVertexFaces per call (alloc)
        public static long GetFaceVertsTicks;     // cost of P.Faces.GetFaceVertices in face precompute (alloc)
        public static int  Calls;

        public static void Reset()
        {
            FacePrecomputeTicks = VertNormalsTicks = PerVertexLoopTicks = L1Ticks = GetVertexFacesTicks = GetFaceVertsTicks = 0;
            Calls = 0;
        }
    }

    public static class DevelopabilityEnergy
    {
        // EXPERIMENTAL (default OFF -> shipping behavior is byte-identical): adaptive per-vertex
        // DetMix. When enabled, the lambda_min/det blend `a` is raised toward 1 at vertices whose
        // tangent eigenvalues are near-degenerate (lambda_min ~ lambda_max), where the picked
        // eigenvector x_min is direction-arbitrary and injects a spurious tangential "force". A
        // real crease (lambda_min << lambda_max) keeps a ~ 0 (paper-faithful). Blend driver:
        //   sep = (lmax - lmin)/(lmax + lmin) in [0,1]   (0 = degenerate, 1 = well separated)
        //   a_degeneracy = (1 - sep)^AdaptiveDetMixPower
        //   a_effective = max(detMix, a_degeneracy)
        public static bool AdaptiveDetMix = false;
        public static double AdaptiveDetMixPower = 2.0;

        // EXPERIMENTAL (default OFF): harmonic-mean / det-over-trace developability energy
        //   E = lambda_min * lambda_max / (lambda_min + lambda_max)
        // This is the "mode 2" hinted at in TangentEigenpairs' comments. Unlike a linear DetMix
        // blend it is a FIXED smooth function of the eigenvalues, so its gradient
        //   dE/dl_min = l_max^2/(l_min+l_max)^2 ,  dE/dl_max = l_min^2/(l_min+l_max)^2
        // is EXACT through the existing two-pass (x_min, x_max) machinery (no frozen-coefficient
        // approximation). It -> lambda_min when l_min << l_max (creases like the paper energy) and
        // -> lambda/2 with basis-invariant gradient at degeneracy (l_min ~ l_max), so it removes
        // the arbitrary-eigenvector tangential "force" at symmetric vertices WITHOUT abandoning
        // creasing. Overrides the DetMix blend when enabled. NOT YET FD-bench-verified.
        public static bool HarmonicEnergy = false;

        // deCraze L1 smoothing band (radians). The deCraze penalty is |phi| on the UNSIGNED
        // dihedral, whose subgradient holds CONSTANT magnitude as phi -> 0 and reverses spatial
        // direction across the phi=0 cusp - a non-vanishing, direction-flipping force that makes
        // near-flat regions buzz/jitter under Nesterov momentum instead of settling. Huber-smoothing
        // replaces |phi| with a quadratic for phi < CrazeBand: the force then tapers linearly to 0 at
        // flat (so flat edges settle into a smooth well), while edges above the band keep the full
        // linear L1 pull (so real creases stay sparse and untouched). CrazeBand = 0 restores the
        // original pure-L1 behaviour exactly. Set per-solve from the component's CrazeBand input.
        public static double CrazeBand = 0.0;

        // Huber-smoothed value h(phi) and derivative h'(phi) of the deCraze L1 dihedral penalty.
        // With delta = CrazeBand: for phi < delta, h = phi^2/(2*delta) and h' = phi/delta (force
        // tapers to 0 at flat); for phi >= delta, h = phi - delta/2 and h' = 1 (full L1 pull).
        // C1-continuous at phi=delta. delta <= 0 -> pure L1 (h = phi, h' = 1), the original behaviour.
        private static double CrazeHuberVal(double phi)
        {
            double d = CrazeBand;
            if (d <= 0.0) return phi;
            return (phi < d) ? 0.5 * phi * phi / d : phi - 0.5 * d;
        }
        private static double CrazeHuberDeriv(double phi)
        {
            double d = CrazeBand;
            if (d <= 0.0) return 1.0;
            return (phi < d) ? phi / d : 1.0;
        }

        private static Vec3 Pos(PlanktonVertex v) { return new Vec3(v.X, v.Y, v.Z); }

        // Per-(vert, face) trig + frame snapshot shared between the covariance build loop and the
        // gradient loop in ComputeHingeEnergyAndGrad. The two used to recompute the same
        // Vec3.Angle / Vec3.Cross(Nv, Nf) / SafeAcos / Normalized / muvf / muff / Nfw for the same
        // (vert, face) pair; caching saves a few trig/sqrt ops per face per vertex.
        // hasGeom = false means Nv was parallel/antiparallel to Nf (sinPhi underflowed), so the
        // post-skip frame values are not set - gradient loop must check before reading.
        private struct FaceTrig
        {
            public double theta;
            public double sinPhi;
            public double cosPhi;
            public double phi;
            public Vec3 nuf;
            public Vec3 muvf;
            public Vec3 muff;
            public Vec3 Nfw;
            public bool hasGeom;
        }

        // Per-task scratch for the parallel per-vertex loop. Holds the hoisted per-call buffers
        // (faces / locIdx / trig / branchX / consPd / consRowPref / consTri) plus the thread-local
        // gradient accumulator gradLocal. localInit allocates one of these per task; the body
        // reads/writes through aliased locals; localFinally hands the gradLocal to the shared
        // reduce list. The scratch class is mutable so the body can re-grow branchX/consPd/etc.
        // and write the new references back before returning.
        private sealed class PerTaskScratch
        {
            public List<int> faces;
            public List<int> locIdx;
            public FaceTrig[] trig;
            public Vec3[] branchX;
            public double[] consPd;
            public double[] consRowPref;
            public double[] consTri;
            public Vec3[] gradLocal;
        }

        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad)
        {
            bool[] isFold;
            ComputeHingeEnergyAndGrad(P, out energy, out energyGrad, out isFold, 0.0);
        }

        // Overload that also reports which vertices are SEVERE folds (coherence < 0.05). The
        // coherence is already computed for the fold guard, so this costs nothing extra and lets
        // the caller collapse those vertices to heal the pinch (MeshOps.CollapseFolds) without
        // recomputing any face data - exactly the no-duplicate-pass integration we want.
        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad,
            out bool[] isFold)
        {
            ComputeHingeEnergyAndGrad(P, out energy, out energyGrad, out isFold, 0.0);
        }

        // 5-arg overload adds the branching (anti-crazing) penalty from Stein/Grinspun/Crane App
        // B.5.1: per vertex i, an extra cost psi_i := min_{|u|=1} max_{a,b in St(i)} <x_a - x_b, u>^2
        // where {x_*} are the +/- signed face normals. The covariance energy penalizes the SUM of
        // squared widths of the normal convex hull (smaller eigenvalue of M); psi penalizes its
        // MIN width - strictly anti-branching by construction. Set branchWeight = 0 to disable.
        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad,
            out bool[] isFold,
            double branchWeight)
        {
            ComputeHingeEnergyAndGrad(P, out energy, out energyGrad, out isFold, branchWeight, 0.0);
        }

        // 6-arg overload adds the COMBINATORIAL energy from Stein/Grinspun/Crane App B.2 - per
        // vertex i, the within-cluster pair-sum of face normals over the BEST connected 2-partition
        // of St(i):
        //     E_i^P := min_P  Sum_{(s,t) in same cluster of P} |N_s - N_t|^2
        // Where the covariance lambda (Eq 10) reads zero for ANY hinge - true clean creases and
        // smeared piecewise hinges alike, since both keep normals on a great circle - this penalises
        // within-cluster SPREAD. A perfectly piecewise-flat hinge has both clusters tight, so E=0;
        // a 1->4 subdivision that adds small mid-patch dihedrals scatters within-cluster normals,
        // so E > 0. The flow descending E_i^P pulls within-patch normals together while leaving
        // between-cluster differences (the real seams) untouched - the "merge piecewise into global
        // developability" force. Set consolidateWeight = 0 to disable.
        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad,
            out bool[] isFold,
            double branchWeight,
            double consolidateWeight)
        {
            ComputeHingeEnergyAndGrad(P, out energy, out energyGrad, out isFold, branchWeight, consolidateWeight, false);
        }

        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad,
            out bool[] isFold,
            double branchWeight,
            double consolidateWeight,
            bool useMaxCov)
        {
            ComputeHingeEnergyAndGrad(P, out energy, out energyGrad, out isFold, branchWeight, consolidateWeight, useMaxCov, 4.0, 0.0);
        }

        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad,
            out bool[] isFold,
            double branchWeight,
            double consolidateWeight,
            bool useMaxCov,
            double sharpness)
        {
            ComputeHingeEnergyAndGrad(P, out energy, out energyGrad, out isFold, branchWeight, consolidateWeight, useMaxCov, sharpness, 0.0);
        }

        // Energy-only entry point: caller wants per-vertex energy (and the fold flags, which are a
        // free byproduct) but NOT the analytic gradient. CHA's gradient-distribution phase is
        // roughly the same cost as its covariance build; skipping it cuts SetEnergyOutput's
        // per-solve cost ~50% (the gradient it would compute is just discarded).
        public static void ComputeHingeEnergy(
            PlanktonMesh P,
            out double[] energy,
            out bool[] isFold,
            double branchWeight,
            double consolidateWeight,
            bool useMaxCov,
            double sharpness,
            double crazeWeight)
        {
            Vec3[] gradDiscard;
            ComputeHingeEnergyAndGrad(P, out energy, out gradDiscard, out isFold,
                branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight, false);
        }

        // 9-arg overload adds the L1 dihedral sparsity penalty (Lasso-style, Tibshirani 1996;
        // mesh adaptation per He & Schaefer 2013 "Mesh denoising via L0 minimization"): per
        // interior edge e an additional cost |phi_e| * crazeWeight, where phi_e is the unsigned
        // dihedral angle. L1 rewards SPARSITY in dihedrals - flat-edge regions drop their
        // dihedral to exactly zero while real seams keep theirs, so small patches merge into
        // their neighbours. Each edge is corner-weighted by the average of its two endpoints'
        // cornerWeight so cube-style corners are not pulled flat. Set crazeWeight = 0 to disable.
        // wantGrad: when false, skip every gradient-distribution write (covariance grad, factorv
        // loop, max-cov M-term + triple sum, branching, consolidation, L1) - energy is still
        // populated. Use via ComputeHingeEnergy when only the energy array is wanted.
        // Backward-compatible 12-arg overload: callers that don't need degenVerts are unchanged.
        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad,
            out bool[] isFold,
            double branchWeight,
            double consolidateWeight,
            bool useMaxCov,
            double sharpness,
            double crazeWeight,
            bool wantGrad = true,
            double[] brushWeights = null,
            double detMix = 0.0)
        {
            bool[] _;
            ComputeHingeEnergyAndGrad(P, out energy, out energyGrad, out isFold, out _,
                branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight,
                wantGrad, brushWeights, detMix);
        }

        // Full overload: also returns per-vertex degeneracy flag (sep < 0.1 in paper-faithful mode).
        // Use in the optimizer to zero momentum at isotropic vertices without touching the gradient.
        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad,
            out bool[] isFold,
            out bool[] degenVerts,
            double branchWeight,
            double consolidateWeight,
            bool useMaxCov,
            double sharpness,
            double crazeWeight,
            bool wantGrad = true,
            double[] brushWeights = null,
            double detMix = 0.0)
        {
            int nV = P.Vertices.Count;
            int nF = P.Faces.Count;

            energy = new double[nV];
            degenVerts = new bool[nV];
            bool[] dv = degenVerts;  // local alias — out params cannot be captured by lambdas
            energyGrad = new Vec3[nV];
            isFold = new bool[nV];
            // Per-vertex CornerWeight cache - filled inside the per-vertex loop, read by the L1
            // dihedral block AFTER the per-vertex loop so the edge weight = average of endpoints.
            double[] cornerWeights = new double[nV];

            // Position cache: every block below (face precompute, theta sums, gradient distribution,
            // branching, consolidation, L1 dihedral) used to call Pos(P.Vertices[...]) on every
            // access, constructing a new Vec3 from 3 float->double conversions per call. Reading
            // each vertex once into a Vec3[] is dramatically cheaper - typically saves O(valence)
            // Vec3 constructions per vertex per term, and removes the per-call PlanktonVertex
            // indexer overhead.
            bool perf = CHAStats.Enabled;
            long t0 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            Vec3[] pos = new Vec3[nV];
            for (int vi = 0; vi < nV; vi++)
            {
                PlanktonVertex pv = P.Vertices[vi];
                pos[vi] = new Vec3(pv.X, pv.Y, pv.Z);
            }

            // Parallelism config, hoisted so the (scatter-free, fully deterministic) face
            // precompute / adjacency / vertex-normal phases below can share it with the
            // per-vertex loop. Cap at ProcessorCount-2 (leave the Rhino UI + GH redraw a core).
            int maxThreads = System.Math.Max(1, System.Environment.ProcessorCount - 2);
            var parOpts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = maxThreads };
            int faceChunk = System.Math.Max(64, (nF + maxThreads * 4 - 1) / (maxThreads * 4));
            int vertChunk = System.Math.Max(64, (nV + maxThreads * 4 - 1) / (maxThreads * 4));

            // --- Precompute face data ---
            // dNdp[3*f + i] = (e_opp_i x N_f) / dA_f - Eq 8's per-face gradient scaffold, computed
            // once. Every block below (covariance gradient, L1, max-cov M-term, max-cov triple sum,
            // branching, consolidation) used to compute Vec3.Cross(e_opp, N) / dA at every call;
            // they now dot dPsi/dN_f with dNdp directly.
            // dTheta[3*f + i] = Cross(N_f, e_(i,i+1)) / |e_(i,i+1)|^2 - per-face local-edge cross
            // products for the corner-angle gradient. The covariance gradient loop used to
            // recompute these per (vert, face) - cache makes the theta gradient a pair of lookups.
            // faceEdge[3*f + i] = pos[v_(i+1)%3] - pos[v_i] - per-face edge vectors used by the
            // factorv distribution loop to avoid re-reading positions each face.
            // faceSliver[f] = true when the face is degenerate (aspect < 1%) or unused; the
            // gradient loop's per-(vert, face) sliver test was the same calculation.
            // fvFlat[3*f + i] is the v-index of local-i in face f - stored flat (instead of an
            // int[][]) so we can fill via direct half-edge walking and avoid GetFaceVertices's
            // per-face int[3] allocation. ~25K allocations and ~17ms eliminated per CHA call.
            int[] fvFlat = new int[nF * 3];
            Vec3[] faceNormals = new Vec3[nF];
            double[] doubleAreas = new double[nF];
            // dNdp/dTheta are gradient-only scaffolds (read solely under wantGrad guards below),
            // so don't allocate+zero ~2*nF*3 Vec3 on the energy-only display path.
            Vec3[] dNdp = wantGrad ? new Vec3[nF * 3] : null;
            Vec3[] dTheta = wantGrad ? new Vec3[nF * 3] : null;
            Vec3[] faceEdge = new Vec3[nF * 3];
            bool[] faceSliver = new bool[nF];
            // Vertex -> incident-faces flat lookup is built after the face precompute below by
            // walking outgoing-halfedge chains directly (no GetVertexFaces allocation).

            System.Threading.Tasks.Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(0, nF, faceChunk), parOpts, _fr =>
            {
            for (int f = _fr.Item1; f < _fr.Item2; f++)
            {
                if (P.Faces[f].IsUnused) { faceSliver[f] = true; continue; }
                int h0 = P.Faces[f].FirstHalfedge;
                if (h0 < 0) { faceSliver[f] = true; continue; }
                int h1 = P.Halfedges[h0].NextHalfedge;
                int h2 = P.Halfedges[h1].NextHalfedge;
                // Triangle check: third .NextHalfedge must close back to h0.
                if (P.Halfedges[h2].NextHalfedge != h0) { faceSliver[f] = true; continue; }
                int b3 = 3 * f;
                int v0 = P.Halfedges[h0].StartVertex;
                int v1 = P.Halfedges[h1].StartVertex;
                int v2 = P.Halfedges[h2].StartVertex;
                fvFlat[b3]     = v0;
                fvFlat[b3 + 1] = v1;
                fvFlat[b3 + 2] = v2;

                Vec3 p0 = pos[v0];
                Vec3 p1 = pos[v1];
                Vec3 p2 = pos[v2];

                Vec3 e01 = p1 - p0;
                Vec3 e12 = p2 - p1;
                Vec3 e20 = p0 - p2;
                faceEdge[b3]     = e01;
                faceEdge[b3 + 1] = e12;
                faceEdge[b3 + 2] = e20;

                Vec3 cross = Vec3.Cross(e01, p2 - p0);
                doubleAreas[f] = cross.Length;

                double e01Sq = e01 * e01;
                double e12Sq = e12 * e12;
                double e20Sq = e20 * e20;
                double maxEdgeSq = Math.Max(e01Sq, Math.Max(e12Sq, e20Sq));
                faceSliver[f] = (maxEdgeSq < 1e-20) || (doubleAreas[f] < 1e-2 * maxEdgeSq);

                if (doubleAreas[f] > 1e-16)
                {
                    Vec3 N = cross / doubleAreas[f];
                    faceNormals[f] = N;
                    // dNdp/dTheta are GRADIENT scaffolds only - skip them on the energy-only path
                    // (EmitSnapshot, run every GH display tick) where wantGrad is false.
                    if (wantGrad)
                    {
                        // Eq 8 distribution vectors: e_opp for local vertex i is (next.next - next).
                        // local 0 opp = p2 - p1 = e12, local 1 opp = p0 - p2 = e20, local 2 opp = p1 - p0 = e01.
                        // Corner-angle gradient: dTheta[3f + li] is the cross for the edge FROM local
                        // li TO local (li+1)%3, crossed with N and scaled by 1/|edge|^2.
                        // Cross(N, e) == -Cross(e, N), so the three dNdp crosses and the three dTheta
                        // crosses are the SAME three vectors - compute each once (6 crosses -> 3),
                        // bit-identical to the originals.
                        Vec3 c01 = Vec3.Cross(e01, N);   // -> dNdp[local 2], dTheta[local 0]
                        Vec3 c12 = Vec3.Cross(e12, N);   // -> dNdp[local 0], dTheta[local 1]
                        Vec3 c20 = Vec3.Cross(e20, N);   // -> dNdp[local 1], dTheta[local 2]
                        double invDA = 1.0 / doubleAreas[f];
                        dNdp[b3]     = c12 * invDA;
                        dNdp[b3 + 1] = c20 * invDA;
                        dNdp[b3 + 2] = c01 * invDA;
                        if (e01Sq > 0) dTheta[b3]     = c01 * (-1.0 / e01Sq);
                        if (e12Sq > 0) dTheta[b3 + 1] = c12 * (-1.0 / e12Sq);
                        if (e20Sq > 0) dTheta[b3 + 2] = c20 * (-1.0 / e20Sq);
                    }
                }
            }
            });

            // Per-vertex incident-face lookup, built by walking outgoing-halfedge chains directly
            // (zero allocations). Order matches Plankton's CCW topological convention - matters
            // for B.2 consolidation (enumerates cyclic-connected 2-partitions of the face fan)
            // and for the B.5.1 argmin tie-break.
            // Step: h is an outgoing halfedge of v; its face is on the LEFT of h. Next outgoing
            // CCW around v is pair(h).next (the halfedge that leaves v in the adjacent face).
            int[] vfStart = new int[nV + 1];
            int vfTotal = 0;
            // First pass: count incident triangular faces per vertex.
            int[] vfCount = new int[nV];
            System.Threading.Tasks.Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(0, nV, vertChunk), parOpts, _vr =>
            {
            for (int v = _vr.Item1; v < _vr.Item2; v++)
            {
                if (P.Vertices[v].IsUnused) continue;
                int h0 = P.Vertices[v].OutgoingHalfedge;
                if (h0 < 0) continue;
                int h = h0;
                int safetyCap = 64;   // valences > 64 are pathological; protects against loops
                do
                {
                    int fAdj = P.Halfedges[h].AdjacentFace;
                    if (fAdj >= 0 && !faceSliver[fAdj]) vfCount[v]++;
                    int pair = P.Halfedges.GetPairHalfedge(h);
                    if (pair < 0) break;
                    h = P.Halfedges[pair].NextHalfedge;
                    if (h < 0) break;
                    safetyCap--;
                } while (h != h0 && safetyCap > 0);
            }
            });
            for (int v = 0; v < nV; v++) { vfStart[v] = vfTotal; vfTotal += vfCount[v]; vfCount[v] = 0; }
            vfStart[nV] = vfTotal;
            int[] vfFace = new int[vfTotal];
            byte[] vfLocal = new byte[vfTotal];   // local index in face is 0/1/2 - byte saves cache
            // Second pass: fill (face, local-index) per vertex.
            System.Threading.Tasks.Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(0, nV, vertChunk), parOpts, _vr =>
            {
            for (int v = _vr.Item1; v < _vr.Item2; v++)
            {
                if (P.Vertices[v].IsUnused) continue;
                int h0 = P.Vertices[v].OutgoingHalfedge;
                if (h0 < 0) continue;
                int h = h0;
                int safetyCap = 64;
                do
                {
                    int fAdj = P.Halfedges[h].AdjacentFace;
                    if (fAdj >= 0 && !faceSliver[fAdj])
                    {
                        // h is outgoing from v IN face fAdj: v is its StartVertex, so v's local
                        // index in fAdj equals whichever of fvFlat[3*fAdj + 0..2] matches v.
                        int b3v = 3 * fAdj;
                        int li = (v == fvFlat[b3v]) ? 0 : (v == fvFlat[b3v + 1]) ? 1 : 2;
                        int idx = vfStart[v] + vfCount[v]++;
                        vfFace[idx] = fAdj;
                        vfLocal[idx] = (byte)li;
                    }
                    int pair = P.Halfedges.GetPairHalfedge(h);
                    if (pair < 0) break;
                    h = P.Halfedges[pair].NextHalfedge;
                    if (h < 0) break;
                    safetyCap--;
                } while (h != h0 && safetyCap > 0);
            }
            });
            long t1 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (perf) CHAStats.FacePrecomputeTicks += t1 - t0;

            // --- Area-weighted vertex normals ---
            Vec3[] vertNormalsRaw = new Vec3[nV];
            Vec3[] vertNormals = new Vec3[nV];
            double[] vertDA = new double[nV];   // sum of incident double-areas, for the fold guard
            System.Threading.Tasks.Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(0, nV, vertChunk), parOpts, _vr =>
            {
            for (int v = _vr.Item1; v < _vr.Item2; v++)
            {
                if (P.Vertices[v].IsUnused) continue;
                int vs = vfStart[v], ve = vfStart[v + 1];
                for (int k = vs; k < ve; k++)
                {
                    int f = vfFace[k];
                    vertNormalsRaw[v] += doubleAreas[f] * faceNormals[f];
                    vertDA[v] += doubleAreas[f];
                }
                vertNormals[v] = vertNormalsRaw[v].Normalized();
            }
            });
            long t2 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (perf) CHAStats.VertNormalsTicks += t2 - t1;

            // --- Per-vertex energy + gradient ---
            // Hoisted per-vertex buffers: previously two List<int> were allocated PER vertex
            // PER call, with .Add() growing the backing array. .Clear() reuses the array so
            // every vertex after the first sees zero allocation in this gather step.
            // The FaceTrig[] is the shared cache between the covariance build pass and the
            // gradient distribution pass below - sized to the worst valence we've seen so far.
            // Local aliases for out parameters - C# forbids capturing 'out' inside lambdas.
            // The aliases share the same array references, so writes flow through.
            double[] energyOut = energy;
            bool[] isFoldOut = isFold;
            Vec3[] energyGradOut = energyGrad;
            // Parallel per-vertex loop. Each task gets fresh scratch (faces/locIdx/trig/branchX/
            // consPd/consRowPref/consTri) plus a per-task gradLocal accumulator. Body writes
            // energy[vert] / isFold[vert] / cornerWeights[vert] directly (each is per-vertex and
            // each vert is processed by exactly one task) and writes gradient contributions to
            // gradLocal (thread-local). After all tasks complete, gradLocals reduce into the
            // shared energyGrad sequentially - no lock contention during the parallel block.
            var taskGrads = new List<Vec3[]>();
            object taskGradsLock = new object();
            // maxThreads / parOpts hoisted above (shared with the precompute phases). The
            // per-vertex chunk size matches vertChunk but is kept named for clarity here.
            int parChunkSize = vertChunk;
            System.Threading.Tasks.Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(0, nV, parChunkSize),
                parOpts,
                () => new PerTaskScratch
                {
                    faces = new List<int>(16),
                    locIdx = new List<int>(16),
                    trig = new FaceTrig[16],
                    branchX = null,
                    consPd = null,
                    consRowPref = null,
                    consTri = null,
                    gradLocal = wantGrad ? new Vec3[nV] : null,
                },
                (range, state, scratch) =>
                {
                    var faces = scratch.faces;
                    var locIdx = scratch.locIdx;
                    FaceTrig[] trig = scratch.trig;
                    Vec3[] branchX = scratch.branchX;
                    double[] consPd = scratch.consPd;
                    double[] consRowPref = scratch.consRowPref;
                    double[] consTri = scratch.consTri;
                    Vec3[] gradLocal = scratch.gradLocal;
            for (int vert = range.Item1; vert < range.Item2; vert++)
            {
                if (P.Vertices[vert].IsUnused || P.Vertices.IsBoundary(vert))
                    continue;

                // Gather adjacent triangle faces (and the local index of `vert` in each) from the
                // precomputed flat vfStart/vfFace/vfLocal arrays - zero allocations, no IndexOf scan.
                int vs = vfStart[vert], ve = vfStart[vert + 1];
                faces.Clear();
                locIdx.Clear();
                for (int k = vs; k < ve; k++)
                {
                    faces.Add(vfFace[k]);
                    locIdx.Add(vfLocal[k]);
                }

                if (faces.Count < 4) continue; // skip valence < 4

                // Smooth corner weight: w = 1 / (1 + (defect / (pi/4))^4) - set in the covariance /
                // max-cov branch below from sumTheta, then used to scale the per-vertex energy AND
                // every gradient contribution at this vertex (covariance, branching, consolidation,
                // max-cov). Replaces the old binary skip - sharp junctions are mostly preserved
                // (cube corner ~ 6% weight) while intermediate junctions still descend.
                double cornerWeight = 1.0;

                // Fold guard: when the area-weighted normal nearly cancels (the 1-ring folds back
                // on itself past flat), Nv is meaningless and the factorv = (...)/|rawNormal| term
                // amplifies by ~1/coherence, spiking THIS vertex's neighbours' gradients (the
                // "about to explode" case). Its developability is undefined - skip it. This is the
                // zero-length VERTEX NORMAL, distinct from zero-area faces / zero-length edges.
                double rawLenV = vertNormalsRaw[vert].Length;
                if (rawLenV < 0.1 * vertDA[vert])
                {
                    if (rawLenV < 0.05 * vertDA[vert]) isFoldOut[vert] = true;   // severe fold -> flag for healing collapse
                    continue;
                }

                Vec3 Nv = vertNormals[vert];

                if (!useMaxCov)
                {
                // --- Build covariance matrix M (symmetric 3x3) ---
                double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
                double sumTheta = 0;   // Gauss-Bonnet defect = 2*pi - sumTheta, checked after loop

                // Grow the trig cache if this vertex has higher valence than any seen so far.
                if (trig.Length < faces.Count) Array.Resize(ref trig, faces.Count);

                for (int fi = 0; fi < faces.Count; fi++)
                {
                    int f = faces[fi];
                    int li = locIdx[fi];
                    int b3v = 3 * f;

                    Vec3 Pi = pos[fvFlat[b3v + li]];
                    Vec3 Pj = pos[fvFlat[b3v + ((li + 1) % 3)]];
                    Vec3 Pk = pos[fvFlat[b3v + ((li + 2) % 3)]];
                    double theta = Vec3.Angle(Pj - Pi, Pk - Pi);
                    sumTheta += theta;
                    trig[fi].theta = theta;

                    Vec3 Nf = faceNormals[f];
                    Vec3 NvxNf = Vec3.Cross(Nv, Nf);
                    double sinPhi = NvxNf.Length;
                    double cosPhi = Nv * Nf;
                    trig[fi].sinPhi = sinPhi;
                    trig[fi].cosPhi = cosPhi;

                    if (1.0 + sinPhi == 1.0) { trig[fi].hasGeom = false; continue; }

                    double phi = SafeAcos(cosPhi);
                    Vec3 nuf = NvxNf / sinPhi;
                    Vec3 muvf = Vec3.Cross(nuf, Nv);
                    Vec3 muff = Vec3.Cross(nuf, Nf);
                    Vec3 Nfw = muvf * phi;

                    trig[fi].phi = phi;
                    trig[fi].nuf = nuf;
                    trig[fi].muvf = muvf;
                    trig[fi].muff = muff;
                    trig[fi].Nfw = Nfw;
                    trig[fi].hasGeom = true;

                    m00 += theta * Nfw.X * Nfw.X;
                    m01 += theta * Nfw.X * Nfw.Y;
                    m02 += theta * Nfw.X * Nfw.Z;
                    m11 += theta * Nfw.Y * Nfw.Y;
                    m12 += theta * Nfw.Y * Nfw.Z;
                    m22 += theta * Nfw.Z * Nfw.Z;
                }

                // Smooth corner falloff (see CornerWeight): sharp junctions get small but non-zero
                // weight, intermediate junctions get partial weight, smooth-surface vertices get 1.
                cornerWeight = CornerWeight(Math.Abs(2.0 * Math.PI - sumTheta), sharpness);
                cornerWeights[vert] = cornerWeight;
                if (cornerWeight < 1e-6) continue;

                // --- Eigendecompose (robust 2x2 tangent block) ---
                // Compute BOTH eigenpairs of M restricted to the tangent plane. Mode 0 uses only
                // lambda_min (paper-faithful Eq 5); modes 1 (det = lambda_min * lambda_max) and 2
                // (harmonic mean = det / trace) combine both, producing a basis-invariant gradient
                // even when the two eigenvalues are degenerate (icosahedron vertices, symmetric
                // quads). The eigenvector ambiguity at degeneracy that drives the spatial twist
                // disappears in modes 1/2 because both x_min and x_max contribute.
                double lambda_min_v, lambda_max_v;
                Vec3 x_min, x_max;
                // Mesh-intrinsic tangent seed: first outgoing edge direction from this vertex.
                // Rotates with the mesh so the eigenvector basis is rotation-invariant, unlike
                // the world-X/Y fallback in TangentEigenpairs which was the source of
                // rotation-dependent results (different rotations -> different t1 -> different x_min).
                Vec3 t1Hint = Vec3.Zero;
                {
                    int _oh = P.Vertices[vert].OutgoingHalfedge;
                    if (_oh >= 0)
                    {
                        int _pair = P.Halfedges.GetPairHalfedge(_oh);
                        if (_pair >= 0)
                        {
                            int _nb = P.Halfedges[_pair].StartVertex;
                            PlanktonVertex _pv = P.Vertices[vert];
                            PlanktonVertex _pn = P.Vertices[_nb];
                            t1Hint = new Vec3(_pn.X - _pv.X, _pn.Y - _pv.Y, _pn.Z - _pv.Z);
                        }
                    }
                }
                TangentEigenpairs(m00, m01, m02, m11, m12, m22, Nv,
                    out lambda_min_v, out x_min, out lambda_max_v, out x_max, t1Hint);

                // Linear blend between paper-faithful lambda_min (detMix=0) and det = lambda_min *
                // lambda_max (detMix=1). The blend is naturally smooth in detMix; per-pass weights
                // come from differentiating E_blend = (1-a)*l_min + a*l_min*l_max:
                //   dE/dl_min = (1-a) + a*l_max     -> w_min (multiplies grad_lambda_min)
                //   dE/dl_max = a*l_min             -> w_max (multiplies grad_lambda_max)
                // At detMix=0: w_min=1, w_max=0 (pure mode-0; pass 1 skipped).
                // At detMix=1: w_min=l_max, w_max=l_min (pure mode-1 det energy).
                double a = detMix; if (a < 0) a = 0; else if (a > 1) a = 1;

                // In paper-faithful mode (a≈0, no harmonic blend), flag vertices where both
                // tangent eigenvalues are nearly equal. The min-eigenvector direction is then
                // numerically arbitrary; the caller can zero momentum there so it cannot
                // accumulate drift in a random direction.
                if (a < 0.01 && !HarmonicEnergy)
                {
                    double tr_dv = lambda_max_v + lambda_min_v;
                    double sep_dv = tr_dv > 1e-300 ? (lambda_max_v - lambda_min_v) / tr_dv : 0.0;
                    if (sep_dv < 0.1) dv[vert] = true;
                }

                if (AdaptiveDetMix)
                {
                    double denom = lambda_max_v + lambda_min_v;
                    double sep = denom > 1e-300 ? (lambda_max_v - lambda_min_v) / denom : 0.0;
                    if (sep < 0) sep = 0; else if (sep > 1) sep = 1;
                    double aDeg = Math.Pow(1.0 - sep, AdaptiveDetMixPower);
                    if (aDeg > a) a = aDeg;
                }
                double eVal = (1.0 - a) * lambda_min_v + a * lambda_min_v * lambda_max_v;
                double wMin = (1.0 - a) + a * lambda_max_v;
                double wMax = a * lambda_min_v;

                if (HarmonicEnergy)
                {
                    double tr = lambda_min_v + lambda_max_v;
                    if (tr > 1e-300)
                    {
                        double inv = 1.0 / tr;
                        eVal = lambda_min_v * lambda_max_v * inv;
                        wMin = lambda_max_v * lambda_max_v * (inv * inv);
                        wMax = lambda_min_v * lambda_min_v * (inv * inv);
                    }
                    else { eVal = 0.0; wMin = 0.0; wMax = 0.0; }
                }

                energyOut[vert] = cornerWeight * eVal;

                if (!wantGrad) goto AfterCovGrad;

                // --- Gradient ---
                // Two-pass loop: pass 0 uses (x_min, w_min), pass 1 uses (x_max, w_max). Mode 0
                // skips pass 1 (w_max = 0). The face-level guards and trig-cache reads are the same
                // for both passes; only the (x, weight) input changes.
                Vec3 totalFactorv = Vec3.Zero;

                for (int pass = 0; pass < 2; pass++)
                {
                    Vec3 xCur = pass == 0 ? x_min : x_max;
                    double passW = pass == 0 ? wMin : wMax;
                    if (passW <= 0) continue;
                    double cwEff = cornerWeight * passW;

                    for (int fi = 0; fi < faces.Count; fi++)
                    {
                        int f = faces[fi];
                        if (faceSliver[f]) continue;
                        if (!trig[fi].hasGeom) continue;
                        double cosPhi = trig[fi].cosPhi;
                        if (cosPhi < -0.85) continue;

                        int li = locIdx[fi];
                        int b3v = 3 * f;
                        int i = vert;
                        int j = fvFlat[b3v + ((li + 1) % 3)];
                        int k = fvFlat[b3v + ((li + 2) % 3)];

                        Vec3 Nf = faceNormals[f];

                        double theta = trig[fi].theta;
                        double sinPhi = trig[fi].sinPhi;
                        double phi = trig[fi].phi;
                        double tanPhi = sinPhi / cosPhi;
                        Vec3 nuf = trig[fi].nuf;
                        Vec3 muvf = trig[fi].muvf;
                        Vec3 muff = trig[fi].muff;
                        Vec3 Nfw = trig[fi].Nfw;

                        double xNfw = xCur * Nfw;
                        double xNfw2 = xNfw * xNfw;
                        double xMuvf = xCur * muvf;
                        double xNuf = xCur * nuf;

                        int b3 = 3 * f;
                        Vec3 dTli = dTheta[b3 + li];
                        Vec3 dTli2 = dTheta[b3 + ((li + 2) % 3)];
                        Vec3 dTdi = dTli + dTli2;
                        Vec3 dTdj = -dTli;
                        Vec3 dTdk = -dTli2;

                        // 2*xNfw*theta drives both fvec and factorv - compute once (bit-identical).
                        double twoXNfwTheta = 2.0 * xNfw * theta;
                        Vec3 fvec = twoXNfwTheta *
                            (xMuvf * muff + (phi / sinPhi) * xNuf * nuf);

                        double cdi = fvec * dNdp[b3 + li];
                        double cdj = fvec * dNdp[b3 + ((li + 1) % 3)];
                        double cdk = fvec * dNdp[b3 + ((li + 2) % 3)];

                        gradLocal[i] += cwEff * (xNfw2 * dTdi + cdi * Nf);
                        gradLocal[j] += cwEff * (xNfw2 * dTdj + cdj * Nf);
                        gradLocal[k] += cwEff * (xNfw2 * dTdk + cdk * Nf);

                        // rawLen is the vertex raw-normal length - loop-invariant over this vertex's
                        // faces; reuse rawLenV from the fold guard instead of re-sqrt'ing per face.
                        if (rawLenV < 1e-16) continue;
                        if (Math.Abs(tanPhi) < 1e-16) continue;

                        Vec3 factorv = -twoXNfwTheta *
                            ((xCur * (muvf + phi * Nv)) * muvf +
                             (phi / tanPhi) * xNuf * nuf) / rawLenV;
                        // factorv from this pass is weighted by passW so totalFactorv is the
                        // SUM of (w_min * factorv_xmin) + (w_max * factorv_xmax) ready for the
                        // single apply step below.
                        totalFactorv += passW * factorv;
                    }
                }

                // Apply vertex-normal derivative through cross-product matrices.
                // For each face g in 1-ring: dP/di = J(eik)-J(eij), dP/dj = -J(eik), dP/dk = J(eij);
                // row_vec * J(a) = row_vec x a.
                // eij_g / eik_g read from faceEdge[]: faceEdge[3g + i] = pos[v_(i+1)%3] - pos[v_i],
                // so the edge from local-gli to local-(gli+1)%3 is faceEdge[3g + gli] = eij_g, and
                // the edge from local-(gli+2)%3 to local-gli is faceEdge[3g + (gli+2)%3]
                // = pos[v_gli] - pos[v_(gli+2)%3] = -eik_g - hence the negate below.
                for (int gi = 0; gi < faces.Count; gi++)
                {
                    int g = faces[gi];
                    int gli = locIdx[gi];
                    int bg = 3 * g;

                    int ig = vert;
                    int jg = fvFlat[bg + ((gli + 1) % 3)];
                    int kg = fvFlat[bg + ((gli + 2) % 3)];

                    Vec3 eij_g = faceEdge[bg + gli];
                    Vec3 eik_g = -faceEdge[bg + ((gli + 2) % 3)];

                    Vec3 cxEij = Vec3.Cross(totalFactorv, eij_g);
                    Vec3 cxEik = Vec3.Cross(totalFactorv, eik_g);

                    gradLocal[ig] += cornerWeight * (cxEik - cxEij);
                    gradLocal[jg] += cornerWeight * (-cxEik);
                    gradLocal[kg] += cornerWeight * cxEij;
                }
                AfterCovGrad: ;
                }
                else
                {
                    // Smooth corner weight (same as the sum-covariance branch above): O(n^3)
                    // enumeration would be wasted on hard corners; the falloff also leaves the
                    // intermediate range producing useful gradient.
                    // Indexed iteration reuses locIdx[] (already parallel to faces[]) instead of
                    // re-scanning fvFlat[3*f + ...] with Array.IndexOf, and reads positions from the
                    // pos[] cache instead of constructing Vec3 per access.
                    double sumThetaMC = 0;
                    for (int fiT = 0; fiT < faces.Count; fiT++)
                    {
                        int b3v = 3 * faces[fiT];
                        int liT = locIdx[fiT];
                        Vec3 Pi_t = pos[fvFlat[b3v + liT]];
                        Vec3 Pj_t = pos[fvFlat[b3v + ((liT + 1) % 3)]];
                        Vec3 Pk_t = pos[fvFlat[b3v + ((liT + 2) % 3)]];
                        sumThetaMC += Vec3.Angle(Pj_t - Pi_t, Pk_t - Pi_t);
                    }
                    cornerWeight = CornerWeight(Math.Abs(2.0 * Math.PI - sumThetaMC), sharpness);
                    cornerWeights[vert] = cornerWeight;
                    if (cornerWeight < 1e-6) continue;

                    // --- B.4 max covariance (replaces the sum-covariance above) ---
                    // lambda^max := min_{|u|=1} phi(u), phi(u) = max_{ijk} <u, N_ijk>^2.
                    // phi is piecewise smooth over spherical Voronoi cells of the +/- signed
                    // normal SITES; its min lives at a Voronoi vertex = spherical centroid of some
                    // triple of sites. We enumerate all C(2n, 3) triples, take v = (b-a) x (c-a) /
                    // |...|, evaluate phi at v over the n face normals, keep the smallest. By the
                    // envelope theorem the subgradient at the minimum is just the gradient of
                    // <v, N_M>^2 holding v fixed, where M is the maximising face - one of the
                    // triple members. Chain through Eq 8 dN_f/dp = (e_opp x N_f) N_f^T / dA.
                    int nMC = faces.Count;
                    int sm = 2 * nMC;
                    double minPhi = double.MaxValue;
                    Vec3 vStar = Vec3.Zero;
                    int maxFaceIdx = -1;
                    int aStarSlot = -1, bStarSlot = -1, cStarSlot = -1;
                    double crossLenStar = 0;

                    for (int a = 0; a < sm; a++)
                    {
                        Vec3 sa = ((a & 1) == 0) ? faceNormals[faces[a >> 1]] : -faceNormals[faces[a >> 1]];
                        for (int b = a + 1; b < sm; b++)
                        {
                            Vec3 sb = ((b & 1) == 0) ? faceNormals[faces[b >> 1]] : -faceNormals[faces[b >> 1]];
                            for (int c = b + 1; c < sm; c++)
                            {
                                Vec3 sc = ((c & 1) == 0) ? faceNormals[faces[c >> 1]] : -faceNormals[faces[c >> 1]];
                                Vec3 baMC = sb - sa;
                                Vec3 caMC = sc - sa;
                                Vec3 crossMC = Vec3.Cross(baMC, caMC);
                                double crossLen = crossMC.Length;
                                if (crossLen < 1e-12) continue;
                                Vec3 w = crossMC / crossLen;

                                double phi = 0;
                                int maxIdx = -1;
                                for (int k = 0; k < nMC; k++)
                                {
                                    double dot = w * faceNormals[faces[k]];
                                    double dotSq = dot * dot;
                                    if (dotSq > phi) { phi = dotSq; maxIdx = k; }
                                }

                                if (phi < minPhi)
                                {
                                    minPhi = phi;
                                    vStar = w;
                                    maxFaceIdx = maxIdx;
                                    aStarSlot = a; bStarSlot = b; cStarSlot = c;
                                    crossLenStar = crossLen;
                                }
                            }
                        }
                    }

                    if (minPhi < double.MaxValue && maxFaceIdx >= 0)
                    {
                        energyOut[vert] = cornerWeight * minPhi;
                        if (!wantGrad) continue;

                        int fM = faces[maxFaceIdx];
                        int liM = locIdx[maxFaceIdx];
                        Vec3 NM = faceNormals[fM];
                        double dAM = doubleAreas[fM];
                        double factor = 2.0 * (vStar * NM);   // 2<v*, N_M>, shared by M term + triple sum

                        if (dAM > 1e-16)
                        {
                            // Eq 8 distribution via precomputed dNdp.
                            int bM = 3 * fM;
                            double ci_i = factor * (vStar * dNdp[bM + liM]);
                            double ci_j = factor * (vStar * dNdp[bM + ((liM + 1) % 3)]);
                            double ci_k = factor * (vStar * dNdp[bM + ((liM + 2) % 3)]);

                            gradLocal[fvFlat[bM + liM]]                 += (cornerWeight * ci_i) * NM;
                            gradLocal[fvFlat[bM + ((liM + 1) % 3)]]     += (cornerWeight * ci_j) * NM;
                            gradLocal[fvFlat[bM + ((liM + 2) % 3)]]     += (cornerWeight * ci_k) * NM;
                        }

                        // --- Triple sum: chain df/dN_face_sigma for each of the 3 winning sites
                        // through Eq 8. Captures v = c/|c|'s implicit dependence on positions, where
                        // c = (s_b - s_a) x (s_c - s_a). At the Voronoi-vertex min the envelope
                        // theorem leaves this term residual (multiple max-faces tie), and that's
                        // exactly what the paper's B.4 long formula encodes alongside the M term.
                        //
                        // Derivation:
                        //   dc/dN_face_sigma . dN = sign_sigma . (s_next - s_prev) x dN
                        //                          (cyclic order a -> b -> c -> a)
                        //   df/dc = (2<v, M> / |c|) . (N_M - <v, M> v)
                        //   => df/dN_face_sigma = sign_sigma . (2<v, M> / |c|) . (s_next - s_prev) x N_M_perp_v
                        // Chained through dN_face/dp via Eq 8 = (e_opp x N_face) N_face^T / dA gives
                        // the contribution at each vertex p in face_sigma.
                        if (crossLenStar > 1e-12)
                        {
                            Vec3 NM_perp_v = NM - (vStar * NM) * vStar;
                            int[] winSlots = { aStarSlot, bStarSlot, cStarSlot };
                            for (int slotIdx = 0; slotIdx < 3; slotIdx++)
                            {
                                int aSlot = winSlots[slotIdx];
                                int aNext = winSlots[(slotIdx + 1) % 3];
                                int aPrev = winSlots[(slotIdx + 2) % 3];
                                double signA = (aSlot & 1) == 0 ? 1.0 : -1.0;
                                int fidA = aSlot >> 1;
                                int fA = faces[fidA];
                                int liA = locIdx[fidA];
                                Vec3 NA = faceNormals[fA];
                                double dAA = doubleAreas[fA];
                                if (dAA < 1e-16) continue;

                                Vec3 sNext = ((aNext & 1) == 0 ? 1.0 : -1.0) * faceNormals[faces[aNext >> 1]];
                                Vec3 sPrev = ((aPrev & 1) == 0 ? 1.0 : -1.0) * faceNormals[faces[aPrev >> 1]];
                                Vec3 diffSites = sNext - sPrev;
                                Vec3 diffCrossPerp = Vec3.Cross(diffSites, NM_perp_v);
                                // Absorb 1/dAA into dNdp; scale carries the rest.
                                double scale = factor * signA / crossLenStar;

                                int bA = 3 * fA;
                                double ti_i = scale * (dNdp[bA + liA] * diffCrossPerp);
                                double ti_j = scale * (dNdp[bA + ((liA + 1) % 3)] * diffCrossPerp);
                                double ti_k = scale * (dNdp[bA + ((liA + 2) % 3)] * diffCrossPerp);

                                gradLocal[fvFlat[bA + liA]]                 += (cornerWeight * ti_i) * NA;
                                gradLocal[fvFlat[bA + ((liA + 1) % 3)]]     += (cornerWeight * ti_j) * NA;
                                gradLocal[fvFlat[bA + ((liA + 2) % 3)]]     += (cornerWeight * ti_k) * NA;
                            }
                        }
                    }
                }

                // --- B.5.1 branching penalty (optional) ---
                // psi_i := min_{|u|=1} max_{a,b} <x_a - x_b, u>^2 over the +/- signed face normals
                // {x_k} = {+/- N_f : f in 1-ring}. The minimizing u for any candidate pair (a,b) is
                // the unit altitude of the triangle (0, x_a, x_b), so we enumerate pairs and take
                // the smallest max-projection-squared. Cheap: O(n^4) for n ~ 6.
                if (branchWeight > 0)
                {
                    int nFR = faces.Count;
                    int m = 2 * nFR;
                    if (branchX == null || branchX.Length < m) branchX = new Vec3[m];
                    for (int idx = 0; idx < m; idx++)
                    {
                        Vec3 nf = faceNormals[faces[idx >> 1]];
                        branchX[idx] = (idx & 1) == 0 ? nf : -nf;
                    }

                    double psiMin = double.MaxValue;
                    int aStar = -1, bStar = -1, cStar = -1, dStar = -1;
                    Vec3 uStar = Vec3.Zero, wStar = Vec3.Zero;
                    double crossLenStar = 0;

                    for (int a = 0; a < m; a++)
                    for (int b = a + 1; b < m; b++)
                    {
                        Vec3 w = branchX[b] - branchX[a];
                        Vec3 cross = Vec3.Cross(Nv, w);
                        double cl = cross.Length;
                        if (cl < 1e-12) continue;
                        Vec3 u = cross / cl;

                        // max over (c, d) of (<X[c],u> - <X[d],u>)^2 = (max_c <X[c],u> - min_d <X[d],u>)^2.
                        // A single O(m) scan for the argmax + argmin replaces the original O(m^2)
                        // pair enumeration. The unordered winning pair is identical; the gradient
                        // formula uses ds/dx_c = +u, ds/dx_d = -u with the same sign convention.
                        double maxDot = double.NegativeInfinity, minDot = double.PositiveInfinity;
                        int cB = -1, dB = -1;
                        for (int k = 0; k < m; k++)
                        {
                            double dotK = branchX[k] * u;
                            if (dotK > maxDot) { maxDot = dotK; cB = k; }
                            if (dotK < minDot) { minDot = dotK; dB = k; }
                        }
                        double spread = maxDot - minDot;
                        double psiAb = spread * spread;

                        if (psiAb < psiMin)
                        {
                            psiMin = psiAb;
                            aStar = a; bStar = b; cStar = cB; dStar = dB;
                            uStar = u; wStar = w; crossLenStar = cl;
                        }
                    }

                    if (psiMin < double.MaxValue && psiMin > 0)
                    {
                        energyOut[vert] += cornerWeight * branchWeight * psiMin;
                        if (!wantGrad) continue;

                        // --- Subgradient of psi at the winning (a*, b*, c*, d*).
                        // psi = s^2, s = <xDiff, u*>, u* = c/|c|, c = Nv x w, w = X[b*] - X[a*].
                        // Chain through u*: dw/dx_a = -I, dc/dx_a applied to delta = -Nv x delta,
                        // d|c|/dx_a applied to delta = -<u*, Nv x delta>. So
                        //   du*/dx_a delta = -(1/|c|)(I - u* u*^T)(Nv x delta)
                        // and ds/dx_a applied to delta = <xDiff, du*/dx_a delta>
                        //                              = -(delta/|c|) . [(xDiff x Nv) - s (u* x Nv)].
                        // Hence dpsi/dx_a = -(2s/|c|) [(xDiff x Nv) - s (u* x Nv)], dpsi/dx_b is its
                        // negative (w = x_b - x_a flips sign). NOTE: the paper's
                        //   d u*/d x_a = -1/|w|^3 (Nv x w) w^T
                        // is the INTRINSIC-2D form where Nv perp w, xDiff so |c| = |w| and Nv x w is
                        // in the (x_a, x_b) plane - in 3D it points perpendicular to w, so we use the
                        // chain-rule form above and FD-check confirms it.
                        // For x_{c*}, x_{d*}: ds/dx_c = u*, ds/dx_d = -u*.
                        Vec3 xDiff = branchX[cStar] - branchX[dStar];
                        double s = xDiff * uStar;

                        int[] winIdx = { aStar, bStar, cStar, dStar };
                        Vec3[] winGrad = new Vec3[4];
                        if (crossLenStar > 1e-15)
                        {
                            Vec3 xDiffXNv = Vec3.Cross(xDiff, Nv);
                            Vec3 uStarXNv = Vec3.Cross(uStar, Nv);
                            Vec3 gradU = ((-2.0 * s) / crossLenStar) * (xDiffXNv - s * uStarXNv);
                            winGrad[0] = gradU;
                            winGrad[1] = -gradU;
                        }
                        winGrad[2] = (2.0 * s) * uStar;
                        winGrad[3] = (-2.0 * s) * uStar;

                        // Chain x_alpha = sign_alpha * N_{f_alpha}. Multiple winning slots can map
                        // to the same face (e.g. +N_f as x_a*, -N_f as x_c*) - sum their dPsi/dN_f
                        // BEFORE chaining through dN_f/dp so each face is processed once.
                        for (int i = 0; i < 4; i++)
                        {
                            if (winIdx[i] < 0) continue;
                            int fid_i = winIdx[i] >> 1;
                            int sgn_i = (winIdx[i] & 1) == 0 ? 1 : -1;
                            int f_i = faces[fid_i];
                            Vec3 dPsiDNf = ((double)sgn_i) * winGrad[i];

                            // fold in any later winning slot pointing at the same face
                            for (int j = i + 1; j < 4; j++)
                            {
                                if (winIdx[j] < 0) continue;
                                if (faces[winIdx[j] >> 1] != f_i) continue;
                                int sgn_j = (winIdx[j] & 1) == 0 ? 1 : -1;
                                dPsiDNf += ((double)sgn_j) * winGrad[j];
                                winIdx[j] = -1;
                            }

                            // Chain dN_f/dp_v = dNdp[3*f + i] (Eq 8, precomputed) - dot with dPsi/dN_f.
                            int li_i = locIdx[fid_i];
                            Vec3 Nf_i = faceNormals[f_i];
                            if (doubleAreas[f_i] < 1e-16) continue;

                            int b_i = 3 * f_i;
                            double ci_i = dPsiDNf * dNdp[b_i + li_i];
                            double cj_i = dPsiDNf * dNdp[b_i + ((li_i + 1) % 3)];
                            double ck_i = dPsiDNf * dNdp[b_i + ((li_i + 2) % 3)];

                            gradLocal[fvFlat[b_i + li_i]]                 += (cornerWeight * branchWeight * ci_i) * Nf_i;
                            gradLocal[fvFlat[b_i + ((li_i + 1) % 3)]]     += (cornerWeight * branchWeight * cj_i) * Nf_i;
                            gradLocal[fvFlat[b_i + ((li_i + 2) % 3)]]     += (cornerWeight * branchWeight * ck_i) * Nf_i;
                        }

                        // --- Subgradient w.r.t. Nv: u* depends on Nv, which depends on every face
                        // in the 1-ring via Nraw. dPsi/dNv = (2s/|c|) [(w x xDiff) - s (w x u*)];
                        // project off the Nv component (that part of the gradient gets killed when
                        // we re-normalize anyway), divide by |Nraw|, and distribute through the
                        // SAME cross-product chain the existing covariance totalFactorv loop uses.
                        Vec3 dPsiDNv = (2.0 * s / crossLenStar) *
                                       (Vec3.Cross(wStar, xDiff) - s * Vec3.Cross(wStar, uStar));
                        Vec3 dPsiDNvPerp = dPsiDNv - (Nv * dPsiDNv) * Nv;
                        double rawLenNv = vertNormalsRaw[vert].Length;
                        if (rawLenNv > 1e-15)
                        {
                            Vec3 factorvBranch = (cornerWeight * branchWeight / rawLenNv) * dPsiDNvPerp;
                            for (int gi = 0; gi < faces.Count; gi++)
                            {
                                int gB = faces[gi];
                                int gliB = locIdx[gi];
                                int bgB = 3 * gB;
                                int igB = vert;
                                int jgB = fvFlat[bgB + ((gliB + 1) % 3)];
                                int kgB = fvFlat[bgB + ((gliB + 2) % 3)];
                                Vec3 eij_gB = faceEdge[bgB + gliB];
                                Vec3 eik_gB = -faceEdge[bgB + ((gliB + 2) % 3)];
                                Vec3 cxEij_B = Vec3.Cross(factorvBranch, eij_gB);
                                Vec3 cxEik_B = Vec3.Cross(factorvBranch, eik_gB);
                                gradLocal[igB] += cxEik_B - cxEij_B;
                                gradLocal[jgB] += -cxEik_B;
                                gradLocal[kgB] += cxEij_B;
                            }
                        }
                    }
                }

                // --- B.2 combinatorial (consolidation) energy (optional) ---
                // E_i^P := min over connected 2-partitions P of St(i) of the within-cluster pair sum
                //         Sum_{(s,t) in same cluster} |N_s - N_t|^2
                // The 1-ring faces sit in a cyclic fan; a connected 2-partition is a choice of two
                // cuts on that cycle, equivalently a pair (a, b) with a < b giving cluster 1 = [a,
                // b-1] and cluster 2 = the cyclic complement. n(n-1)/2 partitions for n faces; for
                // typical n ~ 6 that's 15. Reports the unweighted E to be added to energy[vert] and
                // distributes the gradient via Eq 8 (dN_f/dp = (e_opp x N_f) N_f^T / dA_f).
                if (consolidateWeight > 0)
                {
                    int nFR = faces.Count;
                    if (nFR >= 2)
                    {
                        // O(n^2) precompute + O(n^2) enumeration replaces the original O(n^4). Math:
                        //   within(C1) + within(C2) = T_total - cross(C1, C2)
                        // where cross(C1, C2) = clusterRowSum(C1) - 2 * within(C1) using row sums of
                        // the symmetric |N_i - N_j|^2 matrix. So
                        //   within_total(a, b) = T_total - clusterRowSum(C1=[a,b-1]) + 2 * T1[a, b]
                        // with T1[a, b] = within-cluster pair-sum for the arc [a, b-1]. T1 is filled
                        // incrementally via row prefix sums in O(n^2); each (a, b) becomes O(1).
                        int sz = nFR * nFR;
                        if (consPd == null || consPd.Length < sz) consPd = new double[sz];
                        if (consRowPref == null || consRowPref.Length < sz) consRowPref = new double[sz];
                        int triSz = (nFR + 1) * (nFR + 1);
                        if (consTri == null || consTri.Length < triSz) consTri = new double[triSz];

                        // 1. Symmetric pair-distance matrix |N_i - N_j|^2
                        for (int i = 0; i < nFR; i++)
                        {
                            consPd[i * nFR + i] = 0.0;
                            Vec3 Ni = faceNormals[faces[i]];
                            for (int j = i + 1; j < nFR; j++)
                            {
                                Vec3 diff_ij = Ni - faceNormals[faces[j]];
                                double p = diff_ij * diff_ij;
                                consPd[i * nFR + j] = p;
                                consPd[j * nFR + i] = p;
                            }
                        }

                        // 2. Per-row prefix sums. consRowPref[i*nFR + j] = sum_{k=0..j} consPd[i, k].
                        for (int i = 0; i < nFR; i++)
                        {
                            double s = 0;
                            int rowBase = i * nFR;
                            for (int j = 0; j < nFR; j++)
                            {
                                s += consPd[rowBase + j];
                                consRowPref[rowBase + j] = s;
                            }
                        }

                        // 3. Triangle table T1[a, b] = sum of pd[i, j] for a <= i < j < b.
                        // Recurrence: T1[a, b+1] = T1[a, b] + sum_{i in [a, b-1]} pd[i, b].
                        int triStride = nFR + 1;
                        for (int a = 0; a < triStride; a++)
                        {
                            consTri[a * triStride + a] = 0;
                            if (a + 1 < triStride) consTri[a * triStride + (a + 1)] = 0;
                        }
                        for (int a = 0; a < nFR; a++)
                        {
                            for (int b = a + 1; b < nFR; b++)
                            {
                                // Sum of column b from row a to row b-1.
                                int rowBaseB = b * nFR;
                                double delta = consRowPref[rowBaseB + (b - 1)];
                                if (a > 0) delta -= consRowPref[rowBaseB + (a - 1)];
                                consTri[a * triStride + (b + 1)] = consTri[a * triStride + b] + delta;
                            }
                        }

                        double tTotal = consTri[0 * triStride + nFR];   // T1[0, nFR] = T_total

                        // 4. Enumerate (a, b) and pick argmin within_total.
                        double minE = double.MaxValue;
                        int bestA = -1, bestB = -1;
                        for (int a = 0; a < nFR; a++)
                        {
                            // clusterRowSum incrementally over b: when C1 = [a, b-1] grows by one face,
                            // rowSum[b-1] = consRowPref[(b-1)*nFR + nFR - 1].
                            double clusterRowSum = consRowPref[a * nFR + (nFR - 1)];   // rowSum[a]
                            for (int b = a + 1; b < nFR; b++)
                            {
                                double arcSum = consTri[a * triStride + b];
                                double total = tTotal - clusterRowSum + 2.0 * arcSum;
                                if (total < minE) { minE = total; bestA = a; bestB = b; }
                                clusterRowSum += consRowPref[b * nFR + (nFR - 1)];   // add rowSum[b]
                            }
                        }

                        if (minE < double.MaxValue && minE > 0)
                        {
                            energyOut[vert] += cornerWeight * consolidateWeight * minE;
                            if (!wantGrad) continue;

                            // Gradient: for each within-cluster pair (i, j), add
                            //   d|N_i - N_j|^2 / dp = 2 (N_i - N_j) . (dN_i/dp - dN_j/dp)
                            // Eq 8 says dN_f/dp_v = (e_opp_v x N_f) N_f^T / dA_f, so the gradient
                            // contribution to vertex p of face s in the pair is
                            //   2 (N_s - N_t) . (e_opp_p x N_s) / dA_s  times  N_s
                            // (and the opposite sign for vertices of face t).
                            for (int i = 0; i < nFR; i++)
                            {
                                bool iInC1 = (i >= bestA) && (i < bestB);
                                int fi = faces[i];
                                int li_i = locIdx[i];
                                Vec3 Ni = faceNormals[fi];
                                if (doubleAreas[fi] < 1e-16) continue;
                                int bi = 3 * fi;

                                for (int j = i + 1; j < nFR; j++)
                                {
                                    bool jInC1 = (j >= bestA) && (j < bestB);
                                    if (iInC1 != jInC1) continue;   // cross-cluster: no contribution

                                    int fj = faces[j];
                                    int lj_j = locIdx[j];
                                    Vec3 Nj = faceNormals[fj];
                                    if (doubleAreas[fj] < 1e-16) continue;
                                    int bj = 3 * fj;

                                    Vec3 factor = (2.0 * cornerWeight * consolidateWeight) * (Ni - Nj);

                                    // Eq 8 via precomputed dNdp: distribution is a dot product.
                                    double ci_i = factor * dNdp[bi + li_i];
                                    double ci_j = factor * dNdp[bi + ((li_i + 1) % 3)];
                                    double ci_k = factor * dNdp[bi + ((li_i + 2) % 3)];
                                    gradLocal[fvFlat[bi + li_i]]                 += ci_i * Ni;
                                    gradLocal[fvFlat[bi + ((li_i + 1) % 3)]]     += ci_j * Ni;
                                    gradLocal[fvFlat[bi + ((li_i + 2) % 3)]]     += ci_k * Ni;

                                    // Sign flip on factor for face j (we differentiate -N_j)
                                    double cj_i = -factor * dNdp[bj + lj_j];
                                    double cj_j = -factor * dNdp[bj + ((lj_j + 1) % 3)];
                                    double cj_k = -factor * dNdp[bj + ((lj_j + 2) % 3)];
                                    gradLocal[fvFlat[bj + lj_j]]                 += cj_i * Nj;
                                    gradLocal[fvFlat[bj + ((lj_j + 1) % 3)]]     += cj_j * Nj;
                                    gradLocal[fvFlat[bj + ((lj_j + 2) % 3)]]     += cj_k * Nj;
                                }
                            }
                        }
                    }
                }
            }
                    // Write back any re-grown buffers so the next task on this thread (and the
                    // localFinally) see the updated references.
                    scratch.trig = trig;
                    scratch.branchX = branchX;
                    scratch.consPd = consPd;
                    scratch.consRowPref = consRowPref;
                    scratch.consTri = consTri;
                    return scratch;
                },
                scratch =>
                {
                    if (scratch.gradLocal != null)
                    {
                        lock (taskGradsLock) { taskGrads.Add(scratch.gradLocal); }
                    }
                });
            // Reduce per-task gradient accumulators into the shared energyGrad. Sequential after
            // the parallel block - cheap (~nV * nTasks Vec3 adds) and lock-free.
            if (wantGrad)
            {
                for (int t = 0; t < taskGrads.Count; t++)
                {
                    Vec3[] gl = taskGrads[t];
                    for (int v = 0; v < nV; v++) energyGrad[v] += gl[v];
                }
            }
            long t3 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (perf) CHAStats.PerVertexLoopTicks += t3 - t2;

            // --- L1 dihedral sparsity (deCraze) ---
            // Per interior edge e, total energy contribution = |phi_e| * crazeWeight * wAvg, where
            // phi_e is the unsigned dihedral (acos(N_A . N_B)) and wAvg = (cornerWeight_va +
            // cornerWeight_vb) / 2 protects sharp junctions. Sum of L1 = Tibshirani-style sparse
            // regulariser; per He & Schaefer 2013 this drops within-patch dihedrals to zero while
            // leaving real seams alone. Subgradient via the chain rule:
            //   dphi/d(NA . NB) = -1/sin(phi)
            //   d(NA . NB)/dp_v in face A = (NB . (e_opp x NA)) / dA_A * NA   (Eq 8 convention)
            // distributed to ALL three vertices of EACH adjacent face. Skipped when sin(phi) is
            // tiny (the dihedral is 0 or pi - the L1 sub-derivative is 0 there anyway).
            // Enter the L1 block if EITHER a global crazeWeight is set OR a brushWeights map is
            // active (so painted regions still get smoothed even when global crazeWeight = 0).
            bool brushActive = brushWeights != null && brushWeights.Length == nV;
            if (crazeWeight > 0 || brushActive)
            {
                int nHE = P.Halfedges.Count;
                for (int h = 0; h < nHE; h += 2)
                {
                    if (P.Halfedges[h].IsUnused) continue;
                    int fA = P.Halfedges[h].AdjacentFace;
                    int fB = P.Halfedges[h + 1].AdjacentFace;
                    if (fA < 0 || fB < 0) continue;   // boundary edge
                    if (faceSliver[fA] || faceSliver[fB]) continue;   // unused / non-triangle / sliver

                    Vec3 NA = faceNormals[fA];
                    Vec3 NB = faceNormals[fB];
                    double cosPhi = NA * NB;
                    if (cosPhi > 1.0) cosPhi = 1.0;
                    else if (cosPhi < -1.0) cosPhi = -1.0;
                    double phi = Math.Acos(cosPhi);

                    int va = P.Halfedges[h].StartVertex;
                    int vb = P.Halfedges[h + 1].StartVertex;
                    // Per-edge effective crazeWeight = global + brush boost averaged across endpoints.
                    // Painted regions get a much stronger normal-smoothing pull while leaving the
                    // rest of the mesh at the global setting (or fully off if crazeWeight == 0).
                    double edgeCraze = crazeWeight;
                    if (brushActive) edgeCraze += 0.5 * (brushWeights[va] + brushWeights[vb]);
                    if (edgeCraze <= 0) continue;

                    double wAvg = 0.5 * (cornerWeights[va] + cornerWeights[vb]);
                    // Huber-smooth |phi| near flat (CrazeBand). hVal feeds the reported energy;
                    // CrazeHuberDeriv(phi) attenuates the gradient so the force tapers to 0 at flat
                    // instead of holding constant magnitude across the phi=0 cusp - the jitter fix.
                    double hVal = CrazeHuberVal(phi);
                    double edgeScale = edgeCraze * wAvg * CrazeHuberDeriv(phi);
                    energyOut[va] += 0.5 * cornerWeights[va] * edgeCraze * hVal;
                    energyOut[vb] += 0.5 * cornerWeights[vb] * edgeCraze * hVal;
                    if (!wantGrad) continue;

                    Vec3 crossAB = Vec3.Cross(NA, NB);
                    double sinPhi = crossAB.Length;
                    if (sinPhi < 1e-12) continue;
                    double invSin = -1.0 / sinPhi;

                    // Eq 8 dN/dp is precomputed in dNdp[3*f + i]: distribution becomes a dot product
                    // against NB (for face A) / NA (for face B) - no per-edge cross or divide.
                    int bA = 3 * fA, bB = 3 * fB;
                    for (int i = 0; i < 3; i++)
                    {
                        double coeff = edgeScale * invSin * (NB * dNdp[bA + i]);
                        energyGrad[fvFlat[bA + i]] += coeff * NA;
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        double coeff = edgeScale * invSin * (NA * dNdp[bB + i]);
                        energyGrad[fvFlat[bB + i]] += coeff * NB;
                    }
                }
            }
            if (perf) {
                long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
                CHAStats.L1Ticks += t4 - t3;
                CHAStats.Calls++;
            }

            // Kink-outlier rejection: at eigenvalue crossings (the smaller covariance eigenvalue is
            // genuinely non-smooth here), the picked subgradient representative can be enormous and
            // points in a near-random direction. With Step > ~0.01 + momentum=0 the trust-region
            // cap in CreaseMachine no longer hides this and the bad vert flips its 1-ring's eigenvalues
            // every frame -> visible jitter. The fix used in the FD bench - drop any gradient above
            // 8x the per-vertex median - was previously only applied to the numerical-gradient
            // path; the live flow ran without it. Apply here too. No-op when there is no gradient.
            if (wantGrad) RejectKinkOutliers(P, energyGrad);
        }

        // ===== Numerical gradient (correct by construction) =====
        // The analytic gradient above has derivation errors at some configurations
        // (the finite-difference harness catches them). This computes the gradient by
        // central differences of the energy instead. A vertex's energy depends only on
        // its 1-ring, so perturbing v changes only v and its neighbours' energies -
        // each Partial stays O(valence), keeping the whole thing O(E).
        public static void ComputeNumericalGrad(PlanktonMesh P, out double[] energy, out Vec3[] grad)
        {
            ComputeNumericalGrad(P, out energy, out grad, 0.0, 0.0, false, 4.0, 0.0);
        }

        public static void ComputeNumericalGrad(PlanktonMesh P, out double[] energy, out Vec3[] grad, double branchWeight)
        {
            ComputeNumericalGrad(P, out energy, out grad, branchWeight, 0.0, false, 4.0, 0.0);
        }

        public static void ComputeNumericalGrad(PlanktonMesh P, out double[] energy, out Vec3[] grad, double branchWeight, double consolidateWeight)
        {
            ComputeNumericalGrad(P, out energy, out grad, branchWeight, consolidateWeight, false, 4.0, 0.0);
        }

        public static void ComputeNumericalGrad(PlanktonMesh P, out double[] energy, out Vec3[] grad, double branchWeight, double consolidateWeight, bool useMaxCov)
        {
            ComputeNumericalGrad(P, out energy, out grad, branchWeight, consolidateWeight, useMaxCov, 4.0, 0.0);
        }

        public static void ComputeNumericalGrad(PlanktonMesh P, out double[] energy, out Vec3[] grad, double branchWeight, double consolidateWeight, bool useMaxCov, double sharpness)
        {
            ComputeNumericalGrad(P, out energy, out grad, branchWeight, consolidateWeight, useMaxCov, sharpness, 0.0);
        }

        // 8-arg overload: numerical gradient including all optional terms (branching, consolidation,
        // max-cov toggle, corner sharpness, L1 dihedral sparsity).
        public static void ComputeNumericalGrad(PlanktonMesh P, out double[] energy, out Vec3[] grad, double branchWeight, double consolidateWeight, bool useMaxCov, double sharpness, double crazeWeight)
        {
            int nV = P.Vertices.Count;
            energy = new double[nV];
            grad = new Vec3[nV];

            for (int u = 0; u < nV; u++)
                energy[u] = VertexEnergy(P, u, branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight);

            double eps = 1e-4 * RepresentativeEdge(P);
            if (eps <= 0) eps = 1e-4;

            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) continue;
                grad[v] = new Vec3(Partial(P, v, 0, eps, branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight),
                                    Partial(P, v, 1, eps, branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight),
                                    Partial(P, v, 2, eps, branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight));
            }

            RejectKinkOutliers(P, grad);
        }

        // Vertices on an eigenvalue-crossing (a kink in the min-eigenvalue energy) get
        // garbage, enormous gradients - the energy genuinely jumps there. Left in, they
        // dominate the step and drive the flow UPHILL (this was the crazing). Reject them:
        // zero any gradient far above the median so the smooth majority descends cleanly.
        private static void RejectKinkOutliers(PlanktonMesh P, Vec3[] grad)
        {
            int nV = P.Vertices.Count;
            // grad[v].Length (a sqrt) was previously recomputed ~4x per vertex; compute it once
            // into len[]. The median is built from interior vertices only, but the zeroing pass
            // applies to ALL vertices (matching the original) - bit-identical comparisons.
            double[] len = new double[nV];
            for (int v = 0; v < nV; v++) len[v] = grad[v].Length;
            int m = 0;
            for (int v = 0; v < nV; v++)
                if (!P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v) && len[v] > 0) m++;
            if (m < 4) return;

            double[] mg = new double[m];
            int k = 0;
            for (int v = 0; v < nV; v++)
                if (!P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v) && len[v] > 0) mg[k++] = len[v];
            Array.Sort(mg);
            double thr = 8.0 * mg[m / 2];   // 8x the median magnitude
            if (thr <= 0) return;
            for (int v = 0; v < nV; v++)
                if (len[v] > thr) grad[v] = Vec3.Zero;
        }

        private static double Partial(PlanktonMesh P, int v, int axis, double eps)
        {
            return Partial(P, v, axis, eps, 0.0, 0.0, false, 4.0, 0.0);
        }

        private static double Partial(PlanktonMesh P, int v, int axis, double eps, double branchWeight, double consolidateWeight, bool useMaxCov, double sharpness, double crazeWeight)
        {
            float ox = P.Vertices[v].X, oy = P.Vertices[v].Y, oz = P.Vertices[v].Z;
            int[] nb = P.Vertices.GetVertexNeighbours(v);

            double Ep = LocalEnergySum(P, v, nb, axis, ox, oy, oz, +eps, branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight);
            double Em = LocalEnergySum(P, v, nb, axis, ox, oy, oz, -eps, branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight);
            P.Vertices.SetVertex(v, (double)ox, (double)oy, (double)oz); // restore
            return (Ep - Em) / (2.0 * eps);
        }

        private static double LocalEnergySum(PlanktonMesh P, int v, int[] nb, int axis, float ox, float oy, float oz, double delta, double branchWeight, double consolidateWeight, bool useMaxCov, double sharpness, double crazeWeight)
        {
            double x = ox, y = oy, z = oz;
            if (axis == 0) x += delta; else if (axis == 1) y += delta; else z += delta;
            P.Vertices.SetVertex(v, x, y, z);

            // L1 dihedral term reaches OUT past the immediate 1-ring: phi at the edge (v, u)
            // depends on the third vertex of each adjacent face too, so when we perturb v, the
            // VertexEnergy at v's NEIGHBOUR's neighbours can change. Sum the 2-ring when L1 is on.
            double e = VertexEnergy(P, v, branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight);
            for (int n = 0; n < nb.Length; n++) e += VertexEnergy(P, nb[n], branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight);
            if (crazeWeight > 0)
            {
                for (int n = 0; n < nb.Length; n++)
                {
                    int[] nb2 = P.Vertices.GetVertexNeighbours(nb[n]);
                    for (int m = 0; m < nb2.Length; m++)
                    {
                        int q = nb2[m];
                        if (q == v) continue;
                        bool inNb = false;
                        for (int j = 0; j < nb.Length; j++) if (nb[j] == q) { inNb = true; break; }
                        if (inNb) continue;
                        e += VertexEnergy(P, q, branchWeight, consolidateWeight, useMaxCov, sharpness, crazeWeight);
                    }
                }
            }
            return e;
        }

        // Energy of a single vertex, recomputed from current positions (local normals).
        // Matches the per-vertex energy assigned by ComputeHingeEnergyAndGrad.
        public static double VertexEnergy(PlanktonMesh P, int u)
        {
            return VertexEnergy(P, u, 0.0, 0.0, false, 4.0, 0.0);
        }

        public static double VertexEnergy(PlanktonMesh P, int u, double branchWeight)
        {
            return VertexEnergy(P, u, branchWeight, 0.0, false, 4.0, 0.0);
        }

        public static double VertexEnergy(PlanktonMesh P, int u, double branchWeight, double consolidateWeight)
        {
            return VertexEnergy(P, u, branchWeight, consolidateWeight, false, 4.0, 0.0);
        }

        public static double VertexEnergy(PlanktonMesh P, int u, double branchWeight, double consolidateWeight, bool useMaxCov)
        {
            return VertexEnergy(P, u, branchWeight, consolidateWeight, useMaxCov, 4.0, 0.0);
        }

        public static double VertexEnergy(PlanktonMesh P, int u, double branchWeight, double consolidateWeight, bool useMaxCov, double sharpness)
        {
            return VertexEnergy(P, u, branchWeight, consolidateWeight, useMaxCov, sharpness, 0.0);
        }

        // 7-arg overload also adds the L1 dihedral sparsity (deCraze) - per-vertex share is
        // cornerWeight * crazeWeight * (1/2) * Sum over interior edges incident to u of phi_e.
        // Keep in sync with ComputeHingeEnergyAndGrad: the total energy reported here MUST equal
        // the sum of energy[u] that the analytic path computes, or the FD bench drifts.
        public static double VertexEnergy(PlanktonMesh P, int u, double branchWeight, double consolidateWeight, bool useMaxCov, double sharpness, double crazeWeight)
        {
            if (P.Vertices[u].IsUnused || P.Vertices.IsBoundary(u)) return 0.0;

            int[] uFaces = P.Vertices.GetVertexFaces(u);
            var faces = new List<int>();
            var locIdx = new List<int>();
            var fN = new List<Vec3>();
            Vec3 Nraw = Vec3.Zero;
            double sumDA = 0.0;

            foreach (int f in uFaces)
            {
                if (f < 0 || P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                if (fv.Length != 3) continue;
                int li = Array.IndexOf(fv, u);
                if (li < 0) continue;
                Vec3 a = Pos(P.Vertices[fv[0]]);
                Vec3 b = Pos(P.Vertices[fv[1]]);
                Vec3 c = Pos(P.Vertices[fv[2]]);
                Vec3 cr = Vec3.Cross(b - a, c - a);
                double da = cr.Length;
                Vec3 nf = (da > 1e-16) ? cr / da : Vec3.Zero;
                faces.Add(f); locIdx.Add(li); fN.Add(nf);
                Nraw += da * nf; sumDA += da;
            }

            if (faces.Count < 4) return 0.0;
            if (Nraw.Length < 0.1 * sumDA) return 0.0;   // fold guard (see ComputeHingeEnergyAndGrad)
            Vec3 Nv = Nraw.Normalized();

            // Build covariance matrix and sum theta in one pass; corner guard fires after the loop.
            // Keep in sync with ComputeHingeEnergyAndGrad's per-vertex covariance block.
            double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
            double sumTheta = 0;
            for (int fi = 0; fi < faces.Count; fi++)
            {
                int[] fv = P.Faces.GetFaceVertices(faces[fi]);
                int li = locIdx[fi];
                Vec3 Pi = Pos(P.Vertices[fv[li]]);
                Vec3 Pj = Pos(P.Vertices[fv[(li + 1) % 3]]);
                Vec3 Pk = Pos(P.Vertices[fv[(li + 2) % 3]]);
                double theta = Vec3.Angle(Pj - Pi, Pk - Pi);
                sumTheta += theta;

                Vec3 Nf = fN[fi];
                Vec3 NvxNf = Vec3.Cross(Nv, Nf);
                double sinPhi = NvxNf.Length;
                if (1.0 + sinPhi == 1.0) continue;
                double phi = SafeAcos(Nv * Nf);
                Vec3 muvf = Vec3.Cross(NvxNf, Nv).Normalized();
                Vec3 Nfw = muvf * phi;
                m00 += theta * Nfw.X * Nfw.X; m01 += theta * Nfw.X * Nfw.Y; m02 += theta * Nfw.X * Nfw.Z;
                m11 += theta * Nfw.Y * Nfw.Y; m12 += theta * Nfw.Y * Nfw.Z; m22 += theta * Nfw.Z * Nfw.Z;
            }
            double cornerWeightVE = CornerWeight(Math.Abs(2.0 * Math.PI - sumTheta), sharpness);
            if (cornerWeightVE < 1e-6) return 0.0;

            // Nv is an exact zero-eigenvector of M (every Nfw is perpendicular to it), so
            // the energy is the smaller eigenvalue of the 2x2 tangent-plane block - closed
            // form, no iterative eigensolve. ~10x cheaper than the Jacobi sweep.
            double e = useMaxCov
                ? MaxCovariancePsi(fN)
                : MinTangentEigenvalue(m00, m01, m02, m11, m12, m22, Nv);
            if (branchWeight > 0) e += branchWeight * BranchPsi(Nv, fN);
            if (consolidateWeight > 0) e += consolidateWeight * ConsolidatePsi(fN);
            double weighted = cornerWeightVE * e;

            // L1 dihedral sparsity (deCraze): u's share of |phi_e| summed over edges incident to u
            // is half (the other half goes to the edge's other endpoint via that vertex's own
            // VertexEnergy call) and is corner-weighted by u alone (the other endpoint contributes
            // its own factor in its own VertexEnergy call). Sum over all u then matches the global
            // L1 = Sum_edges |phi_e| with the wAvg corner weighting.
            if (crazeWeight > 0)
            {
                double l1 = 0;
                int[] outH = P.Vertices.GetHalfedges(u);
                foreach (int h in outH)
                {
                    if (P.Halfedges[h].IsUnused) continue;
                    int hP = P.Halfedges.GetPairHalfedge(h);
                    int fA = P.Halfedges[h].AdjacentFace;
                    int fB = P.Halfedges[hP].AdjacentFace;
                    if (fA < 0 || fB < 0) continue;
                    int[] fvA = P.Faces.GetFaceVertices(fA);
                    int[] fvB = P.Faces.GetFaceVertices(fB);
                    if (fvA.Length != 3 || fvB.Length != 3) continue;
                    Vec3 a0 = Pos(P.Vertices[fvA[0]]);
                    Vec3 a1 = Pos(P.Vertices[fvA[1]]);
                    Vec3 a2 = Pos(P.Vertices[fvA[2]]);
                    Vec3 crA = Vec3.Cross(a1 - a0, a2 - a0);
                    double dAA = crA.Length;
                    if (dAA < 1e-16) continue;
                    Vec3 NA = crA / dAA;
                    Vec3 b0 = Pos(P.Vertices[fvB[0]]);
                    Vec3 b1 = Pos(P.Vertices[fvB[1]]);
                    Vec3 b2 = Pos(P.Vertices[fvB[2]]);
                    Vec3 crB = Vec3.Cross(b1 - b0, b2 - b0);
                    double dAB = crB.Length;
                    if (dAB < 1e-16) continue;
                    Vec3 NB = crB / dAB;
                    double cosPhi = NA * NB;
                    if (cosPhi > 1.0) cosPhi = 1.0;
                    else if (cosPhi < -1.0) cosPhi = -1.0;
                    l1 += CrazeHuberVal(Math.Acos(cosPhi));   // Huber-smoothed (CrazeBand) - matches analytic path
                }
                weighted += 0.5 * cornerWeightVE * crazeWeight * l1;
            }
            return weighted;
        }

        // Energy-only B.5.1 branching penalty: psi = min over signed normal pairs (a, b) of
        // (max over (c, d) of <X[c] - X[d], u*_ab>^2), where u*_ab = (Nv x w)/|Nv x w|, w = x_b - x_a,
        // X = {+/- N_f : f in 1-ring}. Same minimum as the analytic-path block in ComputeHingeEnergy
        // AndGrad - kept here as a pure value (no gradient) for VertexEnergy and FD probes.
        private static double BranchPsi(Vec3 Nv, List<Vec3> faceNormals)
        {
            int n = faceNormals.Count;
            if (n < 2) return 0.0;
            int m = 2 * n;

            double psiMin = double.MaxValue;
            for (int a = 0; a < m; a++)
            {
                Vec3 xa = (a & 1) == 0 ? faceNormals[a >> 1] : -faceNormals[a >> 1];
                for (int b = a + 1; b < m; b++)
                {
                    Vec3 xb = (b & 1) == 0 ? faceNormals[b >> 1] : -faceNormals[b >> 1];
                    Vec3 w = xb - xa;
                    Vec3 cross = Vec3.Cross(Nv, w);
                    double cl = cross.Length;
                    if (cl < 1e-12) continue;
                    Vec3 u = cross / cl;

                    double psiAb = 0;
                    for (int c = 0; c < m; c++)
                    {
                        Vec3 xc = (c & 1) == 0 ? faceNormals[c >> 1] : -faceNormals[c >> 1];
                        for (int d = c + 1; d < m; d++)
                        {
                            Vec3 xd = (d & 1) == 0 ? faceNormals[d >> 1] : -faceNormals[d >> 1];
                            double sd = (xc - xd) * u;
                            double s2 = sd * sd;
                            if (s2 > psiAb) psiAb = s2;
                        }
                    }
                    if (psiAb < psiMin) psiMin = psiAb;
                }
            }
            return psiMin >= double.MaxValue ? 0.0 : psiMin;
        }

        // Energy-only B.4 max covariance: lambda^max = min over triples (a, b, c) of +/- signed
        // face normals of max over face normals of <v, N>^2, where v is the spherical centroid of
        // the triple. Same minimum as the analytic-path block - kept here for VertexEnergy and FD.
        private static double MaxCovariancePsi(List<Vec3> faceNormals)
        {
            int n = faceNormals.Count;
            if (n < 3) return 0.0;
            int sm = 2 * n;
            double minPhi = double.MaxValue;
            for (int a = 0; a < sm; a++)
            {
                Vec3 sa = ((a & 1) == 0) ? faceNormals[a >> 1] : -faceNormals[a >> 1];
                for (int b = a + 1; b < sm; b++)
                {
                    Vec3 sb = ((b & 1) == 0) ? faceNormals[b >> 1] : -faceNormals[b >> 1];
                    for (int c = b + 1; c < sm; c++)
                    {
                        Vec3 sc = ((c & 1) == 0) ? faceNormals[c >> 1] : -faceNormals[c >> 1];
                        Vec3 cross = Vec3.Cross(sb - sa, sc - sa);
                        double crossLen = cross.Length;
                        if (crossLen < 1e-12) continue;
                        Vec3 w = cross / crossLen;
                        double phi = 0;
                        for (int k = 0; k < n; k++)
                        {
                            double dot = w * faceNormals[k];
                            double dotSq = dot * dot;
                            if (dotSq > phi) phi = dotSq;
                        }
                        if (phi < minPhi) minPhi = phi;
                    }
                }
            }
            return minPhi >= double.MaxValue ? 0.0 : minPhi;
        }

        // Energy-only B.2 combinatorial (consolidation) penalty: enumerate connected 2-partitions
        // (a, b) of the cyclic n-face fan and return the smallest within-cluster pair-sum of normal
        // squared-differences. Same minimum as the analytic-path block - kept here for VertexEnergy
        // and FD probes.
        private static double ConsolidatePsi(List<Vec3> faceNormals)
        {
            int n = faceNormals.Count;
            if (n < 2) return 0.0;

            double[] pd = new double[n * n];
            for (int i = 0; i < n; i++)
            {
                Vec3 Ni = faceNormals[i];
                for (int j = i + 1; j < n; j++)
                {
                    Vec3 diff = Ni - faceNormals[j];
                    pd[i * n + j] = diff * diff;
                }
            }

            double minE = double.MaxValue;
            for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            {
                double total = 0;
                for (int i = 0; i < n; i++)
                {
                    bool iInC1 = (i >= a) && (i < b);
                    for (int j = i + 1; j < n; j++)
                    {
                        bool jInC1 = (j >= a) && (j < b);
                        if (iInC1 == jInC1) total += pd[i * n + j];
                    }
                }
                if (total < minE) minE = total;
            }
            return minE >= double.MaxValue ? 0.0 : minE;
        }

        private static double MinTangentEigenvalue(double m00, double m01, double m02,
                                                   double m11, double m12, double m22, Vec3 Nv)
        {
            Vec3 xMin, xMax;
            double lMin, lMax;
            TangentEigenpairs(m00, m01, m02, m11, m12, m22, Nv, out lMin, out xMin, out lMax, out xMax);
            return lMin;
        }

        // Backwards-compatible single-eigenpair shim: returns lambda_min + its eigenvector.
        // Kept so existing call sites (the !useMaxCov branch) keep compiling unchanged in mode 0.
        private static double MinTangentEigenpair(double m00, double m01, double m02,
                                                  double m11, double m12, double m22,
                                                  Vec3 Nv, out Vec3 x)
        {
            Vec3 xMin, xMax;
            double lMin, lMax;
            TangentEigenpairs(m00, m01, m02, m11, m12, m22, Nv, out lMin, out xMin, out lMax, out xMax);
            x = xMin;
            return lMin;
        }

        // Both tangent eigenpairs of M (smaller AND larger). The energy is lambda_min in the
        // paper-faithful mode (0); modes 1 (det = lambda_min * lambda_max) and 2 (harmonic mean
        // = det/trace) use both eigenpairs and produce a basis-invariant gradient even when
        // the two eigenvalues are degenerate (icosahedron vertices, symmetric quads, etc.).
        // Both eigenvectors are computed by closed-form 2x2 decomposition in the tangent plane
        // perpendicular to Nv (M's exact null direction).
        private static void TangentEigenpairs(double m00, double m01, double m02,
                                              double m11, double m12, double m22,
                                              Vec3 Nv,
                                              out double lambda_min, out Vec3 x_min,
                                              out double lambda_max, out Vec3 x_max,
                                              Vec3 t1Hint = default(Vec3))
        {
            // orthonormal tangent basis perpendicular to Nv.
            // Prefer t1Hint (a mesh edge direction) so the basis is intrinsic to the mesh and
            // rotates with it. Fall back to world X/Y only when no hint is supplied.
            Vec3 t1;
            double hLen = t1Hint.Length;
            if (hLen > 1e-6)
            {
                double dot = t1Hint * Nv;
                Vec3 proj = new Vec3(t1Hint.X - dot * Nv.X, t1Hint.Y - dot * Nv.Y, t1Hint.Z - dot * Nv.Z);
                double pLen = proj.Length;
                t1 = (pLen > 1e-6) ? proj * (1.0 / pLen) : Vec3.Cross(Nv, new Vec3(0, 1, 0)).Normalized();
            }
            else
            {
                t1 = Vec3.Cross(Nv, new Vec3(1, 0, 0));
                if (t1.Length < 1e-6) t1 = Vec3.Cross(Nv, new Vec3(0, 1, 0));
                t1 = t1.Normalized();
            }
            Vec3 t2 = Vec3.Cross(Nv, t1).Normalized();

            double a = QuadForm(m00, m01, m02, m11, m12, m22, t1, t1);
            double b = QuadForm(m00, m01, m02, m11, m12, m22, t1, t2);
            double d = QuadForm(m00, m01, m02, m11, m12, m22, t2, t2);

            double half = 0.5 * (a + d);
            double disc = Math.Sqrt(0.25 * (a - d) * (a - d) + b * b);
            lambda_min = half - disc;
            lambda_max = half + disc;

            // eigenvector of [[a,b],[b,d]] for lambda_min: (b, lambda_min - a) or (lambda_min - d, b)
            double c1x = b, c1y = lambda_min - a;
            double c2x = lambda_min - d, c2y = b;
            double y1, y2;
            if (c1x * c1x + c1y * c1y >= c2x * c2x + c2y * c2y) { y1 = c1x; y2 = c1y; }
            else { y1 = c2x; y2 = c2y; }
            Vec3 xv_min = y1 * t1 + y2 * t2;
            double xl_min = xv_min.Length;
            x_min = (xl_min > 1e-300) ? xv_min / xl_min : t1;

            // x_max is perpendicular to x_min in the tangent plane. By choosing it as Nv x x_min
            // we get a unit vector orthogonal to both Nv and x_min (a second tangent axis), and
            // at degeneracy the (x_min, x_max) pair spans the tangent plane regardless of which
            // arbitrary x_min came out - so sums over both eigenvectors are basis-invariant.
            Vec3 xv_max = Vec3.Cross(Nv, x_min);
            double xl_max = xv_max.Length;
            x_max = (xl_max > 1e-300) ? xv_max / xl_max : t2;
        }

        // u^T M w for the symmetric covariance M given by its 6 distinct entries.
        private static double QuadForm(double m00, double m01, double m02,
                                       double m11, double m12, double m22, Vec3 u, Vec3 w)
        {
            double mx = m00 * w.X + m01 * w.Y + m02 * w.Z;
            double my = m01 * w.X + m11 * w.Y + m12 * w.Z;
            double mz = m02 * w.X + m12 * w.Y + m22 * w.Z;
            return u.X * mx + u.Y * my + u.Z * mz;
        }

        private static double RepresentativeEdge(PlanktonMesh P)
        {
            for (int i = 0; i < P.Halfedges.Count; i += 2)
            {
                if (P.Halfedges[i].IsUnused) continue;
                Vec3 a = Pos(P.Vertices[P.Halfedges[i].StartVertex]);
                Vec3 b = Pos(P.Vertices[P.Halfedges[i + 1].StartVertex]);
                double L = (b - a).Length;
                if (L > 0) return L;
            }
            return 1.0;
        }

        // --- Helpers ---

        private static double SafeAcos(double x)
        {
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, x)));
        }

        // Smooth replacement for the binary corner guard. Returns 1 at zero defect (smooth or
        // hinge), 0.5 at defect = pi/4 (where the old hard guard cut over), and falls off as
        // sharpness rises - cube-style corners (defect ~ pi/2) contribute ~20% at sharpness=2,
        // ~6% at sharpness=4 (default), ~1.5% at sharpness=6, ~0.4% at sharpness=8.
        //   w(d) = 1 / (1 + (d / (pi/4))^sharpness)
        // Sharpness = 0 disables the falloff (returns 1 always - no corner preservation).
        private static double CornerWeight(double defectAbs, double sharpness)
        {
            if (sharpness <= 0.0) return 1.0;
            double r = defectAbs / (0.25 * Math.PI);
            return 1.0 / (1.0 + Math.Pow(r, sharpness));
        }

        // Jacobi eigenvalue algorithm for 3x3 symmetric matrix.
        // Returns eigenvalues sorted by |value| ascending, with corresponding eigenvectors.
        private static void SymEigen3x3(
            double a00, double a01, double a02,
            double a11, double a12, double a22,
            double[] evals, Vec3[] evecs)
        {
            // Working copy (full symmetric matrix)
            double[,] A = { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } };
            // Eigenvector matrix (columns = eigenvectors), starts as identity
            double[,] V = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

            for (int iter = 0; iter < 50; iter++)
            {
                // Find largest off-diagonal |A[p,q]|
                int p = 0, q = 1;
                double best = Math.Abs(A[0, 1]);
                if (Math.Abs(A[0, 2]) > best) { best = Math.Abs(A[0, 2]); p = 0; q = 2; }
                if (Math.Abs(A[1, 2]) > best) { best = Math.Abs(A[1, 2]); p = 1; q = 2; }

                if (best < 1e-15) break;

                double app = A[p, p], aqq = A[q, q], apq = A[p, q];
                double tau = (aqq - app) / (2.0 * apq);
                double t = Math.Sign(tau) / (Math.Abs(tau) + Math.Sqrt(1.0 + tau * tau));
                double c = 1.0 / Math.Sqrt(1.0 + t * t);
                double s = t * c;

                // Update A: rotate rows/cols p,q
                A[p, p] = app - t * apq;
                A[q, q] = aqq + t * apq;
                A[p, q] = 0; A[q, p] = 0;

                int r = 3 - p - q; // the other index
                double arp = A[r, p], arq = A[r, q];
                A[r, p] = c * arp - s * arq; A[p, r] = A[r, p];
                A[r, q] = s * arp + c * arq; A[q, r] = A[r, q];

                // Update eigenvectors
                for (int i = 0; i < 3; i++)
                {
                    double vip = V[i, p], viq = V[i, q];
                    V[i, p] = c * vip - s * viq;
                    V[i, q] = s * vip + c * viq;
                }
            }

            // Extract and sort by |eigenvalue| ascending
            int[] idx = { 0, 1, 2 };
            double[] d = { A[0, 0], A[1, 1], A[2, 2] };
            if (Math.Abs(d[idx[0]]) > Math.Abs(d[idx[1]])) { int tmp = idx[0]; idx[0] = idx[1]; idx[1] = tmp; }
            if (Math.Abs(d[idx[0]]) > Math.Abs(d[idx[2]])) { int tmp = idx[0]; idx[0] = idx[2]; idx[2] = tmp; }
            if (Math.Abs(d[idx[1]]) > Math.Abs(d[idx[2]])) { int tmp = idx[1]; idx[1] = idx[2]; idx[2] = tmp; }

            for (int i = 0; i < 3; i++)
            {
                evals[i] = d[idx[i]];
                evecs[i] = new Vec3(V[0, idx[i]], V[1, idx[i]], V[2, idx[i]]);
            }
        }
    }
}
