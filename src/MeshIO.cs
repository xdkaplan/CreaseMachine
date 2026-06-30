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
            string p = path.ToLowerInvariant();
            if (p.EndsWith(".obj")) return LoadObj(path);
            if (p.EndsWith(".fbx")) return FbxIO.LoadBinaryFbx(path);   // preserves unwelded seam topology (per-face components)
            return LoadBinaryStl(path);
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

        public static PlanktonMesh LoadObj(string path) => LoadObj(path, out _);

        // As LoadObj, plus the partition: `g piece_<id>` face groups -> a per-face pieceMap (parallel to the
        // mesh faces). Faces before any group, or under a non-`piece_` group, take the current id (default 0).
        // A plain OBJ with no piece_ groups -> all-zero pieceMap (single piece). The doc-save form
        // (SAVE-OPEN.md) is a welded mesh + these groups; creases re-derive from (mesh + pieceMap) on load.
        public static PlanktonMesh LoadObj(string path, out int[] pieceMap)
        {
            var m = new PlanktonMesh();
            var pieces = new List<int>();   // per-face piece id, parallel to faces added
            int cur = 0;                    // current group's piece id
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var ln = raw.Trim();
                if (ln.StartsWith("v "))
                {
                    var p = ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    m.Vertices.Add(D(p[1]), D(p[2]), D(p[3]));
                }
                else if (ln.StartsWith("g ") || ln.StartsWith("g\t"))
                {
                    var p = ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    // `g piece_<int>` sets the current piece id; any other group is ignored (id unchanged).
                    if (p.Length >= 2 && p[1].StartsWith("piece_") && int.TryParse(p[1].Substring(6), out int pid)) cur = pid;
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
                    for (int i = 2; i < idx.Count; i++) { m.Faces.AddFace(idx[0], idx[i - 1], idx[i]); pieces.Add(cur); } // fan-triangulate; each tri inherits the group id
                }
            }
            pieceMap = pieces.ToArray();
            return m;
        }

        // Document save (SAVE-OPEN.md): WELD coincident verts by position so a seam becomes a SHARED edge
        // between two groups, then write the partition as `g piece_<id>` face groups. Welded + pieceMap is
        // enough to reconstruct every crease on Open (interior edges between differing groups) — no seam table.
        // pieceMap is read per ORIGINAL face so the partition stays index-aligned; an unwelded authoring mesh
        // (FBX solid) is joined here, and the develop unweld regenerates from the Pattern. (Distinct from the
        // plain WriteObj below, which does NOT weld — Export keeps the developed mesh's intentional seam gaps.)
        public static void WriteObj(PlanktonMesh P, int[] pieceMap, string path)
        {
            int nV = P.Vertices.Count;
            double scale = 0;
            for (int v = 0; v < nV; v++) { if (P.Vertices[v].IsUnused) continue; var p = P.Vertices[v]; double m = Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))); if (m > scale) scale = m; }
            double tol = scale > 0 ? scale * 1e-5 : 1e-5;

            var sb = new System.Text.StringBuilder();
            var posIndex = new Dictionary<string, int>();   // quantized position -> 1-based OBJ vertex index (the weld)
            var weld = new int[nV];                          // original vertex -> 1-based OBJ index (0 = unused)
            int next = 1;
            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused) { weld[v] = 0; continue; }
                var p = P.Vertices[v];
                long kx = (long)Math.Round(p.X / tol), ky = (long)Math.Round(p.Y / tol), kz = (long)Math.Round(p.Z / tol);
                string key = kx + "_" + ky + "_" + kz;
                if (!posIndex.TryGetValue(key, out int idx)) { idx = next++; posIndex[key] = idx; sb.AppendLine("v " + F(p.X) + " " + F(p.Y) + " " + F(p.Z)); }
                weld[v] = idx;
            }

            var byPiece = new SortedDictionary<int, List<int>>();   // piece id -> its used faces (ascending id)
            for (int f = 0; f < P.Faces.Count; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int pid = (pieceMap != null && f < pieceMap.Length) ? pieceMap[f] : 0;
                if (!byPiece.TryGetValue(pid, out var lst)) { lst = new List<int>(); byPiece[pid] = lst; }
                lst.Add(f);
            }
            foreach (var kv in byPiece)
            {
                sb.AppendLine("g piece_" + kv.Key);
                foreach (int f in kv.Value)
                {
                    int[] fv = P.Faces.GetFaceVertices(f);
                    if (fv == null || fv.Length < 3) continue;
                    var line = new System.Text.StringBuilder("f");
                    bool ok = true;
                    for (int k = 0; k < fv.Length; k++) { int mi = weld[fv[k]]; if (mi <= 0) { ok = false; break; } line.Append(' ').Append(mi); }
                    if (ok) sb.AppendLine(line.ToString());
                }
            }
            System.IO.File.WriteAllText(path, sb.ToString());
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
                if (P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                if (fv == null || fv.Length < 3) continue;
                var line = new System.Text.StringBuilder("f");   // n-gon faces preserved (e.g. Dev2PQ quad strips): f a b c d…
                bool ok = true;
                for (int k = 0; k < fv.Length; k++) { int mi = map[fv[k]]; if (mi < 0) { ok = false; break; } line.Append(' ').Append(mi); }
                if (ok) sb.AppendLine(line.ToString());
            }
            System.IO.File.WriteAllText(path, sb.ToString());
        }

        // ASCII STL (triangle facets; n-gon faces fan-triangulated). Universal for print / CAM / Rhino.
        public static void WriteStl(PlanktonMesh P, string path)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("solid mesh");
            for (int f = 0; f < P.Faces.Count; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                if (fv == null || fv.Length < 3) continue;
                for (int k = 1; k + 1 < fv.Length; k++)   // fan: (0, k, k+1)
                {
                    var a = P.Vertices[fv[0]]; var b = P.Vertices[fv[k]]; var c = P.Vertices[fv[k + 1]];
                    double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                    double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    double len = System.Math.Sqrt(nx * nx + ny * ny + nz * nz); if (len > 1e-20) { nx /= len; ny /= len; nz /= len; }
                    sb.AppendLine("facet normal " + F(nx) + " " + F(ny) + " " + F(nz));
                    sb.AppendLine("  outer loop");
                    sb.AppendLine("    vertex " + F(a.X) + " " + F(a.Y) + " " + F(a.Z));
                    sb.AppendLine("    vertex " + F(b.X) + " " + F(b.Y) + " " + F(b.Z));
                    sb.AppendLine("    vertex " + F(c.X) + " " + F(c.Y) + " " + F(c.Z));
                    sb.AppendLine("  endloop");
                    sb.AppendLine("endfacet");
                }
            }
            sb.AppendLine("endsolid mesh");
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
