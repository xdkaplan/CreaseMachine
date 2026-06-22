using System;
using System.Collections.Generic;

namespace CreaseMachine
{
    /// <summary>
    /// Small periodic uniform cubic (degree-3) B-spline: a low-DOF control polygon plus curve
    /// evaluation / sampling. The foundation for the studio's seam-wire visualizer and (later)
    /// user-editable seam curves. Pure geometry (Vec3 only — no Plankton, no Rhino).
    /// </summary>
    public sealed class BSpline
    {
        /// <summary>Control points (the low-DOF degrees of freedom), treated as a closed/periodic loop.</summary>
        public readonly Vec3[] Control;

        public BSpline(Vec3[] control) { Control = control; }

        /// <summary>Evaluate the periodic uniform cubic at parameter t in [0, Control.Length).</summary>
        public Vec3 Eval(double t)
        {
            int m = Control.Length;
            int seg = ((int)Math.Floor(t)) % m; if (seg < 0) seg += m;
            double u = t - Math.Floor(t), u2 = u * u, u3 = u2 * u;
            double b0 = (1 - 3 * u + 3 * u2 - u3) / 6.0;
            double b1 = (4 - 6 * u2 + 3 * u3) / 6.0;
            double b2 = (1 + 3 * u + 3 * u2 - 3 * u3) / 6.0;
            double b3 = u3 / 6.0;
            return Control[(seg - 1 + m) % m] * b0 + Control[seg] * b1 + Control[(seg + 1) % m] * b2 + Control[(seg + 2) % m] * b3;
        }

        /// <summary>Dense closed polyline sampling (perSeg points per control span), for display.</summary>
        public Vec3[] SampleCurve(int perSeg)
        {
            int m = Control.Length;
            if (m < 4) return (Vec3[])Control.Clone();
            var o = new List<Vec3>(m * perSeg + 1);
            for (int k = 0; k < m; k++) for (int s = 0; s < perSeg; s++) o.Add(Eval(k + s / (double)perSeg));
            o.Add(o[0]);
            return o.ToArray();
        }

        /// <summary>n on-curve points evenly spaced in parameter — the targets for an n-vertex boundary loop.</summary>
        public Vec3[] EvenTargets(int n)
        {
            int m = Control.Length;
            var t = new Vec3[n];
            for (int i = 0; i < n; i++) t[i] = Eval(i * (double)m / n);
            return t;
        }

        /// <summary>Fit a low-DOF closed cubic to a boundary loop: control points = an evenly-spaced
        /// subsample (~1 per <paramref name="ratio"/> loop vertices, min 4). The curve smooths it.</summary>
        public static BSpline FitClosed(Vec3[] loop, int ratio)
        {
            int n = loop.Length;
            int m = Math.Max(4, (int)Math.Round(n / (double)Math.Max(1, ratio)));
            if (m > n) m = n;
            var c = new Vec3[m];
            for (int k = 0; k < m; k++) c[k] = loop[(int)Math.Round(k * (double)n / m) % n];
            return new BSpline(c);
        }
    }
}
