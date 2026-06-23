using System;
using System.Collections.Generic;
using Plankton;
using CreaseMachine;

namespace PieceSolver
{
    // Derives per-vertex RULING directions on the (developing) mesh and returns them as line segments for
    // display. A ruling is the surface's zero-curvature direction - the straight generator of a developable.
    // We estimate the per-vertex shape operator (2nd fundamental form) from how the normal turns across the
    // 1-ring (normal-curvature fit via Euler's formula), then take the principal direction of SMALLEST
    // |curvature|: that is the ruling. Same recipe the paper uses (the zero-eigenvector of the shape operator).
    // Crisp only where the surface is actually developable (one curvature ~0); near-flat or near-isotropic
    // vertices have no meaningful ruling and are skipped.
    static class RulingField
    {
        // Line-segment endpoints (6 floats per ruling: x0 y0 z0 x1 y1 z1), in mesh model space.
        // lenFrac = half-length as a fraction of mean edge; liftFrac = offset along the normal (x mean edge)
        // so the segments sit just above the surface and don't z-fight.
        public static float[] Compute(PlanktonMesh M, double lenFrac, double liftFrac)
        {
            int nV = M.Vertices.Count;
            var pos = new Vec3[nV]; var nrm = new Vec3[nV];
            for (int v = 0; v < nV; v++) { if (M.Vertices[v].IsUnused) continue; var p = M.Vertices[v]; pos[v] = new Vec3(p.X, p.Y, p.Z); }

            for (int f = 0; f < M.Faces.Count; f++)
            {
                if (M.Faces[f].IsUnused) continue;
                int[] fv = M.Faces.GetFaceVertices(f); if (fv.Length < 3) continue;
                Vec3 cr = Vec3.Cross(pos[fv[1]] - pos[fv[0]], pos[fv[2]] - pos[fv[0]]);
                nrm[fv[0]] += cr; nrm[fv[1]] += cr; nrm[fv[2]] += cr;
            }
            for (int v = 0; v < nV; v++) { double L = nrm[v].Length; nrm[v] = L > 1e-20 ? nrm[v] * (1.0 / L) : new Vec3(0, 0, 1); }

            double meanEdge = 0; int nE = 0; int nH = M.Halfedges.Count;
            for (int h = 0; h < nH; h += 2) { if (M.Halfedges[h].IsUnused) continue; int i = M.Halfedges[h].StartVertex, j = M.Halfedges[h + 1].StartVertex; meanEdge += (pos[i] - pos[j]).Length; nE++; }
            meanEdge = nE > 0 ? meanEdge / nE : 1.0;
            double half = lenFrac * meanEdge, lift = liftFrac * meanEdge;

            var outv = new List<float>();
            for (int v = 0; v < nV; v++)
            {
                if (M.Vertices[v].IsUnused) continue;
                var nb = M.Vertices.GetVertexNeighbours(v); if (nb == null || nb.Length < 3) continue;
                Vec3 n = nrm[v];
                Vec3 t1 = Vec3.Cross(n, Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0));
                double t1l = t1.Length; if (t1l < 1e-12) continue; t1 = t1 * (1.0 / t1l);
                Vec3 t2 = Vec3.Cross(n, t1);

                // least-squares fit of II: kappa(dir) = II11 ca^2 + 2 II12 ca cb + II22 cb^2 over neighbours
                double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0, b0 = 0, b1 = 0, b2 = 0;
                foreach (int u in nb)
                {
                    if (u < 0 || M.Vertices[u].IsUnused) continue;
                    Vec3 e = pos[u] - pos[v]; double ee = e * e; if (ee < 1e-18) continue;
                    double kappa = 2.0 * (e * n) / ee;                 // normal curvature in direction e
                    double a = e * t1, bb = e * t2; double tl = Math.Sqrt(a * a + bb * bb); if (tl < 1e-12) continue;
                    double ca = a / tl, cb = bb / tl;
                    double c0 = ca * ca, c1 = 2 * ca * cb, c2 = cb * cb;
                    m00 += c0 * c0; m01 += c0 * c1; m02 += c0 * c2; m11 += c1 * c1; m12 += c1 * c2; m22 += c2 * c2;
                    b0 += c0 * kappa; b1 += c1 * kappa; b2 += c2 * kappa;
                }
                if (!Solve3(m00, m01, m02, m11, m12, m22, b0, b1, b2, out double II11, out double II12, out double II22)) continue;

                double tr = II11 + II22, det = II11 * II22 - II12 * II12;
                double disc = Math.Sqrt(Math.Max(0, tr * tr * 0.25 - det));
                double l1 = tr * 0.5 + disc, l2 = tr * 0.5 - disc;
                double lbig = Math.Abs(l1) >= Math.Abs(l2) ? l1 : l2;
                double lsmall = Math.Abs(l1) < Math.Abs(l2) ? l1 : l2;
                if (Math.Abs(lbig) * meanEdge < 0.02) continue;          // ~flat: no meaningful ruling
                if (Math.Abs(lsmall) > 0.6 * Math.Abs(lbig)) continue;   // ~isotropic: no clear flat direction

                double ex, ey;   // eigenvector for lsmall (~zero-curvature) = ruling direction (in t1,t2)
                if (Math.Abs(II12) > 1e-12) { ex = II12; ey = lsmall - II11; }
                else if (Math.Abs(II11) <= Math.Abs(II22)) { ex = 1; ey = 0; }
                else { ex = 0; ey = 1; }
                double el = Math.Sqrt(ex * ex + ey * ey); if (el < 1e-12) continue; ex /= el; ey /= el;

                Vec3 r = t1 * ex + t2 * ey;
                Vec3 c = pos[v] + n * lift;
                Vec3 p0 = c - r * half, p1 = c + r * half;
                outv.Add((float)p0.X); outv.Add((float)p0.Y); outv.Add((float)p0.Z);
                outv.Add((float)p1.X); outv.Add((float)p1.Y); outv.Add((float)p1.Z);
            }
            return outv.ToArray();
        }

        // Per-vertex ruling DIRECTION field for the surface LIC (instead of discrete segments). Returns
        // float[nV*3]: the unit ruling direction scaled by kappa_max (the MAX principal curvature =
        // 1 / radius of maximum curvature), robustly normalised so the bulk spans [0,1]. kappa_max - NOT
        // anisotropy - is the strength driver: flat / barely-curved areas read ~0 (no hairs; we don't
        // care where a flat plane's ruling points), tightly-curved areas read high (bold hairs). Whether
        // that curvature is developable shows in the LIC COHERENCE (combed = single ruling; swirly =
        // doubly-curved), so non-developable spots still light up instead of hiding as low "confidence".
        public static float[] ComputeField(PlanktonMesh M, out float fieldMax)
        {
            fieldMax = 1f;
            int nV = M.Vertices.Count;
            var field = new float[nV * 3];
            var pos = new Vec3[nV]; var nrm = new Vec3[nV];
            for (int v = 0; v < nV; v++) { if (M.Vertices[v].IsUnused) continue; var p = M.Vertices[v]; pos[v] = new Vec3(p.X, p.Y, p.Z); }

            for (int f = 0; f < M.Faces.Count; f++)
            {
                if (M.Faces[f].IsUnused) continue;
                int[] fv = M.Faces.GetFaceVertices(f); if (fv.Length < 3) continue;
                Vec3 cr = Vec3.Cross(pos[fv[1]] - pos[fv[0]], pos[fv[2]] - pos[fv[0]]);
                nrm[fv[0]] += cr; nrm[fv[1]] += cr; nrm[fv[2]] += cr;
            }
            for (int v = 0; v < nV; v++) { double L = nrm[v].Length; nrm[v] = L > 1e-20 ? nrm[v] * (1.0 / L) : new Vec3(0, 0, 1); }

            for (int v = 0; v < nV; v++)
            {
                if (M.Vertices[v].IsUnused) continue;
                var nb = M.Vertices.GetVertexNeighbours(v); if (nb == null || nb.Length < 3) continue;
                Vec3 n = nrm[v];
                Vec3 t1 = Vec3.Cross(n, Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0));
                double t1l = t1.Length; if (t1l < 1e-12) continue; t1 = t1 * (1.0 / t1l);
                Vec3 t2 = Vec3.Cross(n, t1);

                double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0, b0 = 0, b1 = 0, b2 = 0;
                foreach (int u in nb)
                {
                    if (u < 0 || M.Vertices[u].IsUnused) continue;
                    Vec3 e = pos[u] - pos[v]; double ee = e * e; if (ee < 1e-18) continue;
                    double kappa = 2.0 * (e * n) / ee;
                    double a = e * t1, bb = e * t2; double tl = Math.Sqrt(a * a + bb * bb); if (tl < 1e-12) continue;
                    double ca = a / tl, cb = bb / tl;
                    double c0 = ca * ca, c1 = 2 * ca * cb, c2 = cb * cb;
                    m00 += c0 * c0; m01 += c0 * c1; m02 += c0 * c2; m11 += c1 * c1; m12 += c1 * c2; m22 += c2 * c2;
                    b0 += c0 * kappa; b1 += c1 * kappa; b2 += c2 * kappa;
                }
                if (!Solve3(m00, m01, m02, m11, m12, m22, b0, b1, b2, out double II11, out double II12, out double II22)) continue;

                double tr = II11 + II22, det = II11 * II22 - II12 * II12;
                double disc = Math.Sqrt(Math.Max(0, tr * tr * 0.25 - det));
                double l1 = tr * 0.5 + disc, l2 = tr * 0.5 - disc;
                double lbig = Math.Abs(l1) >= Math.Abs(l2) ? l1 : l2;
                double lsmall = Math.Abs(l1) < Math.Abs(l2) ? l1 : l2;
                if (Math.Abs(lbig) < 1e-20) continue;

                double ex, ey;   // eigenvector for lsmall (min curvature) = ruling direction (in t1,t2)
                if (Math.Abs(II12) > 1e-12) { ex = II12; ey = lsmall - II11; }
                else if (Math.Abs(II11) <= Math.Abs(II22)) { ex = 1; ey = 0; }
                else { ex = 0; ey = 1; }
                double el = Math.Sqrt(ex * ex + ey * ey); if (el < 1e-12) continue; ex /= el; ey /= el;

                // Strength = kappa_max (max principal curvature = 1 / radius of max curvature), NOT
                // anisotropy: flat areas -> ~0 (no hairs), tightly curved -> bold. Direction is still the
                // min-curvature (ruling) eigenvector; on doubly-curved spots it is arbitrary, so the hairs
                // go bold + swirly there - which is exactly the non-developable signal we want to SEE.
                Vec3 r = t1 * ex + t2 * ey;
                float kmax = (float)Math.Abs(lbig);
                field[v * 3]     = (float)r.X * kmax;
                field[v * 3 + 1] = (float)r.Y * kmax;
                field[v * 3 + 2] = (float)r.Z * kmax;
            }
            // Robust scale: normalise kappa_max by a high percentile (field magnitude == kappa_max since
            // the direction is unit) so the bulk spans [0,1] and a few sharp-crease spikes just saturate.
            var mags = new System.Collections.Generic.List<float>(nV);
            for (int v = 0; v < nV; v++)
            {
                float L = (float)System.Math.Sqrt((double)field[v * 3] * field[v * 3] +
                    (double)field[v * 3 + 1] * field[v * 3 + 1] + (double)field[v * 3 + 2] * field[v * 3 + 2]);
                if (L > 0f) mags.Add(L);
            }
            mags.Sort();
            fieldMax = mags.Count > 0 ? System.Math.Max(1e-8f, mags[(int)(0.92 * (mags.Count - 1))]) : 1f;
            return field;
        }

        // Solve the symmetric 3x3 [[a,b,c],[b,d,e],[c,e,f]] x = (r0,r1,r2) via cofactor inverse. False if singular.
        static bool Solve3(double a, double b, double c, double d, double e, double f, double r0, double r1, double r2,
                           out double x, out double y, out double z)
        {
            x = y = z = 0;
            double det = a * (d * f - e * e) - b * (b * f - e * c) + c * (b * e - d * c);
            if (Math.Abs(det) < 1e-18) return false;
            double id = 1.0 / det;
            double i00 = d * f - e * e, i01 = c * e - b * f, i02 = b * e - c * d;
            double i11 = a * f - c * c, i12 = b * c - a * e, i22 = a * d - b * b;
            x = id * (i00 * r0 + i01 * r1 + i02 * r2);
            y = id * (i01 * r0 + i11 * r1 + i12 * r2);
            z = id * (i02 * r0 + i12 * r1 + i22 * r2);
            return true;
        }
    }
}
