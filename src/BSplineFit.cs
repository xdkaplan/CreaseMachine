using System.Collections.Generic;

namespace CreaseMachine
{
    /// <summary>
    /// Degree-3 (cubic) B-spline fit for fixed "bent-wire" seam edges. A boundary loop of N mesh
    /// vertices is represented by a LOW-DOF periodic uniform cubic B-spline with about N/ratio control
    /// points (default: 1 control point per 5 mesh points). The mesh boundary vertices are mapped onto
    /// that smooth curve (<see cref="LowDofTargets"/>) and pinned there during the isometric solve, so a
    /// seam becomes a smooth low-DOF wire rather than a faceted polyline that respects every mesh kink.
    /// <see cref="SampleCurve"/> returns a dense closed polyline for display. Pure geometry (Vec3 only).
    /// </summary>
    public static class BSplineFit
    {
        // Control points: evenly-spaced subsample of the loop, count = max(4, round(N/ratio)).
        static Vec3[] ControlPoints(Vec3[] loop, int ratio)
        {
            int n = loop.Length;
            int m = System.Math.Max(4, (int)System.Math.Round(n / (double)System.Math.Max(1, ratio)));
            if (m > n) m = n;
            var c = new Vec3[m];
            for (int k = 0; k < m; k++) c[k] = loop[(int)System.Math.Round(k * (double)n / m) % n];
            return c;
        }

        // Periodic uniform cubic B-spline evaluated at parameter t in [0, m): smooths its control polygon.
        static Vec3 Eval(Vec3[] c, double t)
        {
            int m = c.Length;
            int seg = ((int)System.Math.Floor(t)) % m; if (seg < 0) seg += m;
            double u = t - System.Math.Floor(t), u2 = u * u, u3 = u2 * u;
            double b0 = (1 - 3 * u + 3 * u2 - u3) / 6.0;
            double b1 = (4 - 6 * u2 + 3 * u3) / 6.0;
            double b2 = (1 + 3 * u + 3 * u2 - 3 * u3) / 6.0;
            double b3 = u3 / 6.0;
            return c[(seg - 1 + m) % m] * b0 + c[seg] * b1 + c[(seg + 1) % m] * b2 + c[(seg + 2) % m] * b3;
        }

        /// <summary>One target position per loop vertex, snapped onto the low-DOF cubic B-spline (the wire).</summary>
        public static Vec3[] LowDofTargets(Vec3[] loop, int ratio)
        {
            int n = loop.Length; if (n < 4) return (Vec3[])loop.Clone();
            var c = ControlPoints(loop, ratio); int m = c.Length;
            var t = new Vec3[n];
            for (int i = 0; i < n; i++) t[i] = Eval(c, i * (double)m / n);
            return t;
        }

        /// <summary>Dense closed polyline sampling of the wire, for display (perSeg points per control span).</summary>
        public static Vec3[] SampleCurve(Vec3[] loop, int ratio, int perSeg)
        {
            int n = loop.Length; if (n < 4) return (Vec3[])loop.Clone();
            var c = ControlPoints(loop, ratio); int m = c.Length;
            var outp = new List<Vec3>(m * perSeg + 1);
            for (int k = 0; k < m; k++) for (int s = 0; s < perSeg; s++) outp.Add(Eval(c, k + s / (double)perSeg));
            outp.Add(outp[0]);
            return outp.ToArray();
        }
    }
}
