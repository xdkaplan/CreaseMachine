using OpenTK.Mathematics;

namespace PieceSolver
{
    // open-color palette (https://github.com/yeun/open-color) — standardized, accessible swatches.
    // Exact open-color hex values, exposed as sRGB 0-1 Vector3 (used directly, like the other view colours).
    // Add more shades/hues here as needed; today only the ones in use are listed.
    static class OpenColor
    {
        static Vector3 Hex(int rgb) => new Vector3(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

        // Reds — the remove / carve previews.
        public static readonly Vector3 Red1 = Hex(0xffe3e3);   // carve no-op affordance (non-active face under the brush)
        public static readonly Vector3 Red3 = Hex(0xffa8a8);   // no-selection remove: marked, piece not yet fully covered
        public static readonly Vector3 Red5 = Hex(0xff6b6b);   // a piece/face that WILL be deleted (remove + carve)

        // Blue 9 — the crease lines.
        public static readonly Vector3 Blue9 = Hex(0x1864ab);
        // Indigo 3 — the active-piece selection highlight.
        public static readonly Vector3 Indigo3 = Hex(0x91a7ff);
        // Gray 4 — the mesh edge overlay.
        public static readonly Vector3 Gray4 = Hex(0xced4da);

        // The chromatic hues at shade 3, in hue order — the standardized "rainbow" for per-piece tinting.
        public static readonly Vector3[] Rainbow3 =
        {
            Hex(0xffa8a8), // red 3
            Hex(0xfaa2c1), // pink 3
            Hex(0xe599f7), // grape 3
            Hex(0xb197fc), // violet 3
            Hex(0x91a7ff), // indigo 3
            Hex(0x74c0fc), // blue 3
            Hex(0x66d9e8), // cyan 3
            Hex(0x63e6be), // teal 3
            Hex(0x8ce99a), // green 3
            Hex(0xc0eb75), // lime 3
            Hex(0xffe066), // yellow 3
            Hex(0xffc078), // orange 3
        };
    }
}
