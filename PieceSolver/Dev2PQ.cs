using System;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Plankton;

namespace PieceSolver
{
    // Dev2PQ: ruling-aligned PQ-strip remeshing of a DEVELOPABLE surface (Verhoeven/Vaxman 2021), driven as a
    // subprocess exactly like Bff.cs drives bff-command-line.exe. Feed a developed (developable) triangle mesh;
    // get back a polygon (PQ-strip) mesh. Runs per-panel, downstream of develop. See dev2pq/DEV2PQ-INTEGRATION.md.
    //
    // The ONLY gate is the timeout (caller-supplied): the mesher can crash (DCEL abort, exit 3 — dialog-suppressed
    // so it exits cleanly), hang (a few branched figures), or emit an empty mesh. All three are a clean `false`;
    // this never throws to the caller. No deviance metric — a result within the timeout IS the success (PQSuccess).
    static class Dev2PQ
    {
        // Built by dev2pq/build.bat; the GMP runtime dlls sit next to it, so no WorkingDirectory is needed.
        public const string ExePath = @"C:\Repo\xdkaplan\CreaseMachine\dev2pq\dev2pq.exe";

        // returns false on crash / hang(>timeoutMs) / empty output; never throws.
        public static bool TryDev2PQ(PlanktonMesh mesh, int timeoutMs, out PlanktonMesh result, out string log)
        {
            result = null;
            var sb = new StringBuilder();
            try
            {
                if (!File.Exists(ExePath)) { log = "dev2pq.exe not found at: " + ExePath + " (build dev2pq/build.bat)"; return false; }

                string inPath = Path.Combine(Path.GetTempPath(), "piecesolver_dev2pq_in.off");
                string outPath = Path.Combine(Path.GetTempPath(), "piecesolver_dev2pq_out.off");
                WriteOff(mesh, inPath);
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = "\"" + inPath + "\" \"" + outPath + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) { log = "dev2pq: failed to start process."; return false; }
                    // Drain stdout/stderr async so a chatty child can't deadlock the pipe while we wait.
                    var so = proc.StandardOutput.ReadToEndAsync();
                    var se = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                        log = "dev2pq timed out >" + timeoutMs + "ms (killed)";
                        return false;   // HANG guard — Bff.cs has no timeout, so do NOT mirror that line
                    }
                    try { if (!string.IsNullOrWhiteSpace(so.Result)) sb.AppendLine(so.Result.TrimEnd()); } catch { }
                    try { if (!string.IsNullOrWhiteSpace(se.Result)) sb.AppendLine(se.Result.TrimEnd()); } catch { }
                    if (proc.ExitCode != 0) { sb.AppendLine("dev2pq exited " + proc.ExitCode + " (crash/abort)"); log = sb.ToString(); return false; }
                }

                if (!File.Exists(outPath)) { sb.AppendLine("dev2pq produced no output."); log = sb.ToString(); return false; }

                result = ReadOffPolygon(outPath, out int nf);
                if (result == null || nf == 0) { sb.AppendLine("dev2pq output empty (0 faces)."); log = sb.ToString(); result = null; return false; }
                sb.AppendLine("dev2pq: " + mesh.Vertices.Count + "v -> " + result.Vertices.Count + "v / " + nf + " PQ faces");
                log = sb.ToString();
                return true;
            }
            catch (Exception ex) { sb.AppendLine("dev2pq failed: " + ex.Message); log = sb.ToString(); result = null; return false; }
        }

        // Triangle mesh -> ASCII OFF. Vertex indices preserved (unused verts written in place keep alignment).
        static void WriteOff(PlanktonMesh m, string path)
        {
            int nV = m.Vertices.Count;
            var faces = new System.Collections.Generic.List<int[]>();
            for (int f = 0; f < m.Faces.Count; f++)
            {
                if (m.Faces[f].IsUnused) continue;
                var fv = m.Faces.GetFaceVertices(f);
                if (fv != null && fv.Length >= 3) faces.Add(fv);
            }
            var sb = new StringBuilder();
            sb.Append("OFF\n").Append(nV).Append(' ').Append(faces.Count).Append(" 0\n");
            for (int v = 0; v < nV; v++)
            {
                var p = m.Vertices[v];
                sb.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(p.Z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            foreach (var fv in faces)
            {
                sb.Append(fv.Length);
                for (int k = 0; k < fv.Length; k++) sb.Append(' ').Append(fv[k]);
                sb.Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
        }

        // Polygon OFF -> PlanktonMesh (n-gon faces). nf = face count read (0 = empty).
        static PlanktonMesh ReadOffPolygon(string path, out int nf)
        {
            nf = 0;
            var toks = new System.Collections.Generic.List<string>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var ln = raw.Trim();
                if (ln.Length == 0 || ln.StartsWith("#")) continue;
                if (ln.Equals("OFF", StringComparison.OrdinalIgnoreCase)) continue;   // header (may be on its own line)
                if (ln.StartsWith("OFF", StringComparison.OrdinalIgnoreCase)) ln = ln.Substring(3).Trim();   // "OFF nv nf ne" on one line
                if (ln.Length == 0) continue;
                toks.AddRange(ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            }
            int idx = 0;
            int nv = NextInt(toks, ref idx); int faceCount = NextInt(toks, ref idx); NextInt(toks, ref idx);  // nv nf ne
            if (nv <= 0) return null;
            var m = new PlanktonMesh();
            for (int v = 0; v < nv; v++)
            {
                double x = NextDouble(toks, ref idx), y = NextDouble(toks, ref idx), z = NextDouble(toks, ref idx);
                m.Vertices.Add(x, y, z);
            }
            for (int f = 0; f < faceCount; f++)
            {
                int deg = NextInt(toks, ref idx);
                if (deg < 3) { for (int k = 0; k < deg; k++) NextInt(toks, ref idx); continue; }
                var face = new int[deg];
                bool ok = true;
                for (int k = 0; k < deg; k++) { int vi = NextInt(toks, ref idx); face[k] = vi; if (vi < 0 || vi >= nv) ok = false; }
                if (ok) { try { m.Faces.AddFace(face); nf++; } catch { } }
            }
            return m;
        }

        static int NextInt(System.Collections.Generic.List<string> t, ref int i)
            => (i < t.Count) ? (int)double.Parse(t[i++], CultureInfo.InvariantCulture) : 0;
        static double NextDouble(System.Collections.Generic.List<string> t, ref int i)
            => (i < t.Count) ? double.Parse(t[i++], CultureInfo.InvariantCulture) : 0.0;
    }
}
