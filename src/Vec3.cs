using System;

namespace CreaseMachine
{
    /// <summary>
    /// Minimal double-precision 3D vector with no Rhino dependency, so the
    /// developability energy can compile and be unit-tested against just
    /// Plankton. Operator semantics deliberately mirror Rhino's Vector3d -
    /// in particular, <c>a * b</c> on two vectors is the DOT product - so the
    /// energy code ports across almost verbatim.
    /// </summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static Vec3 Zero { get { return new Vec3(0.0, 0.0, 0.0); } }

        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        public bool IsValid
        {
            get
            {
                return !(double.IsNaN(X) || double.IsNaN(Y) || double.IsNaN(Z)
                       || double.IsInfinity(X) || double.IsInfinity(Y) || double.IsInfinity(Z));
            }
        }

        /// <summary>Returns a unit vector in the same direction, or Zero if degenerate.</summary>
        public Vec3 Normalized()
        {
            double len = Length;
            if (len < 1e-30) return Zero;
            double inv = 1.0 / len;
            return new Vec3(X * inv, Y * inv, Z * inv);
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator -(Vec3 a) { return new Vec3(-a.X, -a.Y, -a.Z); }
        public static Vec3 operator *(Vec3 a, double t) { return new Vec3(a.X * t, a.Y * t, a.Z * t); }
        public static Vec3 operator *(double t, Vec3 a) { return new Vec3(a.X * t, a.Y * t, a.Z * t); }
        public static double operator *(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; } // DOT
        public static Vec3 operator /(Vec3 a, double t) { return new Vec3(a.X / t, a.Y / t, a.Z / t); }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y,
                            a.Z * b.X - a.X * b.Z,
                            a.X * b.Y - a.Y * b.X);
        }

        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        /// <summary>Unsigned angle between two vectors in [0, pi]; matches Rhino's Vector3d.VectorAngle.</summary>
        public static double Angle(Vec3 a, Vec3 b)
        {
            double la = a.Length, lb = b.Length;
            if (la < 1e-30 || lb < 1e-30) return 0.0;
            double c = (a * b) / (la * lb);
            if (c > 1.0) c = 1.0; else if (c < -1.0) c = -1.0;
            return Math.Acos(c);
        }
    }
}
