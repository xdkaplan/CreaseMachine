using System.ComponentModel;
using System.Runtime.CompilerServices;
using CreaseMachine;

namespace CreaseStudio
{
    // View-model for the simulation settings (right toolbar). Bindable (INotifyPropertyChanged) so
    // panel controls bind two-way and the flow reads a snapshot each run - the MVVM seam that keeps
    // the growing UI out of code-behind spaghetti. ToFlowParams() hands the engine a value snapshot.
    sealed class SimSettings : INotifyPropertyChanged
    {
        int _iterPerRun = 10, _momFix = 4;
        // CrazeBand is shown to the user in DEGREES (more intuitive than radians); the engine wants
        // radians, so ToFlowParams() converts. 5.7 deg == the engine's documented 0.1 rad default.
        double _step = 0.01, _momentum = 0.9, _deCraze = 0.0, _crazeBandDeg = 5.7, _sharpness = 4.0, _detMix = 0.0;

        public int IterPerRun { get => _iterPerRun; set { if (Set(ref _iterPerRun, value)) OnChanged(nameof(IterLabel)); } }
        public double Step { get => _step; set => Set(ref _step, value); }
        public double Momentum { get => _momentum; set => Set(ref _momentum, value); }
        public double DeCraze { get => _deCraze; set => Set(ref _deCraze, value); }
        public double CrazeBandDeg { get => _crazeBandDeg; set => Set(ref _crazeBandDeg, value); }
        public double Sharpness { get => _sharpness; set => Set(ref _sharpness, value); }
        public double DetMix { get => _detMix; set => Set(ref _detMix, value); }
        public int MomFix { get => _momFix; set => Set(ref _momFix, value); }

        // Brush params (shared by all brushes; the BRUSH tab + the [ ] / Ctrl+Shift+[ ] hotkeys drive
        // these). Strength = per-stroke opacity ceiling (0..1), Flow = build rate per dab (0..1),
        // Size = footprint radius in world units, Softness = Gaussian falloff (0 sharp .. 1 soft).
        double _brushStrength = 1.0, _brushFlow = 1.0, _brushSize = 10.0, _brushSoftness = 0.25;
        public double BrushStrength { get => _brushStrength; set => Set(ref _brushStrength, value); }
        public double BrushFlow { get => _brushFlow; set => Set(ref _brushFlow, value); }
        public double BrushSize { get => _brushSize; set => Set(ref _brushSize, value); }
        public double BrushSoftness { get => _brushSoftness; set => Set(ref _brushSoftness, value); }

        // The canonical "100%" deCraze weight — the top of the 0-100% scale shared by the deCraze
        // slider (its Maximum) and the BUFF brush (deCraze = BrushStrength * DeCrazeMax). Defined once
        // here so the 0.04 isn't a magic number duplicated across the brush and the panel.
        public double DeCrazeMax => 0.04;

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
