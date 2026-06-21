using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreaseMachine;

namespace CreasePatchSolver
{
    // View-model for the simulation settings (right toolbar). Bindable (INotifyPropertyChanged) so
    // panel controls bind two-way and the flow reads a snapshot each run - the MVVM seam that keeps
    // the growing UI out of code-behind spaghetti. ToFlowParams() hands the engine a value snapshot.
    sealed class SimSettings : INotifyPropertyChanged
    {
        int _iterPerRun = 10, _momFix = 4;
        // PatchSolver mesh picker: 1..30 maps to the NURBS test surfaces 0.stl..29.stl. Changing it
        // resets the app to that mesh (load + clear flat + re-arm BFF) from the MainWindow handler.
        int _meshIndex = 1;
        // CrazeBand is shown to the user in DEGREES (more intuitive than radians); the engine wants
        // radians, so ToFlowParams() converts. 5.7 deg == the engine's documented 0.1 rad default.
        double _step = 0.01, _momentum = 0.9, _deCraze = 0.0, _crazeBandDeg = 5.7, _sharpness = 4.0, _detMix = 0.05;
        // Experimental: adaptive DetMix - raise the lambda_min<->det blend toward 1 at near-degenerate
        // ("twisty") vertices via a = (1-sep)^pow, leaving real creases (sep->1) at the DetMix floor.
        bool _adaptiveDetMix = false; double _adaptiveDetMixPow = 2.0;
        // Isometric patch-solver OBJECTIVE weights (IsometricLM.Solve, Levenberg-Marquardt). There is no
        // step / learning rate: LM computes its own step (damped normal equations), so these are pure
        // objective knobs. Iso is the developability gain: higher = drive relErr lower (LM is stable to
        // any scale - it can't diverge like the old explicit step did). Anchor is the faithfulness dial:
        // low = deform M freely toward developable (relErr -> ~0; at anchor 0.001, LM reaches ~0.005%),
        // high = stay near the original. Fair (uniform-Laplacian smoothing) is largely unneeded under LM
        // (no zigzag to suppress); leave it low or 0.
        double _isoWeight = 10.0, _fairWeight = 0.1, _anchorWeight = 0.1;

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

        // Isometric patch-solver objective weights (drive IsometricLM.Solve via IsometricStep()).
        public double IsoWeight { get => _isoWeight; set => Set(ref _isoWeight, value); }
        public double FairWeight { get => _fairWeight; set => Set(ref _fairWeight, value); }
        public double AnchorWeight { get => _anchorWeight; set => Set(ref _anchorWeight, value); }

        // The canonical "100%" deCraze weight — the top of the 0-100% scale used by the deCraze
        // slider (its Maximum maps to DeCrazeMax). Defined once here so the 0.04 isn't a magic number
        // duplicated across the panel and the Run param round-trip.
        public double DeCrazeMax => 0.04;

        // Shading facet (display): 0 = smooth (averaged normals), 1 = faceted (per-face normals). The
        // shader blends between them, so this is continuous and live. Default faceted.
        double _facet = 0.9;
        public double Facet { get => _facet; set => Set(ref _facet, value); }

        // Facet response curve: the shader blends by pow(Facet, FacetExp). 1 = linear; higher keeps
        // facets soft until Facet is high, then sharpens quickly.
        double _facetExp = 4.0;
        public double FacetExp { get => _facetExp; set => Set(ref _facetExp, value); }

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
