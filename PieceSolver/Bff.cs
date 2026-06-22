using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using Plankton;
using CreaseMachine;

namespace PieceSolver
{
    // Boundary First Flattening (Sawhney/Crane). Drives the standalone bff-command-line.exe as a
    // subprocess: write the live mesh to a temp OBJ, run BFF with --writeOnlyUVs, read back the
    // flattened OBJ. The BFF output OBJ already lies on the XY plane (v X Y 0.0) with the SAME vertex
    // count + ordering and SAME face count as the input, so flat[i] corresponds to mesh[i].
    static class Bff
    {
        // BFF loads its own DLLs from its own folder, so we run it with WorkingDirectory = its folder.
        public const string ExePath = @"C:\Repo\GeometryCollective\boundary-first-flattening\binaries\windows-v1.6\bff-command-line.exe";

        public static bool TryFlatten(PlanktonMesh mesh, out PlanktonMesh flat, out string log)
        {
            flat = null;
            var sb = new StringBuilder();
            try
            {
                if (!File.Exists(ExePath))
                {
                    log = "BFF executable not found at: " + ExePath;
                    return false;
                }

                string inPath = Path.Combine(Path.GetTempPath(), "patchsolver_bff_in.obj");
                string outPath = Path.Combine(Path.GetTempPath(), "patchsolver_bff_out.obj");

                MeshIO.WriteObj(mesh, inPath);
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = "\"" + inPath + "\" \"" + outPath + "\"",   // default output keeps vertex indexing (UVs as vt)
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(ExePath)
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) { log = "BFF: failed to start process."; return false; }
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine(stdout.TrimEnd());
                    if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine(stderr.TrimEnd());

                    if (proc.ExitCode != 0)
                    {
                        sb.AppendLine("BFF exited with code " + proc.ExitCode + ".");
                        log = sb.ToString();
                        return false;
                    }
                }

                if (!File.Exists(outPath))
                {
                    sb.AppendLine("BFF produced no output file at: " + outPath);
                    log = sb.ToString();
                    return false;
                }

                flat = BuildAlignedFlat(mesh, outPath);
                if (flat == null) { sb.AppendLine("BFF: could not align the flat map to the input vertices."); log = sb.ToString(); return false; }
                sb.AppendLine("BFF flattened " + flat.Vertices.Count + " verts (index-aligned to M).");
                log = sb.ToString();
                return true;
            }
            catch (Exception ex)
            {
                sb.AppendLine("BFF failed: " + ex.Message);
                log = sb.ToString();
                return false;
            }
        }

        // BFF's DEFAULT output preserves the input vertex indexing and writes the flattening as `vt`
        // coords; --writeOnlyUVs reindexes into a packed UV layout, which SCRAMBLES the M<->M'
        // correspondence (M'[i] becomes a different vertex). Here we build M' with the SAME connectivity
        // as the input mesh, each vertex placed at its `vt` (on z=0), so M'[i] == mesh[i] exactly - the
        // index alignment the isometric solver depends on. (Assumes disk topology: one vt per vertex.)
        static PlanktonMesh BuildAlignedFlat(PlanktonMesh mesh, string objPath)
        {
            int nV = mesh.Vertices.Count;
            var vtU = new System.Collections.Generic.List<double>();
            var vtV = new System.Collections.Generic.List<double>();
            var vtForVertex = new int[nV];
            for (int i = 0; i < nV; i++) vtForVertex[i] = -1;

            foreach (var raw in File.ReadAllLines(objPath))
            {
                var ln = raw.TrimStart();
                if (ln.StartsWith("vt "))
                {
                    var p = ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3) { vtU.Add(ParseD(p[1])); vtV.Add(ParseD(p[2])); }
                }
                else if (ln.StartsWith("f "))
                {
                    var p = ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    for (int k = 1; k < p.Length; k++)
                    {
                        var sl = p[k].Split('/');
                        if (sl.Length < 2 || sl[0].Length == 0 || sl[1].Length == 0) continue;
                        int vi = int.Parse(sl[0], System.Globalization.CultureInfo.InvariantCulture) - 1;   // input v-index (= mesh index)
                        int ti = int.Parse(sl[1], System.Globalization.CultureInfo.InvariantCulture) - 1;   // its vt index
                        if (vi >= 0 && vi < nV) vtForVertex[vi] = ti;
                    }
                }
            }

            var Mp = new PlanktonMesh();
            for (int i = 0; i < nV; i++)
            {
                int ti = vtForVertex[i];
                if (ti >= 0 && ti < vtU.Count) Mp.Vertices.Add(vtU[ti], vtV[ti], 0.0);
                else Mp.Vertices.Add(0.0, 0.0, 0.0);   // unmapped (shouldn't happen for a disk mesh)
            }
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                if (mesh.Faces[f].IsUnused) continue;
                int[] fv = mesh.Faces.GetFaceVertices(f);
                if (fv.Length == 3) Mp.Faces.AddFace(fv);
            }
            return Mp;
        }

        static double ParseD(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }
}
