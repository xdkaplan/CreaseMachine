using System;
using System.Windows;
using System.Windows.Input;
using OpenTK.Wpf;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Plankton;
using CreaseMachine;

namespace CreaseStudio
{
    public partial class MainWindow : Window
    {
        GLWpfControl _gl;
        MeshView _view;
        FlowSession _session;        // live mesh + Nesterov velocity; the flow bakes it in place
        readonly SimSettings _sim = new SimSettings();   // bindable sim params (right toolbar)
        string _meshPath;            // source mesh path, so Reset can reload the input from disk
        bool _glInit, _meshDirty, _reframe;         // _reframe: re-fit the camera on the next upload
        long _totalIters;
        int _depthRbo, _depthW, _depthH;            // our depth attachment (GLWpfControl FBO is colour-only)

        // Display state (render-only, owned here rather than in SimSettings, which stays flow-only):
        bool _flatShading;                          // false = welded/smooth, true = unwelded/faceted
        string[] _matcapPaths;                      // bundled matcap files (assets/matcaps)
        byte[] _matcapPx; int _matcapW, _matcapH;   // pending matcap pixels (BGRA, GL row order)
        bool _matcapDirty;                          // re-upload the matcap texture on the next render

        // Journal harness: every action routes through Execute(), which records the semantic command
        // (record/replay), so a saved .journal replays the live workflow and times each step for
        // perf/value drift across commits. Display state is synced on replay (suppress -> no re-record).
        readonly System.Collections.Generic.List<StudioCommand> _journal = new System.Collections.Generic.List<StudioCommand>();
        bool _suppressUi;                           // programmatic UI sync in progress -> don't re-record
        System.Windows.Threading.DispatcherTimer _replayTimer;
        System.Collections.Generic.List<StudioCommand> _replayQueue;
        int _replayPos;
        double _lastRunMs, _lastUploadMs;           // perf readout (engine flow time / GL upload time)

        ConsoleWindow _console;                     // non-modal log window (Window > Console / Ctrl+Shift+J)
        AboutWindow _about;                         // non-modal about window (Help > About)
        bool _shuttingDown;                         // main window closing -> let tool windows actually close

        // orbit camera
        float _azimuth = 0.6f, _elevation = 0.4f, _distance = 3f;
        Vector3 _target = Vector3.Zero;
        System.Windows.Point _lastMouse;
        enum DragMode { None, Orbit, Pan, Edit }
        DragMode _drag = DragMode.None;   // right-drag = orbit, Shift+right-drag = pan, left-drag = edit (brush)

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _sim;   // right-panel sliders + the run-button caption bind to this

            // The session log lives in a non-modal Console window (Window > Console / Ctrl+Shift+J),
            // hidden by default. Created now so Log() works from startup; its Owner is set lazily on
            // first show (a Window's Owner must already be shown). Closing hides it (the instance is
            // reused) unless the app itself is shutting down.
            _console = new ConsoleWindow();
            _console.Closing += (s, e) => { if (!_shuttingDown) { e.Cancel = true; _console.Hide(); MenuConsole.IsChecked = false; } };
            Closing += (s, e) => _shuttingDown = true;

            _gl = new GLWpfControl();
            CenterHost.Children.Add(_gl);   // GL viewport lives in the center cell of the docked layout
            _gl.Start(new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                Profile = OpenTK.Windowing.Common.ContextProfile.Core,
            });
            _gl.Render += OnRender;
            _gl.MouseDown += OnMouseDown;
            _gl.MouseUp += OnMouseUp;
            _gl.MouseMove += OnMouseMove;
            _gl.MouseWheel += (s, e) => { _distance *= MathF.Pow(0.999f, e.Delta); InvalidateView(); };

            // Load the default mesh as the first journal entry, so recordings are self-contained
            // (they begin with the load). The GL upload itself happens once the context is live.
            string def = @"C:\Temp\Bunny 5k.stl";
            if (System.IO.File.Exists(def)) Execute(StudioCommand.Load(def), record: true);
            else Title = "CreaseStudio — (no mesh at " + def + ")";

            // DISPLAY tab: bundled matcaps (assets/matcaps, copied next to the exe). Thumbnails feed
            // the switcher ListBox; the selected one is decoded to a GL texture by SelectMatcap. The
            // initial decode is done explicitly here (the SelectionChanged handler is wired below, so
            // setting SelectedIndex now doesn't double-fire). Matcaps are sampled by the view-space
            // normal -> a lit-sphere look that reads orientation.
            string mcDir = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "matcaps");
            _matcapPaths = System.IO.Directory.Exists(mcDir)
                ? System.IO.Directory.GetFiles(mcDir, "*.png") : Array.Empty<string>();
            Array.Sort(_matcapPaths, StringComparer.OrdinalIgnoreCase);
            var thumbs = new System.Collections.Generic.List<System.Windows.Media.ImageSource>();
            foreach (var p in _matcapPaths)
            {
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit(); bi.UriSource = new Uri(p); bi.DecodePixelWidth = 64;
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit();
                    thumbs.Add(bi);
                }
                catch { }
            }
            MatcapList.ItemsSource = thumbs;
            // Initial matcap applied (not recorded) before the SelectionChanged handler is wired, so
            // setting the index here doesn't fire a recorded command.
            if (_matcapPaths.Length > 0) { MatcapList.SelectedIndex = 0; ApplyMatcap(0); }

            // Top-bar actions route through Execute() so each is recorded to the session journal.
            IterButton.Click += (s, e) => Execute(StudioCommand.Run(_sim.IterPerRun, _sim.ToFlowParams()), record: true);
            SubdivideButton.Click += (s, e) => Execute(StudioCommand.Subdiv(), record: true);
            ResetButton.Click += (s, e) => Execute(StudioCommand.Reset(), record: true);

            // Collapse chevron at each panel's inner-top corner toggles collapse/expand.
            LeftCollapseBtn.Click += (s, e) => ToggleCollapse(LeftCol, ref _leftRestore);
            RightCollapseBtn.Click += (s, e) => ToggleCollapse(RightCol, ref _rightRestore);
            // Dragging a splitter below the threshold collapses that panel (remembering the width it
            // had before the drag, to restore to). Above the threshold, drag just resizes normally.
            LeftSplitter.PreviewMouseLeftButtonDown += (s, e) => _preDragWidth = LeftCol.ActualWidth;
            LeftSplitter.PreviewMouseLeftButtonUp += (s, e) => AfterSplitterDrag(LeftCol, ref _leftRestore);
            RightSplitter.PreviewMouseLeftButtonDown += (s, e) => _preDragWidth = RightCol.ActualWidth;
            RightSplitter.PreviewMouseLeftButtonUp += (s, e) => AfterSplitterDrag(RightCol, ref _rightRestore);

            // DISPLAY tab: welded/unwelded + matcap switcher. Recorded too; the _suppressUi guard stops
            // the programmatic control-sync during replay from re-recording.
            WeldedRadio.Checked += (s, e) => { if (!_suppressUi) Execute(StudioCommand.Shading(false), record: true); };
            UnweldedRadio.Checked += (s, e) => { if (!_suppressUi) Execute(StudioCommand.Shading(true), record: true); };
            MatcapList.SelectionChanged += (s, e) => { if (!_suppressUi && MatcapList.SelectedIndex >= 0) Execute(StudioCommand.Matcap(MatcapList.SelectedIndex), record: true); };

            // Console-window transport: save the recorded session, replay a journal file, clear recording.
            _console.SaveButton.Click += (s, e) => SaveSession();
            _console.ReplayButton.Click += (s, e) => OpenAndReplay();
            _console.ClearButton.Click += (s, e) => { _journal.Clear(); _console.ClearLog(); Log("journal cleared"); };

            // Menu bar + keyboard shortcuts (Ctrl+S = Save As, Ctrl+Shift+J = toggle Console).
            MenuSaveAs.Click += (s, e) => SaveSession();
            MenuAbout.Click += (s, e) => ShowAbout();
            MenuConsole.Click += (s, e) => ShowConsole(MenuConsole.IsChecked);   // IsCheckable flips first
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveSession(); e.Handled = true; }
                else if (e.Key == Key.J && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { ToggleConsole(); e.Handled = true; }
            };
        }

        // ===================== window menu: Console + About (both non-modal) =====================

        void ShowConsole(bool show)
        {
            if (_console == null) return;
            if (show)
            {
                if (_console.Owner == null) { try { _console.Owner = this; } catch { } }   // owner must be shown first
                _console.Show(); _console.Activate();
            }
            else _console.Hide();
            MenuConsole.IsChecked = show;
        }

        void ToggleConsole() => ShowConsole(!(_console != null && _console.IsVisible));

        void ShowAbout()
        {
            if (_about == null)
            {
                _about = new AboutWindow();
                _about.Closing += (s, e) => { if (!_shuttingDown) { e.Cancel = true; _about.Hide(); } };
            }
            if (_about.Owner == null) { try { _about.Owner = this; } catch { } }
            _about.Show(); _about.Activate();
        }

        // A panel is "collapsed" when its column is narrower than CollapseThreshold (32px). The
        // chevron button toggles between a thin CollapsedWidth bar and the last expanded width;
        // dragging the splitter below the threshold also collapses (restoring to the pre-drag width).
        const double CollapseThreshold = 32;
        const double CollapsedWidth = 26;
        double _leftRestore = 220, _rightRestore = 240;
        double _preDragWidth;

        void ToggleCollapse(System.Windows.Controls.ColumnDefinition col, ref double restore)
        {
            if (col.ActualWidth < CollapseThreshold)
                col.Width = new GridLength(restore >= CollapseThreshold ? restore : 200);    // expand
            else { restore = col.ActualWidth; col.Width = new GridLength(CollapsedWidth); }   // collapse
        }

        void AfterSplitterDrag(System.Windows.Controls.ColumnDefinition col, ref double restore)
        {
            double w = col.ActualWidth;
            if (w < CollapseThreshold)
            {
                if (_preDragWidth >= CollapseThreshold) restore = _preDragWidth;   // came from this width
                col.Width = new GridLength(CollapsedWidth);
            }
            else { restore = w; col.Width = new GridLength(w); }   // pin the dragged width; remember it
        }

        // ===================== command sink (the journal chokepoint) =====================

        // The single entry point for every studio action. Performs the side effect (Apply*), records
        // the semantic command when asked, and syncs the display controls (so replay reflects state).
        // Every UI handler and the replay loop go through here, so the journal is the complete,
        // authoritative record of what happened - and replay drives the exact same code paths.
        void Execute(StudioCommand c, bool record)
        {
            switch (c.Kind)
            {
                case CmdKind.Load: ApplyLoad(c.Path); break;
                case CmdKind.Run: ApplyRun(c.N, c.P); break;
                case CmdKind.Subdivide: ApplySubdivide(); break;
                case CmdKind.Reset: ApplyReset(); break;
                case CmdKind.Shading: ApplyShading(c.Flag); break;
                case CmdKind.Matcap: ApplyMatcap(c.N); break;
            }
            if (record) { _journal.Add(c); Log(c.Serialize()); }
            SyncControls(c);
        }

        // Reflect a command's state in the input controls without re-recording (_suppressUi guards the
        // control event handlers). Only meaningful during replay; for user actions the control is
        // already in the target state, so these sets are no-ops.
        void SyncControls(StudioCommand c)
        {
            _suppressUi = true;
            try
            {
                if (c.Kind == CmdKind.Shading) { if (c.Flag) UnweldedRadio.IsChecked = true; else WeldedRadio.IsChecked = true; }
                else if (c.Kind == CmdKind.Matcap && c.N >= 0 && c.N < MatcapList.Items.Count) MatcapList.SelectedIndex = c.N;
                else if (c.Kind == CmdKind.Run)
                {
                    // show the replayed run's parameters on the sliders (cosmetic; the run used c.P).
                    _sim.IterPerRun = c.N; _sim.Step = c.P.Step; _sim.Momentum = c.P.Momentum;
                    _sim.DeCraze = c.P.deCraze; _sim.CrazeBandDeg = c.P.CrazeBand * 180.0 / Math.PI;
                    _sim.Sharpness = c.P.Sharpness; _sim.DetMix = c.P.DetMix; _sim.MomFix = c.P.MomFix;
                }
            }
            finally { _suppressUi = false; }
        }

        // ===================== Apply* : the actual side effects (no recording) =====================

        void ApplyLoad(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) { Title = "CreaseStudio — missing: " + path; return; }
            try { _session = new FlowSession(MeshIO.Load(path)); _meshPath = path; }
            catch (Exception ex) { Title = "CreaseStudio — load failed: " + ex.Message; return; }
            _totalIters = 0;
            _reframe = true;          // new mesh -> re-fit the camera on the next upload
            _meshDirty = true;
            Title = "CreaseStudio — " + System.IO.Path.GetFileName(path);
            _gl?.InvalidateVisual();
        }

        // Run n flow steps in-process (on the UI thread). Uses the supplied params (NOT live sliders),
        // so a recorded run replays identically. Times the flow loop for the perf-drift readout.
        void ApplyRun(int n, FlowParams p)
        {
            if (_session == null || n <= 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                _session.CollapseShort();
                _session.CollapseSliver();
                _session.NesterovStep(p, out bool[] fold);
                _session.HealFolds(fold);
            }
            sw.Stop();
            _lastRunMs = sw.Elapsed.TotalMilliseconds;
            _totalIters += n;
            _meshDirty = true;
            Title = "CreaseStudio — iter " + _totalIters + "  (" + _session.Mesh.Vertices.Count + " verts)";
            UpdateStatus();
            _gl?.InvalidateVisual();
        }

        // One 1->4 subdivision of the live mesh (geometry-preserving; momentum resets inside
        // FlowSession.Subdivide as indices are renumbered). Camera unchanged.
        void ApplySubdivide()
        {
            if (_session == null) return;
            _session.Subdivide();
            _meshDirty = true;
            Title = "CreaseStudio — subdivided  (" + _session.Mesh.Vertices.Count + " verts)";
            _gl?.InvalidateVisual();
        }

        // Reload the current mesh from disk and reset the flow. Same bounds -> camera kept (no reframe).
        void ApplyReset()
        {
            if (_meshPath == null || !System.IO.File.Exists(_meshPath)) return;
            try { _session = new FlowSession(MeshIO.Load(_meshPath)); }
            catch (Exception ex) { Title = "CreaseStudio — reset failed: " + ex.Message; return; }
            _totalIters = 0;
            _meshDirty = true;
            Title = "CreaseStudio — " + System.IO.Path.GetFileName(_meshPath);
            _gl?.InvalidateVisual();
        }

        // Welded (smooth) <-> unwelded (faceted) shading. Render-only: re-uploads the same mesh.
        void ApplyShading(bool flat)
        {
            if (_flatShading == flat) return;
            _flatShading = flat;
            _meshDirty = true;
            _gl?.InvalidateVisual();
        }

        // Decode matcap[i] to BGRA (GL row order) and queue it for upload next render. GL texture
        // calls must run with the context current (inside OnRender), so we only stage the pixels here.
        void ApplyMatcap(int i)
        {
            if (_matcapPaths == null || i < 0 || i >= _matcapPaths.Length) return;
            try
            {
                _matcapPx = DecodeMatcapBgra(_matcapPaths[i], out _matcapW, out _matcapH);
                _matcapDirty = true;
                _gl?.InvalidateVisual();
            }
            catch { }
        }

        // ===================== journal save / replay =====================

        void SaveSession()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            { Filter = "Journal (*.journal)|*.journal|All files (*.*)|*.*", FileName = "session.journal", InitialDirectory = @"C:\Temp" };
            if (dlg.ShowDialog() != true) return;
            var lines = new System.Collections.Generic.List<string> { "# CreaseStudio session journal" };
            foreach (var c in _journal) lines.Add(c.Serialize());
            try { System.IO.File.WriteAllLines(dlg.FileName, lines); Log("saved " + _journal.Count + " commands -> " + System.IO.Path.GetFileName(dlg.FileName)); }
            catch (Exception ex) { Log("save failed: " + ex.Message); }
        }

        void OpenAndReplay()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Journal (*.journal)|*.journal|All files (*.*)|*.*", InitialDirectory = @"C:\Temp" };
            if (dlg.ShowDialog() != true) return;
            var cmds = new System.Collections.Generic.List<StudioCommand>();
            try { foreach (var ln in System.IO.File.ReadAllLines(dlg.FileName)) { var c = StudioCommand.Parse(ln); if (c != null) cmds.Add(c); } }
            catch (Exception ex) { Log("load failed: " + ex.Message); return; }
            ReplayJournal(cmds);
        }

        // Replay a command list against the live GUI, one command per timer tick (so the viewport
        // repaints between steps). Each command is timed; runs also log FlowMetrics, giving numbers
        // directly comparable to the CLI baseline - the soft perf/value drift signal across commits.
        void ReplayJournal(System.Collections.Generic.List<StudioCommand> cmds)
        {
            if (cmds == null || cmds.Count == 0) { Log("nothing to replay"); return; }
            _replayQueue = cmds; _replayPos = 0;
            Log("--- replay: " + cmds.Count + " commands ---");
            if (_replayTimer == null)
            {
                _replayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                _replayTimer.Tick += ReplayTick;
            }
            _replayTimer.Start();
        }

        void ReplayTick(object sender, EventArgs e)
        {
            if (_replayQueue == null || _replayPos >= _replayQueue.Count) { _replayTimer.Stop(); Log("--- replay done ---"); return; }
            var c = _replayQueue[_replayPos++];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Execute(c, record: false);
            sw.Stop();
            string extra = "";
            if (c.Kind == CmdKind.Run && _session != null)
            {
                var m = FlowMetrics.Compute(_session.Mesh, c.P.CrazeBand, c.P.UseMaxCov, c.P.Sharpness);
                extra = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "  sumE={0:0.000} panels={1} maxDih={2:0.0}", m.SumE, m.Panels, m.MaxDihDeg);
            }
            Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0,3}/{1}  {2}  [{3:0.0} ms]{4}", _replayPos, _replayQueue.Count, c.Serialize(), sw.Elapsed.TotalMilliseconds, extra));
        }

        void Log(string msg) => _console?.AppendLine(msg);

        void UpdateStatus()
        {
            if (StatusText == null) return;
            int v = _session != null ? _session.Mesh.Vertices.Count : 0;
            StatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "flow {0:0.0} ms · upload {1:0.0} ms · {2} verts", _lastRunMs, _lastUploadMs, v);
        }

        static byte[] DecodeMatcapBgra(string path, out int w, out int h)
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit(); bi.UriSource = new Uri(path);
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit();
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                bi, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            w = conv.PixelWidth; h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            var flip = new byte[px.Length];                 // top-down rows -> bottom-up for GL
            for (int y = 0; y < h; y++) Array.Copy(px, y * w * 4, flip, (h - 1 - y) * w * 4, w * 4);
            return flip;
        }

        // Mouse scheme: right-drag = orbit, Shift+right-drag = pan, left-drag = Edit (the active
        // editor; the brush is the only one, and it isn't built yet). Wheel = zoom (wired in ctor).
        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _lastMouse = e.GetPosition(_gl);
            if (e.ChangedButton == MouseButton.Right)
                _drag = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? DragMode.Pan : DragMode.Orbit;
            else if (e.ChangedButton == MouseButton.Left)
                _drag = DragMode.Edit;
            else return;
            _gl.CaptureMouse();   // keep dragging even if the cursor leaves the viewport
        }

        void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _drag = DragMode.None;
            _gl.ReleaseMouseCapture();
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_drag == DragMode.None) return;
            var p = e.GetPosition(_gl);
            float dx = (float)(p.X - _lastMouse.X), dy = (float)(p.Y - _lastMouse.Y);
            _lastMouse = p;
            switch (_drag)
            {
                case DragMode.Orbit:
                    _azimuth -= dx * 0.01f;
                    _elevation = Math.Clamp(_elevation + dy * 0.01f, -1.5f, 1.5f);
                    InvalidateView();
                    break;
                case DragMode.Pan:
                    PanCamera(dx, dy);
                    break;
                case DragMode.Edit:
                    // Left-drag editing (NOISE brush paint) plugs in here once the brush exists.
                    break;
            }
        }

        // Translate the orbit target in the camera's screen plane (Shift+right-drag). Speed scales
        // with zoom distance so the feel is consistent at any zoom.
        void PanCamera(float dx, float dy)
        {
            Vector3 dir = new Vector3(
                MathF.Cos(_elevation) * MathF.Sin(_azimuth),
                MathF.Cos(_elevation) * MathF.Cos(_azimuth),
                MathF.Sin(_elevation));                                  // eye = target + dir*distance
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, dir));   // camera-right in world
            Vector3 up = Vector3.Normalize(Vector3.Cross(dir, right));              // camera-up in world
            float scale = _distance * 0.0015f;
            _target += (-dx * right + dy * up) * scale;
            InvalidateView();
        }

        void InvalidateView() { _gl?.InvalidateVisual(); }

        void OnRender(TimeSpan delta)
        {
            if (!_glInit) { _view = new MeshView(); _glInit = true; }

            // apply a queued matcap texture (GL thread = here)
            if (_matcapDirty && _view != null && _matcapPx != null) { _view.SetMatcap(_matcapPx, _matcapW, _matcapH); _matcapDirty = false; }

            // re-upload when the mesh changed (run / subdivide / reset / load) or shading toggled;
            // re-fit the camera after a load (new bounds). Upload time feeds the perf readout.
            if (_meshDirty && _session != null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _view.Upload(_session.Mesh, _flatShading);
                sw.Stop();
                _lastUploadMs = sw.Elapsed.TotalMilliseconds;
                _meshDirty = false;
                if (_reframe) { _target = _view.Center; _distance = _view.Radius * 3f; _reframe = false; }
                UpdateStatus();
            }

            // Depth state EVERY frame: GLWpfControl rebinds its own framebuffer / resets GL state
            // each render, so a one-time enable doesn't stick. Without this there's no depth test and
            // the far side of the mesh paints over the near side (looks like "backfaces visible").
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.DepthMask(true);
            GL.Disable(EnableCap.CullFace);   // winding is mixed post-weld; rely on depth, not culling

            // GLWpfControl's framebuffer is colour-only (no depth), so attach our own depth
            // renderbuffer to the currently-bound FBO, sized to the viewport. Re-attach each frame
            // (the control may rebuild its FBO on resize); recreate the rbo only when the size changes.
            int[] vp = new int[4];
            GL.GetInteger(GetPName.Viewport, vp);
            int fbw = vp[2], fbh = vp[3];
            if (fbw > 0 && fbh > 0)
            {
                if (_depthRbo == 0 || fbw != _depthW || fbh != _depthH)
                {
                    if (_depthRbo == 0) _depthRbo = GL.GenRenderbuffer();
                    GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
                    GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, fbw, fbh);
                    _depthW = fbw; _depthH = fbh;
                }
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    RenderbufferTarget.Renderbuffer, _depthRbo);
            }

            GL.ClearColor(0.12f, 0.12f, 0.14f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            double w = _gl.ActualWidth, h = Math.Max(1, _gl.ActualHeight);
            float aspect = (float)(w / h);

            // Z-up (Rhino convention): elevation raises +Z, up vector is +Z.
            Vector3 dir = new Vector3(
                MathF.Cos(_elevation) * MathF.Sin(_azimuth),
                MathF.Cos(_elevation) * MathF.Cos(_azimuth),
                MathF.Sin(_elevation));
            Vector3 eye = _target + dir * _distance;
            Matrix4 view = Matrix4.LookAt(eye, _target, Vector3.UnitZ);
            float r = _view != null ? _view.Radius : 1f;
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45f), aspect, MathF.Max(1e-3f, r * 0.01f), r * 100f);

            _view?.Draw(view, proj);
        }
    }
}
