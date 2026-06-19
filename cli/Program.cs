using System;
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
//   adaptiveDetMix(bool)  adetmixpow(num)   <- experimental sep-adaptive DetMix (off by default)
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
    // EXPERIMENTAL: adaptive DetMix. When on, the lambda_min/det blend is raised toward 1 at
    // near-degenerate vertices (sep -> 0) via a_deg = (1 - sep)^pow, leaving real creases (sep -> 1)
    // at the detMix floor. Off by default -> byte-identical to the shipping flow.
    static bool adaptiveDetMix = false;
    static double adaptiveDetMixPow = 2.0;

    static int Main(string[] args)
    {
        Console.WriteLine("CreaseMachine headless REPL.  'help' for commands, 'quit' to exit.");
        // Optional: `crease <script.txt>` runs a command file then drops to interactive.
        if (args.Length > 0 && System.IO.File.Exists(args[0]))
            foreach (var ln in System.IO.File.ReadAllLines(args[0])) { Console.WriteLine("> " + ln); if (!Dispatch(ln)) return 0; }

        // Only draw the "> " prompt for a human at a terminal; when stdin is piped (e.g. the GUI
        // driving us as a subprocess) the prompts would just clutter the captured output.
        bool interactive = !Console.IsInputRedirected;
        string line;
        if (interactive) Console.Write("> ");
        while ((line = Console.ReadLine()) != null)
        {
            if (!Dispatch(line)) break;
            if (interactive) Console.Write("> ");
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
        P = MeshIO.Load(path);
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

        double startE = FlowMetrics.DevEnergy(P, maxCov, sharpness);
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

        var m = FlowMetrics.Compute(P, band, maxCov, sharpness);
        Console.WriteLine("  ran " + N + " (total " + totalIters + ")" +
            "  sumE " + Fmt(startE) + "->" + Fmt(m.SumE) +
            "  maxGrad " + lastMaxGrad.ToString("0.00e+0", CultureInfo.InvariantCulture) +
            "  panels " + m.Panels +
            "  crazeRMS " + m.CrazeRmsDeg.ToString("0.0") + "deg" +
            "  maxDih " + m.MaxDihDeg.ToString("0.0") + "deg" +
            "  rough " + m.DihRoughDeg.ToString("0.0") + "deg" +
            "   [" + R.Echo() + (maxCov ? " maxCov" : "") + " momFix " + momFix +
            (adaptiveDetMix ? " aDetMix^" + adaptiveDetMixPow.ToString("0.##", CultureInfo.InvariantCulture) : "") + "]" +
            "   " + (sw.Elapsed.TotalSeconds).ToString("0.0") + "s");
    }

    static void CmdSubdivide()
    {
        if (P == null) { Console.WriteLine("  ! load a mesh first"); return; }
        P = MeshOps.UniformSubdivide(P);    // shared canonical 1->4 subdivision (src/MeshOps)
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
        if (path.ToLowerInvariant().EndsWith(".ply"))
        {
            // colour by pure developability (where it's NOT a flat sheet); sqrt-normalised [0,1]
            MeshIO.WritePly(P, path, FlowMetrics.EnergyColour01(P, maxCov, sharpness));
            Console.WriteLine("  wrote " + path + " (per-vertex energy colour)");
        }
        else { MeshIO.WriteObj(P, path); Console.WriteLine("  wrote " + path); }
    }

    static void PrintParams()
    {
        Console.WriteLine("  step=" + step + " mom=" + mom + " deCraze=" + deCraze + " band=" + band +
            " detMix=" + detMix + " deBranch=" + deBranch + " deConsolidate=" + deConsolidate +
            " sharpness=" + sharpness + " maxCov=" + maxCov + " momFix=" + momFix +
            " adaptiveDetMix=" + adaptiveDetMix + " adetmixpow=" + adaptiveDetMixPow);
    }

    static void PrintMetrics(string tag)
    {
        if (P == null) { Console.WriteLine("  ! load a mesh first"); return; }
        var m = FlowMetrics.Compute(P, band, maxCov, sharpness);
        Console.WriteLine("  " + tag + ": verts " + P.Vertices.Count + "  sumE " + Fmt(m.SumE) +
            "  panels " + m.Panels + "  crazeRMS " + m.CrazeRmsDeg.ToString("0.0") + "deg" +
            "  maxDih " + m.MaxDihDeg.ToString("0.0") + "deg  rough " + m.DihRoughDeg.ToString("0.0") + "deg" +
            "  (crease cutoff " + (band * 180.0 / Math.PI).ToString("0.0") + "deg)");
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
          adaptiveDetMix(bool) adetmixpow(num)
  example: run 500 deCraze=0.1>0.0 step=0.05   (ramp deCraze 0.1->0 over 500 iters)
  example: run 200 detMix=0.05 adaptiveDetMix=1 adetmixpow=2   (adaptive blend A/B)");
    }

    // ============================ flow ============================

    // One faithful Nesterov flow step (mirrors CreaseMachine.DoFlowStep at Iter=1): heal short/
    // sliver edges, hop to the look-ahead, evaluate CHA there, apply v with the trust-region cap,
    // heal folds. `frac` in [0,1] drives the param ramps. Returns this step's max |grad|.
    static double FlowStep(RunRamps R, double frac)
    {
        var p = new FlowParams
        {
            Step = R.step.At(frac), Momentum = R.mom.At(frac), deCraze = R.deCraze.At(frac),
            CrazeBand = R.band.At(frac), DetMix = R.detMix.At(frac), deBranch = R.deBranch.At(frac),
            deConsolidate = R.deConsolidate.At(frac), Sharpness = R.sharpness.At(frac),
            UseMaxCov = maxCov, MomFix = momFix,
            AdaptiveDetMix = adaptiveDetMix, AdaptiveDetMixPower = adaptiveDetMixPow,
        };
        // Drive the SHARED flow via a transient session wrapping the CLI's P/vel, then sync back.
        // Same order the CLI used inline (collapse short -> collapse sliver -> Nesterov -> fold heal),
        // now the identical implementation the GH component runs.
        var session = new FlowSession { Mesh = P, Vel = vel, BrushWeights = null };
        session.CollapseShort();
        session.CollapseSliver();
        double maxG = session.NesterovStep(p, out bool[] fold);
        session.HealFolds(fold);
        P = session.Mesh; vel = session.Vel;
        return maxG;
    }

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
            return "step " + E(step) + " mom " + E(mom) + " deCraze " + E(deCraze) +
                   " band " + E(band) + " sharp " + E(sharpness) +
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
        if (name == "adaptivedetmix" || name == "adaptdetmix" || name == "adetmix")
        { adaptiveDetMix = val == "1" || val.ToLowerInvariant() == "true"; return; }
        if (name == "adaptivedetmixpow" || name == "adaptivedetmixpower" || name == "adetmixpow")
        { if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double ap) && ap > 0) adaptiveDetMixPow = ap; return; }

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

}
