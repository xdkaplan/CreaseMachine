using System.Windows;

namespace PieceSolver
{
    // Non-modal About window. The owner reuses a single instance (Close just hides it).
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            CloseButton.Click += (s, e) => Hide();
            // Continuous version = the git short hash generated at build (see PieceSolver.csproj `StampGitVersion`
            // -> BuildInfo.GitHash). Meaningless without the repo — that's the point.
            VersionText.Text = "version " + BuildInfo.GitHash;
        }
    }
}
