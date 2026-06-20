using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using Plankton;
using CreaseMachine;

namespace CreasePatchSolver
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
                    Arguments = "\"" + inPath + "\" \"" + outPath + "\" --writeOnlyUVs",
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

                flat = MeshIO.Load(outPath);
                sb.AppendLine("BFF flattened " + flat.Vertices.Count + " verts.");
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
    }
}
