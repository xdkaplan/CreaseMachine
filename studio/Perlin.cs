using System;

namespace CreaseStudio
{
    // Classic Ken Perlin improved 3D gradient noise, output ~[-1, 1]. Permutation built from a
    // deterministic LCG shuffle (seeded), so the noise field is reproducible run to run.
    sealed class Perlin
    {
        readonly int[] _p = new int[512];

        public Perlin(int seed = 1337)
        {
            var perm = new int[256];
            for (int i = 0; i < 256; i++) perm[i] = i;
            uint s = (uint)seed;
            for (int i = 255; i > 0; i--)                 // Fisher-Yates with an LCG
            {
                s = s * 1664525u + 1013904223u;
                int j = (int)(s % (uint)(i + 1));
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
            for (int i = 0; i < 512; i++) _p[i] = perm[i & 255];
        }

        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        static double Lerp(double a, double b, double t) => a + t * (b - a);
        static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        public double Noise(double x, double y, double z)
        {
            int X = (int)Math.Floor(x) & 255, Y = (int)Math.Floor(y) & 255, Z = (int)Math.Floor(z) & 255;
            x -= Math.Floor(x); y -= Math.Floor(y); z -= Math.Floor(z);
            double u = Fade(x), v = Fade(y), w = Fade(z);
            int A = _p[X] + Y, AA = _p[A] + Z, AB = _p[A + 1] + Z;
            int B = _p[X + 1] + Y, BA = _p[B] + Z, BB = _p[B + 1] + Z;
            return Lerp(
                Lerp(Lerp(Grad(_p[AA], x, y, z), Grad(_p[BA], x - 1, y, z), u),
                     Lerp(Grad(_p[AB], x, y - 1, z), Grad(_p[BB], x - 1, y - 1, z), u), v),
                Lerp(Lerp(Grad(_p[AA + 1], x, y, z - 1), Grad(_p[BA + 1], x - 1, y, z - 1), u),
                     Lerp(Grad(_p[AB + 1], x, y - 1, z - 1), Grad(_p[BB + 1], x - 1, y - 1, z - 1), u), v), w);
        }
    }
}
