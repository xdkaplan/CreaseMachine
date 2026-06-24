using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PieceSolver
{
    // Non-modal log window. Hidden by default; toggled from Window > Console or Ctrl+Shift+J. The main window
    // owns the single instance and writes lines via AppendLine. The log is a tiny journal DSL with light syntax
    // highlighting: a '#' line is a dimmed comment; otherwise the first token (the op / command name) is the
    // keyword colour and the rest (its params) the default colour.
    public partial class ConsoleWindow : Window
    {
        readonly Paragraph _para = new Paragraph { Margin = new Thickness(0) };

        static readonly Brush Comment = new SolidColorBrush(Color.FromRgb(0x6A, 0x70, 0x66));   // dimmed narration
        static readonly Brush Keyword = new SolidColorBrush(Color.FromRgb(0x74, 0xC0, 0xFC));   // op/command name (open-color blue 3)

        public ConsoleWindow() { InitializeComponent(); LogDoc.Blocks.Add(_para); }

        public void AppendLine(string s)
        {
            s ??= "";
            if (s.TrimStart().StartsWith('#'))
            {
                _para.Inlines.Add(new Run(s) { Foreground = Comment });
            }
            else
            {
                int sp = s.IndexOf(' ');
                if (sp < 0) _para.Inlines.Add(new Run(s) { Foreground = Keyword });               // verb only (e.g. "revert")
                else { _para.Inlines.Add(new Run(s.Substring(0, sp)) { Foreground = Keyword });   // verb
                       _para.Inlines.Add(new Run(s.Substring(sp))); }                             // params (default colour)
            }
            _para.Inlines.Add(new LineBreak());
            LogBox.ScrollToEnd();
        }

        public void ClearLog() { _para.Inlines.Clear(); }
    }
}
