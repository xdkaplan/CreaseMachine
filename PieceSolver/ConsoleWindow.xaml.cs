using System;
using System.Windows;

namespace PieceSolver
{
    // Non-modal log window. Hidden by default; toggled from Window > Console or Ctrl+Shift+J. The
    // main window owns the single instance and writes lines via AppendLine.
    public partial class ConsoleWindow : Window
    {
        public ConsoleWindow() { InitializeComponent(); }

        public void AppendLine(string s) { LogBox.AppendText(s + Environment.NewLine); LogBox.ScrollToEnd(); }
        public void ClearLog() { LogBox.Clear(); }
    }
}
