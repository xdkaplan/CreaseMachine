using System;
using Plankton;
using CreaseMachine;

namespace CreasePatchSolver
{
    // Co-refines a 3D mesh M and its flat image M' (on z=0) toward DISCRETE ISOMETRY: corresponding
    // edge lengths equal. For triangle meshes equal edge lengths IS exact discrete isometry (a triangle
    // is rigid given its three edge lengths, SSS), so this is the faithful triangle analog of Jiang et
    // al. 2020's parallelogram-congruence E_iso (their Eq. 1, stated on quad diagonals). When M can be
    // made isometric to a FLAT M', M is developable.
    //
    //   E = wIso * Sum_edges (|Mi-Mj|^2 - |M'i-M'j|^2)^2          // developability driver
    //     + wPos * Sum_v |Mv - M0v|^2                             // anchor M to its original shape
    //     + wFair * (uniform-Laplacian fairness on M and M')      // anti-zigzag
    //
    // M and M' share connectivity + vertex ordering (BFF preserves both). M0 is the original M (the
    // proximity anchor — without it the trivial minimizer is M collapsing flat onto M'). M' is kept on
    // z=0. One call = one Jacobi gradient-descent step (caller loops it on spacebar). Returns the raw
    // E_iso (edge-length-squared mismatch) for a convergence readout.
    static class IsometricSolver
    {
        public static double Step(PlanktonMesh M, PlanktonMesh Mp, Vec3[] M0,
                                  double wIso, double wFair, double wPos, double lr)
        {
            int nV = M.Vertices.Count;
            if (nV == 0 || Mp.Vertices.Count != nV) return 0.0;
            var gM = new Vec3[nV];   // dE/dM
            var gP = new Vec3[nV];   // dE/dM'

            // --- E_iso: edge-length-squared mismatch ---
            double eIso = 0.0, sumLen2 = 0.0, sumLen2_0 = 0.0; int nE = 0;
            int nH = M.Halfedges.Count;
            for (int h = 0; h < nH; h += 2)
            {
                if (M.Halfedges[h].IsUnused) continue;
                int i = M.Halfedges[h].StartVertex;
                int j = M.Halfedges[h + 1].StartVertex;
                if (i < 0 || j < 0 || M.Vertices[i].IsUnused || M.Vertices[j].IsUnused) continue;

                Vec3 d  = Pos(M, i)  - Pos(M, j);     // edge vector in M
                Vec3 dp = Pos(Mp, i) - Pos(Mp, j);    // edge vector in M'
                double lM = d * d, lP = dp * dp;      // squared lengths (Vec3*Vec3 = dot)
                double c = lM - lP;
                eIso += c * c; sumLen2 += lM; nE++;
                if (M0 != null && i < M0.Length && j < M0.Length) { Vec3 e0 = M0[i] - M0[j]; sumLen2_0 += e0 * e0; }

                // d/dMi (c^2) = 2c * 2(Mi-Mj) = 4c d ;  d/dM'i (c^2) = 2c * -2(M'i-M'j) = -4c dp
                Vec3 gi = d  * (wIso * 4.0 * c);  gM[i] += gi;  gM[j] -= gi;
                Vec3 gp = dp * (-wIso * 4.0 * c); gP[i] += gp;  gP[j] -= gp;
            }
            if (nE == 0) return 0.0;

            // --- fairness (uniform Laplacian) on both, + proximity anchor on M ---
            for (int v = 0; v < nV; v++)
            {
                if (M.Vertices[v].IsUnused) continue;
                if (wFair > 0.0)
                {
                    int[] nb = M.Vertices.GetVertexNeighbours(v);
                    if (nb != null && nb.Length > 0)
                    {
                        Vec3 lapM = Vec3.Zero, lapP = Vec3.Zero; int cnt = 0;
                        foreach (int u in nb)
                        {
                            if (u < 0 || M.Vertices[u].IsUnused) continue;
                            lapM += Pos(M, v)  - Pos(M, u);
                            lapP += Pos(Mp, v) - Pos(Mp, u);
                            cnt++;
                        }
                        if (cnt > 0) { gM[v] += lapM * (wFair / cnt); gP[v] += lapP * (wFair / cnt); }
                    }
                }
                if (wPos > 0.0 && M0 != null && v < M0.Length)
                    gM[v] += (Pos(M, v) - M0[v]) * (2.0 * wPos);
            }

            // --- Jacobi step with a trust-region cap. Key the cap off the FIXED original scale (M0),
            // not the current mesh: otherwise as the mesh inflates the cap grows too, a positive
            // feedback that lets the flow run away (the bug that made M & M' balloon + go craggy). ---
            double cap = 0.2 * Math.Sqrt((sumLen2_0 > 0.0 ? sumLen2_0 : sumLen2) / nE);
            for (int v = 0; v < nV; v++)
            {
                if (M.Vertices[v].IsUnused) continue;

                Vec3 sM = gM[v] * (-lr);
                double lm = sM.Length; if (lm > cap) sM = sM * (cap / lm);
                Vec3 nm = Pos(M, v) + sM;
                M.Vertices.SetVertex(v, nm.X, nm.Y, nm.Z);

                Vec3 sP = gP[v] * (-lr);
                double lp = sP.Length; if (lp > cap) sP = sP * (cap / lp);
                Vec3 np = Pos(Mp, v) + sP;
                Mp.Vertices.SetVertex(v, np.X, np.Y, 0.0);   // M' stays flat on z=0
            }
            return eIso;
        }

        private static Vec3 Pos(PlanktonMesh P, int v) { var p = P.Vertices[v]; return new Vec3(p.X, p.Y, p.Z); }
    }
}
