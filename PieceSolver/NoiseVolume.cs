using System;

namespace PieceSolver
{
    // Solid (3D) blue-noise-SPECTRUM volume for the surface LIC. A true void-and-cluster blue-noise
    // point set is expensive to generate in 3D at runtime; this uses high-pass-filtered white noise,
    // which has the defining blue-noise SPECTRAL property — energy pushed to high frequencies, no
    // low-frequency clumping — which is exactly what LIC needs for crisp, even streaks. Toroidal, so
    // the GL_REPEAT-wrapped texture tiles seamlessly across the mesh. Swap in a baked void-and-cluster
    // volume (or one derived from a supplied blue-noise tile) later if a true point set is wanted.
    static class NoiseVolume
    {
        // Returns an n*n*n array of single-channel bytes (x + y*n + z*n*n).
        public static byte[] Blue(int n, int seed)
        {
            int N = n * n * n;
            var rng = new Random(seed);
            var w = new float[N];
            for (int i = 0; i < N; i++) w[i] = (float)rng.NextDouble();

            var blur = BoxBlur3(w, n, 2);   // ~Gaussian low-pass (two separable box passes)
            var hp = new float[N];
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < N; i++) { hp[i] = w[i] - blur[i]; if (hp[i] < mn) mn = hp[i]; if (hp[i] > mx) mx = hp[i]; }

            float inv = 1f / Math.Max(1e-6f, mx - mn);
            var outb = new byte[N];
            for (int i = 0; i < N; i++)
            {
                int b = (int)((hp[i] - mn) * inv * 255f);
                outb[i] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
            }
            return outb;
        }

        static float[] BoxBlur3(float[] src, int n, int passes)
        {
            var a = (float[])src.Clone();
            var b = new float[a.Length];
            for (int p = 0; p < passes; p++)
            {
                BlurAxis(a, b, n, 0); BlurAxis(b, a, n, 1); BlurAxis(a, b, n, 2);
                var t = a; a = b; b = t;
            }
            return a;
        }

        // Toroidal 3-tap box blur along one axis (0=x, 1=y, 2=z).
        static void BlurAxis(float[] src, float[] dst, int n, int axis)
        {
            int n2 = n * n;
            for (int z = 0; z < n; z++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int i = x + y * n + z * n2;
                        int im, ip;
                        if (axis == 0) { im = ((x - 1 + n) % n) + y * n + z * n2; ip = ((x + 1) % n) + y * n + z * n2; }
                        else if (axis == 1) { im = x + ((y - 1 + n) % n) * n + z * n2; ip = x + ((y + 1) % n) * n + z * n2; }
                        else { im = x + y * n + ((z - 1 + n) % n) * n2; ip = x + y * n + ((z + 1) % n) * n2; }
                        dst[i] = (src[im] + src[i] + src[ip]) * (1f / 3f);
                    }
        }
    }
}
