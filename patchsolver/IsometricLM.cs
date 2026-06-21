using System;
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
    //   posM(vertex v)    : M_v  - M0_v                        (3 rows/vertex)    anchor (also pins M's pose)
    // M' carries no anchor: its in-plane pose (translate/rotate the pattern) is a gauge freedom that
    // leaves every edge length unchanged. The LM damping lambda*I regularises that null space, so the
    // pattern doesn't drift within the damped solve.
    //
    // Each outer iteration solves  (J^T J + lambda I) delta = -J^T r  by MATRIX-FREE Conjugate Gradient
    // (no sparse matrix is assembled - we apply J and its exact transpose J^T directly). The step is
    // accepted if it lowers the energy (lambda *= 0.5) or rejected and re-tried with more damping
    // (lambda *= 4). lambda persists across calls (ref) so the trust region carries between ticks.
    static class IsometricLM
    {
        // Run `outerIters` LM iterations on (M, Mp). Weights define the objective (no step size). lambda
        // persists via ref. Returns the raw E_iso (Sum of squared-length mismatches, unweighted) for the
        // convergence readout - same quantity IsometricSolver.Step returns, so the GUI display is parity.
        public static double Solve(PlanktonMesh M, PlanktonMesh Mp, Vec3[] M0,
                                   double wIso, double wFair, double wPos,
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

            double sIso = Math.Sqrt(Math.Max(wIso, 0.0));
            double sFair = Math.Sqrt(Math.Max(wFair, 0.0));
            double sPos = Math.Sqrt(Math.Max(wPos, 0.0));

            // ---- working positions (linearization point). M: x,y,z ; M': x,y on z=0 ----
            double[] Mx = new double[nV], My = new double[nV], Mz = new double[nV];
            double[] Px = new double[nV], Py = new double[nV];
            for (int v = 0; v < nV; v++)
            { var p = M.Vertices[v]; Mx[v] = p.X; My[v] = p.Y; Mz[v] = p.Z; var q = Mp.Vertices[v]; Px[v] = q.X; Py[v] = q.Y; }

            int oP = 3 * nV;               // offset of the M' variables inside a length-N vector
            int N = 3 * nV + 2 * nV;       // variables: M (xyz) + M' (xy)
            int R_FM = nE;                 // residual blocks: [iso nE][fairM 3nV][fairP 2nV][posM 3nV]
            int R_FP = R_FM + 3 * nV;
            int R_PM = R_FP + 2 * nV;
            int nR = R_PM + 3 * nV;

            // scratch buffers (reused across CG iterations to avoid per-apply allocation)
            double[] r0 = new double[nR];      // current residual values
            double[] jvtmp = new double[nR];   // J * v
            double[] b = new double[N];        // -J^T r0
            double[] x = new double[N];        // CG solution (delta)
            double[] cgR = new double[N], cgP = new double[N], cgAp = new double[N];
            double[] tMx = new double[nV], tMy = new double[nV], tMz = new double[nV], tPx = new double[nV], tPy = new double[nV];

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
                        rout[R_FM + 3 * v + 0] = sFair * (mx[v] - ax * inv);
                        rout[R_FM + 3 * v + 1] = sFair * (my[v] - ay * inv);
                        rout[R_FM + 3 * v + 2] = sFair * (mz[v] - az * inv);
                        rout[R_FP + 2 * v + 0] = sFair * (px[v] - bx * inv);
                        rout[R_FP + 2 * v + 1] = sFair * (py[v] - by * inv);
                    }
                if (sPos > 0.0)
                    for (int v = 0; v < nV; v++)
                    {
                        if (!used[v] || M0 == null || v >= M0.Length) continue;
                        rout[R_PM + 3 * v + 0] = sPos * (mx[v] - M0[v].X);
                        rout[R_PM + 3 * v + 1] = sPos * (my[v] - M0[v].Y);
                        rout[R_PM + 3 * v + 2] = sPos * (mz[v] - M0[v].Z);
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
                for (int e = 0; e < nE; e++)
                {
                    int i = ei[e], j = ej[e];
                    double dmx = Mx[i] - Mx[j], dmy = My[i] - My[j], dmz = Mz[i] - Mz[j];
                    double dpx = Px[i] - Px[j], dpy = Py[i] - Py[j];
                    double vmx = v[3 * i] - v[3 * j], vmy = v[3 * i + 1] - v[3 * j + 1], vmz = v[3 * i + 2] - v[3 * j + 2];
                    double vpx = v[oP + 2 * i] - v[oP + 2 * j], vpy = v[oP + 2 * i + 1] - v[oP + 2 * j + 1];
                    rout[e] = sIso * (2.0 * (dmx * vmx + dmy * vmy + dmz * vmz) - 2.0 * (dpx * vpx + dpy * vpy));
                }
                if (sFair > 0.0)
                    for (int vtx = 0; vtx < nV; vtx++)
                    {
                        var nb = nbr[vtx]; int d = nb.Length; if (d == 0) continue;
                        double ax = 0, ay = 0, az = 0, bx = 0, by = 0; double inv = 1.0 / d;
                        for (int k = 0; k < d; k++) { int u = nb[k]; ax += v[3 * u]; ay += v[3 * u + 1]; az += v[3 * u + 2]; bx += v[oP + 2 * u]; by += v[oP + 2 * u + 1]; }
                        rout[R_FM + 3 * vtx + 0] = sFair * (v[3 * vtx] - ax * inv);
                        rout[R_FM + 3 * vtx + 1] = sFair * (v[3 * vtx + 1] - ay * inv);
                        rout[R_FM + 3 * vtx + 2] = sFair * (v[3 * vtx + 2] - az * inv);
                        rout[R_FP + 2 * vtx + 0] = sFair * (v[oP + 2 * vtx] - bx * inv);
                        rout[R_FP + 2 * vtx + 1] = sFair * (v[oP + 2 * vtx + 1] - by * inv);
                    }
                if (sPos > 0.0)
                    for (int vtx = 0; vtx < nV; vtx++)
                    {
                        if (!used[vtx]) continue;
                        rout[R_PM + 3 * vtx + 0] = sPos * v[3 * vtx];
                        rout[R_PM + 3 * vtx + 1] = sPos * v[3 * vtx + 1];
                        rout[R_PM + 3 * vtx + 2] = sPos * v[3 * vtx + 2];
                    }
            }

            // ---- J^T r  (exact transpose of ApplyJ) -> vout ----
            void ApplyJt(double[] r, double[] vout)
            {
                Array.Clear(vout, 0, N);
                for (int e = 0; e < nE; e++)
                {
                    int i = ei[e], j = ej[e];
                    double w = sIso * r[e];
                    double dmx = Mx[i] - Mx[j], dmy = My[i] - My[j], dmz = Mz[i] - Mz[j];
                    double dpx = Px[i] - Px[j], dpy = Py[i] - Py[j];
                    double gx = 2.0 * dmx * w, gy = 2.0 * dmy * w, gz = 2.0 * dmz * w;
                    vout[3 * i] += gx; vout[3 * i + 1] += gy; vout[3 * i + 2] += gz;
                    vout[3 * j] -= gx; vout[3 * j + 1] -= gy; vout[3 * j + 2] -= gz;
                    double hx = 2.0 * dpx * w, hy = 2.0 * dpy * w;
                    vout[oP + 2 * i] -= hx; vout[oP + 2 * i + 1] -= hy;
                    vout[oP + 2 * j] += hx; vout[oP + 2 * j + 1] += hy;
                }
                if (sFair > 0.0)
                    for (int vtx = 0; vtx < nV; vtx++)
                    {
                        var nb = nbr[vtx]; int d = nb.Length; if (d == 0) continue;
                        double inv = 1.0 / d;
                        double wmx = sFair * r[R_FM + 3 * vtx + 0], wmy = sFair * r[R_FM + 3 * vtx + 1], wmz = sFair * r[R_FM + 3 * vtx + 2];
                        double wpx = sFair * r[R_FP + 2 * vtx + 0], wpy = sFair * r[R_FP + 2 * vtx + 1];
                        vout[3 * vtx] += wmx; vout[3 * vtx + 1] += wmy; vout[3 * vtx + 2] += wmz;
                        vout[oP + 2 * vtx] += wpx; vout[oP + 2 * vtx + 1] += wpy;
                        for (int k = 0; k < d; k++)
                        {
                            int u = nb[k];
                            vout[3 * u] -= wmx * inv; vout[3 * u + 1] -= wmy * inv; vout[3 * u + 2] -= wmz * inv;
                            vout[oP + 2 * u] -= wpx * inv; vout[oP + 2 * u + 1] -= wpy * inv;
                        }
                    }
                if (sPos > 0.0)
                    for (int vtx = 0; vtx < nV; vtx++)
                    {
                        if (!used[vtx]) continue;
                        vout[3 * vtx] += sPos * r[R_PM + 3 * vtx + 0];
                        vout[3 * vtx + 1] += sPos * r[R_PM + 3 * vtx + 1];
                        vout[3 * vtx + 2] += sPos * r[R_PM + 3 * vtx + 2];
                    }
            }

            double Dot(double[] a, double[] c) { double s = 0; for (int k = 0; k < a.Length; k++) s += a[k] * c[k]; return s; }

            // ---- LM outer loop ----
            double curE = Energy(Mx, My, Mz, Px, Py);
            if (lambda <= 0) lambda = 1.0;
            for (int outer = 0; outer < outerIters; outer++)
            {
                ComputeR(Mx, My, Mz, Px, Py, r0);
                ApplyJt(r0, b);                        // b0 = J^T r0  (we solve A x = -b0)
                for (int k = 0; k < N; k++) b[k] = -b[k];

                bool accepted = false;
                for (int tries = 0; tries < 6 && !accepted; tries++)
                {
                    double lam = lambda;
                    // ---- matrix-free CG: solve (J^T J + lam I) x = b ----
                    Array.Clear(x, 0, N);
                    Array.Copy(b, cgR, N);             // r = b - A*0 = b
                    Array.Copy(b, cgP, N);
                    double rs = Dot(cgR, cgR), rs0 = rs;
                    for (int it = 0; it < cgIters && rs > 1e-18 * (rs0 + 1e-30); it++)
                    {
                        ApplyJ(cgP, jvtmp); ApplyJt(jvtmp, cgAp);          // cgAp = J^T J p
                        for (int k = 0; k < N; k++) cgAp[k] += lam * cgP[k];  // + lambda p
                        double pAp = Dot(cgP, cgAp); if (pAp <= 0) break;
                        double alpha = rs / pAp;
                        for (int k = 0; k < N; k++) { x[k] += alpha * cgP[k]; cgR[k] -= alpha * cgAp[k]; }
                        double rsn = Dot(cgR, cgR), beta = rsn / rs;
                        for (int k = 0; k < N; k++) cgP[k] = cgR[k] + beta * cgP[k];
                        rs = rsn;
                    }

                    // ---- trial step + accept/reject ----
                    for (int v = 0; v < nV; v++)
                    {
                        tMx[v] = Mx[v] + x[3 * v]; tMy[v] = My[v] + x[3 * v + 1]; tMz[v] = Mz[v] + x[3 * v + 2];
                        tPx[v] = Px[v] + x[oP + 2 * v]; tPy[v] = Py[v] + x[oP + 2 * v + 1];
                    }
                    double trialE = Energy(tMx, tMy, tMz, tPx, tPy);
                    if (trialE < curE)
                    {
                        Array.Copy(tMx, Mx, nV); Array.Copy(tMy, My, nV); Array.Copy(tMz, Mz, nV);
                        Array.Copy(tPx, Px, nV); Array.Copy(tPy, Py, nV);
                        curE = trialE; lambda = Math.Max(lambda * 0.5, 1e-9); accepted = true;
                    }
                    else lambda = Math.Min(lambda * 4.0, 1e12);
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
