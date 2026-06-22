using System;
using System.Globalization;
using CreaseMachine;

namespace CreasePatchSolver
{
    enum CmdKind { Load, Run, Subdivide, Reset, Matcap, Solve }

    // Full parameter snapshot for a Solve (bake) command. The studio's Solve develops the mesh to the
    // selected Accuracy (target in-plane strain %), optionally subdividing + re-developing SubdivLevel
    // times, using the IsometricLM patch-solver weights. Captured at record time so a replayed Solve
    // reproduces the bake deterministically regardless of the current slider state. A plain value bag,
    // mirroring FlowParams' role for the Run command.
    struct BakeParams
    {
        public double TargetStrainPct;   // Accuracy: the allowable in-plane strain % the bake develops to
        public int SubdivLevel;          // how many 1->4 subdivide+re-develop rounds Solve performs
        public double Iso, Fair, Anchor, Scale, Bend;   // IsometricLM weights
        public bool DiffFair, BendDiff;  // differential-fairness / differential-bending modes
        public bool FixEdges;            // pin boundary loops onto a low-DOF B-spline (Dirichlet)
        public int SeamRatio;            // ~1 control point per SeamRatio boundary points (when FixEdges)

        public static BakeParams Defaults() => new BakeParams
        {
            TargetStrainPct = 0.2, SubdivLevel = 2,
            Iso = 10.0, Fair = 0.0, Anchor = 0.0, Scale = 10.0, Bend = 0.6,
            DiffFair = false, BendDiff = false, FixEdges = false, SeamRatio = 5,
        };
    }

    // One recorded studio action - a semantic INTENT, not raw input. Robust to UI changes and
    // deterministic to replay. A Run carries a full FlowParams snapshot and a Solve a full BakeParams
    // snapshot, so replay reproduces the exact step regardless of the current slider state. Serialized
    // as one text line in a grammar that is a SUPERSET of the CLI's script grammar (load / run N k=v /
    // subdivide / reset / solve k=v), so the same .journal can drive the headless CLI too. '#' lines
    // and blanks are comments.
    sealed class StudioCommand
    {
        public CmdKind Kind;
        public int N;            // Run: iteration count | Matcap: index
        public string Path;      // Load: mesh path
        public FlowParams P;     // Run: parameter snapshot
        public BakeParams B;     // Solve: bake parameter snapshot

        public static StudioCommand Load(string path) => new StudioCommand { Kind = CmdKind.Load, Path = path };
        public static StudioCommand Run(int n, FlowParams p) => new StudioCommand { Kind = CmdKind.Run, N = n, P = p };
        public static StudioCommand Subdiv() => new StudioCommand { Kind = CmdKind.Subdivide };
        public static StudioCommand Reset() => new StudioCommand { Kind = CmdKind.Reset };
        public static StudioCommand Matcap(int i) => new StudioCommand { Kind = CmdKind.Matcap, N = i };
        public static StudioCommand Solve(BakeParams b) => new StudioCommand { Kind = CmdKind.Solve, B = b };

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public string Serialize()
        {
            switch (Kind)
            {
                case CmdKind.Load: return "load " + Path;
                // Names match the CLI's run-param vocabulary (it lower-cases names and accepts all of
                // these, incl. maxcov/momfix), so a studio .journal is runnable by the headless CLI.
                case CmdKind.Run: return string.Format(CultureInfo.InvariantCulture,
                    "run {0} step={1} mom={2} decraze={3} band={4} sharp={5} detmix={6} debranch={7} deconsolidate={8} maxcov={9} momfix={10} adaptivedetmix={11} adetmixpow={12}",
                    N, F(P.Step), F(P.Momentum), F(P.deCraze), F(P.CrazeBand), F(P.Sharpness), F(P.DetMix),
                    F(P.deBranch), F(P.deConsolidate), P.UseMaxCov ? 1 : 0, P.MomFix,
                    P.AdaptiveDetMix ? 1 : 0, F(P.AdaptiveDetMixPower));
                // The studio's main develop. Carries the full bake snapshot; the CLI maps it to its
                // bake-to-accuracy + subdivide equivalent (run + subdivide), ignoring the LM-only weights.
                case CmdKind.Solve: return string.Format(CultureInfo.InvariantCulture,
                    "solve acc={0} subdiv={1} iso={2} fair={3} anchor={4} scale={5} bend={6} difffair={7} benddiff={8} fixedges={9} seamratio={10}",
                    F(B.TargetStrainPct), B.SubdivLevel, F(B.Iso), F(B.Fair), F(B.Anchor), F(B.Scale), F(B.Bend),
                    B.DiffFair ? 1 : 0, B.BendDiff ? 1 : 0, B.FixEdges ? 1 : 0, B.SeamRatio);
                case CmdKind.Subdivide: return "subdivide";
                case CmdKind.Reset: return "reset";
                case CmdKind.Matcap: return "matcap " + N;
            }
            return "";
        }

        // Parse one journal line. Returns null for comments / blanks / unknown verbs.
        public static StudioCommand Parse(string line)
        {
            if (line == null) return null;
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') return null;
            var tok = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            switch (tok[0].ToLowerInvariant())
            {
                case "load": return Load(line.Substring(line.IndexOf(' ') + 1).Trim());
                case "subdivide":
                case "subd": return Subdiv();
                case "reset": return Reset();
                case "matcap": return Matcap(tok.Length > 1 ? ParseInt(tok[1]) : 0);
                case "solve":
                    var b = BakeParams.Defaults();
                    for (int i = 1; i < tok.Length; i++)
                    {
                        int eq = tok[i].IndexOf('=');
                        if (eq <= 0) continue;
                        string k = tok[i].Substring(0, eq).ToLowerInvariant();
                        double.TryParse(tok[i].Substring(eq + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out double d);
                        switch (k)
                        {
                            case "acc": case "accuracy": case "strain": b.TargetStrainPct = d; break;
                            case "subdiv": case "subdivlevel": b.SubdivLevel = (int)d; break;
                            case "iso": b.Iso = d; break;
                            case "fair": b.Fair = d; break;
                            case "anchor": b.Anchor = d; break;
                            case "scale": b.Scale = d; break;
                            case "bend": b.Bend = d; break;
                            case "difffair": case "fairdiff": b.DiffFair = d != 0; break;
                            case "benddiff": b.BendDiff = d != 0; break;
                            case "fixedges": case "fixbsplineedges": b.FixEdges = d != 0; break;
                            case "seamratio": b.SeamRatio = (int)d; break;
                        }
                    }
                    return Solve(b);
                case "run":
                    int n = tok.Length > 1 ? ParseInt(tok[1]) : 0;
                    var p = Defaults();
                    for (int i = 2; i < tok.Length; i++)
                    {
                        int eq = tok[i].IndexOf('=');
                        if (eq <= 0) continue;
                        string k = tok[i].Substring(0, eq).ToLowerInvariant();
                        double.TryParse(tok[i].Substring(eq + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out double d);
                        switch (k)   // accept the CLI's aliases too -> a true superset of its grammar
                        {
                            case "step": p.Step = d; break;
                            case "mom": case "momentum": p.Momentum = d; break;
                            case "decraze": case "craze": p.deCraze = d; break;
                            case "band": case "crazeband": p.CrazeBand = d; break;
                            case "sharp": case "sharpness": p.Sharpness = d; break;
                            case "detmix": p.DetMix = d; break;
                            case "debranch": case "branch": p.deBranch = d; break;
                            case "deconsolidate": case "deconsol": case "consolidate": p.deConsolidate = d; break;
                            case "maxcov": p.UseMaxCov = d != 0; break;
                            case "momfix": p.MomFix = (int)d; break;
                            case "adaptivedetmix": case "adaptdetmix": case "adetmix": p.AdaptiveDetMix = d != 0; break;
                            case "adaptivedetmixpow": case "adaptivedetmixpower": case "adetmixpow": p.AdaptiveDetMixPower = d; break;
                        }
                    }
                    return Run(n, p);
            }
            return null;
        }

        static int ParseInt(string s) { int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out int v); return v; }
        static FlowParams Defaults() => new FlowParams { Step = 0.05, Momentum = 0.9, CrazeBand = 0.1, Sharpness = 4.0, MomFix = 4 };
    }
}
