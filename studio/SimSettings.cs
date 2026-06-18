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
        double _step = 0.05, _momentum = 0.9, _deCraze = 0.0, _crazeBand = 0.1, _sharpness = 4.0, _detMix = 0.0;

        public int IterPerRun { get => _iterPerRun; set { if (Set(ref _iterPerRun, value)) OnChanged(nameof(IterLabel)); } }
        public double Step { get => _step; set => Set(ref _step, value); }
        public double Momentum { get => _momentum; set => Set(ref _momentum, value); }
        public double DeCraze { get => _deCraze; set => Set(ref _deCraze, value); }
        public double CrazeBand { get => _crazeBand; set => Set(ref _crazeBand, value); }
        public double Sharpness { get => _sharpness; set => Set(ref _sharpness, value); }
        public double DetMix { get => _detMix; set => Set(ref _detMix, value); }
        public int MomFix { get => _momFix; set => Set(ref _momFix, value); }

        // convenience for the run button caption
        public string IterLabel => "+" + _iterPerRun + " iter";

        public FlowParams ToFlowParams() => new FlowParams
        {
            Step = Step,
            Momentum = Momentum,
            deCraze = DeCraze,
            CrazeBand = CrazeBand,
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
