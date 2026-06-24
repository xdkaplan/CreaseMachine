using System;
using System.Globalization;

namespace PieceSolver
{
    enum CmdKind { Load, Subdivide, Revert, Matcap, Solve }

    // Full parameter snapshot for a Solve (bake) command. The studio's Solve develops the mesh to the
    // selected Accuracy (target in-plane strain %), optionally subdividing + re-developing SubdivLevel
    // times, using the IsometricLM patch-solver weights. Captured at record time so a replayed Solve
    // reproduces the bake deterministically regardless of the current slider state. A plain value bag.
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
    // deterministic to replay. A Solve carries a full BakeParams snapshot, so replay reproduces the exact
    // step regardless of the current slider state. Serialized as one text line in a grammar that is a
    // SUPERSET of the CLI's script grammar (load / subdivide / reset / solve k=v), so the same .journal
    // can drive the headless CLI too. '#' lines and blanks are comments.
    sealed class StudioCommand
    {
        public CmdKind Kind;
        public int N;            // Matcap: index
        public string Path;      // Load: mesh path
        public BakeParams B;     // Solve: bake parameter snapshot

        public static StudioCommand Load(string path) => new StudioCommand { Kind = CmdKind.Load, Path = path };
        public static StudioCommand Subdiv() => new StudioCommand { Kind = CmdKind.Subdivide };
        public static StudioCommand Revert() => new StudioCommand { Kind = CmdKind.Revert };   // re-init from the input mesh (was "Reset")
        public static StudioCommand Matcap(int i) => new StudioCommand { Kind = CmdKind.Matcap, N = i };
        public static StudioCommand Solve(BakeParams b) => new StudioCommand { Kind = CmdKind.Solve, B = b };

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public string Serialize()
        {
            switch (Kind)
            {
                case CmdKind.Load: return "load " + Path;
                // The studio's main develop. Carries the full bake snapshot; the CLI maps it to its
                // bake-to-accuracy + subdivide equivalent (run + subdivide), ignoring the LM-only weights.
                case CmdKind.Solve: return string.Format(CultureInfo.InvariantCulture,
                    "solve acc={0} subdiv={1} iso={2} fair={3} anchor={4} scale={5} bend={6} difffair={7} benddiff={8} fixedges={9} seamratio={10}",
                    F(B.TargetStrainPct), B.SubdivLevel, F(B.Iso), F(B.Fair), F(B.Anchor), F(B.Scale), F(B.Bend),
                    B.DiffFair ? 1 : 0, B.BendDiff ? 1 : 0, B.FixEdges ? 1 : 0, B.SeamRatio);
                case CmdKind.Subdivide: return "subdivide";
                case CmdKind.Revert: return "revert";
                case CmdKind.Matcap: return "matcap " + N;
            }
            return "";
        }

        // Parse one journal line. Returns null for comments / blanks / unknown verbs.
        public static StudioCommand Parse(string line)
        {
            if (line == null) return null;
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') return null;                  // full-line comment / blank
            int cm = line.IndexOf(" #"); if (cm >= 0) line = line.Substring(0, cm).TrimEnd();   // inline comment (space before '#'; paths with '#' survive)
            if (line.Length == 0) return null;
            var tok = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            switch (tok[0].ToLowerInvariant())
            {
                case "load": return Load(line.Substring(line.IndexOf(' ') + 1).Trim());
                case "subdivide":
                case "subd": return Subdiv();
                case "revert":
                case "reset": return Revert();   // "reset" kept as a legacy / CLI alias
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
            }
            return null;
        }

        static int ParseInt(string s) { int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out int v); return v; }
    }
}
