using System;
using Plankton;

namespace CreaseMachine
{
    /// <summary>
    /// Rhino-free mesh-development metrics shared by the headless front-ends (CLI, GUI). All
    /// numbers describe "how developable / how creased is this mesh right now", independent of the
    /// flow's optimizer state.
    ///
    /// The developability <see cref="MetricsResult.SumE"/> is the PURE covariance energy
    /// (lambda_min, or lambda_max when <c>useMaxCov</c>) summed over vertices - the regularizers
    /// (deCraze / deBranch / deConsolidate) are excluded so the number reflects developability, not
    /// the magnitude of the penalties the flow adds. Panels are a union-find over faces joined
    /// across edges whose dihedral is below the crease cutoff (<c>crazeBandRad</c>); crazeRMS and
    /// maxDih summarise the dihedral distribution below / overall.
    /// </summary>
    public static class FlowMetrics
    {
        public struct MetricsResult
        {
            public double SumE;        // pure developability energy, summed over vertices
            public int Panels;         // connected face clusters below the crease cutoff
            public double CrazeRmsDeg; // RMS of sub-cutoff (intra-panel) dihedrals, degrees
            public double MaxDihDeg;   // largest dihedral anywhere, degrees
            public double DihRoughDeg; // RMS within-face dihedral disagreement (accordion/craze), degrees
        }

        // Per-vertex pure developability energy (covariance lambda_min, or lambda_max if
        // useMaxCov) - the convergence signal. Regularizers excluded (branch/consolidate/craze = 0).
        public static double[] DevEnergyArray(PlanktonMesh P, bool useMaxCov, double sharpness)
        {
            double[] e; bool[] f;
            DevelopabilityEnergy.ComputeHingeEnergy(P, out e, out f, 0.0, 0.0, useMaxCov, sharpness, 0.0);
            return e;
        }

        public static double DevEnergy(PlanktonMesh P, bool useMaxCov, double sharpness)
        {
            var e = DevEnergyArray(P, useMaxCov, sharpness);
            double s = 0; for (int i = 0; i < e.Length; i++) s += e[i];
            return s;
        }

        // Per-vertex [0,1] colour scalar for PLY export: sqrt of energy normalised by the max
        // (sqrt so low energy stays visible). Drives MeshIO.WritePly's vertex colour.
        public static double[] EnergyColour01(PlanktonMesh P, bool useMaxCov, double sharpness)
        {
            double[] e = DevEnergyArray(P, useMaxCov, sharpness);
            double eMax = 1e-12; for (int i = 0; i < e.Length; i++) if (e[i] > eMax) eMax = e[i];
            double[] c = new double[e.Length];
            for (int i = 0; i < e.Length; i++) c[i] = Math.Sqrt(Math.Max(0, e[i]) / eMax);
            return c;
        }

        public static MetricsResult Compute(PlanktonMesh P, double crazeBandRad, bool useMaxCov, double sharpness)
        {
            var m = new MetricsResult();
            m.SumE = DevEnergy(P, useMaxCov, sharpness);

            double tau = crazeBandRad;   // crease cutoff: below = intra-panel (flat), above = a crease
            int nF = P.Faces.Count;
            int[] par = new int[nF];
            for (int f = 0; f < nF; f++) par[f] = IsTri(P, f) ? f : -1;

            int nH = P.Halfedges.Count;
            double[] phiE = new double[nH / 2];   // per-edge dihedral (0 where not an interior tri-tri edge)
            double sumSq = 0; int nIntra = 0; double maxDih = 0;
            for (int h = 0; h < nH; h += 2)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int fA = P.Halfedges[h].AdjacentFace, fB = P.Halfedges[h + 1].AdjacentFace;
                if (fA < 0 || fB < 0 || par[fA] < 0 || par[fB] < 0) continue;
                double dih = Dihedral(P, fA, fB);
                phiE[h >> 1] = dih;
                if (dih > maxDih) maxDih = dih;
                if (dih < tau) { Union(par, fA, fB); sumSq += dih * dih; nIntra++; }
            }
            int panels = 0;
            for (int f = 0; f < nF; f++) if (par[f] >= 0 && Find(par, f) == f) panels++;

            // Dihedral roughness: within each triangle, how much do its 3 edge dihedrals disagree?
            // A smooth/curved developable region has near-equal edge dihedrals (low); an accordion of
            // sub-creases - the crazing the covariance energy is blind to - makes neighbouring edges
            // alternate fold/flat, so the disagreement is high. This is the Sum|d phi| signal.
            double rSumSq = 0; int rN = 0;
            for (int f = 0; f < nF; f++)
            {
                if (par[f] < 0) continue;
                int[] he = P.Faces.GetHalfedges(f);
                if (he.Length != 3) continue;
                double p0 = phiE[he[0] >> 1], p1 = phiE[he[1] >> 1], p2 = phiE[he[2] >> 1];
                double d01 = p0 - p1, d12 = p1 - p2, d20 = p2 - p0;
                rSumSq += d01 * d01 + d12 * d12 + d20 * d20; rN += 3;
            }

            m.Panels = panels;
            m.CrazeRmsDeg = (nIntra > 0 ? Math.Sqrt(sumSq / nIntra) : 0.0) * 180.0 / Math.PI;
            m.MaxDihDeg = maxDih * 180.0 / Math.PI;
            m.DihRoughDeg = (rN > 0 ? Math.Sqrt(rSumSq / rN) : 0.0) * 180.0 / Math.PI;
            return m;
        }

        static bool IsTri(PlanktonMesh P, int f) { return !P.Faces[f].IsUnused && P.Faces.GetHalfedges(f).Length == 3; }

        static double Dihedral(PlanktonMesh P, int fA, int fB)
        {
            Vec3 a = FaceNormal(P, fA), b = FaceNormal(P, fB);
            double c = a * b; if (c > 1) c = 1; else if (c < -1) c = -1;
            return Math.Acos(c);
        }

        static Vec3 FaceNormal(PlanktonMesh P, int f)
        {
            int[] fv = P.Faces.GetFaceVertices(f);
            Vec3 p0 = V(P, fv[0]), p1 = V(P, fv[1]), p2 = V(P, fv[2]);
            Vec3 cr = Vec3.Cross(p1 - p0, p2 - p0);
            double L = cr.Length;
            return L > 1e-30 ? cr * (1.0 / L) : Vec3.Zero;
        }

        static Vec3 V(PlanktonMesh P, int v) { var p = P.Vertices[v]; return new Vec3(p.X, p.Y, p.Z); }

        static int Find(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }
        static void Union(int[] p, int a, int b) { int ra = Find(p, a), rb = Find(p, b); if (ra != rb) p[ra] = rb; }
    }
}
