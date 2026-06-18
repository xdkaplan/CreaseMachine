using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// CreaseMachine GUI: a thin WinForms control panel that DRIVES crease.exe (the headless REPL)
// as a persistent subprocess. Buttons/fields compose text commands and write them to the
// process's stdin; the process's stdout streams into the log box. No 3D viewer - Export writes
// a PLY/OBJ you open in MeshLab/Blender/Rhino for review (point a viewer at the auto-export
// "watch" file and it reloads as the flow bakes).
namespace CreaseGUI
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class MainForm : Form
    {
        Process proc;
        string creasePath;

        TextBox tN, tStep, tMom, tCraze, tCrazeTo, tBand, tBandTo, tSharp, tSharpTo, tMomFix, tExtra, tWatch;
        CheckBox cbAuto;
        TextBox log;
        Label lblStatus;

        public MainForm()
        {
            Text = "CreaseMachine — headless driver";
            ClientSize = new Size(940, 660);
            MinimumSize = new Size(720, 480);
            Font = new Font("Segoe UI", 9f);
            BuildUi();
            FormClosing += (s, e) => Shutdown();
            Shown += (s, e) => LocateAndStart();
        }

        // ---------------------------- UI ----------------------------

        void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 232, Padding = new Padding(8) };
            Controls.Add(top);

            log = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, WordWrap = false,
                Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro
            };
            Controls.Add(log);
            log.BringToFront();

            int y = 4;
            // row: load + status
            Btn(top, "Load mesh…", 0, y, 90, (s, e) => DoLoad());
            lblStatus = new Label { Left = 100, Top = y + 4, Width = 720, AutoSize = false, Text = "starting…", ForeColor = Color.DimGray };
            top.Controls.Add(lblStatus);
            y += 32;

            // row: N / step / mom / momFix
            Lbl(top, "iters N", 0, y); tN = Tb(top, "200", 52, y, 60);
            Lbl(top, "step", 130, y); tStep = Tb(top, "0.05", 165, y, 55);
            Lbl(top, "mom", 235, y); tMom = Tb(top, "0.9", 270, y, 55);
            Lbl(top, "momFix", 340, y); tMomFix = Tb(top, "4", 392, y, 35);
            y += 30;

            // rampable knobs: value + optional "to"
            Lbl(top, "deCraze", 0, y); tCraze = Tb(top, "0", 60, y, 55); Lbl(top, "→", 120, y); tCrazeTo = Tb(top, "", 138, y, 55);
            Lbl(top, "(ramp end optional)", 205, y);
            y += 28;
            Lbl(top, "band", 0, y); tBand = Tb(top, "0.1", 60, y, 55); Lbl(top, "→", 120, y); tBandTo = Tb(top, "", 138, y, 55);
            Lbl(top, "rad (Huber flat-band)", 205, y);
            y += 28;
            Lbl(top, "sharpness", 0, y); tSharp = Tb(top, "4", 60, y, 55); Lbl(top, "→", 120, y); tSharpTo = Tb(top, "", 138, y, 55);
            Lbl(top, "corner preservation", 205, y);
            y += 30;

            // extra raw params escape-hatch
            Lbl(top, "extra", 0, y); tExtra = Tb(top, "", 60, y, 360); Lbl(top, "e.g. detMix=0.1 maxCov=1", 430, y);
            y += 30;

            // buttons
            Btn(top, "Run", 0, y, 70, (s, e) => DoRun());
            Btn(top, "Subdivide", 76, y, 80, (s, e) => Send("subdivide"));
            Btn(top, "Reset", 162, y, 60, (s, e) => Send("reset"));
            Btn(top, "Zero mom", 228, y, 80, (s, e) => Send("zero-momentum"));
            Btn(top, "Stats", 314, y, 55, (s, e) => Send("stats"));
            Btn(top, "Export…", 375, y, 75, (s, e) => DoExport());
            Btn(top, "Clear log", 456, y, 70, (s, e) => log.Clear());
            y += 32;

            // auto-export watch
            cbAuto = new CheckBox { Left = 0, Top = y + 2, Width = 150, Text = "auto-export after run →" };
            top.Controls.Add(cbAuto);
            tWatch = Tb(top, "C:\\Temp\\crease_live.ply", 170, y, 290);
            Btn(top, "…", 466, y, 28, (s, e) => { var p = PickSave(); if (p != null) tWatch.Text = p; });
        }

        Label Lbl(Control parent, string text, int x, int y)
        { var l = new Label { Text = text, Left = x, Top = y + 3, AutoSize = true }; parent.Controls.Add(l); return l; }

        TextBox Tb(Control parent, string text, int x, int y, int w)
        { var t = new TextBox { Text = text, Left = x, Top = y, Width = w }; parent.Controls.Add(t); return t; }

        Button Btn(Control parent, string text, int x, int y, int w, EventHandler h)
        { var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = 26 }; b.Click += h; parent.Controls.Add(b); return b; }

        // ---------------------------- commands ----------------------------

        void DoLoad()
        {
            using (var ofd = new OpenFileDialog { Title = "Load mesh", Filter = "Mesh (*.stl;*.obj)|*.stl;*.obj|All files|*.*" })
                if (ofd.ShowDialog() == DialogResult.OK) Send("load " + ofd.FileName);
        }

        void DoExport()
        {
            var p = PickSave();
            if (p != null) Send("export " + p);
        }

        string PickSave()
        {
            using (var sfd = new SaveFileDialog { Title = "Export mesh", Filter = "PLY energy-coloured (*.ply)|*.ply|OBJ (*.obj)|*.obj", FileName = "crease_out.ply" })
                return sfd.ShowDialog() == DialogResult.OK ? sfd.FileName : null;
        }

        void DoRun()
        {
            string n = tN.Text.Trim();
            if (n.Length == 0) { AppendLog("! set iters N"); return; }
            var sb = new System.Text.StringBuilder("run " + n);
            Add(sb, "step", tStep, null);
            Add(sb, "mom", tMom, null);
            Add(sb, "deCraze", tCraze, tCrazeTo);
            Add(sb, "band", tBand, tBandTo);
            Add(sb, "sharpness", tSharp, tSharpTo);
            Add(sb, "momFix", tMomFix, null);
            string extra = tExtra.Text.Trim();
            if (extra.Length > 0) sb.Append(' ').Append(extra);
            Send(sb.ToString());

            if (cbAuto.Checked)
            {
                string w = tWatch.Text.Trim();
                if (w.Length > 0) Send("export " + w);
            }
        }

        // append "name=v" or "name=a>b" if the value box is non-empty
        static void Add(System.Text.StringBuilder sb, string name, TextBox v, TextBox to)
        {
            string a = v.Text.Trim();
            if (a.Length == 0) return;
            string b = to == null ? null : to.Text.Trim();
            sb.Append(' ').Append(name).Append('=').Append(a);
            if (!string.IsNullOrEmpty(b)) sb.Append('>').Append(b);
        }

        void Send(string cmd)
        {
            if (proc == null || proc.HasExited) { AppendLog("! engine not running (Load a mesh / check crease.exe)"); return; }
            AppendLog("> " + cmd);
            try { proc.StandardInput.WriteLine(cmd); proc.StandardInput.Flush(); }
            catch (Exception ex) { AppendLog("! send failed: " + ex.Message); }
        }

        // ---------------------------- process ----------------------------

        void LocateAndStart()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string[] cands =
            {
                Path.Combine(dir, "crease.exe"),
                Full(dir, @"..\..\..\..\cli\bin\Release\net48\crease.exe"),
                Full(dir, @"..\..\..\..\cli\bin\Debug\net48\crease.exe"),
            };
            foreach (var c in cands) if (c != null && File.Exists(c)) { creasePath = c; break; }
            if (creasePath == null)
            {
                AppendLog("crease.exe not found in the usual spots — locate it (build cli/CreaseCLI.csproj first).");
                using (var ofd = new OpenFileDialog { Title = "Locate crease.exe", Filter = "crease.exe|crease.exe|Executables|*.exe" })
                    if (ofd.ShowDialog() == DialogResult.OK) creasePath = ofd.FileName;
            }
            if (creasePath == null) { SetStatus("no engine — Run won't work"); return; }
            StartProc();
        }

        static string Full(string baseDir, string rel)
        { try { return Path.GetFullPath(Path.Combine(baseDir, rel)); } catch { return null; } }

        void StartProc()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = creasePath,
                    WorkingDirectory = Path.GetDirectoryName(creasePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLog(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLog("! " + e.Data); };
                proc.Exited += (s, e) => SetStatus("engine exited");
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                SetStatus("engine: " + creasePath);
            }
            catch (Exception ex) { AppendLog("! could not start crease.exe: " + ex.Message); SetStatus("engine failed to start"); }
        }

        void Shutdown()
        {
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    try { proc.StandardInput.WriteLine("quit"); proc.StandardInput.Flush(); } catch { }
                    if (!proc.WaitForExit(700)) proc.Kill();
                }
            }
            catch { }
        }

        // ---------------------------- helpers ----------------------------

        void AppendLog(string s)
        {
            if (log.IsDisposed) return;
            if (log.InvokeRequired) { try { log.BeginInvoke((Action)(() => AppendLog(s))); } catch { } return; }
            log.AppendText(s + Environment.NewLine);
        }

        void SetStatus(string s)
        {
            if (lblStatus.IsDisposed) return;
            if (lblStatus.InvokeRequired) { try { lblStatus.BeginInvoke((Action)(() => SetStatus(s))); } catch { } return; }
            lblStatus.Text = s;
        }
    }
}
