using System;
using System.Collections.Generic;
using System.Globalization;
using Plankton;
using CreaseMachine;

// CreaseMachine headless REPL. Rhino-less: compiles the same engine the .gha uses
// (Vec3 / DevelopabilityEnergy / MeshOps) and mirrors the GH component's flow step,
// so results match the plug-in. Stateful and incremental - each `run` bakes the live
// mesh further; velocity (Nesterov momentum) persists across runs for continuity.
//
// Commands:
//   load <file.stl|.obj>            load + weld a mesh (resets state)
//   run <N> [param=v | param=a>b]   run N flow steps; ramps interpolate a->b over the run
//   subdivide                       one 1->4 midpoint subdivision (paper's refine step)
//   stats                           print metrics at the current state (no flow)
//   export <file.obj|.ply>          write mesh (.ply carries per-vertex energy as colour)
//   reset                           restore the originally-loaded mesh
//   zero-momentum  (zm)             clear Nesterov velocity (cold restart, keep positions)
//   params                          show the current sticky params
//   help / quit
//
// Params (sticky; unspecified ones keep their last value). Numeric ones accept `a>b` ramps:
//   step mom deCraze band detMix deBranch deConsolidate sharpness   maxCov(bool)  momFix(int)
// Defaults match the GH component: step=0.05 mom=0.9 deCraze=0 band=0.1 detMix=0 sharpness=4 momFix=4
static class Program
{
    // ---- session state (persists across runs) ----
    static PlanktonMesh P;            // live mesh, baked in place
    static PlanktonMesh loaded;       // backup for `reset`
    static Vec3[] vel;                // Nesterov velocity, persists across runs
    static long totalIters;

    // sticky params (GH defaults)
    static double step = 0.05, mom = 0.9, deCraze = 0.0, band = 0.1, detMix = 0.0,
                  deBranch = 0.0, deConsolidate = 0.0, sharpness = 4.0;
    static bool maxCov = false;
    static int momFix = 4;

    static int Main(string[] args)
    {
        Console.WriteLine("CreaseMachine headless REPL.  'help' for commands, 'quit' to exit.");
        // Optional: `crease <script.txt>` runs a command file then drops to interactive.
        if (args.Length > 0 && System.IO.File.Exists(args[0]))
            foreach (var ln in System.IO.File.ReadAllLines(args[0])) { Console.WriteLine("> " + ln); if (!Dispatch(ln)) return 0; }

        string line;
        Console.Write("> ");
        while ((line = Console.ReadLine()) != null)
        {
            if (!Dispatch(line)) break;
            Console.Write("> ");
        }
        return 0;
    }

    // returns false to quit
    static bool Dispatch(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith("#")) return true;
        var tok = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        string cmd = tok[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "load":      CmdLoad(tok); break;
                case "run":       CmdRun(tok); break;
                case "subdivide":
                case "subd":      CmdSubdivide(); break;
                case "stats":     PrintMetrics("stats"); break;
                case "export":    CmdExport(tok); break;
                case "reset":     CmdReset(); break;
                case "zero-momentum":
                case "zm":        CmdZeroMomentum(); break;
                case "params":    PrintParams(); break;
                case "help":      PrintHelp(); break;
                case "quit":
                case "exit":      return false;
                default:          Console.WriteLine("  ? unknown command '" + cmd + "' (try 'help')"); break;
            }
        }
        catch (Exception ex) { Console.WriteLine("  ! " + ex.Message); }
        return true;
    }

    // ============================ commands ============================

    static void CmdLoad(string[] tok)
    {
        if (tok.Length < 2) { Console.WriteLine("  usage: load <file.stl|.obj>"); return; }
        string path = JoinPath(tok);
        if (!System.IO.File.Exists(path)) { Console.WriteLine("  ! not found: " + path); return; }
        P = path.ToLowerInvariant().EndsWith(".obj") ? LoadObj(path) : LoadBinaryStl(path);
        loaded = new PlanktonMesh(P);
        vel = new Vec3[P.Vertices.Count];
        totalIters = 0;
        Console.WriteLine("  loaded " + System.IO.Path.GetFileName(path) + ": " +
            P.Vertices.Count + " verts, " + P.Faces.Count + " faces");
    }

    static void CmdRun(string[] tok)
    {
        if (P == null) { Console.WriteLine("  ! load a mesh first"); return; }
        if (tok.Length < 2 || !int.TryParse(tok[1], out int N) || N <= 0)
        { Console.WriteLine("  usage: run <N> [param=v | param=a>b ...]"); return; }

        // Parse overrides into per-param ramps for THIS run. Unspecified params are constant
        // at their sticky value. After the run the sticky value advances to each ramp's end.
        var R = new RunRamps(step, mom, deCraze, band, detMix, deBranch, deConsolidate, sharpness);
        for (int i = 2; i < tok.Length; i++) ApplyOverride(tok[i], R);

        double startE = DevEnergy();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double lastMaxGrad = double.NaN;

        for (int s = 0; s < N; s++)
        {
            double frac = (N <= 1) ? 0.0 : (double)s / (N - 1);
            lastMaxGrad = FlowStep(R, frac);
            totalIters++;
        }
        sw.Stop();

        // advance sticky params to the ramp endpoints
        step = R.step.b; mom = R.mom.b; deCraze = R.deCraze.b; band = R.band.b;
        detMix = R.detMix.b; deBranch = R.deBranch.b; deConsolidate = R.deConsolidate.b; sharpness = R.sharpness.b;

        var m = Metrics(band);
        Console.WriteLine("  ran " + N + " (total " + totalIters + ")" +
            "  ΣE " + Fmt(startE) + "→" + Fmt(m.sumE) +
            "  maxGrad " + lastMaxGrad.ToString("0.00e+0", CultureInfo.InvariantCulture) +
            "  panels " + m.panels +
            "  crazeRMS " + m.crazeRmsDeg.ToString("0.0") + "°" +
            "  maxDih " + m.maxDihDeg.ToString("0.0") + "°" +
            "   [" + R.Echo() + (maxCov ? " maxCov" : "") + " momFix " + momFix + "]" +
            "   " + (sw.Elapsed.TotalSeconds).ToString("0.0") + "s");
    }

    static void CmdSubdivide()
    {
        if (P == null) { Console.WriteLine("  ! load a mesh first"); return; }
        P = UniformSubdivide(P);
        vel = new Vec3[P.Vertices.Count];   // indices renumbered -> momentum reset (topology change)
        Console.WriteLine("  subdivided -> " + P.Vertices.Count + " verts, " + P.Faces.Count + " faces (momentum reset)");
    }

    static void CmdReset()
    {
        if (loaded == null) { Console.WriteLine("  ! nothing loaded"); return; }
        P = new PlanktonMesh(loaded);
        vel = new Vec3[P.Vertices.Count];
        totalIters = 0;
        Console.WriteLine("  reset to loaded mesh: " + P.Vertices.Count + " verts, " + P.Faces.Count + " faces");
    }

    static void CmdZeroMomentum()
    {
        if (P == null) { Console.WriteLine("  ! load a mesh first"); return; }
        vel = new Vec3[P.Vertices.Count];
        Console.WriteLine("  momentum cleared (positions kept)");
    }

    static void CmdExport(string[] tok)
    {
        if (P == null) { Console.WriteLine("  ! load a mesh first"); return; }
        if (tok.Length < 2) { Console.WriteLine("  usage: export <file.obj|.ply>"); return; }
        string path = JoinPath(tok);
        if (path.ToLowerInvariant().EndsWith(".ply")) { WritePly(path); Console.WriteLine("  wrote " + path + " (per-vertex energy colour)"); }
        else { WriteObj(path); Console.WriteLine("  wrote " + path); }
    }

    static void PrintParams()
    {
        Console.WriteLine("  step=" + step + " mom=" + mom + " deCraze=" + deCraze + " band=" + band +
            " detMix=" + detMix + " deBranch=" + deBranch + " deConsolidate=" + deConsolidate +
            " sharpness=" + sharpness + " maxCov=" + maxCov + " momFix=" + momFix);
    }

    static void PrintMetrics(string tag)
    {
        if (P == null) { Console.WriteLine("  ! load a mesh first"); return; }
        var m = Metrics(band);
        Console.WriteLine("  " + tag + ": verts " + P.Vertices.Count + "  ΣE " + Fmt(m.sumE) +
            "  panels " + m.panels + "  crazeRMS " + m.crazeRmsDeg.ToString("0.0") + "°" +
            "  maxDih " + m.maxDihDeg.ToString("0.0") + "°  (crease cutoff " + (band * 180.0 / Math.PI).ToString("0.0") + "°)");
    }

    static void PrintHelp()
    {
        Console.WriteLine(@"  load <f.stl|.obj>            load + weld a mesh (resets state)
  run <N> [p=v | p=a>b ...]    run N flow steps; ramps interpolate a->b over the run
  subdivide                    one 1->4 midpoint subdivision
  stats                        metrics at current state
  export <f.obj|.ply>          write mesh (.ply = per-vertex energy colour)
  reset                        restore the loaded mesh
  zero-momentum | zm           clear Nesterov velocity (keep positions)
  params                       show current sticky params
  quit
  params: step mom deCraze band detMix deBranch deConsolidate sharpness maxCov momFix
  example: run 500 deCraze=0.1>0.0 step=0.05   (ramp deCraze 0.1->0 over 500 iters)");
    }

    // ============================ flow ============================

    // One faithful Nesterov flow step (mirrors CreaseMachine.DoFlowStep at Iter=1): heal short/
    // sliver edges, hop to the look-ahead, evaluate CHA there, apply v with the trust-region cap,
    // heal folds. `frac` in [0,1] drives the param ramps. Returns this step's max |grad|.
    static double FlowStep(RunRamps R, double frac)
    {
        double dStep = R.step.At(frac), dMom = R.mom.At(frac), dCraze = R.deCraze.At(frac),
               dBand = R.band.At(frac), dDet = R.detMix.At(frac), dBranch = R.deBranch.At(frac),
               dCons = R.deConsolidate.At(frac), dSharp = R.sharpness.At(frac);

        if (MeshOps.CollapseShortEdges(P, 0.2) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; }
        if (MeshOps.CollapseSliverEdges(P, 0.05) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; }

        int nV = P.Vertices.Count;
        if (vel == null || vel.Length != nV) vel = new Vec3[nV];
        double L = RepEdge(P);
        double t = dStep * L * L, cap = L;
        double beta = Math.Max(0.0, Math.Min(0.95, dMom));

        var bx = new double[nV]; var by = new double[nV]; var bz = new double[nV];
        for (int v = 0; v < nV; v++)
        {
            var pv = P.Vertices[v];
            bx[v] = pv.X; by[v] = pv.Y; bz[v] = pv.Z;
            if (beta > 0 && !pv.IsUnused && !P.Vertices.IsBoundary(v) && vel[v].IsValid)
                P.Vertices.SetVertex(v, bx[v] + beta * vel[v].X, by[v] + beta * vel[v].Y, bz[v] + beta * vel[v].Z);
        }

        DevelopabilityEnergy.CrazeBand = dBand;
        double[] energy; Vec3[] grad; bool[] fold; bool[] degen;
        DevelopabilityEnergy.ComputeHingeEnergyAndGrad(P, out energy, out grad, out fold, out degen,
            dBranch, dCons, maxCov, dSharp, dCraze, true, null, dDet);

        double maxG = 0.0;
        for (int v = 0; v < nV; v++)
        {
            if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) { P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
            Vec3 g = grad[v];
            if (!g.IsValid) { vel[v] = Vec3.Zero; P.Vertices.SetVertex(v, bx[v], by[v], bz[v]); continue; }
            if ((momFix == 2 || momFix == 4) && beta > 0 && degen[v]) vel[v] = Vec3.Zero;
            if ((momFix == 3 || momFix == 4) && beta > 0 && dDet < 0.5 && (g * vel[v]) > 0.0) vel[v] = Vec3.Zero;
            vel[v] = beta * vel[v] - t * g;
            double vl = vel[v].Length;
            if (vl > cap && vl > 1e-20) vel[v] = vel[v] * (cap / vl);
            P.Vertices.SetVertex(v, bx[v] + vel[v].X, by[v] + vel[v].Y, bz[v] + vel[v].Z);
            double gl = g.Length; if (gl > maxG) maxG = gl;
        }

        if (fold != null && MeshOps.CollapseFolds(P, fold) > 0) { P.Compact(); vel = new Vec3[P.Vertices.Count]; }
        return maxG;
    }

    // ============================ metrics ============================

    struct MeshMetrics { public double sumE, crazeRmsDeg, maxDihDeg; public int panels; }

    // Pure developability energy (covariance lambda_min, or maxCov lambda_max if set) - the
    // convergence signal. The deCraze / deBranch / deConsolidate regularizers are EXCLUDED so the
    // number reflects "how developable is the mesh", not the magnitude of the penalties the flow
    // adds. A clean covariance flow drives this toward 0; if deCraze is destabilizing the flow,
    // THIS number rises (independent of the L1 penalty's own bookkeeping).
    static double[] DevEnergyArray()
    {
        double[] e; bool[] f;
        DevelopabilityEnergy.ComputeHingeEnergy(P, out e, out f, 0.0, 0.0, maxCov, sharpness, 0.0);
        return e;
    }
    static double DevEnergy() { var e = DevEnergyArray(); double s = 0; for (int i = 0; i < e.Length; i++) s += e[i]; return s; }

    static MeshMetrics Metrics(double bandRad)
    {
        var m = new MeshMetrics();
        m.sumE = DevEnergy();

        double tau = bandRad;   // crease cutoff: below = intra-panel (flat), above = a crease
        int nF = P.Faces.Count;
        int[] par = new int[nF];
        for (int f = 0; f < nF; f++) par[f] = IsTri(f) ? f : -1;

        double sumSq = 0; int nIntra = 0; double maxDih = 0;
        for (int h = 0; h < P.Halfedges.Count; h += 2)
        {
            if (P.Halfedges[h].IsUnused) continue;
            int fA = P.Halfedges[h].AdjacentFace, fB = P.Halfedges[h + 1].AdjacentFace;
            if (fA < 0 || fB < 0 || par[fA] < 0 || par[fB] < 0) continue;
            double dih = Dihedral(fA, fB);
            if (dih > maxDih) maxDih = dih;
            if (dih < tau) { Union(par, fA, fB); sumSq += dih * dih; nIntra++; }
        }
        int panels = 0;
        for (int f = 0; f < nF; f++) if (par[f] >= 0 && Find(par, f) == f) panels++;

        m.panels = panels;
        m.crazeRmsDeg = (nIntra > 0 ? Math.Sqrt(sumSq / nIntra) : 0.0) * 180.0 / Math.PI;
        m.maxDihDeg = maxDih * 180.0 / Math.PI;
        return m;
    }

    static bool IsTri(int f) { return !P.Faces[f].IsUnused && P.Faces.GetHalfedges(f).Length == 3; }

    static double Dihedral(int fA, int fB)
    {
        Vec3 a = FaceNormal(fA), b = FaceNormal(fB);
        double c = a * b; if (c > 1) c = 1; else if (c < -1) c = -1;
        return Math.Acos(c);
    }

    static Vec3 FaceNormal(int f)
    {
        int[] fv = P.Faces.GetFaceVertices(f);
        Vec3 p0 = V(fv[0]), p1 = V(fv[1]), p2 = V(fv[2]);
        Vec3 cr = Vec3.Cross(p1 - p0, p2 - p0);
        double L = cr.Length;
        return L > 1e-30 ? cr * (1.0 / L) : Vec3.Zero;
    }

    static int Find(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }
    static void Union(int[] p, int a, int b) { int ra = Find(p, a), rb = Find(p, b); if (ra != rb) p[ra] = rb; }

    // ============================ param parsing ============================

    struct Ramp { public double a, b; public Ramp(double s, double e) { a = s; b = e; } public double At(double f) { return a + (b - a) * f; } }

    class RunRamps
    {
        public Ramp step, mom, deCraze, band, detMix, deBranch, deConsolidate, sharpness;
        public RunRamps(double st, double mo, double cr, double ba, double dm, double br, double co, double sh)
        { step = new Ramp(st, st); mom = new Ramp(mo, mo); deCraze = new Ramp(cr, cr); band = new Ramp(ba, ba);
          detMix = new Ramp(dm, dm); deBranch = new Ramp(br, br); deConsolidate = new Ramp(co, co); sharpness = new Ramp(sh, sh); }
        public string Echo()
        {
            return "step " + E(step) + " mom " + E(mom) + " deCraze " + E(deCraze) + " band " + E(band) +
                   (detMix.a != 0 || detMix.b != 0 ? " detMix " + E(detMix) : "") +
                   (deBranch.a != 0 || deBranch.b != 0 ? " deBranch " + E(deBranch) : "") +
                   (deConsolidate.a != 0 || deConsolidate.b != 0 ? " deConsolidate " + E(deConsolidate) : "");
        }
        static string E(Ramp r) { return r.a == r.b ? r.a.ToString("0.###", CultureInfo.InvariantCulture)
            : r.a.ToString("0.###", CultureInfo.InvariantCulture) + ">" + r.b.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    static void ApplyOverride(string kv, RunRamps R)
    {
        int eq = kv.IndexOf('=');
        if (eq <= 0) { Console.WriteLine("  ! ignoring '" + kv + "' (want name=value)"); return; }
        string name = kv.Substring(0, eq).ToLowerInvariant();
        string val = kv.Substring(eq + 1);

        // bool / int params (not rampable)
        if (name == "maxcov") { maxCov = val == "1" || val.ToLowerInvariant() == "true"; return; }
        if (name == "momfix") { int.TryParse(val, out momFix); momFix = Math.Max(1, Math.Min(4, momFix)); return; }

        Ramp r;
        int gt = val.IndexOf('>');
        if (gt >= 0) r = new Ramp(D(val.Substring(0, gt)), D(val.Substring(gt + 1)));
        else { double v = D(val); r = new Ramp(v, v); }

        switch (name)
        {
            case "step": R.step = r; break;
            case "mom": case "momentum": R.mom = r; break;
            case "decraze": case "craze": R.deCraze = r; break;
            case "band": case "crazeband": R.band = r; break;
            case "detmix": R.detMix = r; break;
            case "debranch": case "branch": R.deBranch = r; break;
            case "deconsolidate": case "consolidate": R.deConsolidate = r; break;
            case "sharpness": case "sharp": R.sharpness = r; break;
            default: Console.WriteLine("  ! unknown param '" + name + "'"); break;
        }
    }

    static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
    static string Fmt(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }
    static string JoinPath(string[] tok) { return string.Join(" ", tok, 1, tok.Length - 1).Trim('"'); }

    // ============================ mesh helpers ============================

    static Vec3 V(int v) { var p = P.Vertices[v]; return new Vec3(p.X, p.Y, p.Z); }

    static double RepEdge(PlanktonMesh m)
    {
        for (int i = 0; i < m.Halfedges.Count; i += 2)
        {
            if (m.Halfedges[i].IsUnused) continue;
            var a = m.Vertices[m.Halfedges[i].StartVertex]; var b = m.Vertices[m.Halfedges[i + 1].StartVertex];
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            double L = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (L > 0) return L;
        }
        return 1.0;
    }

    // 1->4 midpoint subdivision (mirrors CreaseMachine.UniformSubdivide; geometry-preserving).
    static PlanktonMesh UniformSubdivide(PlanktonMesh Pin)
    {
        var S = new PlanktonMesh();
        int nV = Pin.Vertices.Count, nE = Pin.Halfedges.Count / 2, nF = Pin.Faces.Count;
        for (int v = 0; v < nV; v++) { var pv = Pin.Vertices[v]; S.Vertices.Add(pv.X, pv.Y, pv.Z); }
        int[] mid = new int[nE];
        for (int e = 0; e < nE; e++)
        {
            if (Pin.Halfedges[2 * e].IsUnused) { mid[e] = -1; continue; }
            var pa = Pin.Vertices[Pin.Halfedges[2 * e].StartVertex];
            var pb = Pin.Vertices[Pin.Halfedges[2 * e + 1].StartVertex];
            mid[e] = S.Vertices.Add(0.5f * (pa.X + pb.X), 0.5f * (pa.Y + pb.Y), 0.5f * (pa.Z + pb.Z));
        }
        for (int f = 0; f < nF; f++)
        {
            if (Pin.Faces[f].IsUnused) continue;
            int[] hes = Pin.Faces.GetHalfedges(f);
            if (hes.Length != 3) continue;
            int v0 = Pin.Halfedges[hes[0]].StartVertex, v1 = Pin.Halfedges[hes[1]].StartVertex, v2 = Pin.Halfedges[hes[2]].StartVertex;
            int m0 = mid[hes[0] / 2], m1 = mid[hes[1] / 2], m2 = mid[hes[2] / 2];
            if (m0 < 0 || m1 < 0 || m2 < 0) continue;
            S.Faces.AddFace(v0, m0, m2); S.Faces.AddFace(m0, v1, m1); S.Faces.AddFace(m2, m1, v2); S.Faces.AddFace(m0, m1, m2);
        }
        return S;
    }

    // ============================ I/O ============================

    static PlanktonMesh LoadBinaryStl(string path)
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

    static PlanktonMesh LoadObj(string path)
    {
        var m = new PlanktonMesh();
        var verts = new List<int>();
        foreach (var raw in System.IO.File.ReadAllLines(path))
        {
            var ln = raw.Trim();
            if (ln.StartsWith("v ")) { var p = ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries); m.Vertices.Add(D(p[1]), D(p[2]), D(p[3])); }
            else if (ln.StartsWith("f "))
            {
                var p = ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                var idx = new List<int>();
                for (int i = 1; i < p.Length; i++) { int slash = p[i].IndexOf('/'); string s = slash >= 0 ? p[i].Substring(0, slash) : p[i]; idx.Add(int.Parse(s) - 1); }
                for (int i = 2; i < idx.Count; i++) m.Faces.AddFace(idx[0], idx[i - 1], idx[i]);   // fan-triangulate
            }
        }
        return m;
    }

    static void WriteObj(string path)
    {
        var sb = new System.Text.StringBuilder();
        int[] map = new int[P.Vertices.Count]; int idx = 1;
        for (int v = 0; v < P.Vertices.Count; v++)
        {
            if (P.Vertices[v].IsUnused) { map[v] = -1; continue; }
            var p = P.Vertices[v];
            sb.AppendLine("v " + p.X.ToString(CultureInfo.InvariantCulture) + " " + p.Y.ToString(CultureInfo.InvariantCulture) + " " + p.Z.ToString(CultureInfo.InvariantCulture));
            map[v] = idx++;
        }
        for (int f = 0; f < P.Faces.Count; f++)
        {
            if (!IsTri(f)) continue;
            int[] fv = P.Faces.GetFaceVertices(f);
            sb.AppendLine("f " + map[fv[0]] + " " + map[fv[1]] + " " + map[fv[2]]);
        }
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    // ASCII PLY with per-vertex energy as RGB (blue=developable .. red=hot). Open in MeshLab/Blender.
    static void WritePly(string path)
    {
        double[] e = DevEnergyArray();   // colour by pure developability (where it's NOT a flat sheet)
        double eMax = 1e-12; for (int i = 0; i < e.Length; i++) if (e[i] > eMax) eMax = e[i];

        int[] map = new int[P.Vertices.Count]; int nUsed = 0;
        for (int v = 0; v < P.Vertices.Count; v++) map[v] = P.Vertices[v].IsUnused ? -1 : nUsed++;
        int nFaces = 0; for (int ff = 0; ff < P.Faces.Count; ff++) if (IsTri(ff)) nFaces++;

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
            double tcol = Math.Sqrt(Math.Max(0, e[v]) / eMax);   // sqrt so low energy is visible
            int r = (int)(255 * Math.Min(1, tcol * 2));
            int bl = (int)(255 * Math.Min(1, (1 - tcol) * 2));
            int g = (int)(255 * (1 - Math.Abs(tcol - 0.5) * 2));
            sb.AppendLine(p.X.ToString(CultureInfo.InvariantCulture) + " " + p.Y.ToString(CultureInfo.InvariantCulture) + " " + p.Z.ToString(CultureInfo.InvariantCulture) + " " + r + " " + g + " " + bl);
        }
        for (int ff = 0; ff < P.Faces.Count; ff++)
        {
            if (!IsTri(ff)) continue;
            int[] fv = P.Faces.GetFaceVertices(ff);
            sb.AppendLine("3 " + map[fv[0]] + " " + map[fv[1]] + " " + map[fv[2]]);
        }
        System.IO.File.WriteAllText(path, sb.ToString());
    }
}
