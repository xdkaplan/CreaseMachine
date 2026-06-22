using System;
using Plankton;

namespace PieceSolver
{
    // Non-shrinking smoothing filters for the isometric flow. The uniform Laplacian shrinks because it
    // moves each vertex toward its neighbours' centroid - which has an inward NORMAL component (mean-
    // curvature flow -> area loss). These two filters remove that shrink in different ways, so they can
    // distribute strain / de-wrinkle the developing mesh without collapsing it:
    //
    //   Tangential : project the Laplacian onto the tangent plane (drop the normal component) -> vertices
    //                slide WITHIN the surface, evening out spacing, with zero normal motion -> no shrink.
    //   Taubin     : alternate a +lambda Laplacian pass (shrinks) with a -mu pass (inflates), |mu|>lambda,
    //                so the shrink of the first pass is undone by the second (Taubin's lambda|mu filter).
    //
    // BOUNDARY vertices are pinned (never moved): a boundary vertex's one-sided neighbourhood has no
    // outward neighbour, so smoothing pulls it inward and collapses the frame (it does not for Taubin's
    // inflate pass, but pinning is correct for both). The interior still develops freely via the LM step.
    //
    // Applied as a post-step FILTER after the LM developability step (operator splitting), not as an LM
    // residual. `flat` => the mesh lies on z=0 (the flat map M'); its surface normal is +Z, so the
    // tangential projection is simply "drop the z component" and z is held at 0.
    static class IsometricSmoothers
    {
        public enum Kind { None = 0, Tangential = 1, Taubin = 2 }

        // Dispatch by kind. `strength` is the tangential step (~0.5); Taubin uses fixed lambda/mu scaled
        // by `strength` so the same slider drives both comparably. `passes` repeats the whole filter.
        public static void Apply(Kind kind, PlanktonMesh M, double strength, int passes, bool flat)
        {
            if (kind == Kind.None || strength <= 0.0 || passes <= 0) return;
            bool[] bnd = BoundaryMask(M);
            if (kind == Kind.Tangential)
                for (int p = 0; p < passes; p++) LaplacianPass(M, strength, flat, tangential: true, bnd);
            else // Taubin
            {
                double lambda = 0.5 * Math.Min(strength, 1.0);
                double mu = -1.06 * lambda;   // |mu| slightly > lambda -> net pass-band ~ no shrink
                for (int p = 0; p < passes; p++)
                {
                    LaplacianPass(M, lambda, flat, tangential: false, bnd);
                    LaplacianPass(M, mu, flat, tangential: false, bnd);
                }
            }
        }

        // Vertices touched by a boundary half-edge (AdjacentFace < 0). Plankton pairs half-edges as
        // (2e, 2e+1), so the opposite of h is h^1.
        static bool[] BoundaryMask(PlanktonMesh M)
        {
            var bnd = new bool[M.Vertices.Count];
            int nH = M.Halfedges.Count;
            for (int h = 0; h < nH; h++)
            {
                if (M.Halfedges[h].IsUnused) continue;
                if (M.Halfedges[h].AdjacentFace < 0)
                {
                    int a = M.Halfedges[h].StartVertex, b = M.Halfedges[h ^ 1].StartVertex;
                    if (a >= 0) bnd[a] = true;
                    if (b >= 0) bnd[b] = true;
                }
            }
            return bnd;
        }

        // One Laplacian pass (Jacobi: all reads from a snapshot, then write). v += factor * L(v), where
        // L(v) = mean(neighbours) - v, optionally projected to the tangent plane. Boundary vertices fixed.
        static void LaplacianPass(PlanktonMesh M, double factor, bool flat, bool tangential, bool[] bnd)
        {
            int n = M.Vertices.Count;
            double[] px = new double[n], py = new double[n], pz = new double[n];
            for (int v = 0; v < n; v++) { var q = M.Vertices[v]; px[v] = q.X; py[v] = q.Y; pz[v] = q.Z; }

            double[] nx = null, ny = null, nz = null;
            if (tangential && !flat) VertexNormals(M, px, py, pz, out nx, out ny, out nz);

            for (int v = 0; v < n; v++)
            {
                if (M.Vertices[v].IsUnused || bnd[v]) continue;   // pin boundary
                var nb = M.Vertices.GetVertexNeighbours(v);
                if (nb == null || nb.Length == 0) continue;
                double ax = 0, ay = 0, az = 0; int d = 0;
                foreach (int u in nb) { if (u < 0 || M.Vertices[u].IsUnused) continue; ax += px[u]; ay += py[u]; az += pz[u]; d++; }
                if (d == 0) continue;
                double lx = ax / d - px[v], ly = ay / d - py[v], lz = az / d - pz[v];   // uniform Laplacian
                if (tangential)
                {
                    if (flat) lz = 0.0;   // normal = +Z: tangential = drop the normal component
                    else { double dot = lx * nx[v] + ly * ny[v] + lz * nz[v]; lx -= dot * nx[v]; ly -= dot * ny[v]; lz -= dot * nz[v]; }
                }
                double X = px[v] + factor * lx, Y = py[v] + factor * ly, Z = flat ? 0.0 : pz[v] + factor * lz;
                M.Vertices.SetVertex(v, (float)X, (float)Y, (float)Z);
            }
        }

        // Area-weighted vertex normals (cross-product magnitude = 2*area, so larger faces weigh more).
        static void VertexNormals(PlanktonMesh M, double[] px, double[] py, double[] pz,
                                  out double[] nx, out double[] ny, out double[] nz)
        {
            int n = M.Vertices.Count;
            nx = new double[n]; ny = new double[n]; nz = new double[n];
            for (int f = 0; f < M.Faces.Count; f++)
            {
                if (M.Faces[f].IsUnused) continue;
                int[] fv = M.Faces.GetFaceVertices(f);
                if (fv.Length < 3) continue;
                int a = fv[0], b = fv[1], c = fv[2];
                double ux = px[b] - px[a], uy = py[b] - py[a], uz = pz[b] - pz[a];
                double vx = px[c] - px[a], vy = py[c] - py[a], vz = pz[c] - pz[a];
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                foreach (int vv in fv) { nx[vv] += cx; ny[vv] += cy; nz[vv] += cz; }
            }
            for (int v = 0; v < n; v++)
            {
                double L = Math.Sqrt(nx[v] * nx[v] + ny[v] * ny[v] + nz[v] * nz[v]);
                if (L > 1e-12) { nx[v] /= L; ny[v] /= L; nz[v] /= L; }
            }
        }
    }
}
