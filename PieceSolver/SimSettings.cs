using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreaseMachine;

namespace PieceSolver
{
    // View-model for the simulation settings (right toolbar). Bindable (INotifyPropertyChanged) so
    // panel controls bind two-way and the flow reads a snapshot each run - the MVVM seam that keeps
    // the growing UI out of code-behind spaghetti. ToFlowParams() hands the engine a value snapshot.
    sealed class SimSettings : INotifyPropertyChanged
    {
        int _iterPerRun = 10, _momFix = 4;
        // PieceSolver mesh picker: 1..30 maps to the NURBS test surfaces 0.stl..29.stl. Changing it
        // resets the app to that mesh (load + clear flat + re-arm BFF) from the MainWindow handler.
        int _meshIndex = 1;
        // CrazeBand is shown to the user in DEGREES (more intuitive than radians); the engine wants
        // radians, so ToFlowParams() converts. 5.7 deg == the engine's documented 0.1 rad default.
        double _step = 0.01, _momentum = 0.9, _deCraze = 0.0, _crazeBandDeg = 5.7, _sharpness = 4.0, _detMix = 0.05;
        // Experimental: adaptive DetMix - raise the lambda_min<->det blend toward 1 at near-degenerate
        // ("twisty") vertices via a = (1-sep)^pow, leaving real creases (sep->1) at the DetMix floor.
        bool _adaptiveDetMix = false; double _adaptiveDetMixPow = 2.0;
        // Isometric patch-solver knobs (IsometricLM.Solve, Levenberg-Marquardt + post-step smoother).
        // No step/learning rate: LM computes its own step. Iso = developability gain (drive relErr down;
        // LM is stable at any scale). Scale = global scale-pin weight: the ONLY anti-collapse needed -
        // it pins total size to the original so M can't shrink flat, and it REPLACES the dense Anchor
        // (with Anchor=0 + Scale on, LM develops to relErr ~0 with no shrink, headless-verified). Fair =
        // legacy uniform-Laplacian (shrinks; unneeded under LM - prefer the non-shrinking Smoother below).
        // Anchor = optional dense point-to-point faithfulness pin to the original; 0 = off (default).
        double _isoWeight = 10.0, _fairWeight = 0.0, _anchorWeight = 0.0, _scaleWeight = 10.0, _bendWeight = 0.6;   // default = preset D
        // Post-LM smoother (IsometricSmoothers): 0 None, 1 Tangential, 2 Taubin. Non-shrinking strain
        // distributors / de-bucklers applied as a filter after each LM step (boundary vertices pinned).
        // None suffices for the near-developable test patches; the smoothers are insurance for buckling-
        // prone (high-curvature) inputs. Strength ~0.5.
        int _smoothKind = 0; double _smoothStrength = 0.5;

        public int IterPerRun { get => _iterPerRun; set { if (Set(ref _iterPerRun, value)) OnChanged(nameof(IterLabel)); } }
        public double Step { get => _step; set => Set(ref _step, value); }
        public double Momentum { get => _momentum; set => Set(ref _momentum, value); }
        public double DeCraze { get => _deCraze; set => Set(ref _deCraze, value); }
        public double CrazeBandDeg { get => _crazeBandDeg; set => Set(ref _crazeBandDeg, value); }
        public double Sharpness { get => _sharpness; set => Set(ref _sharpness, value); }
        public double DetMix { get => _detMix; set => Set(ref _detMix, value); }
        public bool AdaptiveDetMix { get => _adaptiveDetMix; set => Set(ref _adaptiveDetMix, value); }
        public double AdaptiveDetMixPow { get => _adaptiveDetMixPow; set => Set(ref _adaptiveDetMixPow, value); }
        public int MomFix { get => _momFix; set => Set(ref _momFix, value); }
        public int MeshIndex { get => _meshIndex; set => Set(ref _meshIndex, value); }
        // Test-asset set the Mesh index picks from (the "Source" dropdown):
        //   0 = Solids   — 6-sided FBX (unwelded -> per-face components)
        //   1 = Surfaces — NURBS single open-patch STLs (the default)
        //   2 = Meshes   — arbitrary triangle meshes (currently just the bunny)
        int _assetSet = 1;
        public int AssetSet { get => _assetSet; set => Set(ref _assetSet, value); }

        // Isometric patch-solver knobs (drive IsometricLM.Solve + IsometricSmoothers via IsometricStep()).
        public double IsoWeight { get => _isoWeight; set => Set(ref _isoWeight, value); }
        public double FairWeight { get => _fairWeight; set => Set(ref _fairWeight, value); }
        public double AnchorWeight { get => _anchorWeight; set => Set(ref _anchorWeight, value); }
        public double ScaleWeight { get => _scaleWeight; set => Set(ref _scaleWeight, value); }
        // 2nd-order bending (differential bi-Laplacian) - the anti-wrinkle term, the triangle analog of the
        // paper's 2nd-difference fairness. Smooths buckles inside the LM without shrinking. Preset C uses 0.6.
        public double BendWeight { get => _bendWeight; set => Set(ref _bendWeight, value); }
        public int SmoothKind { get => _smoothKind; set => Set(ref _smoothKind, value); }
        public double SmoothStrength { get => _smoothStrength; set => Set(ref _smoothStrength, value); }

        // Fair mode: when true the fairness term is DIFFERENTIAL (penalizes change in L(M) from L(M0)) -
        // non-shrinking, preserves the input's local detail. False = plain Laplacian (shrinks). Set by the
        // C preset; the developability A/B/C investigation found differential fairness the smoothness win.
        bool _diffFair;
        public bool DiffFair { get => _diffFair; set => Set(ref _diffFair, value); }

        // Bend mode: true = DIFFERENTIAL (preserve original curvature, low-drift; presets B/C). false = PLAIN
        // (drive toward smoothest -> a single entirely-developable patch; the Paper preset D, free deformation).
        bool _bendDiff = false;   // default = preset D (plain bending -> single entirely-developable patch)
        public bool BendDiff { get => _bendDiff; set => Set(ref _bendDiff, value); }

        // ---- Fabrication GO knobs (top-level UI; raw solver weights live under Advanced) ----
        // Accuracy = how developable the result must be, by MATERIAL stiffness (allowable in-plane strain).
        // 0 Fabric (stretchy) .. 3 Plate Metal (near-perfectly developable). Placeholder strain targets for now.
        static readonly string[] AccMaterials = { "Fabric", "Paper", "Sheet Metal", "Plate Metal" };
        static readonly double[] AccStrainPct = { 1.0, 0.2, 0.05, 0.01 };   // allowable strain % per material (rigidity)
        int _accuracyLevel = 2;
        public int AccuracyLevel { get => _accuracyLevel; set { if (Set(ref _accuracyLevel, value)) { OnChanged(nameof(AccuracyLabel)); OnChanged(nameof(AccuracyStrainPct)); NotifyGo(); } } }
        public string AccuracyLabel => AccMaterials[System.Math.Clamp(_accuracyLevel, 0, 3)];
        public double AccuracyStrainPct => AccStrainPct[System.Math.Clamp(_accuracyLevel, 0, 3)];

        // Live developability readout: in-plane strain (relErr %) pushed in by MainWindow after each solve,
        // compared against the selected material's allowable strain -> a GO acceptance gate (the thing that
        // makes Accuracy actually mean something). GREEN GO when strain <= the material's target, else amber.
        double _strainPct = double.NaN;
        public double StrainPct { get => _strainPct; set { if (Set(ref _strainPct, value)) NotifyGo(); } }
        public string StrainText => double.IsNaN(_strainPct) ? "—" : _strainPct.ToString("0.####") + "%";
        public bool IsGo => !double.IsNaN(_strainPct) && _strainPct <= AccuracyStrainPct;
        public string GoText => double.IsNaN(_strainPct) ? "" : (IsGo ? "GO ✓" : "developing");
        public System.Windows.Media.Brush GoBrush => double.IsNaN(_strainPct) ? System.Windows.Media.Brushes.Gray : (IsGo ? GoGreen : GoAmber);
        static readonly System.Windows.Media.Brush GoGreen = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5F, 0xCF, 0x6A));
        static readonly System.Windows.Media.Brush GoAmber = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        void NotifyGo() { OnChanged(nameof(StrainText)); OnChanged(nameof(IsGo)); OnChanged(nameof(GoText)); OnChanged(nameof(GoBrush)); }

        // Tolerance = absolute fit-up gap (mm), discrete choices. For later use (panel sizing / acceptance).
        static readonly double[] TolChoices = { 0.01, 0.1, 0.2, 0.3, 0.5, 0.8, 1, 2, 3, 5, 8 };
        int _toleranceIndex = 4;   // 0.5 mm
        public int ToleranceIndex { get => _toleranceIndex; set { if (Set(ref _toleranceIndex, value)) OnChanged(nameof(ToleranceMm)); } }
        public double ToleranceMm => TolChoices[System.Math.Clamp(_toleranceIndex, 0, TolChoices.Length - 1)];

        // Subdivision level: how many 1->4 subdivide rounds Solve performs (develop -> subdivide -> develop ...).
        int _subdivLevel = 2;
        public int SubdivLevel { get => _subdivLevel; set => Set(ref _subdivLevel, value); }

        // Apply a developability preset (see project_patchsolver_abc memory):
        //   A = current: scale-pin only (max developability, more drift, wrinkly).
        //   B = low-drift: + small point-to-point anchor.
        //   C = low-drift + smooth: + 2nd-order DIFFERENTIAL bending (anti-wrinkle, stays faithful).
        //   D = PAPER (Jiang/Pottmann): no anchor + PLAIN bending -> a single, entirely-developable smooth
        //       patch via free deformation (relErr ~0.04%); high drift by design. Matches the paper's intent.
        public void ApplyPreset(char which)
        {
            IsoWeight = 10; ScaleWeight = 10; SmoothKind = 0; FairWeight = 0.0; DiffFair = false;   // common to all
            switch (which)
            {
                case 'A': AnchorWeight = 0.0; BendWeight = 0.0; BendDiff = true; break;
                case 'B': AnchorWeight = 0.05; BendWeight = 0.0; BendDiff = true; break;
                case 'C': AnchorWeight = 0.05; BendWeight = 0.6; BendDiff = true; break;   // differential bend = faithful de-wrinkler
                case 'D': AnchorWeight = 0.0; BendWeight = 0.6; BendDiff = false; break;   // plain bend + free deform = single developable
            }
        }

        // The canonical "100%" deCraze weight — the top of the 0-100% scale used by the deCraze
        // slider (its Maximum maps to DeCrazeMax). Defined once here so the 0.04 isn't a magic number
        // duplicated across the panel and the Run param round-trip.
        public double DeCrazeMax => 0.04;

        // Shading facet (display): 0 = smooth (averaged normals), 1 = faceted (per-face normals). The
        // shader blends between them, so this is continuous and live. Default faceted.
        double _facet = 0.8;
        public double Facet { get => _facet; set => Set(ref _facet, value); }

        // Facet response curve: the shader blends by pow(Facet, FacetExp). 1 = linear; higher keeps
        // facets soft until Facet is high, then sharpens quickly.
        double _facetExp = 4.0;
        public double FacetExp { get => _facetExp; set => Set(ref _facetExp, value); }

        // Shine (display): blends the default shading between the neutral-lighting matcap (0 = matte) and
        // the environment matcap (1 = reflective sky/landscape). Ignored when UseMatcap is on.
        double _shine = 0.4;
        public double Shine { get => _shine; set => Set(ref _shine, value); }

        // Advanced: when on, the hand-picked matcap (the switcher) overrides the neutral+Shine shading.
        bool _useMatcap;
        public bool UseMatcap { get => _useMatcap; set => Set(ref _useMatcap, value); }

        // Ruling-line overlay: a curvature-driven LIC grain painted on M (the developable "grain"),
        // modulating the matcap. Off by default.
        bool _showRuling;
        public bool ShowRuling { get => _showRuling; set => Set(ref _showRuling, value); }

        // LIC grain tuning (applies to BOTH Ruling and Gradient modes). All three feed shader uniforms
        // pushed every frame, so they retune live with no shader recompile.
        double _licGrain = 10.0;    // grain fineness: noise tiles across the model (higher = finer hairs)
        int    _licLength = 30;     // streak length (high-confidence max): convolution taps each side (uLicTaps)
        double _licAlpha = 0.7;     // grain alpha / depth (high-confidence max, uLicStrength); ruling scales by confidence
        // Ruling curvature remap (levels): kappa_max (= 1/radius-of-max-curvature) is bunched, so a linear
        // map under/over-reads. smoothstep(min,max,kappa_max) windows the active band - below Curv min reads
        // flat (no hairs), above Curv max reads fully curved (bold hairs). Both live.
        double _licCurvMin = 0.05, _licCurvMax = 0.2;
        public double LicGrain { get => _licGrain; set => Set(ref _licGrain, value); }
        public int LicLength { get => _licLength; set => Set(ref _licLength, value); }
        public double LicAlpha { get => _licAlpha; set => Set(ref _licAlpha, value); }
        public double LicCurvMin { get => _licCurvMin; set => Set(ref _licCurvMin, value); }
        public double LicCurvMax { get => _licCurvMax; set => Set(ref _licCurvMax, value); }

        // Pin each patch boundary onto a low-DOF degree-3 B-spline "bent wire" (~1 control point per
        // SeamRatio mesh points) and hold it fixed during the solve, so seams are smooth + shared.
        bool _fixBSplineEdges;
        public bool FixBSplineEdges { get => _fixBSplineEdges; set => Set(ref _fixBSplineEdges, value); }
        int _seamRatio = 5;
        public int SeamRatio { get => _seamRatio; set => Set(ref _seamRatio, value); }

        // convenience for the run button caption
        public string IterLabel => "+" + _iterPerRun + " iter";

        public FlowParams ToFlowParams() => new FlowParams
        {
            Step = Step,
            Momentum = Momentum,
            deCraze = DeCraze * DeCrazeMax,   // DeCraze is a fraction of DeCrazeMax (slider 0-300%)
            CrazeBand = CrazeBandDeg * System.Math.PI / 180.0,   // degrees (UI) -> radians (engine)
            Sharpness = Sharpness,
            DetMix = DetMix,
            AdaptiveDetMix = AdaptiveDetMix,
            AdaptiveDetMixPower = AdaptiveDetMixPow,
            MomFix = MomFix,
            deBranch = 0.0,
            deConsolidate = 0.0,
            UseMaxCov = false,
        };

        // Snapshot the bake (Solve) parameters for the journal: the Accuracy target strain %, the
        // subdivide-round count, and the IsometricLM weights + seam-pin settings RunBake reads. Mirrors
        // ToFlowParams()'s role for the Run command, so a recorded Solve replays deterministically.
        public BakeParams ToBakeParams() => new BakeParams
        {
            TargetStrainPct = AccuracyStrainPct,
            SubdivLevel = SubdivLevel,
            Iso = IsoWeight, Fair = FairWeight, Anchor = AnchorWeight, Scale = ScaleWeight, Bend = BendWeight,
            DiffFair = DiffFair, BendDiff = BendDiff,
            FixEdges = FixBSplineEdges, SeamRatio = SeamRatio,
        };

        // Apply a recorded BakeParams back onto the view-model so a replayed Solve develops with the
        // captured settings (RunBake reads these live from _sim). AccuracyLevel is mapped from the target
        // strain % by picking the nearest material band, so the GO readout + label stay coherent on replay.
        public void ApplyBakeParams(BakeParams b)
        {
            AccuracyLevel = NearestAccuracyLevel(b.TargetStrainPct);
            SubdivLevel = b.SubdivLevel;
            IsoWeight = b.Iso; FairWeight = b.Fair; AnchorWeight = b.Anchor; ScaleWeight = b.Scale; BendWeight = b.Bend;
            DiffFair = b.DiffFair; BendDiff = b.BendDiff;
            FixBSplineEdges = b.FixEdges; SeamRatio = b.SeamRatio;
        }

        // Map a target strain % back to the discrete Accuracy material band whose allowable strain is
        // closest to it (inverse of AccuracyStrainPct), so a replayed/parsed Solve restores the slider.
        static int NearestAccuracyLevel(double pct)
        {
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < AccStrainPct.Length; i++)
            {
                double d = System.Math.Abs(AccStrainPct[i] - pct);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnChanged(name);
            return true;
        }
    }
}
