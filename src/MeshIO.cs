using System;
using System.Collections.Generic;
using System.Globalization;
using Plankton;

namespace CreaseMachine
{
    /// <summary>
    /// Rhino-free mesh load/save shared by the headless front-ends (CLI, GUI). Binary-STL welds
    /// the triangle soup into a connected PlanktonMesh; OBJ already has shared vertices. PLY export
    /// carries an optional per-vertex colour (developability energy) for review in MeshLab/Blender.
    /// </summary>
    public static class MeshIO
    {
        public static PlanktonMesh Load(string path)
        {
            return path.ToLowerInvariant().EndsWith(".obj") ? LoadObj(path) : LoadBinaryStl(path);
        }

        public static PlanktonMesh LoadBinaryStl(string path)
        {
            byte[] b = System.IO.File.ReadAllBytes(path);
            int nTri = BitConverter.ToInt32(b, 80);
            const int baseOff = 84;
            double scale = 0;
            for (int t = 0; t < nTri; t++)
            {
                int o = baseOff + t * 50 + 12;
                for (int k = 0; k < 9; k++) { double c = Math.Abs(BitConverter.ToSingle(b, o + k * 4)); if (c > scale) scale = c; }
            }
            double tol = scale > 0 ? scale * 1e-5 : 1e-5;
            var m = new PlanktonMesh();
            var map = new Dictionary<string, int>();
            for (int t = 0; t < nTri; t++)
            {
                int o = baseOff + t * 50 + 12;
                int[] vidx = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    float x = BitConverter.ToSingle(b, o + (k * 3 + 0) * 4);
                    float y = BitConverter.ToSingle(b, o + (k * 3 + 1) * 4);
                    float z = BitConverter.ToSingle(b, o + (k * 3 + 2) * 4);
                    long kx = (long)Math.Round(x / tol), ky = (long)Math.Round(y / tol), kz = (long)Math.Round(z / tol);
                    string key = kx + "_" + ky + "_" + kz;
                    if (!map.TryGetValue(key, out int vi)) { vi = m.Vertices.Add(x, y, z); map[key] = vi; }
                    vidx[k] = vi;
                }
                if (vidx[0] != vidx[1] && vidx[1] != vidx[2] && vidx[0] != vidx[2]) m.Faces.AddFace(vidx[0], vidx[1], vidx[2]);
            }
            return m;
        }

        public static PlanktonMesh LoadObj(string path)
        {
            var m = new PlanktonMesh();
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var ln = raw.Trim();
                if (ln.StartsWith("v "))
                {
                    var p = ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    m.Vertices.Add(D(p[1]), D(p[2]), D(p[3]));
                }
                else if (ln.StartsWith("f "))
                {
                    var p = ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    var idx = new List<int>();
                    for (int i = 1; i < p.Length; i++)
                    {
                        int slash = p[i].IndexOf('/');
                        string s = slash >= 0 ? p[i].Substring(0, slash) : p[i];
                        idx.Add(int.Parse(s) - 1);
                    }
                    for (int i = 2; i < idx.Count; i++) m.Faces.AddFace(idx[0], idx[i - 1], idx[i]); // fan-triangulate
                }
            }
            return m;
        }

        public static void WriteObj(PlanktonMesh P, string path)
        {
            var sb = new System.Text.StringBuilder();
            int[] map = new int[P.Vertices.Count]; int idx = 1;
            for (int v = 0; v < P.Vertices.Count; v++)
            {
                if (P.Vertices[v].IsUnused) { map[v] = -1; continue; }
                var p = P.Vertices[v];
                sb.AppendLine("v " + F(p.X) + " " + F(p.Y) + " " + F(p.Z));
                map[v] = idx++;
            }
            for (int f = 0; f < P.Faces.Count; f++)
            {
                if (!IsTri(P, f)) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                sb.AppendLine("f " + map[fv[0]] + " " + map[fv[1]] + " " + map[fv[2]]);
            }
            System.IO.File.WriteAllText(path, sb.ToString());
        }

        // ASCII PLY with per-vertex colour (caller supplies a [0,1] scalar per vertex, e.g. energy).
        public static void WritePly(PlanktonMesh P, string path, double[] scalar01)
        {
            int[] map = new int[P.Vertices.Count]; int nUsed = 0;
            for (int v = 0; v < P.Vertices.Count; v++) map[v] = P.Vertices[v].IsUnused ? -1 : nUsed++;
            int nFaces = 0; for (int f = 0; f < P.Faces.Count; f++) if (IsTri(P, f)) nFaces++;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ply"); sb.AppendLine("format ascii 1.0");
            sb.AppendLine("element vertex " + nUsed);
            sb.AppendLine("property float x"); sb.AppendLine("property float y"); sb.AppendLine("property float z");
            sb.AppendLine("property uchar red"); sb.AppendLine("property uchar green"); sb.AppendLine("property uchar blue");
            sb.AppendLine("element face " + nFaces);
            sb.AppendLine("property list uchar int vertex_indices"); sb.AppendLine("end_header");
            for (int v = 0; v < P.Vertices.Count; v++)
            {
                if (P.Vertices[v].IsUnused) continue;
                var p = P.Vertices[v];
                double tcol = scalar01 != null && v < scalar01.Length ? scalar01[v] : 0.0;
                int r = (int)(255 * Math.Min(1, Math.Max(0, tcol) * 2));
                int bl = (int)(255 * Math.Min(1, (1 - tcol) * 2));
                int g = (int)(255 * (1 - Math.Abs(tcol - 0.5) * 2));
                sb.AppendLine(F(p.X) + " " + F(p.Y) + " " + F(p.Z) + " " + r + " " + g + " " + bl);
            }
            for (int f = 0; f < P.Faces.Count; f++)
            {
                if (!IsTri(P, f)) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                sb.AppendLine("3 " + map[fv[0]] + " " + map[fv[1]] + " " + map[fv[2]]);
            }
            System.IO.File.WriteAllText(path, sb.ToString());
        }

        static bool IsTri(PlanktonMesh P, int f) { return !P.Faces[f].IsUnused && P.Faces.GetHalfedges(f).Length == 3; }
        static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        static string F(double d) { return d.ToString(CultureInfo.InvariantCulture); }
    }
}
