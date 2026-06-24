using System;
using System.Collections.Generic;
using Plankton;
using CreaseMachine;

// Finite-difference gradient checker, plus self-tests that validate the checker
// itself against energies whose gradients are known by hand.
class Program
{
    delegate void EnergyFunc(PlanktonMesh P, out double[] energy, out Vec3[] grad);

    static int Main(string[] args)
    {
        // "perf" => fast mode: FD correctness gate + perf bench + value checksums only,
        // skipping the long C:\Temp flow/inspect diagnostics (most of whose files are absent).
        bool perfOnly = Array.IndexOf(args, "perf") >= 0;

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

        // FD-check the combined (covariance + B.5.1 branching) gradient at branchWeight=0.5.
        // BUG count tells us whether the branching subgradient is correct on this mesh.
        EnergyFunc devBranchHalf = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            bool[] fF;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(Pm, out eb, out gb, out fF, 0.5);
        };
        Check("Dev + branch=0.5     (analytic)", devBranchHalf, P);
        EnergyFunc devBranchHalfNum = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            DevelopabilityEnergy.ComputeNumericalGrad(Pm, out eb, out gb, 0.5);
        };
        Check("Dev + branch=0.5     (numerical via VertexEnergy)", devBranchHalfNum, P);
        EnergyFunc devBranchTiny = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            bool[] fF;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(Pm, out eb, out gb, out fF, 0.01);
        };
        Check("Dev + branch=0.01    (analytic)", devBranchTiny, P);
        AnalyzeDevFunc("Dev + branch=0.5 ", devBranchHalf, P);
        EnergyFunc devConsHalf = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            bool[] fF;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(Pm, out eb, out gb, out fF, 0.0, 0.5);
        };
        Check("Dev + consolidate=0.5 (analytic)", devConsHalf, P);
        EnergyFunc devConsHalfNum = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            DevelopabilityEnergy.ComputeNumericalGrad(Pm, out eb, out gb, 0.0, 0.5);
        };
        Check("Dev + consolidate=0.5 (numerical via VertexEnergy)", devConsHalfNum, P);
        AnalyzeDevFunc("Dev + consolidate=0.5 ", devConsHalf, P);
        EnergyFunc maxCov = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            bool[] fF;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(Pm, out eb, out gb, out fF, 0.0, 0.0, true);
        };
        Check("MaxCov                (analytic)", maxCov, P);
        EnergyFunc maxCovNum = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            DevelopabilityEnergy.ComputeNumericalGrad(Pm, out eb, out gb, 0.0, 0.0, true);
        };
        Check("MaxCov                (numerical via VertexEnergy)", maxCovNum, P);
        AnalyzeDevFunc("MaxCov ", maxCov, P);
        EnergyFunc craze = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            bool[] fF;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(Pm, out eb, out gb, out fF, 0.0, 0.0, false, 4.0, 0.5);
        };
        Check("deCraze=0.5            (analytic)", craze, P);
        EnergyFunc crazeNum = (PlanktonMesh Pm, out double[] eb, out Vec3[] gb) =>
        {
            DevelopabilityEnergy.ComputeNumericalGrad(Pm, out eb, out gb, 0.0, 0.0, false, 4.0, 0.5);
        };
        Check("deCraze=0.5            (numerical via VertexEnergy)", crazeNum, P);
        AnalyzeDevFunc("deCraze=0.5 ", craze, P);

        Console.WriteLine();
        UnweldTest();

        Console.WriteLine();
        // Perf bench: time CHA in each config on realistic-sized meshes so we can SEE what's
        // actually slow vs guessing. Three bunnies span the range; bumpy grid (49 verts) is
        // too small to register past noise.
        PerfBench(@"C:\Temp\Bunny 2.5k.stl");
        Console.WriteLine();
        PerfBench(@"C:\Temp\Bunny 5k.stl");
        Console.WriteLine();
        PerfBench(@"C:\Temp\Bunny 20k.stl");
        Console.WriteLine();
        // Value-preservation gate: deterministic checksums of the flow config the user runs.
        // A perf change must reproduce these to ~1e-9 relative (gradient reduction is parallel,
        // so the last ~3 ULPs jitter run-to-run; a real value change shows up far above that).
        Checksum(@"C:\Temp\Bunny 2.5k.stl");
        Checksum(@"C:\Temp\Bunny 5k.stl");
        Checksum(@"C:\Temp\Bunny 20k.stl");

        if (perfOnly) return 0;

        Console.WriteLine();
        FlowTest(BuildBumpyGrid(11), 400);
        Console.WriteLine();
        ScaleInvarianceTest();
        Console.WriteLine();
        OptimizerComparison();
        Console.WriteLine();
        SlippageTest();
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
        Console.WriteLine();
        // NEW: AbouttoBlow.stl, user-reported about-to-explode vertex
        ExplodeDiagnostic(@"C:\Temp\AbouttoBlow.stl");
        Console.WriteLine();
        FlowAndWatch(@"C:\Temp\AbouttoBlow.stl", 40, 0.05, 0.9);
        Console.WriteLine();
        TrackPoint(@"C:\Temp\AbouttoBlow.stl", -14.495, 12.292, -21.835, 20, 0.05, 0.9);
        Console.WriteLine();
        InspectMesh(@"C:\Temp\Twisted Ribbon.stl");
        Console.WriteLine();
        FlowAndWatch(@"C:\Temp\Twisted Ribbon.stl", 200, 0.05, 0.9, 0.0, 0.0);
        Console.WriteLine();
        // With B.2 consolidation at SMALL weight: the Twisted Ribbon starts at a partition-tie
        // KINK (within-cluster sums all ~0 with multiple optimal partitions), so finite-step
        // Nesterov amplifies FP noise and the energy grows even though the subgradient is correct.
        // A near-zero weight keeps the drift small enough to see the behaviour without divergence.
        FlowAndWatch(@"C:\Temp\Twisted Ribbon.stl", 60, 0.05, 0.0, 0.0, 0.05);
        Console.WriteLine();
        // Mesh Cube: canonical piecewise-developable. Every term SHOULD read 0 at every vertex.
        // Run InspectMesh + zero-momentum flows with each term in isolation to see which one drifts.
        InspectMesh(@"C:\Temp\Mesh Cube.stl");
        Console.WriteLine();
        FlowAndWatch(@"C:\Temp\Mesh Cube.stl", 40, 0.05, 0.0, 0.0, 0.0);
        Console.WriteLine();
        FlowAndWatch(@"C:\Temp\Mesh Cube.stl", 40, 0.05, 0.0, 0.05, 0.0);
        Console.WriteLine();
        FlowAndWatch(@"C:\Temp\Mesh Cube.stl", 40, 0.05, 0.0, 0.0, 0.05);
        Console.WriteLine();
        CompareEnergies(@"C:\Temp\Bunny PreSmooyj 2026-06-15.stl", @"C:\Temp\BunnyScraped.stl");
        Console.WriteLine();
        CompareThreeEnergies(@"C:\Temp\Bunny PreSmooyj 2026-06-15.stl",
                             @"C:\Temp\BunnyScraped.stl",
                             @"C:\Temp\BunnyScrapedR2.stl");
        return 0;
    }

    static void CompareThreeEnergies(string pathA, string pathB, string pathC)
    {
        Console.WriteLine("=== Three-way compare: " + System.IO.Path.GetFileNameWithoutExtension(pathA) +
            "  vs  " + System.IO.Path.GetFileNameWithoutExtension(pathB) +
            "  vs  " + System.IO.Path.GetFileNameWithoutExtension(pathC) + " ===");
        if (!System.IO.File.Exists(pathA) || !System.IO.File.Exists(pathB) || !System.IO.File.Exists(pathC))
        { Console.WriteLine("  (one or more files not found)"); return; }
        PlanktonMesh PA = LoadBinaryStl(pathA);
        PlanktonMesh PB = LoadBinaryStl(pathB);
        PlanktonMesh PC = LoadBinaryStl(pathC);
        Console.WriteLine("  A (PreSmooth): " + PA.Vertices.Count + " verts, " + PA.Faces.Count + " faces");
        Console.WriteLine("  B (Scraped)  : " + PB.Vertices.Count + " verts, " + PB.Faces.Count + " faces");
        Console.WriteLine("  C (ScrapedR2): " + PC.Vertices.Count + " verts, " + PC.Faces.Count + " faces");

        Action<string, double, double, double> report3 = (name, eA, eB, eC) =>
        {
            Console.WriteLine("  " + name.PadRight(28) +
                "A=" + eA.ToString("G6").PadRight(12) +
                "B=" + eB.ToString("G6").PadRight(12) +
                "C=" + eC.ToString("G6").PadRight(12) +
                "  C/A=" + (eA > 1e-20 ? (100.0 * eC / eA).ToString("F1") + "%" : "n/a").PadRight(8) +
                "C/B=" + (eB > 1e-20 ? (100.0 * eC / eB).ToString("F1") + "%" : "n/a"));
        };

        double covA = SumEnergyAll(PA, 0, 0, false), covB = SumEnergyAll(PB, 0, 0, false), covC = SumEnergyAll(PC, 0, 0, false);
        double mxA  = SumEnergyAll(PA, 0, 0, true),  mxB  = SumEnergyAll(PB, 0, 0, true),  mxC  = SumEnergyAll(PC, 0, 0, true);
        double bcA  = SumEnergyAll(PA, 1, 0, false), bcB  = SumEnergyAll(PB, 1, 0, false), bcC  = SumEnergyAll(PC, 1, 0, false);
        double cnA  = SumEnergyAll(PA, 0, 1, false), cnB  = SumEnergyAll(PB, 0, 1, false), cnC  = SumEnergyAll(PC, 0, 1, false);
        report3("Covariance lambda (sum)", covA, covB, covC);
        report3("MaxCov lambda^max",       mxA,  mxB,  mxC);
        report3("deBranch psi  (weight=1)", bcA - covA, bcB - covB, bcC - covC);
        report3("deConsolidate E^P (w=1)", cnA - covA, cnB - covB, cnC - covC);
    }

    // Same-topology / different-position comparison. Reports total energy under each formulation
    // for two meshes - the formulation whose energy DROPS from mesh A to B is the one that aligns
    // with the direction of manual sculpting (here: combining patches to reduce crazing).
    static void CompareEnergies(string pathA, string pathB)
    {
        Console.WriteLine("=== Compare energies: " + System.IO.Path.GetFileName(pathA) +
            "  vs  " + System.IO.Path.GetFileName(pathB) + " ===");
        if (!System.IO.File.Exists(pathA) || !System.IO.File.Exists(pathB))
        {
            Console.WriteLine("  (one or both files not found)");
            return;
        }
        PlanktonMesh PA = LoadBinaryStl(pathA);
        PlanktonMesh PB = LoadBinaryStl(pathB);
        Console.WriteLine("  A: " + PA.Vertices.Count + " verts, " + PA.Faces.Count + " faces");
        Console.WriteLine("  B: " + PB.Vertices.Count + " verts, " + PB.Faces.Count + " faces");

        // Each formulation: report (totalA, totalB, delta=B-A, percent reduction).
        Action<string, double, double> report = (name, eA, eB) =>
        {
            double delta = eB - eA;
            double pct = eA > 1e-20 ? 100.0 * (1.0 - eB / eA) : double.NaN;
            Console.WriteLine("  " + name.PadRight(28) + "A=" + eA.ToString("G10").PadRight(16) +
                "B=" + eB.ToString("G10").PadRight(16) +
                "  B-A=" + (delta >= 0 ? "+" : "") + delta.ToString("G6").PadRight(13) +
                "  " + (double.IsNaN(pct) ? "" : (pct >= 0 ? "(-" + pct.ToString("F3") + "%)" : "(+" + (-pct).ToString("F3") + "%)")));
        };

        report("Covariance lambda (sum)", SumEnergyAll(PA, 0, 0, false), SumEnergyAll(PB, 0, 0, false));
        report("MaxCov lambda^max",       SumEnergyAll(PA, 0, 0, true),  SumEnergyAll(PB, 0, 0, true));
        report("deBranch psi (weight=1)", SumEnergyAll(PA, 1, 0, false) - SumEnergyAll(PA, 0, 0, false),
                                          SumEnergyAll(PB, 1, 0, false) - SumEnergyAll(PB, 0, 0, false));
        report("deConsolidate E^P (w=1)", SumEnergyAll(PA, 0, 1, false) - SumEnergyAll(PA, 0, 0, false),
                                          SumEnergyAll(PB, 0, 1, false) - SumEnergyAll(PB, 0, 0, false));
    }

    static double SumEnergyAll(PlanktonMesh P, double branchWeight, double consolidateWeight, bool useMaxCov)
    {
        double s = 0;
        for (int v = 0; v < P.Vertices.Count; v++)
            s += DevelopabilityEnergy.VertexEnergy(P, v, branchWeight, consolidateWeight, useMaxCov);
        return s;
    }

    // Per-edge dihedral angles + per-vertex valence + per-vertex Energy. Tells us whether a mesh
    // is genuinely at the dev-flow minimum (small dihedrals, energy 0) or just being silently
    // dismissed (valence < 4 -> VertexEnergy returns 0 regardless of actual normal spread).
    static void InspectMesh(string path)
    {
        Console.WriteLine("=== Inspect " + System.IO.Path.GetFileName(path) + " ===");
        if (!System.IO.File.Exists(path)) { Console.WriteLine("  (file not found)"); return; }
        PlanktonMesh P = LoadBinaryStl(path);

        int nV = P.Vertices.Count;
        int nF = P.Faces.Count;
        int interior = 0, boundary = 0;
        for (int v = 0; v < nV; v++)
        {
            if (P.Vertices[v].IsUnused) continue;
            if (P.Vertices.IsBoundary(v)) boundary++; else interior++;
        }
        Console.WriteLine("  " + nV + " verts (" + interior + " interior, " + boundary + " boundary), " + nF + " faces");

        // Valence + Energy table for INTERIOR vertices only (boundary verts have Energy=0 by guard)
        Console.WriteLine("  --- interior vertex valences + Energy ---");
        int subValence4 = 0, val4 = 0, val5 = 0, val6 = 0, valGE7 = 0;
        for (int v = 0; v < nV; v++)
        {
            if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) continue;
            int val = P.Vertices.GetVertexNeighbours(v).Length;
            double e = DevelopabilityEnergy.VertexEnergy(P, v);
            if (val < 4) subValence4++;
            else if (val == 4) val4++;
            else if (val == 5) val5++;
            else if (val == 6) val6++;
            else valGE7++;
            Console.WriteLine("    v" + v.ToString().PadLeft(3) + "  valence=" + val + "  Energy=" + e.ToString("G5"));
        }
        Console.WriteLine("  valence histogram (interior): <4=" + subValence4 + "  4=" + val4 + "  5=" + val5 + "  6=" + val6 + "  >=7=" + valGE7);
        if (subValence4 > 0)
            Console.WriteLine("  WARNING: " + subValence4 + " interior verts have valence<4 -- VertexEnergy returns 0 there by guard, NOT by being developable");

        // Per-edge dihedral angles. Pair (h, h+1) is one edge.
        Console.WriteLine("  --- edge dihedrals (interior edges only) ---");
        int nE = 0;
        double maxDihedral = 0;
        double sumAbsDihedral = 0;
        int countAbove01 = 0;   // > ~5.7 deg
        int countAbove1 = 0;    // > ~57 deg
        for (int h = 0; h < P.Halfedges.Count; h += 2)
        {
            if (P.Halfedges[h].IsUnused) continue;
            int fA = P.Halfedges[h].AdjacentFace;
            int fB = P.Halfedges[h + 1].AdjacentFace;
            if (fA < 0 || fB < 0) continue;   // boundary edge, no dihedral
            // Face normals
            Vec3 NA = FaceNormal(P, fA);
            Vec3 NB = FaceNormal(P, fB);
            double c = NA * NB;
            if (c > 1) c = 1; else if (c < -1) c = -1;
            double dihedral = Math.Acos(c);
            if (dihedral > maxDihedral) maxDihedral = dihedral;
            sumAbsDihedral += dihedral;
            if (dihedral > 0.1) countAbove01++;
            if (dihedral > 1.0) countAbove1++;
            nE++;
        }
        Console.WriteLine("  " + nE + " interior edges, max dihedral = " + (maxDihedral * 180.0 / Math.PI).ToString("F3") +
            " deg, mean = " + (sumAbsDihedral / Math.Max(1, nE) * 180.0 / Math.PI).ToString("F3") +
            " deg, " + countAbove01 + " > 5.7 deg, " + countAbove1 + " > 57 deg");

        Console.WriteLine("  total Energy via VertexEnergy = " + SumEnergy(P).ToString("G5"));
    }

    static Vec3 FaceNormal(PlanktonMesh P, int f)
    {
        int[] fv = P.Faces.GetFaceVertices(f);
        Vec3 a = Pos(P, fv[0]); Vec3 b = Pos(P, fv[1]); Vec3 c = Pos(P, fv[2]);
        Vec3 cr = Vec3.Cross(b - a, c - a);
        double L = cr.Length;
        return L > 1e-30 ? cr / L : Vec3.Zero;
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

    static void FlowAndWatch(string path, int steps, double step, double beta)
    {
        FlowAndWatch(path, steps, step, beta, 0.0, 0.0);
    }

    static void FlowAndWatch(string path, int steps, double step, double beta, double branchWeight)
    {
        FlowAndWatch(path, steps, step, beta, branchWeight, 0.0);
    }

    // Run the EXACT CreaseMachine flow (collapse + Nesterov momentum + velocity cap, energy guard
    // active, optional B.5.1 branching at branchWeight, optional B.2 consolidation at
    // consolidateWeight) on the mesh and watch for the explosion; on blow-up, dump the culprit
    // vertex so we can see the failing quantity (coherence = fold? minAspect = sliver? minEdge =
    // short edge?). Reports total Energy and maxGrad each step.
    static void FlowAndWatch(string path, int steps, double step, double beta, double branchWeight, double consolidateWeight)
    {
        Console.WriteLine("=== Flow & watch (Step=" + step + ", beta=" + beta +
            (branchWeight > 0 ? ", deBranch=" + branchWeight : "") +
            (consolidateWeight > 0 ? ", deConsolidate=" + consolidateWeight : "") +
            ") " + System.IO.Path.GetFileName(path) + " ===");
        if (!System.IO.File.Exists(path)) { Console.WriteLine("  (file not found)"); return; }
        PlanktonMesh P = LoadBinaryStl(path);
        Vec3[] vel = new Vec3[P.Vertices.Count];
        double startE = double.NaN;

        for (int s = 0; s < steps; s++)
        {
            if (MeshOps.CollapseShortEdges(P, 0.2) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; }
            if (MeshOps.CollapseSliverEdges(P, 0.05) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; }
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
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out fold, branchWeight, consolidateWeight);   // P is at the LOOKAHEAD
            double maxg = 0; int argmax = -1; double totalE = 0;
            for (int v = 0; v < nV; v++) { double m = g[v].Length; if (m > maxg) { maxg = m; argmax = v; } totalE += e[v]; }
            if (double.IsNaN(startE)) startE = totalE;
            Console.WriteLine("  step " + s.ToString().PadLeft(3) + " verts=" + nV.ToString().PadLeft(4) +
                " Energy=" + totalE.ToString("G5").PadRight(10) + " maxGrad=" + maxg.ToString("G5"));

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
        double endE = SumEnergy(P);
        Console.WriteLine("  start Energy = " + startE.ToString("G5") + " ,  end Energy = " + endE.ToString("G5") +
            (endE < startE ? "  (descended " + (100.0 * (1.0 - endE / startE)).ToString("F1") + "%)" : "  (DID NOT DESCEND)"));
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

    // CHA microbench: time a representative mesh in every config the CreaseMachine exposes, so
    // we can see where time actually goes vs intuition. Each config runs WARMUP_ITERS untimed
    // (JIT + cache warmup), then BENCH_ITERS timed; reports mean ms and per-call ops/sec.
    static void PerfBench(string path)
    {
        Console.WriteLine("=== CHA perf bench: " + System.IO.Path.GetFileName(path) + " ===");
        if (!System.IO.File.Exists(path)) { Console.WriteLine("  (file not found)"); return; }
        PlanktonMesh P = LoadBinaryStl(path);
        int nV = P.Vertices.Count, nF = P.Faces.Count;
        Console.WriteLine("  mesh: " + nV + " verts, " + nF + " faces");
        Console.WriteLine();

        const int WARMUP = 3;
        const int ITERS  = 20;
        var sw = new System.Diagnostics.Stopwatch();

        double tickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Action<string, Action> bench = (label, fn) =>
        {
            for (int i = 0; i < WARMUP; i++) fn();
            // GC right before measurement, then suppress collections inside the loop
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            CHAStats.Reset();
            CHAStats.Enabled = true;
            sw.Restart();
            for (int i = 0; i < ITERS; i++) fn();
            sw.Stop();
            CHAStats.Enabled = false;
            double meanMs = sw.Elapsed.TotalMilliseconds / ITERS;
            int calls = Math.Max(1, CHAStats.Calls);
            double facePcMs   = CHAStats.FacePrecomputeTicks  * tickToMs / calls;
            double vertNorMs  = CHAStats.VertNormalsTicks     * tickToMs / calls;
            double perVertMs  = CHAStats.PerVertexLoopTicks   * tickToMs / calls;
            double l1Ms       = CHAStats.L1Ticks              * tickToMs / calls;
            double gvfMs      = CHAStats.GetVertexFacesTicks  * tickToMs / calls;
            double gfvMs      = CHAStats.GetFaceVertsTicks    * tickToMs / calls;
            double otherMs    = meanMs - facePcMs - vertNorMs - perVertMs - l1Ms;
            Console.WriteLine("  " + label.PadRight(48) + meanMs.ToString("F2").PadLeft(8) + " ms/call" +
                "   [facePc " + facePcMs.ToString("F1") + "  vN " + vertNorMs.ToString("F1") +
                "  perV " + perVertMs.ToString("F1") + "  L1 " + l1Ms.ToString("F1") +
                "  other " + otherMs.ToString("F1") +
                "  (GFV " + gfvMs.ToString("F1") + " GVF " + gvfMs.ToString("F1") + ")]");
        };

        double[] e; Vec3[] g; bool[] ff;

        // Baselines
        bench("covariance only, grad (flow path)", () => {
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out ff,
                0.0, 0.0, false, 4.0, 0.0);
        });
        bench("covariance only, energy-only (output path)", () => {
            DevelopabilityEnergy.ComputeHingeEnergy(P, out e, out ff,
                0.0, 0.0, false, 4.0, 0.0);
        });

        // deCraze (L1) - what the user currently runs
        bench("+ deCraze=0.5, grad", () => {
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out ff,
                0.0, 0.0, false, 4.0, 0.5);
        });
        bench("+ deCraze=0.5, energy-only", () => {
            DevelopabilityEnergy.ComputeHingeEnergy(P, out e, out ff,
                0.0, 0.0, false, 4.0, 0.5);
        });

        // deBranch (B.5.1) - O(m^4) enumeration per vertex
        bench("+ deBranch=0.5, grad", () => {
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out ff,
                0.5, 0.0, false, 4.0, 0.0);
        });

        // deConsolidate (B.2) - O(m^2*m) enumeration
        bench("+ deConsolidate=0.5, grad", () => {
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out ff,
                0.0, 0.5, false, 4.0, 0.0);
        });

        // useMaxCov (B.4) - O(m^3) enumeration
        bench("useMaxCov, grad", () => {
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out ff,
                0.0, 0.0, true, 4.0, 0.0);
        });

        // Combined: simulate one full Grasshopper solve (flow + output)
        bench("flow CHA + energy output (cov+deCraze=0.5)", () => {
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out ff,
                0.0, 0.0, false, 4.0, 0.5);
            DevelopabilityEnergy.ComputeHingeEnergy(P, out e, out ff,
                0.0, 0.0, false, 4.0, 0.5);
        });
    }

    // Value-preservation gate for perf refactors. Runs the flow config the user actually drives
    // (covariance + deCraze=0.5, CrazeBand=0.1) and prints deterministic checksums at full
    // precision: total energy, summed gradient magnitude, and an index-weighted gradient probe
    // (sensitive to per-vertex changes that would cancel in a plain sum). Capture at baseline;
    // every optimization must reproduce all three to ~1e-9 relative. sumE is fully deterministic
    // (per-vertex energy, serial sum here); the gradient sums carry ~1e-13 relative parallel-
    // reduction jitter, far below the threshold where a real value bug would land.
    static void Checksum(string path)
    {
        string tag = System.IO.Path.GetFileName(path);
        if (!System.IO.File.Exists(path)) { Console.WriteLine("=== Checksum " + tag + ": (file not found) ==="); return; }
        PlanktonMesh P = LoadBinaryStl(path);
        double savedBand = DevelopabilityEnergy.CrazeBand;
        DevelopabilityEnergy.CrazeBand = 0.1;
        double[] e; Vec3[] g; bool[] ff;
        DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out e, out g, out ff, 0.0, 0.0, false, 4.0, 0.5);
        DevelopabilityEnergy.CrazeBand = savedBand;

        int nV = P.Vertices.Count;
        double sumE = 0, sumG = 0, probe = 0;
        for (int v = 0; v < nV; v++)
        {
            sumE += e[v];
            double gl = g[v].Length;
            if (!double.IsNaN(gl) && !double.IsInfinity(gl)) sumG += gl;
            probe += (v + 1) * (0.37 * g[v].X + 0.51 * g[v].Y + 0.71 * g[v].Z);
        }
        Console.WriteLine("=== Checksum " + tag.PadRight(16) + " verts=" + nV.ToString().PadLeft(6) +
            "  sumE=" + sumE.ToString("G17") +
            "  sum|g|=" + sumG.ToString("G17") +
            "  probe=" + probe.ToString("G17"));
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

    // UnweldByRegion: split a grid into two halves (one straight crease down the middle), unweld, and
    // confirm each piece became its own connected component, faces are 1:1, the seam vertices were
    // duplicated (verts touched by both halves), and vertexMap maps back to coincident source verts.
    static void UnweldTest()
    {
        Console.WriteLine("=== UnweldByRegion (piece -> connected component) ===");
        PlanktonMesh P = BuildBumpyGrid(7);
        int nF = P.Faces.Count, nV = P.Vertices.Count;

        double minx = double.MaxValue, maxx = double.MinValue;
        for (int v = 0; v < nV; v++) { double x = P.Vertices[v].X; if (x < minx) minx = x; if (x > maxx) maxx = x; }
        double mid = 0.5 * (minx + maxx);
        var pieceMap = new int[nF];
        var seen0 = new bool[nV]; var seen1 = new bool[nV];
        int p0 = 0, p1 = 0;
        for (int f = 0; f < nF; f++)
        {
            int[] fv = P.Faces.GetFaceVertices(f);
            double cx = 0; foreach (int v in fv) cx += P.Vertices[v].X; cx /= fv.Length;
            int piece = cx < mid ? 0 : 1; pieceMap[f] = piece;
            if (piece == 0) p0++; else p1++;
            foreach (int v in fv) { if (piece == 0) seen0[v] = true; else seen1[v] = true; }
        }
        int seam = 0; for (int v = 0; v < nV; v++) if (seen0[v] && seen1[v]) seam++;

        var M = MeshOps.UnweldByRegion(P, pieceMap, out int[] vmap);
        int comps = MeshOps.ComponentCount(M);

        bool facesOk = M.Faces.Count == nF;
        bool compsOk = comps == 2;
        bool vertsOk = M.Vertices.Count == nV + seam;          // each seam vert duplicated once
        bool mapOk = vmap.Length == M.Vertices.Count;
        bool coincident = true;
        for (int v = 0; v < M.Vertices.Count; v++)
        {
            int gv = vmap[v];
            if (gv < 0 || gv >= nV) { coincident = false; break; }
            var a = M.Vertices[v]; var b = P.Vertices[gv];
            if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z) > 1e-12) { coincident = false; break; }
        }
        Console.WriteLine("  pieces=" + p0 + "/" + p1 + "  faces " + nF + "->" + M.Faces.Count + " (" + facesOk + ")"
            + "  comps=" + comps + " (" + compsOk + ")"
            + "  verts " + nV + "->" + M.Vertices.Count + " expect " + (nV + seam) + " (" + vertsOk + ")"
            + "  map=" + mapOk + "  coincident=" + coincident);
        Console.WriteLine("  RESULT: " + ((facesOk && compsOk && vertsOk && mapOk && coincident) ? "PASS" : "FAIL"));
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

    // Run the CreaseMachine flow (analytic grad, global step t = step*L^2) and return the energy
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
            // reference). Boundary vertices stay put. This mirrors exactly what CreaseMachine does:
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
        AnalyzeDevFunc("", DevelopabilityEnergy.ComputeHingeEnergyAndGrad, P);
    }

    static void AnalyzeDevFunc(string tag, EnergyFunc f, PlanktonMesh P)
    {
        double[] e0; Vec3[] g0;
        f(P, out e0, out g0);

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

            // FD eps-dependent at the same point -> we're sitting on a subgradient kink
            // (the min-max-pair winning configuration changes within the FD step), so the
            // analytic SUBGRADIENT is one valid element of the subdifferential and FD picks
            // a different one. Not a bug, just non-smoothness.
            Vec3 fdDiff = nFine - nCoarse;
            double fdJitter = fdDiff.Length / (1e-12 + Math.Max(nFine.Length, nCoarse.Length));
            if (fdJitter > 0.05) { kink++; continue; }

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
        Console.WriteLine("  " + tag + "classification:  clean=" + clean +
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

    // ===================================================================================
    // Vertex-slippage / symmetry instrument.
    //
    // WHY: at a low-angle, multi-panel vertex the normal covariance is near-degenerate
    // (lambda_min ~= lambda_max, both small), so the paper's lambda_min energy picks a
    // direction-arbitrary eigenvector and emits a small but mis-aimed gradient. That
    // gradient lives mostly INSIDE the developable level set, so it slides the vertex
    // along a zero-energy direction ("slippage") instead of reducing energy. Nesterov
    // momentum and the per-vertex velocity cap then amplify the drift. A high-symmetry
    // icosahedron / icosphere is the cleanest fixture: a perfectly symmetric vertex cloud
    // has principal-axis ratio 1.000, and any slippage breaks that symmetry, driving the
    // ratio above 1 (the "oblong distortion" noted in HANDOFF.md s5).
    //
    // This is a MEASUREMENT, not a fix. It runs the 2x2 ladder
    //   detMix in {0, 1}  x  momentum in {0, 0.9}
    // so the primary agent can read which mechanism is live BEFORE any stabilizer is
    // committed. Expected reading if the diagnosis holds: detMix=1 collapses the
    // axis-ratio growth (the artifact is in the gradient direction, fixed at the source
    // by the basis-invariant det blend), and it does so WITHOUT changing the energy floor
    // (det(M) = lambda_min*lambda_max has the SAME zero set as lambda_min).
    // ===================================================================================
    static void SlippageTest()
    {
        Console.WriteLine("=== Vertex slippage / symmetry (icosahedron + icosphere) ===");
        Console.WriteLine("  metric: principal-axis ratio of the vertex cloud (1.0000 = perfectly symmetric);");
        Console.WriteLine("          energy = sum of paper lambda_min (common yardstick across rows).");
        Console.WriteLine("          A fix that only de-aliases the gradient must drop axisRatio growth");
        Console.WriteLine("          while leaving the energy floor unchanged.");

        RunSlippageFixture("icosahedron (12v/20f)", BuildIcosahedron(), 150, 0.02);
        RunSlippageFixture("icosphere L1 (42v/80f)", BuildIcosphere(1), 150, 0.02);
    }

    static void RunSlippageFixture(string name, PlanktonMesh seed, int steps, double step)
    {
        Console.WriteLine("  -- " + name + "  (steps=" + steps + ", step=" + step + ") --");
        Console.WriteLine("     config                          axisRatio (start -> end)  energy (start -> end)    maxDrift");

        // The 2x2 source-diagnosis ladder: which mechanism is live?
        RunSlipRow("detMix=0.0 mom=0.00",           seed, steps, step, 0.0, 0.00, 0.0);
        RunSlipRow("detMix=0.0 mom=0.90",           seed, steps, step, 0.0, 0.90, 0.0);
        RunSlipRow("detMix=1.0 mom=0.00",           seed, steps, step, 1.0, 0.00, 0.0);
        RunSlipRow("detMix=1.0 mom=0.90",           seed, steps, step, 1.0, 0.90, 0.0);
        // The stabilizer rows: the relaxer should drop axisRatio growth on the WORST baseline
        // (detMix=0 mom=0.9) WITHOUT raising the energy floor vs that same baseline.
        RunSlipRow("detMix=0.0 mom=0.90 relax=0.3", seed, steps, step, 0.0, 0.90, 0.3);
        RunSlipRow("detMix=1.0 mom=0.90 relax=0.3", seed, steps, step, 1.0, 0.90, 0.3);
    }

    static void RunSlipRow(string label, PlanktonMesh seed, int steps, double step,
                           double detMix, double beta, double relax)
    {
        PlanktonMesh P = CloneMesh(seed);
        double ar0 = PrincipalAxisRatio(P);
        double e0 = SumEnergy(P);
        int nV = P.Vertices.Count;
        double[] ix = new double[nV], iy = new double[nV], iz = new double[nV];
        SnapshotCentered(P, ix, iy, iz);

        bool diverged = FlowNesterovDetMix(P, steps, step, beta, detMix, relax, true);

        double ar1 = PrincipalAxisRatio(P);
        double e1 = SumEnergy(P);
        double drift = MaxCenteredDrift(P, ix, iy, iz);

        string lab = label + (diverged ? " (DIVERGED)" : "");
        Console.WriteLine("     " + lab.PadRight(32) +
            (ar0.ToString("F4") + " -> " + ar1.ToString("F4")).PadRight(26) +
            (e0.ToString("G4") + " -> " + e1.ToString("G4")).PadRight(24) +
            drift.ToString("G4"));
    }

    // Run the same Nesterov step the live component uses (lookahead, raw grad, t = step*L^2,
    // velocity capped at one edge), with detMix wired through and an optional projected-tangential
    // relax (the slippage stabilizer) applied per step. Returns true if it diverged.
    static bool FlowNesterovDetMix(PlanktonMesh P, int steps, double step, double beta, double detMix, double relaxWeight, bool cap)
    {
        double L = RepEdge(P);
        double alpha = step * L * L;
        double capLen = L;
        int nV = P.Vertices.Count;
        Vec3[] vel = new Vec3[nV];
        double[] bx = new double[nV], by = new double[nV], bz = new double[nV];

        for (int s = 0; s < steps; s++)
        {
            for (int v = 0; v < nV; v++)
            {
                bx[v] = P.Vertices[v].X; by[v] = P.Vertices[v].Y; bz[v] = P.Vertices[v].Z;
                if (beta > 0 && !P.Vertices.IsBoundary(v) && vel[v].IsValid)
                    P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
            }

            double[] e; Vec3[] g; bool[] ff;
            DevelopabilityEnergy.ComputeHingeEnergyAndGrad(
                P, out e, out g, out ff, 0.0, 0.0, false, 4.0, 0.0, true, null, detMix);

            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices.IsBoundary(v)) { P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
                if (!g[v].IsValid) return true;
                vel[v] = beta * vel[v] - alpha * g[v];
                double vl = vel[v].Length;
                if (cap && vl > capLen && vl > 1e-20) vel[v] = vel[v] * (capLen / vl);
                P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
            }

            if (relaxWeight > 0.0)
                MeshOps.ProjectedTangentialRelax(P, g, relaxWeight, 0.25);
        }
        return false;
    }

    // Principal-axis ratio = sqrt(lambda_max / lambda_min) of the vertex-position covariance
    // about the centroid. Isotropic cloud -> 1.0; an oblong cloud grows above 1.
    static double PrincipalAxisRatio(PlanktonMesh P)
    {
        int nV = P.Vertices.Count, n = 0;
        double cx = 0, cy = 0, cz = 0;
        for (int v = 0; v < nV; v++)
        {
            if (P.Vertices[v].IsUnused) continue;
            cx += P.Vertices[v].X; cy += P.Vertices[v].Y; cz += P.Vertices[v].Z; n++;
        }
        if (n < 3) return 1.0;
        cx /= n; cy /= n; cz /= n;

        double sxx = 0, syy = 0, szz = 0, sxy = 0, sxz = 0, syz = 0;
        for (int v = 0; v < nV; v++)
        {
            if (P.Vertices[v].IsUnused) continue;
            double dx = P.Vertices[v].X - cx, dy = P.Vertices[v].Y - cy, dz = P.Vertices[v].Z - cz;
            sxx += dx * dx; syy += dy * dy; szz += dz * dz;
            sxy += dx * dy; sxz += dx * dz; syz += dy * dz;
        }
        double[,] m = new double[3, 3] { { sxx, sxy, sxz }, { sxy, syy, syz }, { sxz, syz, szz } };
        double l0, l1, l2; Jacobi3(m, out l0, out l1, out l2);
        double lo = Math.Min(l0, Math.Min(l1, l2));
        double hi = Math.Max(l0, Math.Max(l1, l2));
        if (lo <= 1e-20) return double.PositiveInfinity;
        return Math.Sqrt(hi / lo);
    }

    // Cyclic Jacobi eigenvalues of a 3x3 symmetric matrix (eigenvalues only, order arbitrary).
    static void Jacobi3(double[,] m, out double l0, out double l1, out double l2)
    {
        for (int sweep = 0; sweep < 60; sweep++)
        {
            double off = Math.Abs(m[0, 1]) + Math.Abs(m[0, 2]) + Math.Abs(m[1, 2]);
            if (off < 1e-20) break;
            for (int p = 0; p < 3; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    double apq = m[p, q];
                    if (Math.Abs(apq) < 1e-300) continue;
                    int r = 3 - p - q;
                    double app = m[p, p], aqq = m[q, q];
                    double theta = (aqq - app) / (2.0 * apq);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    if (theta == 0.0) t = 1.0;
                    double c = 1.0 / Math.Sqrt(t * t + 1.0), sn = t * c;
                    double apr = m[p, r], aqr = m[q, r];
                    m[p, p] = app - t * apq;
                    m[q, q] = aqq + t * apq;
                    m[p, q] = 0.0; m[q, p] = 0.0;
                    m[p, r] = c * apr - sn * aqr; m[r, p] = m[p, r];
                    m[q, r] = sn * apr + c * aqr; m[r, q] = m[q, r];
                }
        }
        l0 = m[0, 0]; l1 = m[1, 1]; l2 = m[2, 2];
    }

    static void SnapshotCentered(PlanktonMesh P, double[] ox, double[] oy, double[] oz)
    {
        int nV = P.Vertices.Count, n = 0;
        double cx = 0, cy = 0, cz = 0;
        for (int v = 0; v < nV; v++) { cx += P.Vertices[v].X; cy += P.Vertices[v].Y; cz += P.Vertices[v].Z; n++; }
        if (n > 0) { cx /= n; cy /= n; cz /= n; }
        for (int v = 0; v < nV; v++)
        {
            ox[v] = P.Vertices[v].X - cx; oy[v] = P.Vertices[v].Y - cy; oz[v] = P.Vertices[v].Z - cz;
        }
    }

    // Max per-vertex displacement after removing net translation (a coarse slippage indicator;
    // global rotation is assumed negligible for these short flows).
    static double MaxCenteredDrift(PlanktonMesh P, double[] ox, double[] oy, double[] oz)
    {
        int nV = P.Vertices.Count, n = 0;
        double cx = 0, cy = 0, cz = 0;
        for (int v = 0; v < nV; v++) { cx += P.Vertices[v].X; cy += P.Vertices[v].Y; cz += P.Vertices[v].Z; n++; }
        if (n > 0) { cx /= n; cy /= n; cz /= n; }
        double maxD = 0.0;
        for (int v = 0; v < nV; v++)
        {
            double dx = (P.Vertices[v].X - cx) - ox[v];
            double dy = (P.Vertices[v].Y - cy) - oy[v];
            double dz = (P.Vertices[v].Z - cz) - oz[v];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d > maxD) maxD = d;
        }
        return maxD;
    }

    static PlanktonMesh CloneMesh(PlanktonMesh P)
    {
        var Q = new PlanktonMesh();
        for (int v = 0; v < P.Vertices.Count; v++)
            Q.Vertices.Add(P.Vertices[v].X, P.Vertices[v].Y, P.Vertices[v].Z);
        for (int f = 0; f < P.Faces.Count; f++)
        {
            int[] fv = P.Faces.GetFaceVertices(f);
            Q.Faces.AddFace(fv[0], fv[1], fv[2]);
        }
        return Q;
    }

    // Regular icosahedron, vertices projected to the unit sphere. 12 verts (all valence 5),
    // 20 faces, consistent outward winding.
    static PlanktonMesh BuildIcosahedron()
    {
        double t = (1.0 + Math.Sqrt(5.0)) / 2.0;
        double[][] v =
        {
            new double[] { -1,  t,  0 }, new double[] {  1,  t,  0 }, new double[] { -1, -t,  0 }, new double[] {  1, -t,  0 },
            new double[] {  0, -1,  t }, new double[] {  0,  1,  t }, new double[] {  0, -1, -t }, new double[] {  0,  1, -t },
            new double[] {  t,  0, -1 }, new double[] {  t,  0,  1 }, new double[] { -t,  0, -1 }, new double[] { -t,  0,  1 }
        };
        var P = new PlanktonMesh();
        for (int i = 0; i < 12; i++)
        {
            double x = v[i][0], y = v[i][1], z = v[i][2];
            double l = Math.Sqrt(x * x + y * y + z * z);
            P.Vertices.Add(x / l, y / l, z / l);
        }
        int[][] f =
        {
            new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 }, new[] { 0, 7, 10 }, new[] { 0, 10, 11 },
            new[] { 1, 5, 9 }, new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 }, new[] { 7, 1, 8 },
            new[] { 3, 9, 4 }, new[] { 3, 4, 2 }, new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
            new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 }, new[] { 8, 6, 7 }, new[] { 9, 8, 1 }
        };
        for (int i = 0; i < 20; i++) P.Faces.AddFace(f[i][0], f[i][1], f[i][2]);
        return P;
    }

    // Geodesic icosphere: icosahedron 1->4 subdivided 'levels' times, midpoints reprojected
    // to the unit sphere (shared via an edge-keyed midpoint cache so the mesh stays manifold).
    static PlanktonMesh BuildIcosphere(int levels)
    {
        PlanktonMesh P = BuildIcosahedron();
        for (int l = 0; l < levels; l++) P = SubdivideSphere(P, 1.0);
        return P;
    }

    static PlanktonMesh SubdivideSphere(PlanktonMesh P, double radius)
    {
        var Q = new PlanktonMesh();
        for (int v = 0; v < P.Vertices.Count; v++)
            Q.Vertices.Add(P.Vertices[v].X, P.Vertices[v].Y, P.Vertices[v].Z);
        var mid = new System.Collections.Generic.Dictionary<long, int>();
        for (int f = 0; f < P.Faces.Count; f++)
        {
            int[] fv = P.Faces.GetFaceVertices(f);
            int a = fv[0], b = fv[1], c = fv[2];
            int ab = EdgeMidpoint(Q, mid, a, b, radius);
            int bc = EdgeMidpoint(Q, mid, b, c, radius);
            int ca = EdgeMidpoint(Q, mid, c, a, radius);
            Q.Faces.AddFace(a, ab, ca);
            Q.Faces.AddFace(b, bc, ab);
            Q.Faces.AddFace(c, ca, bc);
            Q.Faces.AddFace(ab, bc, ca);
        }
        return Q;
    }

    static int EdgeMidpoint(PlanktonMesh Q, System.Collections.Generic.Dictionary<long, int> mid, int i, int j, double radius)
    {
        int lo = Math.Min(i, j), hi = Math.Max(i, j);
        long key = ((long)lo << 32) | (uint)hi;
        int idx;
        if (mid.TryGetValue(key, out idx)) return idx;
        double mx = (Q.Vertices[i].X + Q.Vertices[j].X) * 0.5;
        double my = (Q.Vertices[i].Y + Q.Vertices[j].Y) * 0.5;
        double mz = (Q.Vertices[i].Z + Q.Vertices[j].Z) * 0.5;
        double len = Math.Sqrt(mx * mx + my * my + mz * mz);
        if (len > 1e-15) { double sc = radius / len; mx *= sc; my *= sc; mz *= sc; }
        idx = Q.Vertices.Count;
        Q.Vertices.Add(mx, my, mz);
        mid[key] = idx;
        return idx;
    }
}
