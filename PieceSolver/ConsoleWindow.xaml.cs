using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PieceSolver
{
    // Non-modal log window. Hidden by default; toggled from Window > Console or Ctrl+Shift+J. The main window
    // owns the single instance and writes lines via AppendLine — the ONE sink every log line funnels through
    // (Doc.Record -> Echo, and MainWindow.Log), so the ISO timestamp + syntax highlighting are applied here,
    // orchestrally, never at the call sites. The timestamp is display-only: the saved .journal (the Doc event
    // log) stays timestamp-free so it parses / replays cleanly.
    //   <date>T  -> barely-readable (recedes)   <time HH:mm:ss> -> readable anchor
    //   '#' line -> dimmed comment              else: first token (op/command) keyword colour, params default
    public partial class ConsoleWindow : Window
    {
        readonly Paragraph _para = new Paragraph { Margin = new Thickness(0) };

        static readonly Brush TsDate  = new SolidColorBrush(Color.FromRgb(0x46, 0x49, 0x40));   // timestamp date — barely readable
        static readonly Brush TsTime  = new SolidColorBrush(Color.FromRgb(0x86, 0x8C, 0x7A));   // timestamp HH:mm:ss — readable, muted
        static readonly Brush Comment = new SolidColorBrush(Color.FromRgb(0x6A, 0x70, 0x66));   // dimmed narration
        static readonly Brush Keyword = new SolidColorBrush(Color.FromRgb(0x74, 0xC0, 0xFC));   // op/command name (open-color blue 3)

        public ConsoleWindow() { InitializeComponent(); LogDoc.Blocks.Add(_para); }

        public void AppendLine(string s)
        {
            s ??= "";
            var now = DateTime.Now;
            _para.Inlines.Add(new Run(now.ToString("yyyy-MM-dd") + "T") { Foreground = TsDate });   // ISO date — recedes
            _para.Inlines.Add(new Run(now.ToString("HH:mm:ss")) { Foreground = TsTime });           // the readable bit
            _para.Inlines.Add(new Run("  ") { Foreground = TsDate });                               // gap

            if (s.TrimStart().StartsWith('#'))
            {
                _para.Inlines.Add(new Run(s) { Foreground = Comment });
            }
            else
            {
                int sp = s.IndexOf(' ');
                if (sp < 0) _para.Inlines.Add(new Run(s) { Foreground = Keyword });                 // verb only (e.g. "undo")
                else { _para.Inlines.Add(new Run(s.Substring(0, sp)) { Foreground = Keyword });     // verb
                       _para.Inlines.Add(new Run(s.Substring(sp))); }                               // params (default colour)
            }
            _para.Inlines.Add(new LineBreak());
            LogBox.ScrollToEnd();
        }

        public void ClearLog() { _para.Inlines.Clear(); }
    }
}
