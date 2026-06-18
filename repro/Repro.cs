using System;
using System.Collections.Generic;
using Plankton;
using CreaseMachine;

// Headless reproduction of the "geodesic sphere racks at high iteration count" report.
//
// Replicates CreaseMachine.DoFlowStep EXACTLY (collapse short/sliver -> Nesterov lookahead
// step with per-vertex velocity cap -> fold heal), driven from the engine's Rhino-free core,
// and measures the global SHAPE asymmetry each checkpoint via the principal-axis ratio of the
// vertex cloud (PCA of position covariance about the centroid):
//   axisRatio = sqrt(lambda_max / lambda_min)   ->  1.0 for a perfect sphere, grows as it racks.
// This is exactly the metric HANDOFF.md section 5 suggests for a symmetry regression test.
//
// We sweep (Step, Momentum, DetMix) on the SAME mesh so we can see which knob controls the
// instability and whether DetMix (a GENERAL degenerate-eigenvector fix, not an icosa special
// case) suppresses it.
class Repro
{
    static string StlPath = @"C:\Users\AlexKaplan\Downloads\geodesic-sphere-2026-06-16-20-16-59.stl";

    static int Main(string[] args)
    {
        if (args.Length > 0) StlPath = args[0];
        if (!System.IO.File.Exists(StlPath)) { Console.WriteLine("STL not found: " + StlPath); return 1; }

        PlanktonMesh P0 = LoadBinaryStl(StlPath);
        Console.WriteLine("Loaded: " + Used(P0) + " used verts, " + P0.Faces.Count + " faces");
        ReportValenceHistogram(P0);
        Console.WriteLine("Initial axisRatio = " + AxisRatio(P0).ToString("F5") + "  (1.0 = perfect sphere)");
        Console.WriteLine();

        int steps = 4000, report = 500;

        // (label, Step, Momentum/beta, DetMix)
        var configs = new (string, double, double, double)[]
        {
            ("DEFAULT product (Step=0.05, Mom=0.9, DetMix=0)", 0.05,  0.9, 0.0),
            ("tiny step      (Step=1e-4, Mom=0.9, DetMix=0)", 1e-4,  0.9, 0.0),
            ("no momentum    (Step=0.05, Mom=0.0, DetMix=0)", 0.05,  0.0, 0.0),
            ("DetMix=0.2     (Step=0.05, Mom=0.9, DetMix=0.2)",0.05, 0.9, 0.2),
            ("DetMix=1.0     (Step=0.05, Mom=0.9, DetMix=1.0)",0.05, 0.9, 1.0),
        };

        foreach (var cfg in configs)
            RunConfig(cfg.Item1, LoadBinaryStl(StlPath), steps, report, cfg.Item2, cfg.Item3, cfg.Item4);

        // --- EXPERIMENTAL: adaptive per-vertex DetMix (basis-invariant gradient ONLY at
        // near-degenerate vertices; paper-faithful lambda_min where a real crease is forming) ---
        DevelopabilityEnergy.AdaptiveDetMix = true;
        DevelopabilityEnergy.AdaptiveDetMixPower = 2.0;
        foreach (double beta in new double[] { 0.0, 0.5 })
            RunConfig("ADAPTIVE pow=2, Mom=" + beta + " (Step=0.05)", LoadBinaryStl(StlPath),
                      steps, report, 0.05, beta, 0.0);
        DevelopabilityEnergy.AdaptiveDetMix = false;

        // --- principled candidate: harmonic-mean energy (det/trace), exact gradient ---
        DevelopabilityEnergy.HarmonicEnergy = true;
        foreach (double beta in new double[] { 0.9, 0.5, 0.0 })
            RunConfig("HARMONIC (det/trace), Mom=" + beta + " (Step=0.05)", LoadBinaryStl(StlPath),
                      steps, report, 0.05, beta, 0.0);
        DevelopabilityEnergy.HarmonicEnergy = false;

        // --- THE FIX: zero momentum at degenerate (isotropic) vertices ---
        // sep = (λ_max - λ_min)/(λ_max + λ_min) < 0.1 → eigenvector direction unreliable.
        // Zeroing vel there each step gives pure gradient descent at those vertices;
        // the gradient is still computed and applied, but momentum cannot accumulate
        // a direction-arbitrary bias. This is an optimizer choice, not an energy change.
        RunConfig("DEGEN-ZERO-MOM mom=0.9 DetMix=0",
                  LoadBinaryStl(StlPath), steps, report, 0.05, 0.9, 0.0, degenZeroMom: true);
        RunConfig("DEGEN-ZERO-MOM mom=0.9 DetMix=1.0",
                  LoadBinaryStl(StlPath), steps, report, 0.05, 0.9, 1.0, degenZeroMom: true);

        // --- Abandoned approach: gradient-based momentum restart ---
        // Reset vel[v] when dot(grad, vel) > 0: velocity is heading uphill.
        // At degenerate vertices the arbitrary gradient flips sign each step ->
        // vel stays near zero -> no drift accumulation. At crease vertices grad
        // and vel are consistently antiparallel -> no restart -> momentum kept.
        // Paper energy and gradient are untouched; only the optimizer changes.
        RunConfig("GRAD-RESTART mom=0.9 DetMix=0",
                  LoadBinaryStl(StlPath), steps, report, 0.05, 0.9, 0.0, gradRestart: true);
        RunConfig("GRAD-RESTART mom=0.9 DetMix=0.2",
                  LoadBinaryStl(StlPath), steps, report, 0.05, 0.9, 0.2, gradRestart: true);
        RunConfig("GRAD-RESTART mom=0.9 DetMix=1.0",
                  LoadBinaryStl(StlPath), steps, report, 0.05, 0.9, 1.0, gradRestart: true);

        return 0;
    }

    static void RunConfig(string label, PlanktonMesh P, int steps, int report,
                          double step, double beta, double detMix, bool gradRestart = false, bool degenZeroMom = false)
    {
        Console.WriteLine("=== " + label + " ===");
        Console.WriteLine("  iter |   sumE     |  maxGrad  | axisRatio | semiaxes a:b:c (norm) | nV | v5radCV");
        Vec3[] vel = new Vec3[P.Vertices.Count];
        double onset = -1;   // first iter axisRatio > 1.05
        int collapseEvents = 0;

        for (int s = 0; s <= steps; s++)
        {
            if (s % report == 0 || s == steps)
            {
                double[] eRep; bool[] fRep;
                DevelopabilityEnergy.ComputeHingeEnergy(P, out eRep, out fRep, 0, 0, false, 4.0, 0.0);
                double sumE = 0; for (int i = 0; i < eRep.Length; i++) sumE += eRep[i];
                double[] semi; double ratio = AxisRatio(P, out semi);
                Console.WriteLine("  " + s.ToString().PadLeft(4) + " | " +
                    sumE.ToString("G6").PadRight(10) + " | " +
                    MaxGrad(P, detMix).ToString("G4").PadRight(9) + " | " +
                    ratio.ToString("F5").PadRight(9) + " | " +
                    semi[0].ToString("F3") + ":" + semi[1].ToString("F3") + ":" + semi[2].ToString("F3") + "   | " +
                    Used(P).ToString().PadLeft(3) + "| " +
                    Val5RadialCV(P).ToString("F4"));
            }
            if (s == steps) break;

            // ---- one DoFlowStep (Iter=1), faithful to CreaseMachine.cs ----
            if (MeshOps.CollapseShortEdges(P, 0.2) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; collapseEvents++; }
            if (MeshOps.CollapseSliverEdges(P, 0.05) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; collapseEvents++; }

            int nV = P.Vertices.Count;
            if (vel.Length != nV) vel = new Vec3[nV];
            double L = RepEdge(P);
            double t = step * L * L, cap = L;

            double[] bx = new double[nV], by = new double[nV], bz = new double[nV];
            for (int v = 0; v < nV; v++)
            {
                PlanktonVertex pv = P.Vertices[v];
                bx[v] = pv.X; by[v] = pv.Y; bz[v] = pv.Z;
                if (beta > 0 && !pv.IsUnused && !P.Vertices.IsBoundary(v) && vel[v].IsValid)
                    P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
            }

            double[] e; Vec3[] g; bool[] fold; bool[] degen;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out fold, out degen,
                0.0, 0.0, false, 4.0, 0.0, true, null, detMix);

            // Diagnostic: on step 1, report how many vertices are flagged degenerate.
            if (s == 0 && degenZeroMom)
            {
                int dc = 0; for (int v = 0; v < nV; v++) if (degen[v]) dc++;
                Console.WriteLine("  [diag] step=0 degenVerts flagged: " + dc + " / " + nV);
            }

            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) { P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
                Vec3 gg = g[v];
                if (!gg.IsValid) { vel[v] = Vec3.Zero; P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
                if (gradRestart && beta > 0 && detMix < 0.5 && (gg * vel[v]) > 0.0) vel[v] = Vec3.Zero;
                if (degenZeroMom && beta > 0 && degen[v]) vel[v] = Vec3.Zero;
                vel[v] = beta * vel[v] - t * gg;
                double vl = vel[v].Length;
                if (vl > cap && vl > 1e-20) vel[v] = vel[v] * (cap / vl);
                P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
            }

            if (fold != null && MeshOps.CollapseFolds(P, fold) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; collapseEvents++; }

            if (onset < 0 && AxisRatio(P) > 1.05) onset = s + 1;
        }

        Console.WriteLine("  -> racking onset (axisRatio>1.05): " + (onset < 0 ? "never" : ("iter " + onset)) +
                          "   collapse events: " + collapseEvents);
        Console.WriteLine();
    }

    // ---------- metrics ----------

    static double AxisRatio(PlanktonMesh P) { double[] s; return AxisRatio(P, out s); }

    // sqrt(lambda_max/lambda_min) of the position covariance about the centroid, plus the three
    // normalized semi-axis lengths (sqrt(lambda_i), scaled so their mean is 1).
    static double AxisRatio(PlanktonMesh P, out double[] semiNorm)
    {
        double cx = 0, cy = 0, cz = 0; int n = 0;
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices[v].IsUnused) continue;
            cx += P.Vertices[v].X; cy += P.Vertices[v].Y; cz += P.Vertices[v].Z; n++;
        }
        cx /= n; cy /= n; cz /= n;
        double c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices[v].IsUnused) continue;
            double x = P.Vertices[v].X - cx, y = P.Vertices[v].Y - cy, z = P.Vertices[v].Z - cz;
            c00 += x * x; c01 += x * y; c02 += x * z; c11 += y * y; c12 += y * z; c22 += z * z;
        }
        c00 /= n; c01 /= n; c02 /= n; c11 /= n; c12 /= n; c22 /= n;
        double[] ev = SymEig3(c00, c01, c02, c11, c12, c22); // ascending
        double lmin = Math.Max(ev[0], 1e-30), lmax = Math.Max(ev[2], 1e-30);
        double a = Math.Sqrt(Math.Max(ev[2], 0)), b = Math.Sqrt(Math.Max(ev[1], 0)), cc = Math.Sqrt(Math.Max(ev[0], 0));
        double mean = (a + b + cc) / 3.0; if (mean < 1e-30) mean = 1;
        semiNorm = new double[] { a / mean, b / mean, cc / mean };
        return Math.Sqrt(lmax / lmin);
    }

    // Coefficient of variation of the radial distance of the valence-5 vertices from the centroid.
    // If the 12 poles slip asymmetrically, their radii diverge -> CV grows. Valence recomputed
    // each call so it survives renumbering after a collapse.
    static double Val5RadialCV(PlanktonMesh P)
    {
        double cx = 0, cy = 0, cz = 0; int n = 0;
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices[v].IsUnused) continue;
            cx += P.Vertices[v].X; cy += P.Vertices[v].Y; cz += P.Vertices[v].Z; n++;
        }
        cx /= n; cy /= n; cz /= n;
        var radii = new List<double>();
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) continue;
            if (P.Vertices.GetVertexNeighbours(v).Length != 5) continue;
            double x = P.Vertices[v].X - cx, y = P.Vertices[v].Y - cy, z = P.Vertices[v].Z - cz;
            radii.Add(Math.Sqrt(x * x + y * y + z * z));
        }
        if (radii.Count < 2) return 0;
        double mean = 0; foreach (double r in radii) mean += r; mean /= radii.Count;
        double var = 0; foreach (double r in radii) var += (r - mean) * (r - mean); var /= radii.Count;
        return mean > 1e-30 ? Math.Sqrt(var) / mean : 0;
    }

    static double MaxGrad(PlanktonMesh P, double detMix)
    {
        double[] e; Vec3[] g; bool[] f;
        DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out f, 0.0, 0.0, false, 4.0, 0.0, true, null, detMix);
        double mx = 0;
        for (int v = 0; v < g.Length; v++) { double m = g[v].Length; if (m > mx) mx = m; }
        return mx;
    }

    // ---------- helpers ----------

    static int Used(PlanktonMesh P)
    {
        int n = 0; for (int v = 0; v < P.Vertices.Count; v++) if (!P.Vertices[v].IsUnused) n++; return n;
    }

    static void ReportValenceHistogram(PlanktonMesh P)
    {
        int v4 = 0, v5 = 0, v6 = 0, other = 0, bnd = 0;
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices[v].IsUnused) continue;
            if (P.Vertices.IsBoundary(v)) { bnd++; continue; }
            int val = P.Vertices.GetVertexNeighbours(v).Length;
            if (val == 4) v4++; else if (val == 5) v5++; else if (val == 6) v6++; else other++;
        }
        Console.WriteLine("Valence (interior): 4=" + v4 + "  5=" + v5 + "  6=" + v6 + "  other=" + other + "  boundary=" + bnd);
    }

    static double RepEdge(PlanktonMesh P)
    {
        for (int i = 0; i < P.Halfedges.Count; i += 2)
        {
            if (P.Halfedges[i].IsUnused) continue;
            var a = P.Vertices[P.Halfedges[i].StartVertex];
            var b = P.Vertices[P.Halfedges[i + 1].StartVertex];
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            double L = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (L > 0) return L;
        }
        return 1.0;
    }

    // Analytic eigenvalues of a symmetric 3x3 (ascending). Smith's closed form.
    static double[] SymEig3(double a, double b, double c, double d, double e, double f)
    {
        // matrix [[a,b,c],[b,d,e],[c,e,f]]
        double p1 = b * b + c * c + e * e;
        if (p1 < 1e-300) { double[] di = { a, d, f }; Array.Sort(di); return di; }
        double q = (a + d + f) / 3.0;
        double p2 = (a - q) * (a - q) + (d - q) * (d - q) + (f - q) * (f - q) + 2.0 * p1;
        double p = Math.Sqrt(p2 / 6.0);
        double b00 = (a - q) / p, b11 = (d - q) / p, b22 = (f - q) / p;
        double b01 = b / p, b02 = c / p, b12 = e / p;
        double detB = b00 * (b11 * b22 - b12 * b12) - b01 * (b01 * b22 - b12 * b02) + b02 * (b01 * b12 - b11 * b02);
        double r = detB / 2.0;
        if (r < -1) r = -1; else if (r > 1) r = 1;
        double phi = Math.Acos(r) / 3.0;
        double e1 = q + 2.0 * p * Math.Cos(phi);
        double e3 = q + 2.0 * p * Math.Cos(phi + 2.0 * Math.PI / 3.0);
        double e2 = 3.0 * q - e1 - e3;
        double[] res = { e1, e2, e3 };
        Array.Sort(res);   // ascending: [min, mid, max]
        return res;
    }

    // Binary STL loader with vertex welding (identical logic to test/Program.cs LoadBinaryStl).
    static PlanktonMesh LoadBinaryStl(string path)
    {
        byte[] b = System.IO.File.ReadAllBytes(path);
        int nTri = BitConverter.ToInt32(b, 80);
        const int baseOff = 84;
        double scale = 0;
        for (int t = 0; t < nTri; t++)
        {
            int o = baseOff + t * 50 + 12;
            for (int k = 0; k < 9; k++) { double cc = Math.Abs(BitConverter.ToSingle(b, o + k * 4)); if (cc > scale) scale = cc; }
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
}
