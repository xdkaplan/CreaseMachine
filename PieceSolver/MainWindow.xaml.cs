using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using OpenTK.Wpf;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Plankton;
using CreaseMachine;

namespace PieceSolver
{
    public partial class MainWindow : Window
    {
        GLWpfControl _gl;
        MeshView _view;
        MeshView _flatView;          // BFF flat map M', drawn beside M (offset in +X) on the z=0 plane
        PlanktonMesh _flat;          // the live flat map M' the isometric solver mutates (retained from ShowFlat)
        CreaseMachine.Vec3[] _M0;    // original M positions, captured when the flat first appears (proximity anchor)
        double _lastEIso;            // last E_iso (edge-length-squared mismatch) for the convergence readout
        double _lmLambda;            // Levenberg-Marquardt damping; persists across ticks (trust region).
        const int LmCgIters = 80;    // matrix-free CG iterations per LM linear solve (truncated/inexact is fine)
        double _refMeanLen2;         // mean squared edge length of the ORIGINAL mesh, frozen at first flatten
        double _isoResFactor = 1.0;  // resolution compensation on wIso: (_refMeanLen2 / current meanLen2)^1.5.
                                     // The iso gradient scales ~L^3, so subdivision (edges halve) collapses it
                                     // ~8x and the anchor then dominates, dragging M back toward M0 (relErr
                                     // climbs). This restores the iso<->anchor balance so subdivide is
                                     // resolution-NEUTRAL. ==1.0 at base res (ref==current) -> defaults unchanged.
        bool _hasFlat;               // true after a successful Flatten; cleared on load/reset (stale M')
        bool _bffNeeded = true;      // true => BFF hasn't run for the CURRENT mesh yet. Set on every
                                     // load/reset; cleared the first time BFF flattens this mesh
                                     // (via the Flatten button OR the first PatchStep). The lazy gate.
        bool _meshSwitching;         // re-entrancy guard while MeshIndex programmatically resets the app
        GroundGrid _grid;            // subtle dot grid on the world Z=0 plane (10-unit spacing)
        FlowSession _session;        // live mesh + Nesterov velocity; the flow bakes it in place
        readonly SimSettings _sim = new SimSettings();   // bindable sim params (right toolbar)
        string _meshPath;            // source mesh path, so Reset can reload the input from disk
        bool _glInit, _meshDirty, _reframe, _rulingsDirty;   // _reframe: re-fit camera next upload; _rulingsDirty: recompute ruling overlay
        long _totalIters;
        int _depthRbo, _depthW, _depthH;            // our depth attachment (GLWpfControl FBO is colour-only)

        // Display state (render-only). Facet (smooth<->faceted) is a shader uniform from _sim.Facet.
        string[] _matcapPaths;                      // bundled matcap files (assets/matcaps)
        byte[] _matcapPx; int _matcapW, _matcapH;   // pending matcap pixels (BGRA, GL row order)
        bool _matcapDirty;                          // re-upload the matcap texture on the next render
        int _neutralLightMc = -1;                   // "Neutral Light" matcap; forced as the base whenever a LIC field is on
        // Shine default-shading pair (neutral light + environment map), blended live by the Shine slider.
        byte[] _neutralPx, _envPx; int _neutralW, _neutralH, _envW, _envH;
        bool _neutralDirty, _envDirty;

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
        DragMode _drag = DragMode.None;   // right-drag = orbit, Shift+right-drag = pan, left-drag = paint (Freeze brush)
        // Freeze / Consolidate brush: paints the per-vertex BrushWeights (deCraze boost) the covariance flow
        // honours, so painted regions consolidate / lock creases when you Propose. One brush; the chassis
        // (pick / preview / stroke / falloff / hotkeys) is ported from studio, decoupled from its 14-brush zoo.
        double[] _strokeCov;              // per-vertex coverage this stroke (Flow builds it toward Strength)
        double _dabAccum;                 // screen-px travelled since the last dab (path-spacing accumulator)
        System.Windows.Point _lastHover;  // last hover position, for the footprint preview
        System.Windows.Shapes.Ellipse _previewDot;   // brush-footprint preview overlay
        const double MaxFreeze = 2.0;     // BrushWeights saturation ceiling (matches the documented brush max)

        // async Solve bake: runs on a worker with a modal progress+cancel overlay (replaces the old
        // hold-Space live-step path). The worker does pure compute on the managed mesh; all GL/UI stays
        // on the UI thread, and viewport uploads are gated on !_baking so they never read a mutating mesh.
        bool _baking;
        CancellationTokenSource _bakeCts;
        CancellationToken _bakeToken;
        IProgress<(double frac, string text)> _bakeProgress;

        // Crease proposer: cached per-edge settled fold angles + endpoint vertex indices from the last
        // Propose, so the Crease angle slider re-labels the overlay without re-proposing. The Propose
        // bake reuses the same modal machinery (_baking / _bakeCts / BakeOverlay).
        double[] _creaseFold; int[] _creaseA, _creaseB;
        float[] _creasePts; bool _creaseDirty; int _creaseCount;
        double _bakeStrain = double.NaN;     // worst/final strain % the bake reached (UI reads after)
        string _bakeSummary = "";            // title body the bake produced
        readonly System.Collections.Generic.List<string> _bakeLog = new System.Collections.Generic.List<string>();   // worker log lines, flushed on the UI thread

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

            // Brush-footprint preview: a circle over the viewport on hover (Freeze brush only), hidden on
            // drag / when the cursor leaves. IsHitTestVisible=false so it passes clicks through to the GL view.
            _previewDot = new System.Windows.Shapes.Ellipse
            {
                Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 235, 235, 240)),
                StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            CenterHost.Children.Add(_previewDot);
            _gl.MouseLeave += (s, e) => _previewDot.Visibility = Visibility.Collapsed;

            // Load the default mesh as the first journal entry, so recordings are self-contained
            // (they begin with the load). The GL upload itself happens once the context is live.
            // Default = Mesh 1 (NURBS test surface 0.stl), not the bunny. Missing -> status, no crash.
            string def = MeshPath(_sim.MeshIndex);
            if (System.IO.File.Exists(def)) Execute(StudioCommand.Load(def), record: true);
            else Title = "PieceSolver — (no mesh at " + def + ")";

            // DISPLAY tab: bundled matcaps (assets/matcaps, copied next to the exe). Thumbnails feed
            // the switcher ListBox; the selected one is decoded to a GL texture by SelectMatcap. The
            // initial decode is done explicitly here (the SelectionChanged handler is wired below, so
            // setting SelectedIndex now doesn't double-fire). Matcaps are sampled by the view-space
            // normal -> a lit-sphere look that reads orientation.
            string mcDir = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "matcaps");
            _matcapPaths = System.IO.Directory.Exists(mcDir)
                ? System.IO.Directory.GetFiles(mcDir, "*.png") : Array.Empty<string>();
            Array.Sort(_matcapPaths, StringComparer.OrdinalIgnoreCase);
            // "Neutral Light" matcap (CCC5C9_...): a flat, low-chroma lit sphere we force under the LIC
            // grain so the field reads cleanly. Resolved by filename so the sort order can't break it.
            _neutralLightMc = Array.FindIndex(_matcapPaths, p =>
                System.IO.Path.GetFileName(p).StartsWith("CCC5C9", StringComparison.OrdinalIgnoreCase));
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
            if (_matcapPaths.Length > 0) { int defMc = System.Math.Min(2, _matcapPaths.Length - 1); MatcapList.SelectedIndex = defMc; ApplyMatcap(defMc); }   // picked matcap (only shown under Use Matcap)

            // Default shading = a fixed neutral-lighting matcap + an environment matcap, blended live by
            // the Shine slider (the picker above is only consulted when Use Matcap is on). Stage both for
            // GL-thread upload.
            StageShadingMatcap("CCC5C9_3B2B2B_67585B", isEnv: false);   // neutral lighting (soft near-white)
            StageShadingMatcap("54584E_B1BAC5_818B91", isEnv: true);    // sky / landscape environment map

            // Top-bar actions route through Execute() so each is recorded to the session journal.
            // Solve is the studio's main develop; it records a full BakeParams snapshot so a recorded
            // session reproduces the bake on replay (and via the headless CLI value-check).
            SolveButton.Click += (s, e) => Execute(StudioCommand.Solve(_sim.ToBakeParams()), record: true);   // async bake: develop-to-accuracy + subdivide, on a worker with a progress+cancel modal
            ProposeButton.Click += (s, e) => { _ = OnProposeAsync(); };   // stage 2: propose piece-boundary creases (no-collapse flow, reuses the modal)
            BakeCancel.Click += (s, e) => _bakeCts?.Cancel();
            ResetButton.Click += (s, e) => Execute(StudioCommand.Reset(), record: true);
            // A/B/C developability presets: set the iso weights live (sliders update via binding). Tip:
            // click a preset, Ctrl+R to start clean, then Solve — repeat for each to compare.
            PresetAButton.Click += (s, e) => _sim.ApplyPreset('A');
            PresetBButton.Click += (s, e) => _sim.ApplyPreset('B');
            PresetCButton.Click += (s, e) => _sim.ApplyPreset('C');
            PresetDButton.Click += (s, e) => _sim.ApplyPreset('D');
            // recompute the ruling overlay when its toggle flips (so it appears without needing a solve)
            _sim.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(SimSettings.ShowRuling))
                {
                    _rulingsDirty = true;
                    // When the ruling overlay is switched on, force the Neutral Light matcap as the base
                    // beneath the grain (MVP: no restore-on-off logic yet, by request).
                    if (_sim.ShowRuling && _neutralLightMc >= 0) ApplyMatcap(_neutralLightMc);
                    _gl?.InvalidateVisual();
                }
            };

            // Collapse chevron at each panel's inner-top corner toggles collapse/expand.
            LeftCollapseBtn.Click += (s, e) => ToggleCollapse(LeftCol, ref _leftRestore);
            RightCollapseBtn.Click += (s, e) => ToggleCollapse(RightCol, ref _rightRestore);
            // Dragging a splitter below the threshold collapses that panel (remembering the width it
            // had before the drag, to restore to). Above the threshold, drag just resizes normally.
            LeftSplitter.PreviewMouseLeftButtonDown += (s, e) => _preDragWidth = LeftCol.ActualWidth;
            LeftSplitter.PreviewMouseLeftButtonUp += (s, e) => AfterSplitterDrag(LeftCol, ref _leftRestore);
            RightSplitter.PreviewMouseLeftButtonDown += (s, e) => _preDragWidth = RightCol.ActualWidth;
            RightSplitter.PreviewMouseLeftButtonUp += (s, e) => AfterSplitterDrag(RightCol, ref _rightRestore);

            // DISPLAY tab: matcap switcher (recorded; _suppressUi stops replay-sync re-recording). The
            // Facet slider binds straight to the view-model; a render is kicked when it changes.
            MatcapList.SelectionChanged += (s, e) => { if (!_suppressUi && MatcapList.SelectedIndex >= 0) Execute(StudioCommand.Matcap(MatcapList.SelectedIndex), record: true); };
            _sim.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SimSettings.Facet) || e.PropertyName == nameof(SimSettings.FacetExp)
                    || e.PropertyName == nameof(SimSettings.Shine) || e.PropertyName == nameof(SimSettings.UseMatcap)) _gl?.InvalidateVisual();
                else if (e.PropertyName == nameof(SimSettings.MeshIndex) || e.PropertyName == nameof(SimSettings.AssetSet)) OnMeshIndexChanged();
                else if (e.PropertyName == nameof(SimSettings.CreaseAngleDeg)) RelabelCreases();   // re-label proposed creases live (no re-propose)
                else if (e.PropertyName == nameof(SimSettings.FixBSplineEdges) || e.PropertyName == nameof(SimSettings.SeamRatio)) { RefreshSeamDisplay(); _gl?.InvalidateVisual(); }
            };

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
                else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { Execute(StudioCommand.Reset(), record: true); e.Handled = true; }
                else if (e.Key == Key.OemCloseBrackets && _sim.FreezeBrush)   // ]  = grow brush  /  Ctrl+Shift = harden
                {
                    if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) HardenBrush();
                    else if (Keyboard.Modifiers == ModifierKeys.None) ResizeBrush(1.2);
                    else return;
                    UpdatePreview(_lastHover); e.Handled = true;
                }
                else if (e.Key == Key.OemOpenBrackets && _sim.FreezeBrush)    // [  = shrink brush  /  Ctrl+Shift = soften
                {
                    if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) SoftenBrush();
                    else if (Keyboard.Modifiers == ModifierKeys.None) ResizeBrush(1.0 / 1.2);
                    else return;
                    UpdatePreview(_lastHover); e.Handled = true;
                }
            };
            // (The old hold-Space live-step path is gone — Solve is now the single develop path, async with
            // a progress+cancel modal. ApplyRun/PatchStep remain only for journal replay of legacy Run(N).)
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
            // For a replayed Solve, apply the recorded BakeParams onto _sim BEFORE the bake launches, so
            // RunBake (which reads _sim live) develops with the captured settings. For a live Solve, _sim
            // is already the source of truth, so this sync is skipped (record:true -> no SyncControls).
            if (c.Kind == CmdKind.Solve && !record) SyncControls(c);

            switch (c.Kind)
            {
                case CmdKind.Load: ApplyLoad(c.Path); break;
                case CmdKind.Run: ApplyRun(c.N, c.P); break;
                case CmdKind.Subdivide: ApplySubdivide(); break;
                case CmdKind.Reset: ApplyReset(); break;
                case CmdKind.Matcap: ApplyMatcap(c.N); break;
                // Launch the async bake (fire-and-forget; _baking flips synchronously before the first
                // await, so the replay loop can wait on it). On a live user click this is the develop.
                case CmdKind.Solve: _ = OnSolveAsync(); break;
            }
            if (record) { _journal.Add(c); Log(c.Serialize()); }
            else if (c.Kind != CmdKind.Solve) SyncControls(c);   // replay only: reflect the replayed command in the controls. On a
                                    // LIVE run the controls are already the source of truth, and
                                    // re-writing them here round-trips params lossily (it collapsed the
                                    // deCraze slider by ×DeCrazeMax every iteration).
        }

        // Reflect a command's state in the input controls without re-recording (_suppressUi guards the
        // control event handlers). Only meaningful during replay; for user actions the control is
        // already in the target state, so these sets are no-ops.
        void SyncControls(StudioCommand c)
        {
            _suppressUi = true;
            try
            {
                if (c.Kind == CmdKind.Matcap && c.N >= 0 && c.N < MatcapList.Items.Count) MatcapList.SelectedIndex = c.N;
                else if (c.Kind == CmdKind.Solve) _sim.ApplyBakeParams(c.B);   // restore the recorded bake settings before the replayed bake runs
                else if (c.Kind == CmdKind.Run)
                {
                    // show the replayed run's parameters on the sliders (cosmetic; the run used c.P).
                    _sim.IterPerRun = c.N; _sim.Step = c.P.Step; _sim.Momentum = c.P.Momentum;
                    _sim.DeCraze = c.P.deCraze / _sim.DeCrazeMax;   // weight -> fraction (inverse of ToFlowParams)
                    _sim.CrazeBandDeg = c.P.CrazeBand * 180.0 / Math.PI;
                    _sim.Sharpness = c.P.Sharpness; _sim.DetMix = c.P.DetMix; _sim.MomFix = c.P.MomFix;
                }
            }
            finally { _suppressUi = false; }
        }

        // ===================== Apply* : the actual side effects (no recording) =====================

        void ApplyLoad(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) { Title = "PieceSolver — missing: " + path; return; }
            try { _session = new FlowSession(MeshIO.Load(path)); _meshPath = path; }
            catch (Exception ex) { Title = "PieceSolver — load failed: " + ex.Message; return; }
            _totalIters = 0;
            _reframe = true;          // new mesh -> re-fit the camera on the next upload
            _meshDirty = true;
            ClearProposedCreases();   // labels reference the prior mesh's vertices
            _hasFlat = false;         // mesh changed -> drop any stale BFF flat map
            _flat = null; _M0 = null; // drop the retained flat map + anchor so neither is reused stale
            _refMeanLen2 = 0; _isoResFactor = 1.0; _lmLambda = 0;   // new mesh -> re-freeze iso scale + LM damping on next flatten
            _bffNeeded = true;        // new mesh -> BFF must (re)run before the sim can step
            Title = "PieceSolver — " + System.IO.Path.GetFileName(path);
            RefreshSeamDisplay();     // show the fitted seam wires immediately if Fix B-spline edges is on
            _gl?.InvalidateVisual();
        }

        // Path to a NURBS test surface by 1-based picker index: Mesh 1 -> 0.stl, Mesh 30 -> 29.stl.
        static string NurbsMeshPath(int meshIndex) => System.IO.Path.Combine(
            @"C:\Repo\xdkaplan\CreaseMachine\test\models\NURBS test surfaces", (meshIndex - 1) + ".stl");

        // Path to a Solid (6-sided) FBX test mesh by 1-based picker index: Mesh 1 -> Solid0.fbx. These
        // currently live where they were dropped (C:\Temp); relocate alongside the NURBS set when permanent.
        static string SolidMeshPath(int meshIndex) => System.IO.Path.Combine(@"C:\Temp", "Solid" + (meshIndex - 1) + ".fbx");

        // "Meshes" set — arbitrary triangle meshes, indexed 1-based by the Mesh slider (out-of-range
        // clamps to the last). Add entries here to grow the set.
        static readonly string[] MeshSetPaths = { @"C:\Temp\Bunny 5k.stl", @"C:\Temp\Unwelded.fbx" };
        static string MeshSetPath(int meshIndex) => MeshSetPaths[System.Math.Clamp(meshIndex - 1, 0, MeshSetPaths.Length - 1)];

        // The active test-mesh path for a picker index, per the Source dropdown (Solids/Surfaces/Meshes).
        string MeshPath(int meshIndex) => _sim.AssetSet switch
        {
            0 => SolidMeshPath(meshIndex),    // Solids — 6-sided FBX
            2 => MeshSetPath(meshIndex),      // Meshes — the bunny (only entry for now)
            _ => NurbsMeshPath(meshIndex),    // Surfaces — NURBS STLs (default)
        };

        // MeshIndex slider changed -> reset the app to that NURBS surface. ApplyLoad already clears
        // _hasFlat and re-arms BFF (_bffNeeded = true). The re-entrancy guard stops a programmatic
        // MeshIndex write (e.g. clamping) from recursing; the slider is snapped to integers so no clamp
        // is needed here, but the guard keeps any future programmatic set from re-firing the load.
        void OnMeshIndexChanged()
        {
            if (_meshSwitching) return;
            _meshSwitching = true;
            try
            {
                string path = MeshPath(_sim.MeshIndex);
                if (System.IO.File.Exists(path)) Execute(StudioCommand.Load(path), record: true);
                else Title = "PieceSolver — (no mesh at " + path + ")";
            }
            finally { _meshSwitching = false; }
        }

        // Run n PieceSolver steps in-process (on the UI thread). This is the PieceSolver paradigm —
        // it does NOT touch the Stein/DetMix developability flow (NesterovStep & friends). The
        // FlowParams `p` is intentionally ignored (kept only so the journal/Run plumbing is untouched
        // this pass). Times the loop for the perf-drift readout.
        void ApplyRun(int n, FlowParams p)
        {
            if (_session == null || n <= 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PatchStep(n);                  // BFF gate (once) + n LM iterations in a single Solve (topology built once)
            sw.Stop();
            _lastRunMs = sw.Elapsed.TotalMilliseconds;
            _totalIters += n;
            _meshDirty = true; _rulingsDirty = true;                    // OnRender re-uploads M (_view) once (+ recompute rulings)
            ClearProposedCreases();                                     // geometry moved -> proposal is stale
            if (_hasFlat && _flatView != null) _flatView.Upload(_flat); // re-upload M' once per batch, not per step
            if (_hasFlat && _flat != null) _sim.StrainPct = RelErr(_session.Mesh, _flat) * 100.0;   // live developability readout (Accuracy gate)
            Title = "PieceSolver — iter " + _totalIters + "  (" + _session.Mesh.Vertices.Count + " verts)";
            UpdateStatus();
            _gl?.InvalidateVisual();
        }

        // One PieceSolver step. Lazy-BFF gate: the first step for a given mesh runs Boundary First
        // Flattening (once), uploads the flat map M' beside M, and arms the sim. Subsequent steps (and
        // any step after a manual Flatten already ran for this mesh) skip straight to IsometricStep.
        // Returns false if BFF was needed but failed (caller should stop the run).
        bool PatchStep(int lmIters)
        {
            if (!EnsureFlat()) return false;   // lazy BFF gate (shared with Solve)
            IsometricStep(lmIters);
            return true;
        }

        // ---- fixed B-spline seam edges (bent-wire pins) ----
        bool[] _pinned;        // per-vertex: true where pinned to a seam wire (Dirichlet during the solve)
        float[] _seamLineF;    // GL_LINES positions for the fitted seam curve (cyan)
        float[] _seamCtrlF;    // GL_LINES positions for the control polygon + control-point crosses (amber)
        bool _seamsDirty;      // seam overlay needs re-upload

        // Fit a low-DOF bent-wire B-spline to each boundary loop, SNAP the boundary vertices onto it, and
        // mark them pinned so the solve holds them fixed; then refresh the overlay so it tracks the wires.
        // Call on the (fresh/subdivided) mesh before flattening, since it moves boundary positions.
        void SetupSeamPins()
        {
            _pinned = null;
            if (_session == null) return;
            var mesh = _session.Mesh;
            var loops = CreaseMachine.MeshOps.BoundaryLoops(mesh);
            if (loops.Count == 0) return;
            int ratio = Math.Max(2, _sim.SeamRatio);
            var pin = new bool[mesh.Vertices.Count];
            foreach (var loop in loops)
            {
                var tgt = CreaseMachine.BSpline.FitClosed(LoopPts(mesh, loop), ratio).EvenTargets(loop.Length);
                for (int i = 0; i < loop.Length; i++)
                {
                    mesh.Vertices[loop[i]].X = (float)tgt[i].X; mesh.Vertices[loop[i]].Y = (float)tgt[i].Y; mesh.Vertices[loop[i]].Z = (float)tgt[i].Z;
                    pin[loop[i]] = true;
                }
            }
            _pinned = pin;
            // (Display overlay is refreshed by the caller on the UI thread — RefreshSeamDisplay — since
            // SetupSeamPins may run on the bake worker, which must not touch the seam display buffers.)
        }

        // Read-only: fit the bent-wire B-splines to the CURRENT boundary loops and build the overlay —
        // the smooth curve (cyan) plus the control polygon + control-point markers (amber). No mesh
        // mutation, so the seam wires are visible immediately on load, before (and without) any Solve.
        void RefreshSeamDisplay()
        {
            _seamLineF = null; _seamCtrlF = null; _seamsDirty = true;
            if (_session == null || !_sim.FixBSplineEdges) return;
            var loops = CreaseMachine.MeshOps.BoundaryLoops(_session.Mesh);
            if (loops.Count == 0) return;
            int ratio = Math.Max(2, _sim.SeamRatio);
            double h = 0.006 * MeshDiag(_session.Mesh);            // control-point marker half-size
            var curve = new System.Collections.Generic.List<float>();
            var ctrl = new System.Collections.Generic.List<float>();
            foreach (var loop in loops)
            {
                var bs = CreaseMachine.BSpline.FitClosed(LoopPts(_session.Mesh, loop), ratio);
                AddPolyline(curve, bs.SampleCurve(8), true);       // the smooth degree-3 curve
                AddPolyline(ctrl, bs.Control, true);               // the control polygon
                foreach (var cp in bs.Control) AddCross(ctrl, cp, h);   // control points
            }
            _seamLineF = curve.ToArray(); _seamCtrlF = ctrl.ToArray();
        }

        static CreaseMachine.Vec3[] LoopPts(PlanktonMesh mesh, int[] loop)
        {
            var pts = new CreaseMachine.Vec3[loop.Length];
            for (int i = 0; i < loop.Length; i++) { var p = mesh.Vertices[loop[i]]; pts[i] = new CreaseMachine.Vec3(p.X, p.Y, p.Z); }
            return pts;
        }
        static double MeshDiag(PlanktonMesh m)
        {
            if (m.Vertices.Count == 0) return 1.0;
            var a = m.Vertices[0]; double lx = a.X, ly = a.Y, lz = a.Z, hx = a.X, hy = a.Y, hz = a.Z;
            for (int v = 1; v < m.Vertices.Count; v++) { var p = m.Vertices[v]; lx = Math.Min(lx, p.X); ly = Math.Min(ly, p.Y); lz = Math.Min(lz, p.Z); hx = Math.Max(hx, p.X); hy = Math.Max(hy, p.Y); hz = Math.Max(hz, p.Z); }
            double dx = hx - lx, dy = hy - ly, dz = hz - lz; return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        // Append a polyline as GL_LINES segment pairs (closed -> also connect last back to first).
        static void AddPolyline(System.Collections.Generic.List<float> seg, CreaseMachine.Vec3[] pts, bool closed)
        {
            int n = pts.Length; if (n < 2) return; int last = closed ? n : n - 1;
            for (int i = 0; i < last; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % n];
                seg.Add((float)a.X); seg.Add((float)a.Y); seg.Add((float)a.Z);
                seg.Add((float)b.X); seg.Add((float)b.Y); seg.Add((float)b.Z);
            }
        }
        // Small 3D cross at p (half-size h) as 3 GL_LINES segments.
        static void AddCross(System.Collections.Generic.List<float> seg, CreaseMachine.Vec3 p, double h)
        {
            float x = (float)p.X, y = (float)p.Y, z = (float)p.Z, hf = (float)h;
            seg.Add(x - hf); seg.Add(y); seg.Add(z); seg.Add(x + hf); seg.Add(y); seg.Add(z);
            seg.Add(x); seg.Add(y - hf); seg.Add(z); seg.Add(x); seg.Add(y + hf); seg.Add(z);
            seg.Add(x); seg.Add(y); seg.Add(z - hf); seg.Add(x); seg.Add(y); seg.Add(z + hf);
        }

        // ===================== Solve (bake) =====================

        // Develop to the selected Accuracy, then subdivide + re-develop for each Subdivision level. Resets to
        // the input first (idempotent). Synchronous, no live animation; bounded by a wall-clock budget plus
        // per-round stall detection. Shows the final result + strain.
        // Bake (develop-to-accuracy + subdivide) on a worker, behind a modal progress+cancel overlay. The
        // worker (RunBake) is PURE compute on the managed mesh — no GL, no UI-bound property writes, no
        // dirty-flag sets. All GL/UI happens here on the UI thread; viewport uploads are gated on !_baking
        // so they never read the mesh while the worker is mutating it.
        async Task OnSolveAsync()
        {
            if (_baking || _session == null || _meshPath == null) return;
            ApplyReset();                          // UI thread: reload the input mesh + reset flags
            _view?.Upload(_session.Mesh);          // show the reset mesh, then clear dirty so the bake's mutations aren't uploaded mid-flight
            _meshDirty = false;

            _baking = true;
            _bakeCts = new CancellationTokenSource();
            _bakeToken = _bakeCts.Token;
            _bakeLog.Clear(); _bakeStrain = double.NaN; _bakeSummary = "";
            BakeBar.Value = 0; BakeStatus.Text = "Solving…"; BakeOverlay.Visibility = Visibility.Visible;
            _bakeProgress = new Progress<(double frac, string text)>(p =>
            {
                if (p.frac >= 0) BakeBar.Value = Math.Max(0, Math.Min(100, p.frac * 100.0));
                if (p.text != null) BakeStatus.Text = p.text;
            });

            try { await Task.Run(RunBake); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _bakeLog.Add("solve failed: " + ex.Message); }
            finally
            {
                _baking = false;
                BakeOverlay.Visibility = Visibility.Collapsed;
                foreach (var line in _bakeLog) Log(line);
                if (_view != null) _view.Upload(_session.Mesh);
                if (_hasFlat && _flatView != null && _flat != null) { _flatView.Upload(_flat); PlaceFlat(); }
                _meshDirty = false; _rulingsDirty = true;     // mesh just uploaded; recompute rulings if shown
                RefreshSeamDisplay();
                if (!double.IsNaN(_bakeStrain)) _sim.StrainPct = _bakeStrain;
                Title = "PieceSolver — " + (_bakeToken.IsCancellationRequested ? "cancelled · " : "") + _bakeSummary;
                UpdateStatus(); _gl?.InvalidateVisual();
                _bakeCts?.Dispose(); _bakeCts = null; _bakeProgress = null;
            }
        }

        // ===================== crease proposer (stage 2: "Propose creases") =====================

        // Develop a copy of the geometry to a settled (developable) state behind the bake modal —
        // WITHOUT collapse/heal/subdivide, so topology is preserved and the settled mesh's edges map
        // 1:1 back to the original vertices. We flow IN PLACE (snapshot positions + Nesterov velocity,
        // flow, measure per-edge fold angles, restore), so there is no clone. The fold angles are cached;
        // the Crease angle slider then labels edges >= threshold as proposed piece boundaries. NOT the
        // IsometricLM develop bake (that is "Solve") — this is the covariance flow used only to sniff creases.
        async Task OnProposeAsync()
        {
            if (_baking || _session == null) return;
            FlowParams p = _sim.ToFlowParams();
            PlanktonMesh P = _session.Mesh;
            int nV = P.Vertices.Count;
            var sx = new double[nV]; var sy = new double[nV]; var sz = new double[nV];
            for (int v = 0; v < nV; v++) { var pv = P.Vertices[v]; sx[v] = pv.X; sy[v] = pv.Y; sz[v] = pv.Z; }
            Vec3[] savedVel = (Vec3[])_session.Vel.Clone();

            _baking = true;
            _bakeCts = new CancellationTokenSource();
            _bakeToken = _bakeCts.Token;
            BakeBar.Value = 0; BakeStatus.Text = "Proposing creases…"; BakeOverlay.Visibility = Visibility.Visible;
            _bakeProgress = new Progress<(double frac, string text)>(pp =>
            {
                if (pp.frac >= 0) BakeBar.Value = Math.Max(0, Math.Min(100, pp.frac * 100.0));
                if (pp.text != null) BakeStatus.Text = pp.text;
            });
            var token = _bakeToken; var prog = _bakeProgress;

            double[] fold = null; int[] ea = null, eb = null;
            try
            {
                await Task.Run(() =>
                {
                    const int maxIter = 1200;
                    double g0 = -1.0;
                    for (int i = 0; i < maxIter; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        double g = _session.NesterovStep(p, out _);   // NO collapse/heal -> topology fixed
                        if (g0 < 0) { g0 = g; if (g0 <= 1e-12) break; }   // already developable
                        if ((i & 15) == 0) prog?.Report(((double)i / maxIter, $"flowing… iter {i}   |grad| {g:0.###e0}"));
                        if (g0 > 0 && g < 1e-3 * g0) break;   // settled
                    }
                    fold = CreaseMachine.MeshOps.EdgeDihedrals(_session.Mesh, out ea, out eb);
                });
            }
            catch (OperationCanceledException) { fold = null; }
            catch (Exception ex) { Log("propose failed: " + ex.Message); }
            finally
            {
                // Restore the original geometry + momentum: the viewport returns to the input mesh, and
                // the cached fold labels reference exactly these (unrenumbered) vertices.
                for (int v = 0; v < nV; v++) P.Vertices.SetVertex(v, sx[v], sy[v], sz[v]);
                _session.Vel = savedVel;
                _baking = false;
                BakeOverlay.Visibility = Visibility.Collapsed;
                if (fold != null)
                {
                    _creaseFold = fold; _creaseA = ea; _creaseB = eb;
                    RelabelCreases();
                    Log($"crease proposer: scanned {fold.Length} interior edges, {_creaseCount} proposed at >= {_sim.CreaseAngleDeg:0.#} deg");
                }
                if (_view != null) _view.Upload(_session.Mesh);   // re-show the restored input mesh
                _meshDirty = false;
                Title = "PieceSolver — " + (token.IsCancellationRequested ? "propose cancelled" : _creaseCount + " creases proposed");
                _gl?.InvalidateVisual();
                _bakeCts?.Dispose(); _bakeCts = null; _bakeProgress = null;
            }
        }

        // Rebuild the crease overlay from the cached fold angles at the current Crease angle threshold.
        // CPU-only (stages the line vertices; the GL upload happens in OnRender), so it runs live on the
        // slider. No cached angles -> clears the overlay.
        void RelabelCreases()
        {
            if (_creaseFold == null || _session == null)
            {
                _creaseCount = 0; _creasePts = System.Array.Empty<float>(); _creaseDirty = true; _gl?.InvalidateVisual();
                return;
            }
            double thr = _sim.CreaseAngleDeg * Math.PI / 180.0;
            PlanktonMesh P = _session.Mesh;
            int nV = P.Vertices.Count;
            var pts = new System.Collections.Generic.List<float>();
            int n = 0;
            for (int i = 0; i < _creaseFold.Length; i++)
            {
                if (_creaseFold[i] < thr) continue;
                int a = _creaseA[i], b = _creaseB[i];
                if (a < 0 || b < 0 || a >= nV || b >= nV) continue;
                if (P.Vertices[a].IsUnused || P.Vertices[b].IsUnused) continue;
                var pa = P.Vertices[a]; var pb = P.Vertices[b];
                pts.Add((float)pa.X); pts.Add((float)pa.Y); pts.Add((float)pa.Z);
                pts.Add((float)pb.X); pts.Add((float)pb.Y); pts.Add((float)pb.Z);
                n++;
            }
            _creasePts = pts.ToArray(); _creaseCount = n; _creaseDirty = true;
            _gl?.InvalidateVisual();
        }

        // Drop the cached crease proposal + overlay (fresh mesh, or topology/geometry changed). Idempotent.
        void ClearProposedCreases()
        {
            if (_creaseFold == null && (_creasePts == null || _creasePts.Length == 0)) return;
            _creaseFold = null; _creaseA = null; _creaseB = null;
            _creaseCount = 0; _creasePts = System.Array.Empty<float>(); _creaseDirty = true;
        }

        // Worker entry (background thread): pure compute, dispatched single vs multi-component.
        void RunBake()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const long budgetMs = 10000;
            double target = _sim.AccuracyStrainPct;
            if (CreaseMachine.MeshOps.ComponentCount(_session.Mesh) > 1) RunBakeMulti(sw, budgetMs, target);
            else RunBakeSingle(sw, budgetMs, target);
        }

        // Single-component bake (NURBS surface): flatten once, develop to accuracy, subdivide + re-develop.
        void RunBakeSingle(System.Diagnostics.Stopwatch sw, long budgetMs, double target)
        {
            if (_sim.FixBSplineEdges) SetupSeamPins();
            if (!FlattenPure()) { _bakeSummary = "BFF failed"; return; }
            SolveToAccuracy(target, sw, budgetMs);
            int levels = _sim.SubdivLevel;
            for (int lvl = 0; lvl < levels && sw.ElapsedMilliseconds < budgetMs && !_bakeToken.IsCancellationRequested; lvl++)
            {
                SubdivideCompute();
                if (_sim.FixBSplineEdges) SetupSeamPins();
                SolveToAccuracy(target, sw, budgetMs);
                _bakeProgress?.Report(((lvl + 2.0) / (levels + 1.0), "subdiv " + (lvl + 1) + "/" + levels));
            }
            double pct = (_hasFlat && _flat != null) ? RelErr(_session.Mesh, _flat) * 100.0 : double.NaN;
            _bakeStrain = pct;
            _bakeSummary = (double.IsNaN(pct) ? "solved" : "solved " + pct.ToString("0.###") + "%") + "  (" + _session.Mesh.Vertices.Count + " verts)";
        }

        // Worker-safe flatten: BFF the live mesh into _flat + the anchor M0; NO GL upload (the UI thread
        // uploads after the bake). Mirrors EnsureFlat minus ShowFlat's GPU work.
        bool FlattenPure()
        {
            if (_session == null) return false;
            if (_bffNeeded)
            {
                if (!Bff.TryFlatten(_session.Mesh, out var flat, out var log)) { if (!string.IsNullOrWhiteSpace(log)) _bakeLog.Add(log.TrimEnd()); return false; }
                _flat = flat; CaptureM0(); _hasFlat = true; _bffNeeded = false;
            }
            return _flat != null && _M0 != null;
        }

        // ===================== multi-piece (per-component) solve =====================

        // Solve a multi-component mesh (e.g. an FBX solid -> one patch per face): flatten + develop each
        // component independently, then reassemble in place. Each piece's boundary is FROZEN (Dirichlet)
        // during its develop, so the solid stays joined at the shared seams while each face's interior
        // flattens. (Smoothing / matching the seams to a shared B-spline is a later step.)
        void RunBakeMulti(System.Diagnostics.Stopwatch sw, long budgetMs, double target)
        {
            double worst = 0; int pieces = 0, flattened = 0; int levels = _sim.SubdivLevel;
            for (int lvl = 0; lvl <= levels && sw.ElapsedMilliseconds < budgetMs && !_bakeToken.IsCancellationRequested; lvl++)
            {
                if (lvl > 0) SubdivideCompute();
                var comps = CreaseMachine.MeshOps.SplitComponents(_session.Mesh, out var vmaps);
                pieces = comps.Count; flattened = 0; worst = 0;
                var flatCombined = TopologyClone(_session.Mesh);
                double xoff = 0;
                for (int c = 0; c < comps.Count && sw.ElapsedMilliseconds < budgetMs && !_bakeToken.IsCancellationRequested; c++)
                {
                    var pc = comps[c];
                    if (!Bff.TryFlatten(pc, out var flat, out var log)) { if (!string.IsNullOrWhiteSpace(log)) _bakeLog.Add("panel " + c + ": " + log.TrimEnd()); continue; }
                    DevelopPiece(pc, flat, target, sw, budgetMs);
                    var vmap = vmaps[c];
                    for (int v = 0; v < vmap.Length; v++) { var p = pc.Vertices[v]; _session.Mesh.Vertices[vmap[v]].X = p.X; _session.Mesh.Vertices[vmap[v]].Y = p.Y; _session.Mesh.Vertices[vmap[v]].Z = p.Z; }
                    xoff = PlaceFlatPiece(flat, flatCombined, vmap, xoff);
                    double e = RelErr(pc, flat); if (e > worst) worst = e; flattened++;
                    _bakeProgress?.Report(((lvl + (c + 1.0) / comps.Count) / (levels + 1.0),
                        "panel " + (c + 1) + "/" + comps.Count + (levels > 0 ? "  ·  subdiv " + lvl + "/" + levels : "") + "  ·  worst " + (worst * 100.0).ToString("0.##") + "%"));
                }
                _flat = flatCombined; _hasFlat = true; _bffNeeded = false;
            }
            _bakeStrain = worst * 100.0;
            _bakeSummary = flattened + "/" + pieces + " panels  worst strain " + (worst * 100.0).ToString("0.###") + "%  (" + _session.Mesh.Vertices.Count + " verts)";
        }

        // Develop one component to the accuracy target with its boundary FROZEN (Dirichlet) so it stays
        // joined to its neighbours. BFF (the flat) is already computed by the caller.
        void DevelopPiece(PlanktonMesh pc, PlanktonMesh flat, double target, System.Diagnostics.Stopwatch sw, long budgetMs)
        {
            var pin = CreaseMachine.MeshOps.BoundaryVertexMask(pc);   // freeze the seam boundary in place
            var m0 = new CreaseMachine.Vec3[pc.Vertices.Count];
            for (int v = 0; v < m0.Length; v++) { var p = pc.Vertices[v]; m0[v] = new CreaseMachine.Vec3(p.X, p.Y, p.Z); }
            double lam = 0, prev = double.MaxValue;
            for (int it = 0; it < 800 && sw.ElapsedMilliseconds < budgetMs && !_bakeToken.IsCancellationRequested; it++)
            {
                IsometricLM.Solve(pc, flat, m0, _sim.IsoWeight, _sim.FairWeight, _sim.AnchorWeight, _sim.ScaleWeight, _sim.DiffFair, _sim.BendWeight, _sim.BendDiff, 4, LmCgIters, ref lam, pin);
                double pct = RelErr(pc, flat) * 100.0;
                if (pct <= target) break;
                if (prev - pct < 1e-5) break;
                prev = pct;
            }
        }

        // Clone a mesh's topology (positions copied, then overwritten per piece).
        static PlanktonMesh TopologyClone(PlanktonMesh M)
        {
            var c = new PlanktonMesh();
            for (int v = 0; v < M.Vertices.Count; v++) { var p = M.Vertices[v]; c.Vertices.Add(p.X, p.Y, p.Z); }
            for (int f = 0; f < M.Faces.Count; f++) { if (M.Faces[f].IsUnused) continue; var fv = M.Faces.GetFaceVertices(f); if (fv.Length >= 3) c.Faces.AddFace(fv); }
            return c;
        }

        // Lay a piece's flat (BFF output on z=0) at the running x-offset and write it into the combined
        // flat at the piece's global vertex indices. Panels in a row with a gap. Returns the next offset.
        double PlaceFlatPiece(PlanktonMesh flat, PlanktonMesh flatCombined, int[] vmap, double xoff)
        {
            double minx = double.MaxValue, maxx = double.MinValue, miny = double.MaxValue;
            for (int v = 0; v < flat.Vertices.Count; v++) { var p = flat.Vertices[v]; if (p.X < minx) minx = p.X; if (p.X > maxx) maxx = p.X; if (p.Y < miny) miny = p.Y; }
            double dx = xoff - minx, dy = -miny;
            int n = Math.Min(vmap.Length, flat.Vertices.Count);
            for (int v = 0; v < n; v++) { var p = flat.Vertices[v]; flatCombined.Vertices[vmap[v]].X = (float)(p.X + dx); flatCombined.Vertices[vmap[v]].Y = (float)(p.Y + dy); flatCombined.Vertices[vmap[v]].Z = 0f; }
            double w = maxx - minx; return xoff + w + 0.15 * (w + 1e-6);
        }

        // LM iterations until strain (relErr) <= targetPct, convergence (no progress), or the time budget.
        void SolveToAccuracy(double targetPct, System.Diagnostics.Stopwatch sw, long budgetMs)
        {
            if (_flat == null || _M0 == null) return;
            double prev = double.MaxValue;
            for (int it = 0; it < 800 && sw.ElapsedMilliseconds < budgetMs && !_bakeToken.IsCancellationRequested; it++)
            {
                _lastEIso = IsometricLM.Solve(_session.Mesh, _flat, _M0,
                    _sim.IsoWeight * _isoResFactor, _sim.FairWeight, _sim.AnchorWeight, _sim.ScaleWeight,
                    _sim.DiffFair, _sim.BendWeight, _sim.BendDiff, 4, LmCgIters, ref _lmLambda,
                    _sim.FixBSplineEdges ? _pinned : null);
                double pct = RelErr(_session.Mesh, _flat) * 100.0;
                if ((it & 7) == 0) _bakeProgress?.Report((-1.0, "developing… strain " + pct.ToString("0.###") + "%  (target " + targetPct.ToString("0.###") + "%)"));
                if (pct <= targetPct) break;            // reached the accuracy bar
                if (prev - pct < 1e-5) break;           // converged (no further progress) -> subdivide may help
                prev = pct;
            }
        }

        // `lmIters` Levenberg-Marquardt iterations co-refining M (3D) and its flat image M' toward
        // isometry (= developability) over the index-aligned M <-> M' correspondence. LM solves a damped
        // normal-equation system each iteration (matrix-free CG), so it computes the step direction AND
        // size (no Step knob) and auto-adapts its damping (trust region) — driving relErr -> 0 and staying
        // robust to weight scale where the explicit Jacobi step diverged. The weights are the objective:
        // Iso (developability) vs Anchor (faithfulness) vs Fair (smoothness). lambda persists across calls.
        // No GPU re-upload here — ApplyRun re-uploads M and M' ONCE after the batch.
        void IsometricStep(int lmIters)
        {
            if (_session == null || _flat == null || _M0 == null) return;
            _lastEIso = IsometricLM.Solve(_session.Mesh, _flat, _M0,
                          _sim.IsoWeight * _isoResFactor, _sim.FairWeight, _sim.AnchorWeight, _sim.ScaleWeight, _sim.DiffFair, _sim.BendWeight, _sim.BendDiff,
                          lmIters, LmCgIters, ref _lmLambda, _sim.FixBSplineEdges ? _pinned : null);
            // optional non-shrinking smoother (strain distributor / de-buckler) after the LM step
            var sm = (IsometricSmoothers.Kind)_sim.SmoothKind;
            if (sm != IsometricSmoothers.Kind.None)
            {
                IsometricSmoothers.Apply(sm, _session.Mesh, _sim.SmoothStrength, 2, false);
                IsometricSmoothers.Apply(sm, _flat, _sim.SmoothStrength, 2, true);
            }
        }

        // One 1->4 subdivision of the live mesh. Geometry-preserving (midpoints are linear). When a
        // flat map exists we refine M, M' AND the anchor M0 with the SAME deterministic midpoint
        // scheme on the SAME connectivity, so (a) the M'[i]==M[i] index alignment the solver depends
        // on survives, and (b) M0 stays the ORIGINAL-shape reference at the new resolution (not a
        // re-anchor onto the already-deformed M). Then the solver keeps developing at higher res
        // (paper: subdivide after the flow settles, then keep flowing to sharpen at hi-res). Camera
        // unchanged (bounds are identical — midpoints lie on existing edges).
        // Worker-safe subdivision: refine M (and the aligned M'/anchor), NO GL/UI. ApplySubdivide = this + upload.
        void SubdivideCompute()
        {
            if (_session == null) return;
            bool hadFlat = _flat != null && _M0 != null && _M0.Length == _session.Mesh.Vertices.Count;
            PlanktonMesh m0mesh = null;
            if (hadFlat)
            {
                // carry the anchor positions on the CURRENT (pre-subdivide) connectivity so that
                // subdividing interpolates its midpoints exactly as M and M' get interpolated.
                m0mesh = new PlanktonMesh();
                for (int v = 0; v < _M0.Length; v++) m0mesh.Vertices.Add(_M0[v].X, _M0[v].Y, _M0[v].Z);
                for (int f = 0; f < _session.Mesh.Faces.Count; f++)
                {
                    if (_session.Mesh.Faces[f].IsUnused) continue;
                    int[] fv = _session.Mesh.Faces.GetFaceVertices(f);
                    if (fv.Length == 3) m0mesh.Faces.AddFace(fv);
                }
            }

            _session.Subdivide();                            // refine M
            if (hadFlat)
            {
                _flat = MeshOps.UniformSubdivide(_flat);     // refine M' identically -> alignment preserved
                m0mesh = MeshOps.UniformSubdivide(m0mesh);   // refine the anchor identically
                var verts = m0mesh.Vertices; int n = verts.Count;
                var m0 = new CreaseMachine.Vec3[n];
                for (int v = 0; v < n; v++) { var p = verts[v]; m0[v] = new CreaseMachine.Vec3(p.X, p.Y, p.Z); }
                _M0 = m0;
                // restore the iso<->anchor balance at the finer mesh: the iso gradient scales ~L^3, so
                // compensate wIso by (originalScale / currentScale)^1.5. Frozen ref -> base res stays ==1.
                double cur = MeanLen2(_session.Mesh);
                _isoResFactor = (_refMeanLen2 > 0 && cur > 0) ? Math.Pow(_refMeanLen2 / cur, 1.5) : 1.0;
                _lmLambda = 0;                               // resolution changed -> fresh LM trust region
            }
        }

        void ApplySubdivide()
        {
            if (_session == null) return;
            SubdivideCompute();
            bool aligned = _flat != null && _M0 != null && _M0.Length == _session.Mesh.Vertices.Count;
            if (aligned && _flatView != null)
            {
                _flatView.Upload(_flat);                                  // re-upload the refined M'
                _sim.StrainPct = RelErr(_session.Mesh, _flat) * 100.0;    // strain stays ~constant (subdivide is metric-preserving)
            }
            _meshDirty = true;
            ClearProposedCreases();   // 1->4 renumbers vertices -> labels invalid
            Title = "PieceSolver — subdivided  (" + _session.Mesh.Vertices.Count + " verts)";
            _gl?.InvalidateVisual();
        }

        // Reload the current mesh from disk and reset the flow. Same bounds -> camera kept (no reframe).
        void ApplyReset()
        {
            if (_meshPath == null || !System.IO.File.Exists(_meshPath)) return;
            try { _session = new FlowSession(MeshIO.Load(_meshPath)); }
            catch (Exception ex) { Title = "PieceSolver — reset failed: " + ex.Message; return; }
            _totalIters = 0;
            _meshDirty = true;
            _hasFlat = false;         // mesh changed -> drop any stale BFF flat map
            _flat = null; _M0 = null; // drop the retained flat map + anchor so neither is reused stale
            _refMeanLen2 = 0; _isoResFactor = 1.0; _lmLambda = 0;   // reset -> re-freeze iso scale + LM damping on next flatten
            _bffNeeded = true;        // reset -> BFF must (re)run before the sim can step
            ClearProposedCreases();   // fresh mesh from disk
            Title = "PieceSolver — " + System.IO.Path.GetFileName(_meshPath);
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

        // Find a bundled matcap by filename prefix and stage its pixels for the default shading's neutral
        // or environment slot (uploaded on the GL thread, like the picked matcap). Missing file -> the slot
        // stays empty and the shader falls back (no crash).
        void StageShadingMatcap(string namePrefix, bool isEnv)
        {
            if (_matcapPaths == null) return;
            foreach (var path in _matcapPaths)
            {
                if (!System.IO.Path.GetFileName(path).StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var px = DecodeMatcapBgra(path, out int w, out int h);
                    if (isEnv) { _envPx = px; _envW = w; _envH = h; _envDirty = true; }
                    else { _neutralPx = px; _neutralW = w; _neutralH = h; _neutralDirty = true; }
                    _gl?.InvalidateVisual();
                }
                catch { }
                return;
            }
        }

        // ===================== Flatten (BFF) — direct handler, not journaled =====================

        // Run Boundary First Flattening on the live mesh and show the flat map M' beside M. BFF returns
        // an OBJ already on the XY plane (z=0) with the SAME vertex/face count + ordering as the input,
        // so M'[i] corresponds to M[i]. We offset M' in +X so it sits on the ground plane next to M.
        // BFF gate: run Boundary First Flattening once for the current mesh (lazy), show M', arm the solver.
        // Returns true once a usable flat map + anchor exist. Shared by spacebar (PatchStep) and Solve.
        bool EnsureFlat()
        {
            if (_session == null) return false;
            if (_bffNeeded)
            {
                bool ok = Bff.TryFlatten(_session.Mesh, out var flat, out var log);
                if (!string.IsNullOrWhiteSpace(log)) Log(log.TrimEnd());
                if (!ok || flat == null) return false;   // failure logged; don't start the sim
                ShowFlat(flat);
                _bffNeeded = false;
            }
            return _flat != null && _M0 != null;
        }

        // Upload the BFF flat map M' and place it beside M on the z=0 plane (to +X of M by both radii
        // plus a gap). Shared by the Flatten button and PatchStep's lazy first flatten. Sets _hasFlat.
        void ShowFlat(PlanktonMesh flat)
        {
            if (_view == null || _flatView == null) return;   // GL views not built yet (pre-first-render)
            _flat = flat;             // retain M' — the actual mesh the isometric solver mutates
            CaptureM0();              // snapshot M's positions once per mesh (proximity anchor for the solver)
            _flatView.Upload(flat);   // sets _flatView.Center / .Radius from the flat geometry
            PlaceFlat();              // place M' beside M on the z=0 plane

            _hasFlat = true;
            // NOTE: intentionally no camera reframe here — keep the current view when the 2D pattern
            // appears (M' is placed beside M; the user can zoom-extents manually if they want it framed).
            _gl?.InvalidateVisual();
        }

        // Place the flat map M' beside M on the z=0 plane (to +X of M by both radii plus a gap). Called
        // from ShowFlat (first flatten) and IsometricStep (after M' deforms) so M' tracks alongside M.
        void PlaceFlat()
        {
            if (_view == null || _flatView == null) return;
            float gap = 0.3f * (_view.Radius + _flatView.Radius);
            var targetCenter = new Vector3(
                _view.Center.X + _view.Radius + _flatView.Radius + gap,
                _view.Center.Y,
                0f);
            _flatView.ModelOffset = targetCenter - _flatView.Center;
        }

        // Snapshot the live 3D mesh M's current positions as the proximity anchor M0, ONCE per mesh
        // (when the flat first appears). Without an anchor the trivial isometry minimizer is M
        // collapsing flat onto M'. Guarded so a later re-Flatten on the same mesh doesn't re-anchor to
        // an already-deformed M; _M0 is nulled on load/reset so a stale anchor is never reused.
        void CaptureM0()
        {
            if (_M0 != null || _session == null) return;
            var verts = _session.Mesh.Vertices;
            int n = verts.Count;
            var m0 = new CreaseMachine.Vec3[n];
            for (int v = 0; v < n; v++)
            {
                var p = verts[v];
                m0[v] = new CreaseMachine.Vec3(p.X, p.Y, p.Z);
            }
            _M0 = m0;
            _refMeanLen2 = MeanLen2(_session.Mesh);   // freeze the original edge scale for iso resolution compensation
            _isoResFactor = 1.0;                      // base resolution -> no compensation yet
            _lmLambda = 0;                            // fresh LM trust region for this mesh
        }

        // Mean squared edge length of a mesh (Sum |edge|^2 / edgeCount). Used to freeze the original
        // scale (_refMeanLen2) and to size the post-subdivide iso compensation factor.
        static double MeanLen2(PlanktonMesh M)
        {
            double s = 0; int n = 0, nH = M.Halfedges.Count;
            for (int h = 0; h < nH; h += 2)
            {
                if (M.Halfedges[h].IsUnused) continue;
                int i = M.Halfedges[h].StartVertex, j = M.Halfedges[h + 1].StartVertex;
                var a = M.Vertices[i]; var b = M.Vertices[j];
                double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                s += dx * dx + dy * dy + dz * dz; n++;
            }
            return n > 0 ? s / n : 0.0;
        }

        // Relative L2 edge-length error between M and its flat image M' = the in-plane STRAIN (the paper's
        // developability metric, ‖L'−L‖/‖L‖). Drives the live Accuracy/GO readout. M' is on z=0.
        static double RelErr(PlanktonMesh M, PlanktonMesh Mp)
        {
            if (M == null || Mp == null || Mp.Vertices.Count != M.Vertices.Count) return double.NaN;
            double num = 0, den = 0; int nH = M.Halfedges.Count;
            for (int h = 0; h < nH; h += 2)
            {
                if (M.Halfedges[h].IsUnused) continue;
                int i = M.Halfedges[h].StartVertex, j = M.Halfedges[h + 1].StartVertex;
                var a = M.Vertices[i]; var b = M.Vertices[j]; var c = Mp.Vertices[i]; var d = Mp.Vertices[j];
                double lM = System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
                double lP = System.Math.Sqrt((c.X - d.X) * (c.X - d.X) + (c.Y - d.Y) * (c.Y - d.Y) + (c.Z - d.Z) * (c.Z - d.Z));
                num += (lM - lP) * (lM - lP); den += lM * lM;
            }
            return den > 0 ? System.Math.Sqrt(num / den) : double.NaN;
        }

        // ===================== journal save / replay =====================

        void SaveSession()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            { Filter = "Journal (*.journal)|*.journal|All files (*.*)|*.*", FileName = "session.journal", InitialDirectory = @"C:\Temp" };
            if (dlg.ShowDialog() != true) return;
            var lines = new System.Collections.Generic.List<string> { "# PieceSolver session journal" };
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

        long _replaySolveStartMs;   // wall-clock at the moment the in-flight replayed Solve was launched

        void ReplayTick(object sender, EventArgs e)
        {
            // A Solve is async (runs on a worker behind the modal). Don't advance to the next command
            // while a bake is in flight — wait for it to finish, then log + continue on a later tick. This
            // is the replay gate that keeps the harness from firing the next command mid-bake.
            if (_baking) return;
            if (_replaySolvePending)
            {
                _replaySolvePending = false;
                double ms = Environment.TickCount64 - _replaySolveStartMs;
                string ex = "";
                if (_session != null)
                {
                    var mm = FlowMetrics.Compute(_session.Mesh, 0.1, false, 4.0);
                    ex = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "  sumE={0:0.000} panels={1} maxDih={2:0.0}  strain={3:0.###}%", mm.SumE, mm.Panels, mm.MaxDihDeg, _sim.StrainPct);
                }
                Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0,3}/{1}  {2}  [{3:0.0} ms]{4}", _replayPos, _replayQueue.Count, _replaySolveLine, ms, ex));
            }
            if (_replayQueue == null || _replayPos >= _replayQueue.Count) { _replayTimer.Stop(); Log("--- replay done ---"); return; }
            var c = _replayQueue[_replayPos++];
            // A Solve sets _baking synchronously inside Execute; defer its log line until the bake completes
            // (a subsequent tick, once _baking clears) so the timing + metrics reflect the finished bake.
            if (c.Kind == CmdKind.Solve)
            {
                _replaySolvePending = true; _replaySolveLine = c.Serialize(); _replaySolveStartMs = Environment.TickCount64;
                Execute(c, record: false);
                return;
            }
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
        bool _replaySolvePending;   // a replayed Solve is in flight; log + advance once _baking clears
        string _replaySolveLine;    // the serialized Solve line, held for the deferred completion log

        void Log(string msg) => _console?.AppendLine(msg);

        void UpdateStatus()
        {
            if (StatusText == null) return;
            int v = _session != null ? _session.Mesh.Vertices.Count : 0;
            string eIso = _hasFlat
                ? "  ·  E_iso " + _lastEIso.ToString("0.###e+0", System.Globalization.CultureInfo.InvariantCulture)
                : "";
            StatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "flow {0:0.0} ms · upload {1:0.0} ms · {2} verts{3}", _lastRunMs, _lastUploadMs, v, eIso);
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

        // Mouse scheme: right-drag = orbit, Shift+right-drag = pan. Wheel = zoom (wired in ctor).
        // Left-drag is reserved (no editor wired in this build) and currently does nothing.
        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _lastMouse = e.GetPosition(_gl);
            _previewDot.Visibility = Visibility.Collapsed;   // hide the footprint preview while dragging
            if (e.ChangedButton == MouseButton.Right)
                _drag = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? DragMode.Pan : DragMode.Orbit;
            else if (e.ChangedButton == MouseButton.Left)
            {
                _drag = DragMode.Edit;
                if (_sim.FreezeBrush && _session != null && !_baking)
                {
                    _strokeCov = new double[_session.Mesh.Vertices.Count];   // fresh coverage for this stroke
                    _dabAccum = 0;
                    if (PickSurface(_lastMouse, out var hit)) ApplyDab(hit);   // initial dab at the down point
                }
            }
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
            var p = e.GetPosition(_gl);
            if (_drag == DragMode.None)
            {
                _lastHover = p;
                if (_sim.FreezeBrush) UpdatePreview(p);   // footprint preview on hover
                return;
            }
            if (_drag == DragMode.Edit)
            {
                if (_sim.FreezeBrush && _session != null && !_baking) BrushStrokeTo(p);   // a dab every `spacing` px along the path
                _lastMouse = p;
                return;
            }
            float dx = (float)(p.X - _lastMouse.X), dy = (float)(p.Y - _lastMouse.Y);
            _lastMouse = p;
            switch (_drag)
            {
                case DragMode.Orbit:
                    _azimuth += dx * 0.01f;
                    _elevation = Math.Clamp(_elevation + dy * 0.01f, -1.5f, 1.5f);
                    InvalidateView();
                    break;
                case DragMode.Pan:
                    PanCamera(dx, dy);
                    break;
            }
        }

        // Unit vector from the orbit target toward the eye (eye = target + dir*distance). Z-up.
        Vector3 CamDir() => new Vector3(
            MathF.Cos(_elevation) * MathF.Sin(_azimuth),
            MathF.Cos(_elevation) * MathF.Cos(_azimuth),
            MathF.Sin(_elevation));

        // Translate the orbit target in the camera's screen plane (Shift+right-drag). Speed scales
        // with zoom distance so the feel is consistent at any zoom.
        void PanCamera(float dx, float dy)
        {
            Vector3 dir = CamDir();                                                  // eye = target + dir*distance
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, dir));   // camera-right in world
            Vector3 up = Vector3.Normalize(Vector3.Cross(dir, right));              // camera-up in world
            float scale = _distance * 0.0015f;
            _target += (-dx * right + dy * up) * scale;
            InvalidateView();
        }

        // ===================== Freeze / Consolidate brush (the one brush) =====================

        // [ / ] and Ctrl+Shift+[ / ] drive the same VM params the BRUSH sliders bind to (so they stay in sync).
        void ResizeBrush(double factor) => _sim.BrushSize = Math.Clamp(_sim.BrushSize * factor, 1.0, 100.0);
        void HardenBrush() => _sim.BrushSoftness = Math.Clamp(_sim.BrushSoftness - 0.05, 0.0, 1.0);   // less soft
        void SoftenBrush() => _sim.BrushSoftness = Math.Clamp(_sim.BrushSoftness + 0.05, 0.0, 1.0);   // more soft

        void EnsureBrushWeights()
        {
            int n = _session.Mesh.Vertices.Count;
            if (_session.BrushWeights == null || _session.BrushWeights.Length != n) _session.BrushWeights = new double[n];
        }

        // Place a dab every `spacing` screen-pixels along the stroke path (a -> b), so dab count tracks path
        // LENGTH, not time. _dabAccum carries the leftover distance across moves.
        void BrushStrokeTo(System.Windows.Point b)
        {
            double dx = b.X - _lastMouse.X, dy = b.Y - _lastMouse.Y;
            double seg = Math.Sqrt(dx * dx + dy * dy);
            if (seg < 1e-6) return;
            double spacing = BrushSpacingPx(b);
            double pos = spacing - _dabAccum;
            while (pos <= seg)
            {
                double t = pos / seg;
                if (PickSurface(new System.Windows.Point(_lastMouse.X + dx * t, _lastMouse.Y + dy * t), out var hit)) ApplyDab(hit);
                pos += spacing;
            }
            _dabAccum = seg - (pos - spacing);
        }

        // Dab spacing ~ half the brush's on-screen radius, so spacing scales with the brush and zoom.
        double BrushSpacingPx(System.Windows.Point screen)
        {
            if (_session != null && PickSurface(screen, out var hit)) return Math.Max(1.0, 0.5 * ScreenRadiusPx(hit));
            return 8.0;
        }

        // One Freeze dab: deposit per-vertex BrushWeights (the deCraze boost the covariance flow honours)
        // with a gaussian footprint. Flow builds per-stroke coverage toward 1; the deposited freeze tends to
        // Strength*MaxFreeze. No geometry moves — this only writes the freeze field. Cheap (no CHA).
        void ApplyDab(Vector3 hit)
        {
            var P = _session.Mesh; int nv = P.Vertices.Count;
            if (_strokeCov == null || _strokeCov.Length != nv) return;
            EnsureBrushWeights();
            var bw = _session.BrushWeights;
            double R = _sim.BrushSize, R2 = R * R;
            double sigma = Math.Max(0.05, _sim.BrushSoftness) * R;
            double twoSig2 = 2.0 * sigma * sigma;
            double flow = Math.Max(1e-3, _sim.BrushFlow);
            double ceil = Math.Clamp(_sim.BrushStrength, 0.0, 1.0) * MaxFreeze;
            for (int i = 0; i < nv; i++)
            {
                var pv = P.Vertices[i];
                if (pv.IsUnused || P.Vertices.IsBoundary(i)) continue;
                double dx = pv.X - hit.X, dy = pv.Y - hit.Y, dz = pv.Z - hit.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > R2) continue;
                double gw = Math.Exp(-d2 / twoSig2);
                double dcov = Math.Min(1.0 - _strokeCov[i], flow * gw);
                if (dcov <= 1e-9) continue;
                _strokeCov[i] += dcov;
                if (bw[i] < ceil) bw[i] = Math.Min(ceil, bw[i] + dcov * ceil);
            }
            _gl?.InvalidateVisual();
        }

        // Build a pick ray from the camera params (convention-independent) and intersect the mesh.
        bool PickSurface(System.Windows.Point screen, out Vector3 hit)
        {
            hit = default;
            if (_session == null) return false;
            double w = Math.Max(1, _gl.ActualWidth), h = Math.Max(1, _gl.ActualHeight);
            Vector3 dir = CamDir();
            Vector3 eye = _target + dir * _distance;
            Vector3 forward = -dir;
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
            Vector3 up = Vector3.Cross(right, forward);
            float tanH = MathF.Tan(MathHelper.DegreesToRadians(45f) * 0.5f);   // matches the 45 deg proj FOV
            float aspect = (float)(w / h);
            float ndcX = (float)(2.0 * screen.X / w - 1.0);
            float ndcY = (float)(1.0 - 2.0 * screen.Y / h);
            Vector3 rd = Vector3.Normalize(forward + right * (ndcX * tanH * aspect) + up * (ndcY * tanH));
            return RayMeshHit(eye, rd, out hit);
        }

        // Nearest ray-triangle hit over the live mesh (linear scan; double-sided since winding is mixed).
        bool RayMeshHit(Vector3 ro, Vector3 rd, out Vector3 hit)
        {
            hit = default;
            var P = _session.Mesh;
            double best = double.MaxValue;
            bool found = false;
            int nf = P.Faces.Count;
            for (int f = 0; f < nf; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                if (fv.Length != 3) continue;
                if (RayTri(ro, rd, BV(P, fv[0]), BV(P, fv[1]), BV(P, fv[2]), out double t) && t < best)
                { best = t; hit = ro + rd * (float)t; found = true; }
            }
            return found;
        }

        static bool RayTri(Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, Vector3 c, out double t)
        {
            t = 0;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 pv = Vector3.Cross(rd, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 tv = ro - a;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(rd, qv) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, qv) * inv;
            return t > 1e-6;
        }

        static Vector3 BV(PlanktonMesh P, int i) { var v = P.Vertices[i]; return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }

        void UpdatePreview(System.Windows.Point cursor)
        {
            if (!_sim.FreezeBrush || _session == null || !PickSurface(cursor, out var hit))
            { _previewDot.Visibility = Visibility.Collapsed; return; }
            double rpx = ScreenRadiusPx(hit);
            _previewDot.Width = _previewDot.Height = 2.0 * rpx;
            _previewDot.Margin = new Thickness(cursor.X - rpx, cursor.Y - rpx, 0, 0);
            _previewDot.Visibility = Visibility.Visible;
        }

        // The brush's world radius projected to screen pixels at the given surface point's depth.
        double ScreenRadiusPx(Vector3 hit)
        {
            Vector3 dir = CamDir();
            Vector3 eye = _target + dir * _distance;
            double dist = Math.Max(1e-4, Vector3.Dot(hit - eye, -dir));   // depth along the view axis
            double h = Math.Max(1, _gl.ActualHeight);
            double tanH = Math.Tan(MathHelper.DegreesToRadians(45f) * 0.5);
            return _sim.BrushSize * h / (2.0 * dist * tanH);
        }

        void InvalidateView() { _gl?.InvalidateVisual(); }

        void OnRender(TimeSpan delta)
        {
            if (!_glInit)
            {
                _view = new MeshView();
                _view.SetNoiseVolume(NoiseVolume.Blue(64, 1337), 64);    // solid blue-noise volume for the surface LIC
                _flatView = new MeshView(); _flatView.EnsureProgram();   // BFF flat map, drawn beside M
                _flatView.ShowEdges = true;   // M' is flat (uniform shading) -> reveal its triangulation with edges
                _grid = new GroundGrid();
                _glInit = true;
            }

            // apply a queued matcap texture (GL thread = here); M' shades the same as M
            if (_matcapDirty && _view != null && _matcapPx != null)
            {
                _view.SetMatcap(_matcapPx, _matcapW, _matcapH);
                _flatView?.SetMatcap(_matcapPx, _matcapW, _matcapH);
                _matcapDirty = false;
            }
            // Default-shading pair (neutral + environment), uploaded to both views like the picked matcap.
            if (_neutralDirty && _view != null && _neutralPx != null)
            {
                _view.SetNeutralMatcap(_neutralPx, _neutralW, _neutralH);
                _flatView?.SetNeutralMatcap(_neutralPx, _neutralW, _neutralH);
                _neutralDirty = false;
            }
            if (_envDirty && _view != null && _envPx != null)
            {
                _view.SetEnvMatcap(_envPx, _envW, _envH);
                _flatView?.SetEnvMatcap(_envPx, _envW, _envH);
                _envDirty = false;
            }
            // staged proposed-crease line vertices (GL thread); gate on !_baking like the mesh upload
            if (_creaseDirty && !_baking && _view != null) { _view.SetCreases(_creasePts ?? System.Array.Empty<float>()); _creaseDirty = false; }

            // re-upload when the mesh changed (run / subdivide / reset / load) or shading toggled;
            // re-fit the camera after a load (new bounds). Upload time feeds the perf readout.
            if (_meshDirty && !_baking && _session != null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _view.Upload(_session.Mesh);
                sw.Stop();
                _lastUploadMs = sw.Elapsed.TotalMilliseconds;
                _meshDirty = false;
                UpdateStatus();
            }

            // Re-fit the camera (and ground grid) when flagged — after a load/reset, and when the BFF
            // flat map M' first appears (so M' isn't left off to the side). Fits M, plus M' if shown.
            if (_reframe && _view != null && _view.HasMesh)
            {
                Vector3 fitCenter = _view.Center; float fitRadius = _view.Radius;
                if (_hasFlat && _flatView != null && _flatView.HasMesh)
                {
                    Vector3 flatCen = _flatView.Center + _flatView.ModelOffset;   // M' placed center
                    float flatRad = _flatView.Radius;
                    Vector3 bbLo = Vector3.ComponentMin(fitCenter - new Vector3(fitRadius), flatCen - new Vector3(flatRad));
                    Vector3 bbHi = Vector3.ComponentMax(fitCenter + new Vector3(fitRadius), flatCen + new Vector3(flatRad));
                    fitCenter = (bbLo + bbHi) * 0.5f; fitRadius = MathF.Max(1e-4f, (bbHi - bbLo).Length * 0.5f);
                }
                _target = fitCenter; _distance = fitRadius * 3f;
                _grid.Build(fitCenter, fitRadius);
                _reframe = false;
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
            Vector3 dir = CamDir();
            Vector3 eye = _target + dir * _distance;
            Matrix4 view = Matrix4.LookAt(eye, _target, Vector3.UnitZ);
            float r = _view != null ? _view.Radius : 1f;
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45f), aspect, MathF.Max(1e-3f, r * 0.01f), r * 100f);

            _grid?.Draw(view, proj);   // ground reference, behind the mesh (depth-tested)
            if (_view != null)
            {
                _view.Sharpness = (float)_sim.Facet; _view.FacetExp = (float)_sim.FacetExp;   // Facet -> shader
                _view.Shine = (float)_sim.Shine; _view.UseMatcap = _sim.UseMatcap;            // Shine shading
                _view.ShowCreases = _creaseCount > 0;                                          // proposed-crease overlay
                // Surface-LIC field (modulates the matcap). Recompute only when the mesh/mode changed.
                _view.LicMode = _sim.ShowRuling ? 1 : 0;
                if (_sim.ShowRuling && _view.HasMesh && _session != null && _rulingsDirty && !_baking)
                {
                    float fmax;
                    var fld = RulingField.ComputeField(_session.Mesh, out fmax);
                    _view.SetField(fld, fmax);
                    _rulingsDirty = false;
                }
                // Surface-LIC scale, derived from the noise TEXEL size so consecutive march taps stay
                // CORRELATED (~1 texel/tap) -> coherent streaks. The old 0.03*Radius step was ~4 texels
                // per tap, so the 24-tap convolution sampled decorrelated noise and washed out to a flat
                // grey ("scale massively off"). NoiseVolume is 64^3, repeated LIC_TILES across the model.
                float licTiles = MathF.Max(2f, (float)_sim.LicGrain), NOISE_N = 64f;
                float licExtent = MathF.Max(1e-3f, 2f * _view.Radius);
                _view.NoiseFreq   = licTiles / licExtent;
                _view.LicStep     = licExtent / (licTiles * NOISE_N);   // one noise texel per march tap
                _view.LicTaps     = System.Math.Clamp(_sim.LicLength, 2, 64);
                _view.LicStrength = MathF.Max(0f, MathF.Min(1f, (float)_sim.LicAlpha));
                _view.CurvMin = (float)System.Math.Clamp(_sim.LicCurvMin, 0.0, 1.0);
                _view.CurvMax = MathF.Max(_view.CurvMin + 0.01f, (float)System.Math.Clamp(_sim.LicCurvMax, 0.0, 1.0));   // keep max > min (smoothstep)
                _view.ShowSeams = _sim.FixBSplineEdges;
                if (_sim.FixBSplineEdges && _view.HasMesh && _seamsDirty && !_baking)
                {
                    _view.SetSeams(_seamLineF ?? System.Array.Empty<float>());
                    _view.SetSeamControls(_seamCtrlF ?? System.Array.Empty<float>());
                    _seamsDirty = false;
                }
                _view.Draw(view, proj);
            }
            if (_hasFlat && _flatView != null && _flatView.HasMesh)   // BFF flat map M', beside M on the z=0 plane
            {
                _flatView.Sharpness = (float)_sim.Facet; _flatView.FacetExp = (float)_sim.FacetExp;
                _flatView.Shine = (float)_sim.Shine; _flatView.UseMatcap = _sim.UseMatcap;
                _flatView.Draw(view, proj);
            }
        }
    }
}
