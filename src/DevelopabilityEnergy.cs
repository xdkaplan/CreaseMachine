using System;
using System.Collections.Generic;
using Plankton;

namespace CreaseMachine
{
    // Hinge (covariance) developability energy and its analytic gradient,
    // following Stein, Grinspun & Crane 2018. Rhino-free: uses only Plankton
    // for connectivity and the local Vec3 for math, so it can be unit-tested
    // (e.g. finite-difference gradient checks) against just Plankton.dll.
    public static class DevelopabilityEnergy
    {
        private static Vec3 Pos(PlanktonVertex v) { return new Vec3(v.X, v.Y, v.Z); }

        public static void ComputeHingeEnergyAndGrad(
            PlanktonMesh P,
            out double[] energy,
            out Vec3[] energyGrad)
        {
            bool[] isFold;
            ComputeHingeEnergyAndGrad(P, out energy, out energyGrad, out isFold);
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
            int nV = P.Vertices.Count;
            int nF = P.Faces.Count;

            energy = new double[nV];
            energyGrad = new Vec3[nV];
            isFold = new bool[nV];

            // --- Precompute face data ---
            int[][] faceVerts = new int[nF][];
            Vec3[] faceNormals = new Vec3[nF];
            double[] doubleAreas = new double[nF];

            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) { faceVerts[f] = new int[0]; continue; }
                faceVerts[f] = P.Faces.GetFaceVertices(f);
                if (faceVerts[f].Length != 3) continue;

                Vec3 p0 = Pos(P.Vertices[faceVerts[f][0]]);
                Vec3 p1 = Pos(P.Vertices[faceVerts[f][1]]);
                Vec3 p2 = Pos(P.Vertices[faceVerts[f][2]]);

                Vec3 e01 = p1 - p0;
                Vec3 e02 = p2 - p0;
                Vec3 cross = Vec3.Cross(e01, e02);
                doubleAreas[f] = cross.Length;
                if (doubleAreas[f] > 1e-16)
                    faceNormals[f] = cross / doubleAreas[f];
            }

            // --- Area-weighted vertex normals ---
            Vec3[] vertNormalsRaw = new Vec3[nV];
            Vec3[] vertNormals = new Vec3[nV];
            double[] vertDA = new double[nV];   // sum of incident double-areas, for the fold guard
            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused) continue;
                int[] vf = P.Vertices.GetVertexFaces(v);
                foreach (int f in vf)
                    if (f >= 0 && f < nF && !P.Faces[f].IsUnused)
                    {
                        vertNormalsRaw[v] += doubleAreas[f] * faceNormals[f];
                        vertDA[v] += doubleAreas[f];
                    }
                vertNormals[v] = vertNormalsRaw[v].Normalized();
            }

            // --- Per-vertex energy + gradient ---
            for (int vert = 0; vert < nV; vert++)
            {
                if (P.Vertices[vert].IsUnused || P.Vertices.IsBoundary(vert))
                    continue;

                // Gather valid adjacent triangles and local indices
                int[] adjFaces = P.Vertices.GetVertexFaces(vert);
                var faces = new List<int>();
                var locIdx = new List<int>();
                foreach (int f in adjFaces)
                {
                    if (f < 0 || f >= nF || P.Faces[f].IsUnused) continue;
                    if (faceVerts[f].Length != 3) continue;
                    int li = Array.IndexOf(faceVerts[f], vert);
                    if (li < 0) continue;
                    faces.Add(f);
                    locIdx.Add(li);
                }

                if (faces.Count < 4) continue; // skip valence < 4

                // Fold guard: when the area-weighted normal nearly cancels (the 1-ring folds back
                // on itself past flat), Nv is meaningless and the factorv = (...)/|rawNormal| term
                // amplifies by ~1/coherence, spiking THIS vertex's neighbours' gradients (the
                // "about to explode" case). Its developability is undefined - skip it. This is the
                // zero-length VERTEX NORMAL, distinct from zero-area faces / zero-length edges.
                double rawLenV = vertNormalsRaw[vert].Length;
                if (rawLenV < 0.1 * vertDA[vert])
                {
                    if (rawLenV < 0.05 * vertDA[vert]) isFold[vert] = true;   // severe fold -> flag for healing collapse
                    continue;
                }

                Vec3 Nv = vertNormals[vert];

                // --- Build covariance matrix M (symmetric 3x3) ---
                double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;

                for (int fi = 0; fi < faces.Count; fi++)
                {
                    int f = faces[fi];
                    int li = locIdx[fi];
                    int[] fv = faceVerts[f];

                    Vec3 Pi = Pos(P.Vertices[fv[li]]);
                    Vec3 Pj = Pos(P.Vertices[fv[(li + 1) % 3]]);
                    Vec3 Pk = Pos(P.Vertices[fv[(li + 2) % 3]]);
                    double theta = Vec3.Angle(Pj - Pi, Pk - Pi);

                    Vec3 Nf = faceNormals[f];
                    Vec3 NvxNf = Vec3.Cross(Nv, Nf);
                    double sinPhi = NvxNf.Length;
                    if (1.0 + sinPhi == 1.0) continue;

                    double phi = SafeAcos(Nv * Nf);
                    Vec3 muvf = Vec3.Cross(NvxNf, Nv);
                    muvf = muvf.Normalized();
                    Vec3 Nfw = muvf * phi;

                    m00 += theta * Nfw.X * Nfw.X;
                    m01 += theta * Nfw.X * Nfw.Y;
                    m02 += theta * Nfw.X * Nfw.Z;
                    m11 += theta * Nfw.Y * Nfw.Y;
                    m12 += theta * Nfw.Y * Nfw.Z;
                    m22 += theta * Nfw.Z * Nfw.Z;
                }

                // --- Eigendecompose (robust 2x2 tangent block) ---
                // Nv is an EXACT null vector of M (every Nfw is perpendicular to it), so the
                // energy is the smaller eigenvalue of the 2x2 block in the tangent plane and x
                // its eigenvector. This is the SAME computation VertexEnergy uses, so the energy
                // the analytic gradient differentiates matches the energy the FD check sees -
                // unlike the old full-3x3 Jacobi + "which column is the normal" test, which
                // jittered the picked energy/eigenvector near eigenvalue crossings.
                Vec3 x;
                energy[vert] = MinTangentEigenpair(m00, m01, m02, m11, m12, m22, Nv, out x);

                // --- Gradient ---
                Vec3 totalFactorv = Vec3.Zero;

                for (int fi = 0; fi < faces.Count; fi++)
                {
                    int f = faces[fi];
                    int li = locIdx[fi];
                    int[] fv = faceVerts[f];

                    int i = vert;
                    int j = fv[(li + 1) % 3];
                    int k = fv[(li + 2) % 3];

                    Vec3 Pi = Pos(P.Vertices[i]);
                    Vec3 Pj = Pos(P.Vertices[j]);
                    Vec3 Pk = Pos(P.Vertices[k]);

                    Vec3 eij = Pj - Pi;
                    Vec3 ejk = Pk - Pj;
                    Vec3 eki = Pi - Pk;

                    double theta = Vec3.Angle(eij, -eki);
                    double dA = doubleAreas[f];
                    Vec3 Nf = faceNormals[f];

                    // Degenerate/sliver guard: a thin triangle still has a valid UNIT normal, so
                    // the sinPhi check below misses it - but its 1/dA and 1/edge^2 gradient terms
                    // blow up (a sliver gives gradients ~1/aspect, the spikes that corrupt the
                    // flow). Its developability is meaningless, so drop its gradient contribution.
                    double maxLen2 = Math.Max(eij * eij, Math.Max(ejk * ejk, eki * eki));
                    if (maxLen2 < 1e-20 || dA < 1e-2 * maxLen2) continue;   // aspect < ~1% => sliver

                    Vec3 NvxNf = Vec3.Cross(Nv, Nf);
                    double sinPhi = NvxNf.Length;
                    if (1.0 + sinPhi == 1.0) continue;

                    double cosPhi = Nv * Nf;
                    // Inverted-face guard. As phi -> pi (face normal nearly antipodal to the vertex
                    // normal) the tangent direction of Nf is undefined and the phi/sinPhi (factorf)
                    // and phi/tanPhi (factorv) amplifiers blow up, spiking the gradient toward an
                    // explosion. Threshold justified by the bench: a CONVEX sharp edge (even razor +
                    // strongly asymmetric, where the area-weighted Nv is pulled to one side) tops out
                    // at ~106deg from the vertex normal and is developable (gradient 0 - preserved).
                    // Reaching phi > 148deg requires the surface to fold UNDER itself (a degenerate
                    // overhang / inversion), so this never touches a legitimate convex edge.
                    if (cosPhi < -0.85) continue;   // phi > ~148 deg => inverted/folded face, drop it
                    double phi = SafeAcos(cosPhi);
                    double tanPhi = sinPhi / cosPhi;

                    Vec3 nuf = NvxNf;
                    nuf = nuf.Normalized();
                    Vec3 muvf = Vec3.Cross(nuf, Nv);
                    Vec3 muff = Vec3.Cross(nuf, Nf);
                    Vec3 Nfw = muvf * phi;

                    double xNfw = x * Nfw;
                    double xNfw2 = xNfw * xNfw;
                    double xMuvf = x * muvf;
                    double xNuf = x * nuf;

                    // dTheta / d{i,j,k}
                    double eijSqInv = 1.0 / (eij * eij);
                    double ekiSqInv = 1.0 / (eki * eki);
                    Vec3 eij_n = eij * eijSqInv;
                    Vec3 eki_n = eki * ekiSqInv;
                    Vec3 dTdi = Vec3.Cross(Nf, eij_n + eki_n);
                    Vec3 dTdj = -Vec3.Cross(Nf, eij_n);
                    Vec3 dTdk = -Vec3.Cross(Nf, eki_n);

                    // factorf: face-normal derivative contribution
                    Vec3 fvec = 2.0 * xNfw * theta *
                        (xMuvf * muff + (phi / sinPhi) * xNuf * nuf);

                    // factorf * dN/d{i,j,k}  where dN/di = (ejk x N) * N^T / dA
                    // factorf * dNdi = (fvec . (ejk x N)) / dA * N
                    double cdi = (fvec * Vec3.Cross(ejk, Nf)) / dA;
                    double cdj = (fvec * Vec3.Cross(eki, Nf)) / dA;
                    double cdk = (fvec * Vec3.Cross(eij, Nf)) / dA;

                    energyGrad[i] += xNfw2 * dTdi + cdi * Nf;
                    energyGrad[j] += xNfw2 * dTdj + cdj * Nf;
                    energyGrad[k] += xNfw2 * dTdk + cdk * Nf;

                    // factorv: vertex-normal derivative (accumulate, apply below)
                    double rawLen = vertNormalsRaw[vert].Length;
                    if (rawLen < 1e-16) continue;
                    if (Math.Abs(tanPhi) < 1e-16) continue;

                    Vec3 factorv = -2.0 * xNfw * theta *
                        ((x * (muvf + phi * Nv)) * muvf +
                         (phi / tanPhi) * xNuf * nuf) / rawLen;
                    totalFactorv += factorv;
                }

                // Apply vertex-normal derivative through cross-product matrices.
                // For each face g in 1-ring: dP/di = J(eik)-J(eij), dP/dj = -J(eik), dP/dk = J(eij);
                // row_vec * J(a) = row_vec x a.
                for (int gi = 0; gi < faces.Count; gi++)
                {
                    int g = faces[gi];
                    int gli = locIdx[gi];
                    int[] gv = faceVerts[g];

                    int ig = vert;
                    int jg = gv[(gli + 1) % 3];
                    int kg = gv[(gli + 2) % 3];

                    Vec3 eij_g = Pos(P.Vertices[jg]) - Pos(P.Vertices[ig]);
                    Vec3 eik_g = Pos(P.Vertices[kg]) - Pos(P.Vertices[ig]);

                    Vec3 cxEij = Vec3.Cross(totalFactorv, eij_g);
                    Vec3 cxEik = Vec3.Cross(totalFactorv, eik_g);

                    energyGrad[ig] += cxEik - cxEij;
                    energyGrad[jg] += -cxEik;
                    energyGrad[kg] += cxEij;
                }
            }
        }

        // ===== Numerical gradient (correct by construction) =====
        // The analytic gradient above has derivation errors at some configurations
        // (the finite-difference harness catches them). This computes the gradient by
        // central differences of the energy instead. A vertex's energy depends only on
        // its 1-ring, so perturbing v changes only v and its neighbours' energies -
        // each Partial stays O(valence), keeping the whole thing O(E).
        public static void ComputeNumericalGrad(PlanktonMesh P, out double[] energy, out Vec3[] grad)
        {
            int nV = P.Vertices.Count;
            energy = new double[nV];
            grad = new Vec3[nV];

            for (int u = 0; u < nV; u++)
                energy[u] = VertexEnergy(P, u);

            double eps = 1e-4 * RepresentativeEdge(P);
            if (eps <= 0) eps = 1e-4;

            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) continue;
                grad[v] = new Vec3(Partial(P, v, 0, eps), Partial(P, v, 1, eps), Partial(P, v, 2, eps));
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
            int m = 0;
            for (int v = 0; v < nV; v++)
                if (!P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v) && grad[v].Length > 0) m++;
            if (m < 4) return;

            double[] mg = new double[m];
            int k = 0;
            for (int v = 0; v < nV; v++)
                if (!P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v) && grad[v].Length > 0) mg[k++] = grad[v].Length;
            Array.Sort(mg);
            double thr = 8.0 * mg[m / 2];   // 8x the median magnitude
            if (thr <= 0) return;
            for (int v = 0; v < nV; v++)
                if (grad[v].Length > thr) grad[v] = Vec3.Zero;
        }

        private static double Partial(PlanktonMesh P, int v, int axis, double eps)
        {
            float ox = P.Vertices[v].X, oy = P.Vertices[v].Y, oz = P.Vertices[v].Z;
            int[] nb = P.Vertices.GetVertexNeighbours(v);

            double Ep = LocalEnergySum(P, v, nb, axis, ox, oy, oz, +eps);
            double Em = LocalEnergySum(P, v, nb, axis, ox, oy, oz, -eps);
            P.Vertices.SetVertex(v, (double)ox, (double)oy, (double)oz); // restore
            return (Ep - Em) / (2.0 * eps);
        }

        private static double LocalEnergySum(PlanktonMesh P, int v, int[] nb, int axis, float ox, float oy, float oz, double delta)
        {
            double x = ox, y = oy, z = oz;
            if (axis == 0) x += delta; else if (axis == 1) y += delta; else z += delta;
            P.Vertices.SetVertex(v, x, y, z);

            double e = VertexEnergy(P, v);
            for (int n = 0; n < nb.Length; n++) e += VertexEnergy(P, nb[n]);
            return e;
        }

        // Energy of a single vertex, recomputed from current positions (local normals).
        // Matches the per-vertex energy assigned by ComputeHingeEnergyAndGrad.
        public static double VertexEnergy(PlanktonMesh P, int u)
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

            double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
            for (int fi = 0; fi < faces.Count; fi++)
            {
                int[] fv = P.Faces.GetFaceVertices(faces[fi]);
                int li = locIdx[fi];
                Vec3 Pi = Pos(P.Vertices[fv[li]]);
                Vec3 Pj = Pos(P.Vertices[fv[(li + 1) % 3]]);
                Vec3 Pk = Pos(P.Vertices[fv[(li + 2) % 3]]);
                double theta = Vec3.Angle(Pj - Pi, Pk - Pi);

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

            // Nv is an exact zero-eigenvector of M (every Nfw is perpendicular to it), so
            // the energy is the smaller eigenvalue of the 2x2 tangent-plane block - closed
            // form, no iterative eigensolve. ~10x cheaper than the Jacobi sweep.
            return MinTangentEigenvalue(m00, m01, m02, m11, m12, m22, Nv);
        }

        private static double MinTangentEigenvalue(double m00, double m01, double m02,
                                                   double m11, double m12, double m22, Vec3 Nv)
        {
            Vec3 x;
            return MinTangentEigenpair(m00, m01, m02, m11, m12, m22, Nv, out x);
        }

        // Smaller tangent eigenvalue of M (the developability energy) AND its eigenvector x
        // (the min-curvature direction the gradient needs), computed in the 2D tangent plane
        // perpendicular to Nv - M's exact null vector. Closed form, no iterative eigensolve,
        // and robust where a full-3x3 solver's "which column is the normal" test would flip.
        private static double MinTangentEigenpair(double m00, double m01, double m02,
                                                  double m11, double m12, double m22,
                                                  Vec3 Nv, out Vec3 x)
        {
            // orthonormal tangent basis perpendicular to Nv
            Vec3 t1 = Vec3.Cross(Nv, new Vec3(1, 0, 0));
            if (t1.Length < 1e-6) t1 = Vec3.Cross(Nv, new Vec3(0, 1, 0));
            t1 = t1.Normalized();
            Vec3 t2 = Vec3.Cross(Nv, t1).Normalized();

            double a = QuadForm(m00, m01, m02, m11, m12, m22, t1, t1);
            double b = QuadForm(m00, m01, m02, m11, m12, m22, t1, t2);
            double d = QuadForm(m00, m01, m02, m11, m12, m22, t2, t2);

            double half = 0.5 * (a + d);
            double disc = Math.Sqrt(0.25 * (a - d) * (a - d) + b * b);
            double lambda = half - disc; // smaller eigenvalue of [[a,b],[b,d]]

            // eigenvector of [[a,b],[b,d]] for lambda: (b, lambda-a) or (lambda-d, b); pick
            // the better-conditioned (larger-norm) representative to stay stable near degeneracy.
            double c1x = b, c1y = lambda - a;
            double c2x = lambda - d, c2y = b;
            double y1, y2;
            if (c1x * c1x + c1y * c1y >= c2x * c2x + c2y * c2y) { y1 = c1x; y2 = c1y; }
            else { y1 = c2x; y2 = c2y; }

            Vec3 xv = y1 * t1 + y2 * t2;
            double xl = xv.Length;
            x = (xl > 1e-300) ? xv / xl : t1;
            return lambda;
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
