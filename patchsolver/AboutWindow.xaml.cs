using System.Windows;

namespace CreasePatchSolver
{
    // Non-modal About window. The owner reuses a single instance (Close just hides it).
    public partial class AboutWindow : Window
    {
        public AboutWindow() { InitializeComponent(); CloseButton.Click += (s, e) => Hide(); }
    }
}
