using System;
using System.Collections.Generic;
using Plankton;
using CreaseMachine;

// Finite-difference gradient checker, plus self-tests that validate the checker
// itself against energies whose gradients are known by hand.
class Program
{
    delegate void EnergyFunc(PlanktonMesh P, out double[] energy, out Vec3[] grad);

    static int Main()
    {
        PlanktonMesh P = BuildBumpyGrid(7);
        Console.WriteLine("mesh: " + P.Vertices.Count + " verts, " + P.Faces.Count + " faces");
        Console.WriteLine();

        Console.WriteLine("=== Harness self-test (gradients known by hand) ===");
        // Sum of squared edge lengths. Cross-vertex dependence like the dev energy,
        // so it exercises the same 'sum-all-energy, perturb, difference' path.
        Check("edge-len^2  CORRECT grad",      EdgeEnergy(1.0), P);   // expect PASS
        Check("edge-len^2  WRONG grad (x1.3)", EdgeEnergy(1.3), P);   // expect FAIL
        Check("edge-len^2  WRONG grad (x1.0 but flipped sign)", EdgeEnergy(-1.0), P); // expect FAIL
        Console.WriteLine();

        Console.WriteLine("=== Developability energy ===");
        Check("DevelopabilityEnergy  (analytic)", DevelopabilityEnergy.ComputeHingeEnergyAndGrad, P);
        AnalyzeDev(P);
        Console.WriteLine();
        Check("ComputeNumericalGrad   (the fix)", DevelopabilityEnergy.ComputeNumericalGrad, P);

        Console.WriteLine();
        FlowTest(BuildBumpyGrid(11), 400);
        Console.WriteLine();
        ScaleInvarianceTest();
        Console.WriteLine();
        OptimizerComparison();
        Console.WriteLine();
        DegeneracyTest();
        Console.WriteLine();
        CollapseTest();
        Console.WriteLine();
        ExplodeDiagnostic(@"C:\Temp\AboutToExplode.stl");
        Console.WriteLine();
        FlowAndWatch(@"C:\Temp\AboutToExplode.stl", 40, 0.05, 0.9);
        Console.WriteLine();
        TrackPoint(@"C:\Temp\AboutToExplode.stl", 41.815, 5.542, 11.818, 20, 0.05, 0.9);
        return 0;
    }

    // Locate the vertex nearest a world point and follow its fan through the flow: report its
    // gradient and the MIN coherence in its 1-ring each step (so we see a fold forming), and
    // whether the fold guard is skipping the offender.
    static void TrackPoint(string path, double tx, double ty, double tz, int steps, double step, double beta)
    {
        Console.WriteLine("=== Track fan about (" + tx + ", " + ty + ", " + tz + ") ===");
        if (!System.IO.File.Exists(path)) { Console.WriteLine("  (file not found)"); return; }
        PlanktonMesh P = LoadBinaryStl(path);

        Func<int> nearest = () =>
        {
            int best = -1; double bd = double.MaxValue;
            for (int v = 0; v < P.Vertices.Count; v++)
            {
                if (P.Vertices[v].IsUnused) continue;
                double dx = P.Vertices[v].X - tx, dy = P.Vertices[v].Y - ty, dz = P.Vertices[v].Z - tz;
                double d = dx * dx + dy * dy + dz * dz;
                if (d < bd) { bd = d; best = v; }
            }
            return best;
        };
        int c0 = nearest();
        Console.WriteLine("  nearest vertex = " + c0 + " at distance " +
            Math.Sqrt((P.Vertices[c0].X - tx) * (P.Vertices[c0].X - tx) + (P.Vertices[c0].Y - ty) * (P.Vertices[c0].Y - ty) + (P.Vertices[c0].Z - tz) * (P.Vertices[c0].Z - tz)).ToString("G4") + ", valence " + P.Vertices.GetVertexNeighbours(c0).Length);

        Vec3[] vel = new Vec3[P.Vertices.Count];
        for (int s = 0; s < steps; s++)
        {
            int nV = P.Vertices.Count;
            if (vel.Length != nV) vel = new Vec3[nV];
            double L = RepEdge(P);
            double t = step * L * L, cap = L;

            double[] bx = new double[nV], by = new double[nV], bz = new double[nV];
            for (int v = 0; v < nV; v++)
            {
                bx[v] = P.Vertices[v].X; by[v] = P.Vertices[v].Y; bz[v] = P.Vertices[v].Z;
                if (beta > 0 && !P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v) && vel[v].IsValid)
                    P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
            }
            double[] e; Vec3[] g;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g);   // lookahead

            // diagnose the fan at the target (nearest vertex + its 1-ring), at the lookahead
            int c = nearest();
            int[] ring = P.Vertices.GetVertexNeighbours(c);
            int[] fan = new int[ring.Length + 1]; fan[0] = c; Array.Copy(ring, 0, fan, 1, ring.Length);
            double minCoher = double.MaxValue, maxGradFan = 0; int worst = c;
            foreach (int u in fan)
            {
                double rawLen, sumDA, mE, mA; int val;
                VertexDiag(P, u, out rawLen, out sumDA, out mE, out mA, out val);
                double coh = sumDA > 0 ? rawLen / sumDA : 0;
                if (coh < minCoher) minCoher = coh;
                if (g[u].Length > maxGradFan) { maxGradFan = g[u].Length; worst = u; }
            }
            Console.WriteLine("  step " + s.ToString().PadLeft(2) + "  fanMaxGrad=" + maxGradFan.ToString("G5").PadRight(11) +
                "  minCoher=" + minCoher.ToString("G4").PadRight(9) + "  (guard skips <0.1)");

            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) { P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
                Vec3 gg = g[v];
                if (!gg.IsValid) { vel[v] = Vec3.Zero; P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
                vel[v] = beta * vel[v] - t * gg;
                double vl = vel[v].Length; if (vl > cap && vl > 1e-20) vel[v] = vel[v] * (cap / vl);
                P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
            }
        }
    }

    // Run the EXACT SheetBender flow (collapse + Nesterov momentum + velocity cap, energy guard
    // active) on the mesh and watch for the explosion; on blow-up, dump the culprit vertex so we
    // can see the failing quantity (coherence = fold? minAspect = sliver? minEdge = short edge?).
    static void FlowAndWatch(string path, int steps, double step, double beta)
    {
        Console.WriteLine("=== Flow & watch (Step=" + step + ", beta=" + beta + ") ===");
        if (!System.IO.File.Exists(path)) { Console.WriteLine("  (file not found)"); return; }
        PlanktonMesh P = LoadBinaryStl(path);
        Vec3[] vel = new Vec3[P.Vertices.Count];

        for (int s = 0; s < steps; s++)
        {
            if (MeshOps.CollapseShortEdges(P, 0.2) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; }
            int nV = P.Vertices.Count;
            if (vel.Length != nV) vel = new Vec3[nV];
            double L = RepEdge(P);
            double t = step * L * L, cap = L;

            double[] bx = new double[nV], by = new double[nV], bz = new double[nV];
            for (int v = 0; v < nV; v++)
            {
                bx[v] = P.Vertices[v].X; by[v] = P.Vertices[v].Y; bz[v] = P.Vertices[v].Z;
                if (beta > 0 && !P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v) && vel[v].IsValid)
                    P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
            }
            double[] e; Vec3[] g; bool[] fold;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out fold);   // P is at the LOOKAHEAD
            double maxg = 0; int argmax = -1;
            for (int v = 0; v < nV; v++) { double m = g[v].Length; if (m > maxg) { maxg = m; argmax = v; } }
            Console.WriteLine("  step " + s.ToString().PadLeft(2) + " verts=" + nV + " maxGrad=" + maxg.ToString("G5"));

            if (double.IsNaN(maxg) || maxg > 30)
            {
                Console.WriteLine("  *** SPIKE at step " + s + ", vertex " + argmax + " |grad|=" + maxg.ToString("G5") +
                    "  -- vertex + its 1-ring (lookahead config):");
                int[] nbrs = P.Vertices.GetVertexNeighbours(argmax);
                int[] show = new int[nbrs.Length + 1];
                show[0] = argmax; Array.Copy(nbrs, 0, show, 1, nbrs.Length);
                foreach (int u in show)
                {
                    double rawLen, sumDA, minEdge, minAspect; int val;
                    VertexDiag(P, u, out rawLen, out sumDA, out minEdge, out minAspect, out val);
                    Console.WriteLine("     v" + u.ToString().PadLeft(3) +
                        "  grad=" + g[u].Length.ToString("G4").PadRight(10) +
                        "  coher=" + (sumDA > 0 ? rawLen / sumDA : 0).ToString("G4").PadRight(9) +
                        "  minAsp=" + minAspect.ToString("G4").PadRight(9) +
                        "  minEdge=" + minEdge.ToString("G4").PadRight(8) +
                        "  val=" + val);
                }
                break;
            }

            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) { P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
                Vec3 gg = g[v];
                if (!gg.IsValid) { vel[v] = Vec3.Zero; P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
                vel[v] = beta * vel[v] - t * gg;
                double vl = vel[v].Length; if (vl > cap && vl > 1e-20) vel[v] = vel[v] * (cap / vl);
                P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
            }

            int healed = MeshOps.CollapseFolds(P, fold);
            if (healed > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; Console.WriteLine("    healed " + healed + " fold(s) -> verts=" + P.Vertices.Count); }
        }
    }

    // Load the user's "about to explode" mesh and find WHICH vertices have the biggest gradient
    // and WHY - is it a sliver (low aspect), a short edge, or an incoherent vertex normal (a
    // near-180 fold, where Nv = rawVec/|rawVec| and the 1/|rawVec| chain-rule factor blows up)?
    static void ExplodeDiagnostic(string path)
    {
        Console.WriteLine("=== Explode diagnostic ===");
        if (!System.IO.File.Exists(path)) { Console.WriteLine("  (file not found: " + path + ")"); return; }
        PlanktonMesh P = LoadBinaryStl(path);
        int interior = 0;
        for (int v = 0; v < P.Vertices.Count; v++) if (!P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v)) interior++;
        Console.WriteLine("  loaded " + P.Vertices.Count + " verts, " + P.Faces.Count + " faces, " + interior + " interior");

        double[] e; Vec3[] g;
        DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g);

        int[] idx = new int[P.Vertices.Count];
        for (int v = 0; v < idx.Length; v++) idx[v] = v;
        Array.Sort(idx, (x, y) => g[y].Length.CompareTo(g[x].Length));

        Console.WriteLine("  rank vert  |grad|      coherence  minEdge    minAspect   valence");
        for (int r = 0; r < Math.Min(8, idx.Length); r++)
        {
            int v = idx[r];
            double rawLen, sumDA, minEdge, minAspect; int val;
            VertexDiag(P, v, out rawLen, out sumDA, out minEdge, out minAspect, out val);
            double coher = sumDA > 0 ? rawLen / sumDA : 0;
            Console.WriteLine("  " + r.ToString().PadRight(4) + " " + v.ToString().PadRight(5) +
                " " + g[v].Length.ToString("G5").PadRight(11) +
                " " + coher.ToString("G4").PadRight(10) +
                " " + minEdge.ToString("G4").PadRight(10) +
                " " + minAspect.ToString("G4").PadRight(11) +
                " " + val);
        }
    }

    static void VertexDiag(PlanktonMesh P, int v, out double rawLen, out double sumDA, out double minEdge, out double minAspect, out int valence)
    {
        Vec3 raw = Vec3.Zero; sumDA = 0; minAspect = double.MaxValue;
        int[] vf = P.Vertices.GetVertexFaces(v);
        foreach (int f in vf)
        {
            if (f < 0 || P.Faces[f].IsUnused) continue;
            int[] fv = P.Faces.GetFaceVertices(f); if (fv.Length != 3) continue;
            Vec3 a = Pos(P, fv[0]), b = Pos(P, fv[1]), c = Pos(P, fv[2]);
            Vec3 cr = Vec3.Cross(b - a, c - a); double dA = cr.Length;
            Vec3 N = dA > 1e-16 ? cr / dA : Vec3.Zero;
            raw += dA * N; sumDA += dA;
            double maxL2 = Math.Max((b - a) * (b - a), Math.Max((c - b) * (c - b), (a - c) * (a - c)));
            double asp = maxL2 > 0 ? dA / maxL2 : 0; if (asp < minAspect) minAspect = asp;
        }
        rawLen = raw.Length;
        int[] nb = P.Vertices.GetVertexNeighbours(v); valence = nb.Length;
        minEdge = double.MaxValue;
        foreach (int n in nb) { double d = (Pos(P, v) - Pos(P, n)).Length; if (d < minEdge) minEdge = d; }
        if (minEdge == double.MaxValue) minEdge = 0;
        if (minAspect == double.MaxValue) minAspect = 0;
    }

    static PlanktonMesh LoadBinaryStl(string path)
    {
        byte[] b = System.IO.File.ReadAllBytes(path);
        int nTri = BitConverter.ToInt32(b, 80);
        const int baseOff = 84;

        double scale = 0;
        for (int t = 0; t < nTri; t++)
        {
            int o = baseOff + t * 50 + 12;
            for (int k = 0; k < 9; k++) { double c = Math.Abs(BitConverter.ToSingle(b, o + k * 4)); if (c > scale) scale = c; }
        }
        double tol = scale > 0 ? scale * 1e-5 : 1e-5;

        var P = new PlanktonMesh();
        var map = new Dictionary<string, int>();
        for (int t = 0; t < nTri; t++)
        {
            int o = baseOff + t * 50 + 12;
            int[] vidx = new int[3];
            for (int k = 0; k < 3; k++)
            {
                float x = BitConverter.ToSingle(b, o + (k * 3 + 0) * 4);
                float y = BitConverter.ToSingle(b, o + (k * 3 + 1) * 4);
                float z = BitConverter.ToSingle(b, o + (k * 3 + 2) * 4);
                long kx = (long)Math.Round(x / tol), ky = (long)Math.Round(y / tol), kz = (long)Math.Round(z / tol);
                string key = kx + "_" + ky + "_" + kz;
                int vi;
                if (!map.TryGetValue(key, out vi)) { vi = P.Vertices.Add(x, y, z); map[key] = vi; }
                vidx[k] = vi;
            }
            if (vidx[0] != vidx[1] && vidx[1] != vidx[2] && vidx[0] != vidx[2])
                P.Faces.AddFace(vidx[0], vidx[1], vidx[2]);
        }
        return P;
    }

    // Make a short edge, then confirm the simple collapse removes it and the result is a valid
    // mesh whose gradient is finite (no leftover degenerate triangles).
    static void CollapseTest()
    {
        Console.WriteLine("=== Simple collapse (short-edge removal) ===");
        PlanktonMesh P = BuildBumpyGrid(5);
        int v0 = P.Vertices.Count, f0 = P.Faces.Count;

        int vi = 12, nb = 11;                 // squash center vertex onto its neighbour -> short edge
        Vec3 pn = new Vec3(P.Vertices[nb].X, P.Vertices[nb].Y, P.Vertices[nb].Z);
        Vec3 pv = new Vec3(P.Vertices[vi].X, P.Vertices[vi].Y, P.Vertices[vi].Z);
        Vec3 np = pn + (pv - pn) * 0.02;
        P.Vertices.SetVertex(vi, np.X, np.Y, np.Z);

        int n = MeshOps.CollapseShortEdges(P, 0.2);
        P.Compact();

        double[] e; Vec3[] g;
        DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g);
        bool bad = false; double maxg = 0;
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            double m = g[v].Length;
            if (double.IsNaN(m) || double.IsInfinity(m)) bad = true;
            if (m > maxg) maxg = m;
        }
        Console.WriteLine("  collapses=" + n +
            "   verts " + v0 + "->" + P.Vertices.Count +
            "   faces " + f0 + "->" + P.Faces.Count +
            "   gradOK=" + (!bad) + "  maxGrad=" + maxg.ToString("G5"));
    }

    // Squash one interior vertex toward a neighbour to make progressively thinner slivers, and
    // check the gradient stays bounded. Degenerate triangles make the 1/area and 1/edge^2 terms
    // in the analytic gradient blow up - the spikes that corrupt the flow.
    static void DegeneracyTest()
    {
        Console.WriteLine("=== Degenerate-triangle robustness (gradient must stay BOUNDED) ===");
        foreach (double squash in new double[] { 0.5, 0.1, 0.01, 1e-3, 1e-5, 1e-8 })
        {
            PlanktonMesh P = BuildBumpyGrid(5);
            int vi = 12, nb = 11;                 // center vertex, move it toward its left neighbour
            Vec3 pv = new Vec3(P.Vertices[vi].X, P.Vertices[vi].Y, P.Vertices[vi].Z);
            Vec3 pn = new Vec3(P.Vertices[nb].X, P.Vertices[nb].Y, P.Vertices[nb].Z);
            Vec3 np = pn + (pv - pn) * squash;    // squash->0 => vi collapses onto nb
            P.Vertices.SetVertex(vi, np.X, np.Y, np.Z);

            double[] e; Vec3[] g;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g);
            double maxg = 0; bool bad = false;
            for (int v = 0; v < P.Vertices.Count; v++)
            {
                double m = g[v].Length;
                if (double.IsNaN(m) || double.IsInfinity(m)) bad = true;
                if (m > maxg) maxg = m;
            }
            Console.WriteLine("  squash=" + squash.ToString("G2").PadRight(7) +
                "  maxGrad=" + (bad ? "NaN/Inf !!" : maxg.ToString("G6")));
        }
    }

    // Fixed-step descent vs Nesterov momentum at the SAME Step. Metric that matters for big
    // meshes: gradient evals (= steps) to reach a target energy. Both are 1 grad eval/step.
    static void OptimizerComparison()
    {
        Console.WriteLine("=== Optimizer: fixed-step vs Nesterov momentum (grid 11, Step=0.05) ===");
        const int steps = 200;
        const double step = 0.05;
        const double target = 0.05;
        double[] betas = { 0.0, 0.5, 0.8, 0.9, 0.95 };
        Console.WriteLine("  beta   endEnergy    steps->" + target.ToString("0.00") + "   monotone   note");
        foreach (double beta in betas)
        {
            double[] tr = FlowEnergiesNesterov(BuildBumpyGrid(11), steps, step, beta);
            double startE = tr[0], endE = tr[tr.Length - 1];
            int hit = -1;
            for (int i = 0; i < tr.Length; i++) { if (double.IsNaN(tr[i])) break; if (tr[i] < target) { hit = i; break; } }
            bool rose = false, nan = false;
            for (int i = 1; i < tr.Length; i++) { if (double.IsNaN(tr[i])) { nan = true; break; } if (tr[i] > tr[i - 1] + 1e-9) rose = true; }
            string note = nan ? "DIVERGED(NaN)" : (endE > startE ? "DIVERGED" : "");
            Console.WriteLine("  " + beta.ToString("0.00") + "   " + endE.ToString("G6").PadRight(11) +
                "  " + (hit < 0 ? (">" + steps) : hit.ToString()).PadRight(11) +
                "  " + (rose ? "no " : "yes").PadRight(9) + "  " + note);
        }
        Console.WriteLine("  (beta=0 is plain fixed-step descent; lower steps->target = cheaper on big meshes)");

        // Step-size stability limit: fixed-step (b=0) vs momentum (b=0.9), deep target 0.02.
        Console.WriteLine("  --- Step-size stability: fixed-step vs momentum b=0.9, 400 steps ---");
        const double tgt2 = 0.02;
        foreach (double st in new double[] { 0.05, 0.08, 0.12, 0.20 })
        {
            string f = OneMomRun(st, 0.0, 400, tgt2);
            string m = OneMomRun(st, 0.9, 400, tgt2);
            Console.WriteLine("  Step=" + st.ToString("0.00") + "   fixed: " + f + "   |  mom0.9: " + m);
        }
    }

    static string OneMomRun(double step, double beta, int steps, double target)
    {
        double[] tr = FlowEnergiesNesterov(BuildBumpyGrid(11), steps, step, beta);
        double startE = tr[0], endE = tr[tr.Length - 1];
        int hit = -1; bool nan = false;
        for (int i = 0; i < tr.Length; i++) { if (double.IsNaN(tr[i])) { nan = true; break; } if (hit < 0 && tr[i] < target) hit = i; }
        string note = nan ? "NaN" : (endE > startE ? "DIVERGED" : (endE > 5 ? "unstable" : "ok"));
        return "endE=" + endE.ToString("G5").PadRight(10) + " ->0.02=" + (hit < 0 ? ">400" : hit.ToString()).PadRight(5) + " " + note;
    }

    // Nesterov-accelerated descent. beta=0 reduces exactly to fixed-step. The gradient is
    // evaluated at the LOOKAHEAD x + beta*v (Nesterov's correction, more stable than heavy-ball),
    // and uses the RAW gradient - no magnitude normalization - so the velocity decays as the
    // gradient vanishes (self-damping preserved). Energy is reported at the true iterate x.
    static double[] FlowEnergiesNesterov(PlanktonMesh P, int steps, double step, double beta)
    {
        double L = RepEdge(P);
        double alpha = step * L * L;
        int nV = P.Vertices.Count;
        Vec3[] vel = new Vec3[nV];
        double[] bx = new double[nV], by = new double[nV], bz = new double[nV];
        double[] traj = new double[steps + 1];

        for (int s = 0; s <= steps; s++)
        {
            traj[s] = SumEnergy(P);                       // energy at the current iterate x
            if (s == steps || double.IsNaN(traj[s])) break;

            // save base x, move to lookahead x + beta*v
            for (int v = 0; v < nV; v++)
            {
                bx[v] = P.Vertices[v].X; by[v] = P.Vertices[v].Y; bz[v] = P.Vertices[v].Z;
                if (!P.Vertices.IsBoundary(v))
                    P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
            }

            double[] e; Vec3[] g;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g);   // grad at lookahead

            // v = beta*v - alpha*g ;  x = base + v   (update straight from saved base)
            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices.IsBoundary(v)) { P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
                vel[v] = beta * vel[v] - alpha * g[v];
                P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
            }

        }
        return traj;
    }

    static double SumEnergy(PlanktonMesh P)
    {
        double s = 0;
        for (int v = 0; v < P.Vertices.Count; v++) s += DevelopabilityEnergy.VertexEnergy(P, v);
        return s;
    }

    // The same Step must produce the SAME energy trajectory at any mesh scale: the dev energy
    // is scale-invariant and the step is scaled as L^2, so a 10x-larger mesh flows identically.
    // (With the wrong Step*L scaling the 10x run would descend ~10x slower - a clean discriminator.)
    static void ScaleInvarianceTest()
    {
        Console.WriteLine("=== Scale-invariance (Step*L^2): energy trajectory must match across scale ===");
        double[] e1 = FlowEnergies(BuildBumpyGridScaled(9, 1.0), 60, 0.05);
        double[] e10 = FlowEnergies(BuildBumpyGridScaled(9, 10.0), 60, 0.05);
        double maxRel = 0.0;
        for (int i = 0; i < e1.Length; i++)
        {
            double r = Math.Abs(e1[i] - e10[i]) / (Math.Abs(e1[i]) + 1e-12);
            if (r > maxRel) maxRel = r;
        }
        Console.WriteLine("  scale  1x: E0=" + e1[0].ToString("G6") + "  end=" + e1[e1.Length - 1].ToString("G6"));
        Console.WriteLine("  scale 10x: E0=" + e10[0].ToString("G6") + "  end=" + e10[e10.Length - 1].ToString("G6"));
        Console.WriteLine("  max relative trajectory diff = " + (maxRel * 100).ToString("G4") + "%  -> " +
            (maxRel < 1e-3 ? "SCALE-INVARIANT" : "NOT invariant"));
    }

    // Run the SheetBender flow (analytic grad, global step t = step*L^2) and return the energy
    // at each step. Shared shape with FlowTest, minus the printing.
    static double[] FlowEnergies(PlanktonMesh P, int steps, double step)
    {
        double L = RepEdge(P);
        double t = step * L * L;
        double[] traj = new double[steps + 1];
        for (int s = 0; s <= steps; s++)
        {
            double[] e; Vec3[] g;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g);
            traj[s] = Sum(e);
            if (s == steps) break;
            for (int v = 0; v < P.Vertices.Count; v++)
            {
                if (P.Vertices.IsBoundary(v)) continue;
                P.Vertices.SetVertex(v,
                    P.Vertices[v].X - t * g[v].X,
                    P.Vertices[v].Y - t * g[v].Y,
                    P.Vertices[v].Z - t * g[v].Z);
            }
        }
        return traj;
    }

    // Is -grad actually downhill? For a correct gradient, moving a tiny t along -grad
    // must drop the energy by ~ t*|grad|^2. We probe with all gradients, and again with
    // the kink outliers (|grad| >> median, the discontinuity vertices) zeroed out.
    static void DescentProbe(PlanktonMesh P)
    {
        double[] e0; Vec3[] g;
        DevelopabilityEnergy.ComputeNumericalGrad(P, out e0, out g);
        double E0 = Sum(e0);

        int nV = P.Vertices.Count;
        int m = 0; for (int v = 0; v < nV; v++) if (!P.Vertices.IsBoundary(v)) m++;
        double[] mags = new double[m]; int k = 0;
        for (int v = 0; v < nV; v++) if (!P.Vertices.IsBoundary(v)) mags[k++] = g[v].Length;
        System.Array.Sort(mags);
        double median = mags[m / 2], max = mags[m - 1];
        Console.WriteLine("=== Descent-direction probe ===  E0=" + E0.ToString("G6") +
            "  median|grad|=" + median.ToString("G4") + "  max|grad|=" + max.ToString("G4"));

        Probe(P, g, E0, -1.0);            // all gradients
        Probe(P, g, E0, 5.0 * median);   // kink outliers (|grad| > 5*median) zeroed
    }

    static void Probe(PlanktonMesh P, Vec3[] g0, double E0, double clampThresh)
    {
        int nV = P.Vertices.Count;
        Vec3[] g = (Vec3[])g0.Clone();
        int zeroed = 0;
        if (clampThresh > 0)
            for (int v = 0; v < nV; v++) if (g[v].Length > clampThresh) { g[v] = Vec3.Zero; zeroed++; }

        double gnorm2 = 0;
        for (int v = 0; v < nV; v++) if (!P.Vertices.IsBoundary(v)) gnorm2 += g[v] * g[v];

        float[] sx = new float[nV], sy = new float[nV], sz = new float[nV];
        for (int v = 0; v < nV; v++) { sx[v] = P.Vertices[v].X; sy[v] = P.Vertices[v].Y; sz[v] = P.Vertices[v].Z; }

        Console.WriteLine("  " + (clampThresh > 0 ? ("kink-clamped (" + zeroed + " zeroed)") : "all gradients   ") +
            "  |grad|^2=" + gnorm2.ToString("G4") + "   (descent wants dE < 0)");
        foreach (double t in new double[] { 1e-7, 1e-6, 1e-5, 1e-4 })
        {
            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices.IsBoundary(v)) continue;
                P.Vertices.SetVertex(v, sx[v] - t * g[v].X, sy[v] - t * g[v].Y, sz[v] - t * g[v].Z);
            }
            double E1 = 0; for (int v = 0; v < nV; v++) E1 += DevelopabilityEnergy.VertexEnergy(P, v);
            Console.WriteLine("      t=" + t + "   dE=" + (E1 - E0).ToString("G4"));
            for (int v = 0; v < nV; v++) P.Vertices.SetVertex(v, (double)sx[v], (double)sy[v], (double)sz[v]);
        }
    }

    // Pure dev-force gradient flow with a trust-region step. The acid test: a real
    // gradient descent must drive total energy DOWN every step. If it plateaus high
    // or keeps rising, the energy has crinkly local minima / the force is still noisy.
    static void FlowTest(PlanktonMesh P, int steps)
    {
        Console.WriteLine("=== Dev-only gradient flow (ANALYTIC grad, global fixed-step descent) ===");
        double L = RepEdge(P);
        const double step = 0.05;         // global step t (in edge-lengths): V -= step*L*grad
        double startE = 0, prevE = double.MaxValue;
        int rises = 0;

        for (int s = 0; s <= steps; s++)
        {
            double[] e; Vec3[] g;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g);
            double totalE = Sum(e);
            if (s == 0) startE = totalE;

            double maxg = 0;
            for (int v = 0; v < P.Vertices.Count; v++)
                if (!P.Vertices.IsBoundary(v) && g[v].Length > maxg) maxg = g[v].Length;

            if (s % 40 == 0)
                Console.WriteLine("  step " + s.ToString().PadLeft(3) +
                    "  energy=" + totalE.ToString("G6").PadRight(11) +
                    "  maxGrad=" + maxg.ToString("G4"));

            if (totalE > prevE + 1e-9) rises++;
            prevE = totalE;
            if (maxg < 1e-10) { Console.WriteLine("  converged at step " + s); break; }

            // paper-faithful step (reference LINESEARCH_NONE): a SINGLE global step size,
            // p = -grad, V += step*p. No per-vertex cap or normalization (both #undef in the
            // reference). Boundary vertices stay put. This mirrors exactly what SheetBender does:
            // step is a fraction of edge length, scaled as L^2 so it is scale/subdivision-invariant
            // (the dev gradient carries 1/length units). Here L=1, so L^2 == L - same result.
            double t = step * L * L;
            for (int v = 0; v < P.Vertices.Count; v++)
            {
                if (P.Vertices.IsBoundary(v)) continue;
                Vec3 dv = g[v] * (-t);
                Vec3 p = new Vec3(P.Vertices[v].X, P.Vertices[v].Y, P.Vertices[v].Z);
                P.Vertices.SetVertex(v, p.X + dv.X, p.Y + dv.Y, p.Z + dv.Z);
            }
        }

        Console.WriteLine("  start energy = " + startE.ToString("G6") + " ,  end energy = " + prevE.ToString("G6"));
        Console.WriteLine("  energy ROSE on " + rises + " of " + steps + " steps (clean descent -> ~0)");
        WriteObj(P, "test/flow_result.obj");
        Console.WriteLine("  wrote test/flow_result.obj");
    }

    static double RepEdge(PlanktonMesh P)
    {
        for (int i = 0; i < P.Halfedges.Count; i += 2)
        {
            if (P.Halfedges[i].IsUnused) continue;
            Vec3 a = Pos(P, P.Halfedges[i].StartVertex);
            Vec3 b = Pos(P, P.Halfedges[i + 1].StartVertex);
            double len = (b - a).Length;
            if (len > 0) return len;
        }
        return 1.0;
    }

    static void WriteObj(PlanktonMesh P, string path)
    {
        var sb = new System.Text.StringBuilder();
        int[] map = new int[P.Vertices.Count];
        int idx = 1;
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices[v].IsUnused) { map[v] = -1; continue; }
            sb.AppendLine("v " + P.Vertices[v].X + " " + P.Vertices[v].Y + " " + P.Vertices[v].Z);
            map[v] = idx++;
        }
        for (int f = 0; f < P.Faces.Count; f++)
        {
            if (P.Faces[f].IsUnused) continue;
            int[] fv = P.Faces.GetFaceVertices(f);
            if (fv.Length != 3) continue;
            sb.AppendLine("f " + map[fv[0]] + " " + map[fv[1]] + " " + map[fv[2]]);
        }
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    // Classify each interior vertex of the dev energy: does the finite difference
    // CONVERGE as eps shrinks (so a stable disagreement = a real gradient bug) or
    // BLOW UP as eps shrinks (= a jump discontinuity in the energy, gradient formula
    // is fine but the energy is non-smooth there)?
    static void AnalyzeDev(PlanktonMesh P)
    {
        double[] e0; Vec3[] g0;
        DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e0, out g0);
        EnergyFunc f = DevelopabilityEnergy.ComputeHingeEnergyAndGrad;

        int clean = 0, kink = 0, bug = 0, shownBug = 0;
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices.IsBoundary(v)) continue;
            Vec3 ga = g0[v];
            if (ga.Length < 1e-7) continue;

            Vec3 nCoarse = NumGrad(f, P, v, 1e-2);
            Vec3 nFine = NumGrad(f, P, v, 1e-4);

            double relFine = RelErr(ga, nFine);
            if (relFine < 0.05) { clean++; continue; }

            // FD blowing up as eps shrinks -> the energy jumped (discontinuity/kink)
            if (nFine.Length > 3.0 * nCoarse.Length + 1e-9) { kink++; continue; }

            // FD stable across eps but != analytic -> the analytic gradient is wrong here
            bug++;
            if (shownBug < 6)
            {
                Console.WriteLine("    BUG v" + v +
                    "  analytic=(" + F(ga.X) + "," + F(ga.Y) + "," + F(ga.Z) + ")" +
                    "  fd@1e-2=(" + F(nCoarse.X) + "," + F(nCoarse.Y) + "," + F(nCoarse.Z) + ")" +
                    "  fd@1e-4=(" + F(nFine.X) + "," + F(nFine.Y) + "," + F(nFine.Z) + ")");
                shownBug++;
            }
        }
        Console.WriteLine("  classification:  clean=" + clean +
            "   kink(discontinuity)=" + kink +
            "   BUG(gradient error)=" + bug);
    }

    static double RelErr(Vec3 a, Vec3 n)
    {
        double mr = 0.0;
        for (int ax = 0; ax < 3; ax++)
        {
            double av = Comp(a, ax), nv = Comp(n, ax);
            if (Math.Abs(nv) > 1e-5) { double r = Math.Abs(av - nv) / Math.Abs(nv); if (r > mr) mr = r; }
        }
        return mr;
    }

    static string F(double d) { return d.ToString("G4"); }

    // Sum of squared edge lengths; gradient multiplied by gradScale so we can plant a bug.
    // E_total = sum_edges |p_a - p_b|^2 ; grad[v] = sum_{u~v} 2 (p_v - p_u).
    static EnergyFunc EdgeEnergy(double gradScale)
    {
        return (PlanktonMesh P, out double[] energy, out Vec3[] grad) =>
        {
            int nV = P.Vertices.Count;
            energy = new double[nV];
            grad = new Vec3[nV];
            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused) continue;
                Vec3 pv = Pos(P, v);
                Vec3 g = Vec3.Zero;
                double e = 0.0;
                int[] nb = P.Vertices.GetVertexNeighbours(v);
                foreach (int u in nb)
                {
                    Vec3 diff = pv - Pos(P, u);
                    e += 0.5 * (diff * diff);   // 1/2 -> each edge counted once when summed over both ends
                    g += 2.0 * diff;
                }
                energy[v] = e;
                grad[v] = gradScale * g;
            }
        };
    }

    static void Check(string name, EnergyFunc f, PlanktonMesh P)
    {
        double[] e0; Vec3[] g0;
        f(P, out e0, out g0);

        const double eps = 1e-3;
        double maxRel = 0.0, maxAbs = 0.0;
        int count = 0, nClean = 0, nDiverge = 0;

        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices.IsBoundary(v)) continue;
            Vec3 ga = g0[v];
            if (ga.Length < 1e-7) continue;

            Vec3 gn = NumGrad(f, P, v, eps);
            count++;

            double vRel = 0.0;
            for (int axis = 0; axis < 3; axis++)
            {
                double a = Comp(ga, axis), n = Comp(gn, axis);
                double abs = Math.Abs(a - n);
                if (abs > maxAbs) maxAbs = abs;
                if (Math.Abs(n) > 1e-5)
                {
                    double rel = abs / Math.Abs(n);
                    if (rel > vRel) vRel = rel;
                }
            }
            if (vRel > maxRel) maxRel = vRel;
            if (vRel < 0.05) nClean++;
            if (vRel > 0.50) nDiverge++;
        }

        string verdict = (maxRel < 0.02) ? "PASS" : (maxRel > 0.10 ? "FAIL" : "marginal");
        Console.WriteLine("  " + name.PadRight(42) +
            " checked " + count.ToString().PadLeft(3) +
            " | clean<5%=" + nClean.ToString().PadLeft(3) +
            " diverge>50%=" + nDiverge.ToString().PadLeft(2) +
            " | maxRel=" + (maxRel * 100).ToString("F1").PadLeft(7) + "%" +
            " -> " + verdict);
    }

    static Vec3 NumGrad(EnergyFunc f, PlanktonMesh P, int v, double eps)
    {
        return new Vec3(Partial(f, P, v, 0, eps), Partial(f, P, v, 1, eps), Partial(f, P, v, 2, eps));
    }

    static double Partial(EnergyFunc f, PlanktonMesh P, int v, int axis, double eps)
    {
        float ox = P.Vertices[v].X, oy = P.Vertices[v].Y, oz = P.Vertices[v].Z;

        SetAxis(P, v, axis, ox, oy, oz, +eps);
        double[] ep; Vec3[] d1; f(P, out ep, out d1); double Ep = Sum(ep);

        SetAxis(P, v, axis, ox, oy, oz, -eps);
        double[] em; Vec3[] d2; f(P, out em, out d2); double Em = Sum(em);

        P.Vertices.SetVertex(v, (double)ox, (double)oy, (double)oz); // restore
        return (Ep - Em) / (2.0 * eps);
    }

    static void SetAxis(PlanktonMesh P, int v, int axis, float ox, float oy, float oz, double delta)
    {
        double x = ox, y = oy, z = oz;
        if (axis == 0) x += delta; else if (axis == 1) y += delta; else z += delta;
        P.Vertices.SetVertex(v, x, y, z);
    }

    static Vec3 Pos(PlanktonMesh P, int v) { return new Vec3(P.Vertices[v].X, P.Vertices[v].Y, P.Vertices[v].Z); }
    static double Comp(Vec3 a, int axis) { return axis == 0 ? a.X : (axis == 1 ? a.Y : a.Z); }
    static double Sum(double[] a) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i]; return s; }

    static PlanktonMesh BuildBumpyGrid(int N) { return BuildBumpyGridScaled(N, 1.0); }

    // Bumpy grid uniformly scaled by s (positions and height), for the scale-invariance test.
    static PlanktonMesh BuildBumpyGridScaled(int N, double s)
    {
        var P = new PlanktonMesh();
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
                P.Vertices.Add(s * i, s * j, s * 0.5 * Math.Sin(0.7 * i) * Math.Sin(0.7 * j));
        for (int j = 0; j < N - 1; j++)
            for (int i = 0; i < N - 1; i++)
            {
                int a = j * N + i, b = j * N + i + 1, c = (j + 1) * N + i + 1, d = (j + 1) * N + i;
                P.Faces.AddFace(a, b, c);
                P.Faces.AddFace(a, c, d);
            }
        return P;
    }
}
