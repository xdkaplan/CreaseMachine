using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Plankton;
using CreaseMachine;

namespace CreasePatchSolver
{
    // Levenberg-Marquardt solver for discrete isometry (developability): co-refines the 3D mesh M and
    // its flat image M' (on z=0) so corresponding edge lengths match, while M stays near M0 (anchor)
    // and both stay smooth (fairness). When M is isometric to a FLAT M', M is developable.
    //
    // This is the nonlinear-least-squares solver the paper uses (optimized via Levenberg-Marquardt,
    // Madsen et al. 2004). It replaces the hand-tuned explicit Jacobi step (IsometricSolver.Step):
    // LM solves a damped normal-equation system each iteration, which hands back the optimal step
    // DIRECTION and MAGNITUDE - so there is no Step knob to tune - and the damping lambda auto-adapts
    // (a trust region: shrink it when a step succeeds -> Gauss-Newton, quadratic convergence; grow it
    // when a step fails -> gradient-descent, safe). The result drives relErr -> 0 cleanly and is robust
    // to the weight scale that made the explicit step diverge.
    //
    // Residuals (each pre-multiplied by sqrt(weight) so Sum r^2 == the weighted energy):
    //   iso(edge e=(i,j)) : |Mi-Mj|^2 - |M'i-M'j|^2           (nE scalar rows)   developability driver
    //   fairM(vertex v)   : M_v  - mean(M  neighbours)        (3 rows/vertex)    uniform-Laplacian fairness
    //   fairP(vertex v)   : M'_v - mean(M' neighbours)        (2 rows/vertex)
    //   posM(vertex v)    : M_v  - M0_v                        (3 rows/vertex)    anchor (optional, wPos)
    //   scale             : Sum_e|M edge|^2 - S0               (1 scalar row)     global scale pin (wScale)
    // The dense posM anchor is optional. Its only essential job is anti-collapse: near the developable
    // state, scaling M and M' together keeps them ~isometric, so the iso term barely resists a uniform
    // shrink. The single `scale` row pins that one global mode against the frozen original size S0 -
    // a far lighter reference than pinning every vertex. M' carries no anchor: its in-plane pose is a
    // gauge freedom (every edge length is invariant to it); the LM damping lambda*I regularises it.
    //
    // Each outer iteration solves  (J^T J + lambda I) delta = -J^T r  by MATRIX-FREE Conjugate Gradient
    // (no sparse matrix is assembled - we apply J and its exact transpose J^T directly). The step is
    // accepted if it lowers the energy (lambda *= 0.5) or rejected and re-tried with more damping
    // (lambda *= 4). lambda persists across calls (ref) so the trust region carries between ticks.
    static class IsometricLM
    {
        // FD gradient gate (test-only; default off so production pays nothing). When DebugGradCheck is set,
        // the first outer iteration central-differences the total energy E=||r||^2 against the analytic
        // gradient 2*(J^T r) over every variable and stores the max relative error in LastGradCheckErr.
        // A correct J^T gives <~1e-5; a sign/missing-term bug gives O(1). This is the patchsolver analogue
        // of the covariance engine's GradCheck, and the gate for any ApplyJ/ApplyJt rewrite (e.g. parallel).
        public static bool DebugGradCheck = false;
        public static double LastGradCheckErr = 0.0;

        // Element-count threshold above which the matrix-free apply parallelises (below it, fork/join would
        // dominate). Tunable for benchmarking; raising it past the mesh size forces the serial path.
        public static int ParThreshold = 2000;

        // Run `outerIters` LM iterations on (M, Mp). Weights define the objective (no step size). lambda
        // persists via ref. Returns the raw E_iso (Sum of squared-length mismatches, unweighted) for the
        // convergence readout - same quantity IsometricSolver.Step returns, so the GUI display is parity.
        public static double Solve(PlanktonMesh M, PlanktonMesh Mp, Vec3[] M0,
                                   double wIso, double wFair, double wPos, double wScale, bool diffFair, double wBend, bool bendDiff,
                                   int outerIters, int cgIters, ref double lambda)
        {
            int nV = M.Vertices.Count;
            if (nV == 0 || Mp.Vertices.Count != nV) return 0.0;

            // ---- topology: edge list + neighbour lists (built once per call) ----
            int nH = M.Halfedges.Count;
            int[] ei = new int[nH / 2], ej = new int[nH / 2]; int nE = 0;
            for (int h = 0; h < nH; h += 2)
            {
                if (M.Halfedges[h].IsUnused) continue;
                int i = M.Halfedges[h].StartVertex, j = M.Halfedges[h + 1].StartVertex;
                if (i < 0 || j < 0 || M.Vertices[i].IsUnused || M.Vertices[j].IsUnused) continue;
                ei[nE] = i; ej[nE] = j; nE++;
            }
            bool[] used = new bool[nV];
            int[][] nbr = new int[nV][];
            for (int v = 0; v < nV; v++)
            {
                used[v] = !M.Vertices[v].IsUnused;
                nbr[v] = used[v] ? (M.Vertices.GetVertexNeighbours(v) ?? Array.Empty<int>()) : Array.Empty<int>();
            }

            // per-vertex incident-edge CSR: lets J^T r be assembled as a GATHER (each vertex sums its own
            // incident edges) rather than a SCATTER (edges add to both endpoints). The gather is race-free
            // and deterministic, so the J^T apply can be parallelised over vertices with no accumulators.
            // veSign[t] = +1 if this vertex is the i-endpoint of edge veEdge[t], -1 if the j-endpoint.
            int[] veStart = new int[nV + 1];
            for (int e = 0; e < nE; e++) { veStart[ei[e]]++; veStart[ej[e]]++; }
            for (int v = 0, acc = 0; v <= nV; v++) { int c = veStart[v]; veStart[v] = acc; acc += c; }
            int[] veEdge = new int[2 * nE]; sbyte[] veSign = new sbyte[2 * nE]; int[] veFill = new int[nV];
            for (int e = 0; e < nE; e++)
            {
                int i = ei[e], j = ej[e];
                int pi = veStart[i] + veFill[i]++; veEdge[pi] = e; veSign[pi] = +1;
                int pj = veStart[j] + veFill[j]++; veEdge[pj] = e; veSign[pj] = -1;
            }

            // parallelism for the matrix-free apply (the CG-inner hot path). Every wrapped loop is write-local
            // or gather-by-vertex, so chunked Partitioner ranges are race-free and bit-identical to serial;
            // gated on size so small meshes (where fork/join dominates) stay serial. Capped at ProcessorCount-2
            // like the covariance engine. The Solve bake is synchronous, so this does not contend with live UI.
            int maxPar = Math.Max(1, Environment.ProcessorCount - 2);
            var parOpts = new ParallelOptions { MaxDegreeOfParallelism = maxPar };
            bool parV = nV >= ParThreshold, parE = nE >= ParThreshold;
            int vChunk = Math.Max(64, (nV + maxPar * 4 - 1) / (maxPar * 4));
            int eChunk = Math.Max(64, (nE + maxPar * 4 - 1) / (maxPar * 4));
            void Par(bool on, int n, int chunk, Action<int, int> body)
            {
                if (on) Parallel.ForEach(Partitioner.Create(0, n, chunk), parOpts, rg => body(rg.Item1, rg.Item2));
                else body(0, n);
            }

            double sIso = Math.Sqrt(Math.Max(wIso, 0.0));
            double sFair = Math.Sqrt(Math.Max(wFair, 0.0));
            double sPos = Math.Sqrt(Math.Max(wPos, 0.0));
            double sScale = Math.Sqrt(Math.Max(wScale, 0.0));
            double sBend = Math.Sqrt(Math.Max(wBend, 0.0));
            double S0 = 0.0;   // frozen original total squared edge length (global scale-pin target)
            if (sScale > 0.0 && M0 != null)
                for (int e = 0; e < nE; e++) { int i = ei[e], j = ej[e]; if (i < M0.Length && j < M0.Length) { Vec3 d0 = M0[i] - M0[j]; S0 += d0 * d0; } }

            // differential-coordinate fairness reference: the ORIGINAL (M0) uniform Laplacian per vertex,
            // computed exactly like the fairness residual. With diffFair on, fairness penalizes the CHANGE
            // in (v - neighbour-centroid) from the original rather than driving it to zero - so it de-buckles
            // WITHOUT shrinking (preserves the input's local detail). M-only (the buckling surface).
            Vec3[] lap0 = null;
            if (diffFair && sFair > 0.0 && M0 != null)
            {
                lap0 = new Vec3[nV];
                for (int v = 0; v < nV; v++)
                {
                    var nb = nbr[v]; int d = nb.Length; if (d == 0 || v >= M0.Length) continue;
                    Vec3 a = Vec3.Zero; for (int k = 0; k < d; k++) { int u = nb[k]; if (u >= 0 && u < M0.Length) a += M0[u]; }
                    lap0[v] = M0[v] - a * (1.0 / d);
                }
            }

            // ---- working positions (linearization point). M: x,y,z ; M': x,y on z=0 ----
            double[] Mx = new double[nV], My = new double[nV], Mz = new double[nV];
            double[] Px = new double[nV], Py = new double[nV];
            for (int v = 0; v < nV; v++)
            { var p = M.Vertices[v]; Mx[v] = p.X; My[v] = p.Y; Mz[v] = p.Z; var q = Mp.Vertices[v]; Px[v] = q.X; Py[v] = q.Y; }

            int oP = 3 * nV;               // offset of the M' variables inside a length-N vector
            int N = 3 * nV + 2 * nV;       // variables: M (xyz) + M' (xy)
            int R_FM = nE;                 // residual blocks: [iso nE][fairM 3nV][fairP 2nV][posM 3nV][scale 1]
            int R_FP = R_FM + 3 * nV;
            int R_PM = R_FP + 2 * nV;
            int R_SC = R_PM + 3 * nV;      // global scale pin: one scalar row (Sum|M edge|^2 - S0)
            int R_BI = R_SC + 1;           // 2nd-order bending (bi-Laplacian U^2): 3nV rows, appended last
            int nR = R_BI + 3 * nV;

            // scratch buffers (reused across CG iterations to avoid per-apply allocation)
            double[] r0 = new double[nR];      // current residual values
            double[] jvtmp = new double[nR];   // J * v
            double[] b = new double[N];        // -J^T r0
            double[] x = new double[N];        // CG solution (delta)
            double[] cgR = new double[N], cgP = new double[N], cgAp = new double[N], cgZ = new double[N], cgMinv = new double[N], cgDiag = new double[N];
            double[] gsx = new double[nV], gsy = new double[nV], gsz = new double[nV];   // scale-row gradient (for the Jacobi diagonal)
            double[] eDMx = new double[nE], eDMy = new double[nE], eDMz = new double[nE], eDPx = new double[nE], eDPy = new double[nE];   // per-edge linearization vectors (constant within an outer iter; precomputed to avoid recomputing in every CG apply)
            double[] tMx = new double[nV], tMy = new double[nV], tMz = new double[nV], tPx = new double[nV], tPy = new double[nV];

            // ---- 2nd-order bending (bi-Laplacian) setup. U(x)_i = mean_nbr(x) - x_i (umbrella Laplacian);
            // U^2 is the discrete bi-Laplacian. Bending penalises U^2(M) - U^2(M0) (DIFFERENTIAL): it removes
            // NEW wrinkles while keeping the original's smooth curvature, and is non-shrinking. This is the
            // triangle analog of the paper's 2nd-difference fairness Sum|v_i - 2v_j + v_k|^2 - the smoothness
            // term applied INSIDE the optimisation (a post-filter like Taubin cannot supply it). ----
            double[] pmB = new double[3 * nV], t1B = new double[3 * nV], t2B = new double[3 * nV];
            void Umb(double[] inp, double[] outp)
            {
                Par(parV, nV, vChunk, (lo, hi) =>
                {
                    for (int v = lo; v < hi; v++)
                    {
                        int bb = 3 * v; var nb = nbr[v]; int d = nb.Length;
                        if (!used[v] || d == 0) { outp[bb] = outp[bb + 1] = outp[bb + 2] = 0; continue; }
                        double ax = 0, ay = 0, az = 0;
                        for (int k = 0; k < d; k++) { int u = nb[k]; ax += inp[3 * u]; ay += inp[3 * u + 1]; az += inp[3 * u + 2]; }
                        double iv = 1.0 / d;
                        outp[bb] = ax * iv - inp[bb]; outp[bb + 1] = ay * iv - inp[bb + 1]; outp[bb + 2] = az * iv - inp[bb + 2];
                    }
                });
            }
            void UmbT(double[] inp, double[] outp)   // exact transpose of Umb, as a per-vertex GATHER (race-free):
            {                                        // (U^T r)_w = -r_w + sum_{s in nbr(w), used, deg>0} r_s / deg(s)
                Par(parV, nV, vChunk, (lo, hi) =>
                {
                    for (int w = lo; w < hi; w++)
                    {
                        int bb = 3 * w;
                        double sx = used[w] ? -inp[bb] : 0.0, sy = used[w] ? -inp[bb + 1] : 0.0, sz = used[w] ? -inp[bb + 2] : 0.0;
                        var nb = nbr[w]; int d = nb.Length;
                        for (int k = 0; k < d; k++)
                        {
                            int s = nb[k]; int ds = nbr[s].Length; if (!used[s] || ds == 0) continue;
                            double iv = 1.0 / ds; sx += inp[3 * s] * iv; sy += inp[3 * s + 1] * iv; sz += inp[3 * s + 2] * iv;
                        }
                        outp[bb] = sx; outp[bb + 1] = sy; outp[bb + 2] = sz;
                    }
                });
            }
            // bendDiff=true  -> DIFFERENTIAL bending vs U^2(M0): preserve original curvature (low-drift mode).
            // bendDiff=false -> PLAIN bending toward U^2=0: drive to the SMOOTHEST shape -> a single entirely
            // developable patch (the paper's mode, paired with no anchor / free deformation).
            Vec3[] biRef = null;   // U^2(M0): the original curvature reference for differential bending
            if (sBend > 0.0 && M0 != null && bendDiff)
            {
                for (int v = 0; v < nV; v++) { pmB[3 * v] = M0[v].X; pmB[3 * v + 1] = M0[v].Y; pmB[3 * v + 2] = M0[v].Z; }
                Umb(pmB, t1B); Umb(t1B, t2B); biRef = new Vec3[nV];
                for (int v = 0; v < nV; v++) biRef[v] = new Vec3(t2B[3 * v], t2B[3 * v + 1], t2B[3 * v + 2]);
            }

            // ---- residual values at given positions (nonlinear) -> rout (length nR) ----
            void ComputeR(double[] mx, double[] my, double[] mz, double[] px, double[] py, double[] rout)
            {
                Array.Clear(rout, 0, nR);
                for (int e = 0; e < nE; e++)
                {
                    int i = ei[e], j = ej[e];
                    double dmx = mx[i] - mx[j], dmy = my[i] - my[j], dmz = mz[i] - mz[j];
                    double dpx = px[i] - px[j], dpy = py[i] - py[j];
                    rout[e] = sIso * ((dmx * dmx + dmy * dmy + dmz * dmz) - (dpx * dpx + dpy * dpy));
                }
                if (sFair > 0.0)
                    for (int v = 0; v < nV; v++)
                    {
                        var nb = nbr[v]; int d = nb.Length; if (d == 0) continue;
                        double ax = 0, ay = 0, az = 0, bx = 0, by = 0;
                        for (int k = 0; k < d; k++) { int u = nb[k]; ax += mx[u]; ay += my[u]; az += mz[u]; bx += px[u]; by += py[u]; }
                        double inv = 1.0 / d;
                        double fmx = mx[v] - ax * inv, fmy = my[v] - ay * inv, fmz = mz[v] - az * inv;
                        if (lap0 != null) { fmx -= lap0[v].X; fmy -= lap0[v].Y; fmz -= lap0[v].Z; }   // differential: change from original
                        rout[R_FM + 3 * v + 0] = sFair * fmx;
                        rout[R_FM + 3 * v + 1] = sFair * fmy;
                        rout[R_FM + 3 * v + 2] = sFair * fmz;
                        if (!diffFair) { rout[R_FP + 2 * v + 0] = sFair * (px[v] - bx * inv); rout[R_FP + 2 * v + 1] = sFair * (py[v] - by * inv); }   // M' fairness off in differential mode (M-only, matches validated lab)
                    }
                if (sPos > 0.0)
                    for (int v = 0; v < nV; v++)
                    {
                        if (!used[v] || M0 == null || v >= M0.Length) continue;
                        rout[R_PM + 3 * v + 0] = sPos * (mx[v] - M0[v].X);
                        rout[R_PM + 3 * v + 1] = sPos * (my[v] - M0[v].Y);
                        rout[R_PM + 3 * v + 2] = sPos * (mz[v] - M0[v].Z);
                    }
                if (sScale > 0.0)
                {
                    double sum = 0.0;
                    for (int e = 0; e < nE; e++) { int i = ei[e], j = ej[e]; double dx = mx[i] - mx[j], dy = my[i] - my[j], dz = mz[i] - mz[j]; sum += dx * dx + dy * dy + dz * dz; }
                    rout[R_SC] = (S0 > 0.0) ? sScale * (sum / S0 - 1.0) : 0.0;   // relative -> wScale dimensionless
                }
                if (sBend > 0.0)
                {
                    for (int v = 0; v < nV; v++) { pmB[3 * v] = mx[v]; pmB[3 * v + 1] = my[v]; pmB[3 * v + 2] = mz[v]; }
                    Umb(pmB, t1B); Umb(t1B, t2B);
                    for (int v = 0; v < nV; v++)
                    {
                        if (!used[v]) continue;
                        double bx = t2B[3 * v], by = t2B[3 * v + 1], bz = t2B[3 * v + 2];
                        if (biRef != null) { bx -= biRef[v].X; by -= biRef[v].Y; bz -= biRef[v].Z; }
                        rout[R_BI + 3 * v + 0] = sBend * bx; rout[R_BI + 3 * v + 1] = sBend * by; rout[R_BI + 3 * v + 2] = sBend * bz;
                    }
                }
            }

            double Energy(double[] mx, double[] my, double[] mz, double[] px, double[] py)
            {
                ComputeR(mx, my, mz, px, py, jvtmp);     // reuse jvtmp as residual scratch
                double s = 0; for (int k = 0; k < nR; k++) s += jvtmp[k] * jvtmp[k]; return s;
            }

            // ---- J * v  (linearized at current Mx..,Px..) -> rout ----
            void ApplyJ(double[] v, double[] rout)
            {
                Array.Clear(rout, 0, nR);
                Par(parE, nE, eChunk, (lo, hi) =>
                {
                    for (int e = lo; e < hi; e++)
                    {
                        int i = ei[e], j = ej[e];
                        double dmx = eDMx[e], dmy = eDMy[e], dmz = eDMz[e];
                        double dpx = eDPx[e], dpy = eDPy[e];
                        double vmx = v[3 * i] - v[3 * j], vmy = v[3 * i + 1] - v[3 * j + 1], vmz = v[3 * i + 2] - v[3 * j + 2];
                        double vpx = v[oP + 2 * i] - v[oP + 2 * j], vpy = v[oP + 2 * i + 1] - v[oP + 2 * j + 1];
                        rout[e] = sIso * (2.0 * (dmx * vmx + dmy * vmy + dmz * vmz) - 2.0 * (dpx * vpx + dpy * vpy));
                    }
                });
                if (sFair > 0.0)
                    Par(parV, nV, vChunk, (lo, hi) =>
                    {
                        for (int vtx = lo; vtx < hi; vtx++)
                        {
                            var nb = nbr[vtx]; int d = nb.Length; if (d == 0) continue;
                            double ax = 0, ay = 0, az = 0, bx = 0, by = 0; double inv = 1.0 / d;
                            for (int k = 0; k < d; k++) { int u = nb[k]; ax += v[3 * u]; ay += v[3 * u + 1]; az += v[3 * u + 2]; bx += v[oP + 2 * u]; by += v[oP + 2 * u + 1]; }
                            rout[R_FM + 3 * vtx + 0] = sFair * (v[3 * vtx] - ax * inv);
                            rout[R_FM + 3 * vtx + 1] = sFair * (v[3 * vtx + 1] - ay * inv);
                            rout[R_FM + 3 * vtx + 2] = sFair * (v[3 * vtx + 2] - az * inv);
                            if (!diffFair) { rout[R_FP + 2 * vtx + 0] = sFair * (v[oP + 2 * vtx] - bx * inv); rout[R_FP + 2 * vtx + 1] = sFair * (v[oP + 2 * vtx + 1] - by * inv); }
                        }
                    });
                if (sPos > 0.0)
                    for (int vtx = 0; vtx < nV; vtx++)
                    {
                        if (!used[vtx]) continue;
                        rout[R_PM + 3 * vtx + 0] = sPos * v[3 * vtx];
                        rout[R_PM + 3 * vtx + 1] = sPos * v[3 * vtx + 1];
                        rout[R_PM + 3 * vtx + 2] = sPos * v[3 * vtx + 2];
                    }
                if (sScale > 0.0)
                {
                    double acc = 0.0;
                    for (int e = 0; e < nE; e++)
                    {
                        int i = ei[e], j = ej[e];
                        double dmx = eDMx[e], dmy = eDMy[e], dmz = eDMz[e];
                        double vmx = v[3 * i] - v[3 * j], vmy = v[3 * i + 1] - v[3 * j + 1], vmz = v[3 * i + 2] - v[3 * j + 2];
                        acc += 2.0 * (dmx * vmx + dmy * vmy + dmz * vmz);
                    }
                    rout[R_SC] = (S0 > 0.0) ? sScale * acc / S0 : 0.0;
                }
                if (sBend > 0.0)
                {
                    for (int vt = 0; vt < nV; vt++) { pmB[3 * vt] = v[3 * vt]; pmB[3 * vt + 1] = v[3 * vt + 1]; pmB[3 * vt + 2] = v[3 * vt + 2]; }
                    Umb(pmB, t1B); Umb(t1B, t2B);
                    for (int vt = 0; vt < nV; vt++) { if (!used[vt]) continue; rout[R_BI + 3 * vt + 0] = sBend * t2B[3 * vt]; rout[R_BI + 3 * vt + 1] = sBend * t2B[3 * vt + 1]; rout[R_BI + 3 * vt + 2] = sBend * t2B[3 * vt + 2]; }
                }
            }

            // ---- J^T r  (exact transpose of ApplyJ), assembled as a per-vertex GATHER: each vertex sums its
            //      OWN incident edges (via the veStart/veEdge/veSign CSR) and 1-ring, so every vout slot is
            //      written by exactly one iteration -> race-free + deterministic when parallelised over v. ----
            void ApplyJt(double[] r, double[] vout)
            {
                double wsc = (sScale > 0.0 && S0 > 0.0) ? sScale * r[R_SC] / S0 : 0.0;   // global scale-row weight
                Par(parV, nV, vChunk, (lo, hi) =>
                {
                    for (int v = lo; v < hi; v++)
                    {
                        double gx = 0, gy = 0, gz = 0, hx = 0, hy = 0;
                        // iso + scale: both scatter 2*dM*weight to the two endpoints (opposite signs); gather them
                        // with veSign. iso also touches M' (opposite sign); scale does not.
                        int es = veStart[v], ee = veStart[v + 1];
                        for (int t = es; t < ee; t++)
                        {
                            int e = veEdge[t]; double s = veSign[t];
                            double wi = sIso * r[e];
                            double cm = 2.0 * s * (wi + wsc), cp = 2.0 * s * wi;
                            gx += cm * eDMx[e]; gy += cm * eDMy[e]; gz += cm * eDMz[e];
                            hx -= cp * eDPx[e]; hy -= cp * eDPy[e];
                        }
                        // fairness: own row + gather of -sFair*r_fair[u]/deg(u) over the 1-ring (symmetric adjacency).
                        if (sFair > 0.0)
                        {
                            var nb = nbr[v]; int d = nb.Length;
                            if (d > 0)
                            {
                                gx += sFair * r[R_FM + 3 * v + 0]; gy += sFair * r[R_FM + 3 * v + 1]; gz += sFair * r[R_FM + 3 * v + 2];
                                hx += sFair * r[R_FP + 2 * v + 0]; hy += sFair * r[R_FP + 2 * v + 1];
                                for (int k = 0; k < d; k++)
                                {
                                    int u = nb[k]; int du = nbr[u].Length; if (du == 0) continue; double iu = 1.0 / du;
                                    gx -= sFair * r[R_FM + 3 * u + 0] * iu; gy -= sFair * r[R_FM + 3 * u + 1] * iu; gz -= sFair * r[R_FM + 3 * u + 2] * iu;
                                    hx -= sFair * r[R_FP + 2 * u + 0] * iu; hy -= sFair * r[R_FP + 2 * u + 1] * iu;
                                }
                            }
                        }
                        if (sPos > 0.0 && used[v]) { gx += sPos * r[R_PM + 3 * v + 0]; gy += sPos * r[R_PM + 3 * v + 1]; gz += sPos * r[R_PM + 3 * v + 2]; }
                        vout[3 * v] = gx; vout[3 * v + 1] = gy; vout[3 * v + 2] = gz;
                        vout[oP + 2 * v] = hx; vout[oP + 2 * v + 1] = hy;
                    }
                });
                if (sBend > 0.0)   // U^2^T r = (U^T)(U^T) r, each UmbT a gather; add into the M rows
                {
                    for (int vt = 0; vt < nV; vt++) { pmB[3 * vt] = sBend * r[R_BI + 3 * vt]; pmB[3 * vt + 1] = sBend * r[R_BI + 3 * vt + 1]; pmB[3 * vt + 2] = sBend * r[R_BI + 3 * vt + 2]; }
                    UmbT(pmB, t1B); UmbT(t1B, t2B);
                    for (int vt = 0; vt < nV; vt++) { vout[3 * vt] += t2B[3 * vt]; vout[3 * vt + 1] += t2B[3 * vt + 1]; vout[3 * vt + 2] += t2B[3 * vt + 2]; }
                }
            }

            double Dot(double[] a, double[] c) { double s = 0; for (int k = 0; k < a.Length; k++) s += a[k] * c[k]; return s; }

            // diag(J^T J) for Jacobi (diagonal) preconditioning of CG: sum of squared Jacobian column entries
            // per variable. iso + scale + fairness + pos handled exactly; bending (bi-Laplacian) is smooth and
            // left to the lambda floor. The CG was hitting its iteration cap un-converged (ill-conditioned),
            // so this is the high-value win. Computed once per outer iteration (depends on the linearization).
            void DiagJtJ(double[] dg)
            {
                Array.Clear(dg, 0, N);
                for (int e = 0; e < nE; e++)
                {
                    int i = ei[e], j = ej[e];
                    double cx = 2.0 * sIso * (Mx[i] - Mx[j]), cy = 2.0 * sIso * (My[i] - My[j]), cz = 2.0 * sIso * (Mz[i] - Mz[j]);
                    double ex = cx * cx, ey = cy * cy, ez = cz * cz;
                    dg[3 * i] += ex; dg[3 * i + 1] += ey; dg[3 * i + 2] += ez;
                    dg[3 * j] += ex; dg[3 * j + 1] += ey; dg[3 * j + 2] += ez;
                    double hx = 2.0 * sIso * (Px[i] - Px[j]), hy = 2.0 * sIso * (Py[i] - Py[j]);
                    double fx = hx * hx, fy = hy * hy;
                    dg[oP + 2 * i] += fx; dg[oP + 2 * i + 1] += fy;
                    dg[oP + 2 * j] += fx; dg[oP + 2 * j + 1] += fy;
                }
                if (sScale > 0.0 && S0 > 0.0)   // one global row: accumulate the per-vertex gradient, then square
                {
                    Array.Clear(gsx, 0, nV); Array.Clear(gsy, 0, nV); Array.Clear(gsz, 0, nV);
                    for (int e = 0; e < nE; e++)
                    {
                        int i = ei[e], j = ej[e];
                        double dx = 2.0 * (Mx[i] - Mx[j]), dy = 2.0 * (My[i] - My[j]), dz = 2.0 * (Mz[i] - Mz[j]);
                        gsx[i] += dx; gsy[i] += dy; gsz[i] += dz; gsx[j] -= dx; gsy[j] -= dy; gsz[j] -= dz;
                    }
                    double s = sScale / S0;
                    for (int v = 0; v < nV; v++) { double a = s * gsx[v], b2 = s * gsy[v], c2 = s * gsz[v]; dg[3 * v] += a * a; dg[3 * v + 1] += b2 * b2; dg[3 * v + 2] += c2 * c2; }
                }
                if (sFair > 0.0)
                    for (int v = 0; v < nV; v++)
                    {
                        var nb = nbr[v]; int d = nb.Length; if (d == 0) continue;
                        double diag = sFair * sFair;
                        for (int k = 0; k < d; k++) { int u = nb[k]; int du = nbr[u].Length; if (du > 0) diag += sFair * sFair / ((double)du * du); }
                        dg[3 * v] += diag; dg[3 * v + 1] += diag; dg[3 * v + 2] += diag;
                        if (!diffFair) { dg[oP + 2 * v] += diag; dg[oP + 2 * v + 1] += diag; }
                    }
                if (sPos > 0.0)
                    for (int v = 0; v < nV; v++) { if (!used[v]) continue; double p = sPos * sPos; dg[3 * v] += p; dg[3 * v + 1] += p; dg[3 * v + 2] += p; }
                if (sBend > 0.0)   // bending diag estimate: sBend^2*(U^2 self-coeff)^2, so Minv doesn't blow up here
                    for (int v = 0; v < nV; v++)
                    {
                        if (!used[v]) continue; var nb = nbr[v]; int d = nb.Length; if (d == 0) continue;
                        double iv = 1.0 / d, u2 = 1.0;                              // (U^2)_vv = 1 + sum_{k in nbr} 1/(d_v d_k)
                        for (int k = 0; k < d; k++) { int du = nbr[nb[k]].Length; if (du > 0) u2 += iv / du; }
                        double bd = sBend * sBend * u2 * u2;
                        dg[3 * v] += bd; dg[3 * v + 1] += bd; dg[3 * v + 2] += bd;
                    }
            }

            // ---- LM outer loop ----
            double curE = Energy(Mx, My, Mz, Px, Py);
            if (lambda <= 0) lambda = 1.0;
            double nu = 2.0;   // Nielsen damping: reject-doubling factor
            for (int outer = 0; outer < outerIters; outer++)
            {
                ComputeR(Mx, My, Mz, Px, Py, r0);
                for (int e = 0; e < nE; e++) { int i = ei[e], j = ej[e]; eDMx[e] = Mx[i] - Mx[j]; eDMy[e] = My[i] - My[j]; eDMz[e] = Mz[i] - Mz[j]; eDPx[e] = Px[i] - Px[j]; eDPy[e] = Py[i] - Py[j]; }   // freeze edge vectors for this outer iter
                ApplyJt(r0, b);                        // b0 = J^T r0  (we solve A x = -b0)
                if (DebugGradCheck && outer == 0)      // central-difference E=||r||^2 vs analytic 2*(J^T r)=2*b
                {
                    double eps = 1e-6, maxAbsDiff = 0.0, gInf = 0.0;
                    for (int k = 0; k < N; k++)
                    {
                        double[] arr; int idx;
                        if (k < 3 * nV) { int v = k / 3, c = k % 3; arr = c == 0 ? Mx : (c == 1 ? My : Mz); idx = v; }
                        else { int k2 = k - oP, v = k2 / 2; arr = (k2 % 2 == 0) ? Px : Py; idx = v; }
                        double save = arr[idx];
                        arr[idx] = save + eps; double ep = Energy(Mx, My, Mz, Px, Py);
                        arr[idx] = save - eps; double em = Energy(Mx, My, Mz, Px, Py);
                        arr[idx] = save;
                        double fd = (ep - em) / (2.0 * eps), an = 2.0 * b[k];
                        double ad = Math.Abs(fd - an); if (ad > maxAbsDiff) maxAbsDiff = ad;
                        double aa = Math.Abs(an); if (aa > gInf) gInf = aa;
                    }
                    LastGradCheckErr = maxAbsDiff / (gInf + 1e-12);   // max abs error normalized by gradient inf-norm
                }
                for (int k = 0; k < N; k++) b[k] = -b[k];
                DiagJtJ(cgDiag);                       // Jacobi diagonal (fixed per outer iter; lambda added per try)

                bool accepted = false;
                for (int tries = 0; tries < 6 && !accepted; tries++)
                {
                    double lam = lambda;
                    for (int k = 0; k < N; k++) cgMinv[k] = 1.0 / (cgDiag[k] + lam);   // M^-1 = 1/(diag(J^T J) + lambda)
                    // ---- matrix-free Jacobi-PRECONDITIONED CG: solve (J^T J + lam I) x = b ----
                    Array.Clear(x, 0, N);
                    Array.Copy(b, cgR, N);             // r = b - A*0 = b
                    for (int k = 0; k < N; k++) cgZ[k] = cgMinv[k] * cgR[k];   // z = M^-1 r
                    Array.Copy(cgZ, cgP, N);
                    double rz = Dot(cgR, cgZ), rr0 = Dot(cgR, cgR);
                    for (int it = 0; it < cgIters && Dot(cgR, cgR) > 1e-18 * (rr0 + 1e-30); it++)
                    {
                        ApplyJ(cgP, jvtmp); ApplyJt(jvtmp, cgAp);          // cgAp = J^T J p
                        for (int k = 0; k < N; k++) cgAp[k] += lam * cgP[k];  // + lambda p
                        double pAp = Dot(cgP, cgAp); if (pAp <= 0) break;
                        double alpha = rz / pAp;
                        for (int k = 0; k < N; k++) { x[k] += alpha * cgP[k]; cgR[k] -= alpha * cgAp[k]; }
                        for (int k = 0; k < N; k++) cgZ[k] = cgMinv[k] * cgR[k];   // apply preconditioner
                        double rzn = Dot(cgR, cgZ), beta = rzn / rz;
                        for (int k = 0; k < N; k++) cgP[k] = cgZ[k] + beta * cgP[k];
                        rz = rzn;
                    }

                    // ---- trial step + accept/reject ----
                    for (int v = 0; v < nV; v++)
                    {
                        tMx[v] = Mx[v] + x[3 * v]; tMy[v] = My[v] + x[3 * v + 1]; tMz[v] = Mz[v] + x[3 * v + 2];
                        tPx[v] = Px[v] + x[oP + 2 * v]; tPy[v] = Py[v] + x[oP + 2 * v + 1];
                    }
                    double trialE = Energy(tMx, tMy, tMz, tPx, tPy);
                    // Nielsen gain-ratio damping (Madsen et al. 2004): size lambda from the actual-vs-predicted
                    // energy reduction so a good step lands on the first try far more often -> fewer rejected
                    // tries (each of which re-runs a full CG). rho ~ 1 means the local model was accurate.
                    double predicted = 0.0; for (int k = 0; k < N; k++) predicted += x[k] * (lam * x[k] + b[k]);
                    double rho = predicted > 0.0 ? (curE - trialE) / predicted : (trialE < curE ? 1.0 : -1.0);
                    if (rho > 0.0 && trialE < curE)   // accept (model agrees with a genuine decrease)
                    {
                        Array.Copy(tMx, Mx, nV); Array.Copy(tMy, My, nV); Array.Copy(tMz, Mz, nV);
                        Array.Copy(tPx, Px, nV); Array.Copy(tPy, Py, nV);
                        curE = trialE; accepted = true;
                        double g = 2.0 * rho - 1.0;
                        lambda = Math.Max(lambda * Math.Max(1.0 / 3.0, 1.0 - g * g * g), 1e-9); nu = 2.0;
                    }
                    else { lambda = Math.Min(lambda * nu, 1e12); nu *= 2.0; }
                }
                if (!accepted) break;   // damping maxed out without progress -> converged / stuck
            }

            // ---- write positions back + compute raw E_iso readout ----
            double eIso = 0.0;
            for (int v = 0; v < nV; v++)
            {
                if (!used[v]) continue;
                M.Vertices.SetVertex(v, (float)Mx[v], (float)My[v], (float)Mz[v]);
                Mp.Vertices.SetVertex(v, (float)Px[v], (float)Py[v], 0f);
            }
            for (int e = 0; e < nE; e++)
            {
                int i = ei[e], j = ej[e];
                double dm = (Mx[i] - Mx[j]) * (Mx[i] - Mx[j]) + (My[i] - My[j]) * (My[i] - My[j]) + (Mz[i] - Mz[j]) * (Mz[i] - Mz[j]);
                double dp = (Px[i] - Px[j]) * (Px[i] - Px[j]) + (Py[i] - Py[j]) * (Py[i] - Py[j]);
                eIso += (dm - dp) * (dm - dp);
            }
            return eIso;
        }
    }
}
