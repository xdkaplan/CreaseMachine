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
        MeshView _renderer;
        MeshView _flatRenderer;          // BFF flat map M', drawn beside M (offset in +X) on the z=0 plane
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
                                     // (via EnsureFlat or the Solve bake's FlattenPure). The lazy gate.
        bool _meshSwitching;         // re-entrancy guard while MeshIndex programmatically resets the app
        GroundGrid _grid;            // subtle dot grid on the world Z=0 plane (10-unit spacing)
        FlowSession _session;        // live mesh + Nesterov velocity; the flow bakes it in place
        readonly SimSettings _sim = new SimSettings();   // bindable sim params (right toolbar)
        string _meshPath;            // source mesh path, so Revert can reload the input from disk
        bool _glInit, _meshDirty, _reframe, _rulingsDirty;   // _reframe: re-fit camera next upload; _rulingsDirty: recompute ruling overlay
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
        double _lastUploadMs;                       // perf readout (GL upload time)

        ConsoleWindow _console;                     // non-modal log window (Window > Console / Ctrl+Shift+J)
        AboutWindow _about;                         // non-modal about window (Help > About)
        bool _shuttingDown;                         // main window closing -> let tool windows actually close

        // orbit camera
        // Orbit camera (azimuth / elevation / target / distance) now lives on the View — `_view.Camera` (Camera.cs).
        System.Windows.Point _lastMouse;
        enum DragMode { None, Orbit, Pan, Edit }
        DragMode _drag = DragMode.None;   // right-drag = orbit, Shift+right-drag = pan, left-drag = Crease brush
        System.Windows.Point _rightDownPos;   // where a right-button press started — to tell a click from an orbit-drag
        bool _rightClickArmed;                // a plain right-press that, released without dragging, pops the piece menu
        const double RightClickPx = 6.0;      // right-release within this of the press = a click (menu); beyond = an orbit
        // Modifier state tracked from key events, NOT read live at mouse-down. Reading Keyboard.Modifiers during
        // WPF's deferred mouse-input processing can miss a just-pressed Shift/Ctrl (the "Shift+click sometimes
        // doesn't register" race). Captured in key-event order instead, so a click any time the key is physically
        // held sees it; resynced on window activation to cover a keyup missed while inactive.
        ModifierKeys _heldMods = ModifierKeys.None;
        // Brush-footprint preview overlay (the hover circle). The brush INTERACTION itself lives in the
        // Piecer editor; MainWindow only owns the preview dot + picking (exposed to the editor via IEditorHost).
        System.Windows.Point _lastHover;  // last hover position, for the footprint preview
        System.Windows.Shapes.Ellipse _previewDot;   // brush-footprint preview overlay

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
        bool _creaseDirty; int _creaseCount;
        // TRANSIENT (Supplied): settled (developed) geometry from the last Propose, per original vertex (nV*3).
        // Retained for a future re-surfacing — the "preview proposed mesh" display path was removed; produced by
        // the Propose bake, not regenerable on demand.
        readonly Transient<double[]> _proposedPos = new Transient<double[]>();
        // TRANSIENT (the Solver's): the developed mesh from the last Solve — a derived clone/unweld of the
        // authoring mesh, NOT the authoring mesh itself (which + its Pattern survive the bake). PUSH (produced
        // by the async bake); shown in the Solver phase. v1-split; see docs/archive/SOLVER-PHASE.md.
        readonly Transient<PlanktonMesh> _developed = new Transient<PlanktonMesh>();
        // PARTITION: the thin Pattern companion over the live mesh holds the per-face piece map (PieceMap, was
        // _faceRegion) + the derived crease set (CreaseMap, was _creaseEdges). It is the Doc's Store — the single
        // authority — so read it as `_doc.Pattern` (no shadow field); recreated via _doc.Rebind when the mesh changes.
        // The Doc — the orchestrator that owns the Pattern Store, the typed Selection, and the undo/redo stacks,
        // and gatekeeps all piece mutation through Run/Undo/Redo. Re-pointed at the new Pattern on every rebind.
        // See docs/DOC-TX-REFACTOR.md.
        readonly Doc _doc = new Doc();
        // VIEW: the viewport abstraction bound to the Doc — owns the DISPLAY state (the single source of truth
        // for what's rendered) + the repaint poke (Rot). Built in the ctor (needs _gl). See PieceSolver/View.cs.
        readonly View _view;
        // EDITOR: the active interaction. Non-null == a proposal has been accepted and the Crease brush owns the
        // left-button + hover paths. Set in OpenCreaseReview's onAccept, cleared in ClearProposedCreases (mesh
        // change / review-cancel / load / reset / subdivide / run / solve). The Piecer instance is retained so
        // its selection persists across activations. The brush is LIVE only when EditorActive (which also gates
        // out the transient `_baking` / `_camModal` windows) — exactly the old BrushAvailable condition.
        Editor _activeEditor;
        Piecer _piecer;
        // The old BrushAvailable gate, now expressed over the editor: an editor is bound AND we're not in a
        // transient blocking state (a bake or a camera-modal). Drives the delegation, preview, and [ / ] keys.
        bool EditorActive => _activeEditor != null && !_baking && !_camModal;
        bool _camModal;          // a camera-only modal is up: chrome disabled, viewport still orbits; pieces show full patchwork
        System.Action _camAccept, _camCancel;   // active cam-modal's Accept / Cancel callbacks
        bool _angleDragging;     // Crease angle slider thumb is being dragged: show the neutral crease-shader grooves live, defer the rainbow colour to mouse-up
        // Piece visualization: crease-bounded face regions tinted per piece. The SPLIT buffers now live on the
        // Pattern Real's Geometry Transient (I2a — Supplied by RebuildPieces, staged GL-side in OnRender like the
        // mesh/crease overlay). _pieceDirty is the PUSH flag that re-stages it. Auto-shown after Propose.
        bool _pieceDirty;
        double _bakeStrain = double.NaN;     // worst/final strain % the bake reached (UI reads after)
        string _bakeSummary = "";            // title body the bake produced
        readonly System.Collections.Generic.List<string> _bakeLog = new System.Collections.Generic.List<string>();   // worker log lines, flushed on the UI thread

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _sim;   // right-panel sliders + the run-button caption bind to this

            // The view reacts to the Doc instead of being poked imperatively: a selection change repaints the
            // highlight; a transaction (Run/Undo/Redo) repaints pieces + crease grooves on the new partition.
            _doc.Pieces.Changed += () => { if (_view.Display == DisplaySource.Pieces) RebuildPieces(); };
            _doc.Changed += () => { RebuildPieces(); RebuildCreaseOverlay(); InvalidateView(); };
            _doc.Recorded += line => Echo(line);   // committed ops + comments stream into the Console op-log

            // The session log lives in a non-modal Console window (Window > Console / Ctrl+Shift+J),
            // hidden by default. Created now so logging works from startup; its Owner is set lazily on
            // first show (a Window's Owner must already be shown). Closing hides it (the instance is
            // reused) unless the app itself is shutting down.
            _console = new ConsoleWindow();
            _console.Closing += (s, e) => { if (!_shuttingDown) { e.Cancel = true; _console.Hide(); MenuConsole.IsChecked = false; } };
            Closing += (s, e) => _shuttingDown = true;

            _gl = new GLWpfControl();
            // The viewport IS the editor's host (IEditorHost): display + camera + picking + brush footprint live on
            // it; the render rebuilds + preview dot are wired in as shell hooks until the render-loop drains.
            _view = new View(_doc, _gl, () => _session?.Mesh, () => _sim.BrushSize,
                             RebuildPieces, RebuildCreaseOverlay, UpdatePreview,
                             () => _previewDot.Visibility = Visibility.Collapsed);
            _piecer = new Piecer(_view);   // the Piecing editor now talks to the View, not the window
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
            _gl.MouseWheel += (s, e) => { _view.Camera.Zoom(e.Delta); _view.Rot(); };

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

            // 3D|2D folding split: divider drag resizes / stashes a pane; clicking a gutter tab pops it back.
            CenterDivider.MouseLeftButtonDown += DividerDown;
            SplitGrid.MouseMove += DividerMove;            // on SplitGrid (stable) so a snap-closed divider doesn't drop the drag
            SplitGrid.MouseLeftButtonUp += DividerUp;
            Gutter3D.MouseLeftButtonUp += (s, e) => { _panes = PaneState.Both; ApplyPanes(); };
            Gutter2D.MouseLeftButtonUp += (s, e) => { _panes = PaneState.Both; ApplyPanes(); };
            ApplyPanes();

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
            // Solve is PieceSolver's main develop; it records a full BakeParams snapshot so a recorded
            // session reproduces the bake on replay (and via the headless CLI value-check).
            SolveButton.Click += (s, e) => Execute(StudioCommand.Solve(_sim.ToBakeParams()), record: true);   // async bake: develop-to-accuracy + subdivide, on a worker with a progress+cancel modal
            ProposeButton.Click += (s, e) => { _ = OnProposeAsync(); };   // stage 2: propose piece-boundary creases (no-collapse flow, reuses the modal)
            BakeCancel.Click += (s, e) => _bakeCts?.Cancel();
            CamModalAccept.Click += (s, e) => { var a = _camAccept; CloseCamModal(); a?.Invoke(); };
            CamModalCancel.Click += (s, e) => { var c = _camCancel; CloseCamModal(); c?.Invoke(); };
            MenuImport.Click += (s, e) => ImportMesh();   // File > Import (STL / FBX / OBJ) — no shortcut; Ctrl+O reserved for Open
            MenuRevert.Click += (s, e) => Execute(StudioCommand.Revert(), record: true);   // File > Revert (also Ctrl+R)
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
                    || e.PropertyName == nameof(SimSettings.Shine) || e.PropertyName == nameof(SimSettings.UseMatcap)
                    || e.PropertyName == nameof(SimSettings.InsetWidthFrac)) _gl?.InvalidateVisual();
                else if (e.PropertyName == nameof(SimSettings.MeshIndex) || e.PropertyName == nameof(SimSettings.AssetSet)) OnMeshIndexChanged();
                else if (e.PropertyName == nameof(SimSettings.CreaseAngleDeg))
                {
                    RelabelCreases();   // recompute regions + crease-EDGE overlay
                    RebuildPieces();    // rebuild the crease-shader grooves; colour rule: rainbow when settled, NEUTRAL while dragging
                    _gl?.InvalidateVisual();
                }
                else if (e.PropertyName == nameof(SimSettings.FixBSplineEdges) || e.PropertyName == nameof(SimSettings.SeamRatio)) { RefreshSeamDisplay(); _gl?.InvalidateVisual(); }
            };

            // Console-window transport: save the recorded session, replay a journal file, clear recording.
            _console.SaveButton.Click += (s, e) => SaveSession();
            _console.ReplayButton.Click += (s, e) => OpenAndReplay();
            _console.ClearButton.Click += (s, e) => { _journal.Clear(); _doc.ClearLog(); _console.ClearLog(); _doc.Comment("journal cleared"); };

            // Menu bar + keyboard shortcuts (Ctrl+Shift+J = toggle Console). Session-saving lives on the Console's
            // Save button; Ctrl+S is intentionally NOT bound here — it's reserved for the future real save.
            MenuAbout.Click += (s, e) => ShowAbout();
            MenuConsole.Click += (s, e) => ShowConsole(MenuConsole.IsChecked);   // IsCheckable flips first
            PreviewKeyDown += (s, e) =>
            {
                _heldMods = e.KeyboardDevice.Modifiers;   // track held modifiers so mouse gestures read this, not a stale/early Keyboard.Modifiers
                // TAB toggles the base view between Authoring and Developed. WPF treats Tab as focus traversal, so
                // we intercept it in PreviewKeyDown (window-level). Gated off a focused text control so a future
                // editable field keeps Tab; today there are none, so this is effectively a window-level toggle.
                if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase))
                {
                    ToggleDevelopedView(); e.Handled = true;
                }
                else if (e.Key == Key.J && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { ToggleConsole(); e.Handled = true; }
                else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { Execute(StudioCommand.Revert(), record: true); e.Handled = true; }
                else if (e.Key == Key.Escape && EditorActive && Keyboard.Modifiers == ModifierKeys.None) { if (_activeEditor.GesturePending) _activeEditor.CancelGesture(); else _activeEditor.Deselect(); e.Handled = true; }   // ESC = cancel an in-flight stroke, else deselect
                else if (e.Key == Key.Z && EditorActive && Keyboard.Modifiers == ModifierKeys.Control) { _doc.Undo(); e.Handled = true; }   // Ctrl+Z = undo the last piecing transaction
                else if (e.Key == Key.Y && EditorActive && Keyboard.Modifiers == ModifierKeys.Control) { _doc.Redo(); e.Handled = true; }   // Ctrl+Y = redo
                else if (e.Key == Key.Z && EditorActive && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { _doc.Redo(); e.Handled = true; }   // Ctrl+Shift+Z = redo
                else if (e.Key == Key.M && EditorActive && Keyboard.Modifiers == ModifierKeys.None) { TryMerge(); e.Handled = true; }   // M = merge the selected pieces
                else if ((e.Key == Key.Delete || e.Key == Key.Back) && EditorActive && Keyboard.Modifiers == ModifierKeys.None) { TryDelPiece(); e.Handled = true; }   // Del / Backspace = delete the selected pieces (donate to neighbour)
                else if (e.Key == Key.OemCloseBrackets && EditorActive && Keyboard.Modifiers == ModifierKeys.None)   // ] = grow brush
                {
                    ResizeBrush(1); UpdatePreview(_lastHover); e.Handled = true;
                }
                else if (e.Key == Key.OemOpenBrackets && EditorActive && Keyboard.Modifiers == ModifierKeys.None)    // [ = shrink brush
                {
                    ResizeBrush(-1); UpdatePreview(_lastHover); e.Handled = true;
                }
            };
            PreviewKeyUp += (s, e) => _heldMods = e.KeyboardDevice.Modifiers;   // clear a released modifier (keeps _heldMods current)
            Activated += (s, e) => _heldMods = Keyboard.Modifiers;              // resync on focus regain (covers a keyup missed while inactive)
            // (The old hold-Space live-step path is gone — Solve is now the single develop path, async with
            // a progress+cancel modal.)
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

        // The single entry point for every app action. Performs the side effect (Apply*), records
        // the semantic command when asked, and syncs the display controls (so replay reflects state).
        // Every UI handler and the replay loop go through here, so the journal is the complete,
        // authoritative record of what happened - and replay drives the exact same code paths.
        void Execute(StudioCommand c, bool record)
        {
            // For a replayed Solve, apply the recorded BakeParams onto _sim BEFORE the bake launches, so
            // RunBake (which reads _sim live) develops with the captured settings. For a live Solve, _sim
            // is already the source of truth, so this sync is skipped (record:true -> no SyncControls).
            if (c.Kind == CmdKind.Solve && !record) SyncControls(c);

            // Mid-bake guard (the #2 hazard): a Load / Subdivide / Revert during a Solve would reassign
            // _session out from under the bake, whose `finally` then restores the STALE authoring mesh over
            // your new one. Block the mesh-changers while _baking — the Solve owns the mesh until it finishes
            // or is cancelled. (Replay is safe: it waits for the bake to clear before the next command.)
            if (_baking && (c.Kind == CmdKind.Load || c.Kind == CmdKind.Subdivide || c.Kind == CmdKind.Revert))
            {
                Title = "PieceSolver — solving… cancel or wait before changing the mesh";
                return;
            }

            switch (c.Kind)
            {
                case CmdKind.Load: ApplyLoad(c.Path); break;
                case CmdKind.Subdivide: ApplySubdivide(); break;
                case CmdKind.Revert: Revert(); break;   // unified at merge: command (branch's CmdKind.Revert) + method (master's Revert()) are both the CAD term
                case CmdKind.Matcap: ApplyMatcap(c.N); break;
                // Launch the async bake (fire-and-forget; _baking flips synchronously before the first
                // await, so the replay loop can wait on it). On a live user click this is the develop.
                case CmdKind.Solve: _ = OnSolveAsync(); break;
            }
            if (record)
            {
                _journal.Add(c);                  // typed list — drives the replay queue
                _doc.Record(c.Serialize());       // every command is a bare REPLAYABLE line in the event log (+ Console via Recorded)
            }
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
            }
            finally { _suppressUi = false; }
        }

        // ===================== Apply* : the actual side effects (no recording) =====================

        void ApplyLoad(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) { Title = "PieceSolver — missing: " + path; return; }
            try { _session = new FlowSession(MeshIO.Load(path)); _meshPath = path; }
            catch (Exception ex) { Title = "PieceSolver — load failed: " + ex.Message; return; }
            RebindPattern();          // the partition companion now wraps the new mesh
            _developed.Rot();         // authoring mesh changed -> stale the developed view (falls back to authoring until re-Solve). Becomes a real cascade edge once the mesh is a node.
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

        // Upload the base-mesh buffer for the CURRENT display source. The Developed view is sourced from the
        // governed _developed Transient (Peek: stale/never-supplied => fall back to authoring); Authoring/Pieces
        // share the authoring mesh. The single Display-aware upload site so a stray _meshDirty can't replace the
        // developed view with the authoring mesh.
        void UploadForDisplay()
        {
            if (_renderer == null) return;
            if (_view.Display == DisplaySource.Developed && _developed.Peek(out var dev) && dev != null)
                _renderer.Upload(dev);            // developed view sourced from the governed Transient
            else if (_session?.Mesh != null)
                _renderer.Upload(_session.Mesh);  // authoring / pieces base = the authoring mesh
        }

        // TAB: flip the base view between Authoring and Developed. From Developed -> Authoring. From
        // Authoring/Pieces -> Developed only if a solved result exists (the _developed Transient is Fresh);
        // otherwise land on Authoring (so TAB with nothing solved is a clean no-op-ish that shows authoring).
        void ToggleDevelopedView()
        {
            if (_view.Display == DisplaySource.Developed)
                _view.Display = DisplaySource.Authoring;
            else
                _view.Display = (_developed.Peek(out var d) && d != null) ? DisplaySource.Developed : DisplaySource.Authoring;
            _meshDirty = true; _pieceDirty = true;   // re-evaluate the base upload + ShowPieces for the new display
            UpdateStatus();
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

            // v1-split (docs/archive/SOLVER-PHASE.md): develop a DERIVED clone on a temporary session, so the authoring
            // _session + its Pattern survive the bake (the authoring mesh is restored in the finally; the result
            // is kept as the Solver Transient _developed and shown). Phase C unwelds-by-CreaseMap instead of
            // cloning when the mesh is pieced. Develop-state is reset for the clone exactly as Revert used to.
            var authoring = _session;
            // The derived develop mesh: when the mesh is PIECED (>1 painted region), UNWELD along the creases so
            // each piece is its own connected component — the per-component bake (RunBakeMulti) then develops the
            // painted pieces. Otherwise a plain clone (single-patch develop). The authoring mesh is untouched.
            var pieceMap = _doc.Pattern?.PieceMap;
            bool pieced = false;
            if (pieceMap != null && pieceMap.Length == authoring.Mesh.Faces.Count)
            {
                int first = -1;
                for (int f = 0; f < pieceMap.Length; f++)
                {
                    if (authoring.Mesh.Faces[f].IsUnused) continue;
                    if (first < 0) first = pieceMap[f]; else if (pieceMap[f] != first) { pieced = true; break; }
                }
            }
            var developMesh = pieced
                ? CreaseMachine.MeshOps.UnweldByRegion(authoring.Mesh, pieceMap, out _)
                : TopologyClone(authoring.Mesh);
            _session = new FlowSession(developMesh);
            _hasFlat = false; _flat = null; _M0 = null;
            _refMeanLen2 = 0; _isoResFactor = 1.0; _lmLambda = 0; _bffNeeded = true;
            _meshDirty = false;

            _baking = true; _doc.EnterBusy(Busy.Calculating);   // the Doc rejects Run/Undo/Redo while the worker owns the mesh
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
                _baking = false; _doc.ExitBusy();
                BakeOverlay.Visibility = Visibility.Collapsed;
                foreach (var line in _bakeLog) _doc.Comment(line);
                bool developed = !_bakeToken.IsCancellationRequested;
                if (developed)
                {
                    _developed.Supply(_session.Mesh);   // the Solver's Transient = the developed clone
                    // Flip to the Solver view: the piece view (ShowPieces) REPLACES the matcap mesh, so the
                    // developed mesh stays hidden behind it unless the Piecer-view decorations come off. Drop the
                    // piece colours + crease wires — DISPLAY only; the Pattern / CreaseMap data survive.
                    _view.Display = DisplaySource.Developed; _pieceDirty = true;   // Solve shows the developed mesh; Developed => pieces off (no occlusion)
                    _creaseCount = 0; _creaseDirty = true;   // drop the crease wires (CreaseLines grows empty once cleared)
                }
                _session = authoring;                            // restore the authoring session (Pattern still coupled to it)
                UploadForDisplay();                              // Display==Developed (set above) => governed _developed; else authoring
                if (_hasFlat && _flatRenderer != null && _flat != null) { _flatRenderer.Upload(_flat); PlaceFlat(); }
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

            _baking = true; _doc.EnterBusy(Busy.Calculating);   // the Doc rejects Run/Undo/Redo while the worker owns the mesh
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
            catch (Exception ex) { _doc.Comment("propose failed: " + ex.Message); }
            finally
            {
                // Capture the settled (developable, "crease-proposed") geometry for the DISPLAY preview
                // BEFORE restoring M0 (only on a completed propose — a cancel leaves the flow mid-way).
                if (fold != null)
                {
                    var pp = new double[nV * 3];
                    for (int v = 0; v < nV; v++) { var pv = P.Vertices[v]; pp[v * 3] = pv.X; pp[v * 3 + 1] = pv.Y; pp[v * 3 + 2] = pv.Z; }
                    _proposedPos.Supply(pp);
                }
                // Restore the original geometry + momentum: the live mesh returns to the input, and the
                // cached fold labels reference exactly these (unrenumbered) vertices.
                for (int v = 0; v < nV; v++) P.Vertices.SetVertex(v, sx[v], sy[v], sz[v]);
                _session.Vel = savedVel;
                _baking = false; _doc.ExitBusy();
                BakeOverlay.Visibility = Visibility.Collapsed;
                if (fold != null)
                {
                    _creaseFold = fold; _creaseA = ea; _creaseB = eb;
                    RelabelCreases();
                    OpenCreaseReview();   // camera-only modal: full patchwork + Crease angle slider + Accept/Cancel
                    _doc.Comment($"crease proposer: scanned {fold.Length} interior edges, {_creaseCount} proposed at >= {_sim.CreaseAngleDeg:0.#} deg");
                }
                if (_renderer != null) _renderer.Upload(_session.Mesh);   // input mesh, or the proposed preview if toggled
                _meshDirty = false;
                Title = "PieceSolver — " + (token.IsCancellationRequested ? "propose cancelled" : _creaseCount + " creases proposed");
                _gl?.InvalidateVisual();
                _bakeCts?.Dispose(); _bakeCts = null; _bakeProgress = null;
            }
        }

        // ===================== CamModal (reusable camera-only modal) =====================
        // A modal that blocks all the window chrome but leaves the 3D viewport live for orbit/pan/zoom — a
        // common need (review/confirm a result while still inspecting it in 3D). Title + body content + the
        // Accept/Cancel callbacks are supplied per use; the host just toggles chrome and shows the floating bar.

        void ShowCamModal(string title, System.Windows.FrameworkElement body, System.Action onAccept, System.Action onCancel)
        {
            CamModalTitle.Text = title;
            CamModalBody.Content = body;
            _camAccept = onAccept; _camCancel = onCancel;
            _camModal = true;
            SetChromeEnabled(false);
            CamModalBar.Visibility = Visibility.Visible;
        }

        void CloseCamModal()
        {
            _camModal = false;
            SetChromeEnabled(true);
            CamModalBar.Visibility = Visibility.Collapsed;
            CamModalBody.Content = null;
            _camAccept = null; _camCancel = null;
        }

        // Enable/disable everything except the viewport (and the modal bar itself), so only camera + the modal
        // are interactive. The viewport (CenterHost) is deliberately left out.
        void SetChromeEnabled(bool en)
        {
            MenuBar.IsEnabled = en;
            TopBar.IsEnabled = en;
            LeftPanel.IsEnabled = en;
            RightPanel.IsEnabled = en;
            BottomBar.IsEnabled = en;
            LeftSplitter.IsEnabled = en;
            RightSplitter.IsEnabled = en;
        }

        // ===================== Crease review (a CamModal use) =====================
        // After Propose: review the proposed creases in 3D with the full patchwork colours and a live Crease
        // angle slider. Accept commits them (back to neutral display, brush goes live); Cancel discards.
        void OpenCreaseReview()
        {
            ShowCamModal("Review creases — drag Crease ∠, then Accept", BuildCreaseReviewBody(),
                onAccept: () =>
                {
                    _activeEditor = _piecer;   // proposal accepted -> the Crease brush goes live (was: _faceRegion != null)
                    RebuildPieces();   // _camModal now false -> recolour to neutral (via the editor's FaceFill); creases stay committed
                    _doc.Comment($"creases committed: {_creaseCount} edge(s) at {_sim.CreaseAngleDeg:0.#} deg");
                    Title = "PieceSolver — " + _creaseCount + " creases committed";
                },
                onCancel: () =>
                {
                    ClearProposedCreases();   // discard the proposal entirely
                    if (_renderer != null && _session != null) _renderer.Upload(_session.Mesh);
                    _gl?.InvalidateVisual();
                    _doc.Comment("crease proposal discarded");
                });
            RebuildPieces();   // _camModal == true -> full rainbow patchwork
        }

        // The crease-review modal body: a single live "Crease ∠" slider bound to the same VM property the old
        // panel slider used, so dragging re-thresholds + re-segments the patchwork live (PropertyChanged hook).
        System.Windows.FrameworkElement BuildCreaseReviewBody()
        {
            System.Windows.Media.SolidColorBrush Brush(byte r, byte g, byte b)
                => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            var dp = new System.Windows.Controls.DockPanel { DataContext = _sim, MinWidth = 320, LastChildFill = true };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = "Crease ∠", Width = 62, Foreground = Brush(0xC8, 0xC8, 0xC8),
                FontSize = 11, VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            System.Windows.Controls.DockPanel.SetDock(label, System.Windows.Controls.Dock.Left);
            var val = new System.Windows.Controls.TextBlock
            {
                Width = 44, Foreground = Brush(0x8A, 0x8A, 0x98), FontSize = 11,
                TextAlignment = System.Windows.TextAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            val.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding("CreaseAngleDeg") { StringFormat = "{0:0.#}°" });
            System.Windows.Controls.DockPanel.SetDock(val, System.Windows.Controls.Dock.Right);
            var slider = new System.Windows.Controls.Slider
            {
                Minimum = 5, Maximum = 90, MinWidth = 200, VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(6, 0, 6, 0)
            };
            slider.SetBinding(System.Windows.Controls.Slider.ValueProperty,
                new System.Windows.Data.Binding("CreaseAngleDeg") { Mode = System.Windows.Data.BindingMode.TwoWay });
            // While the thumb is dragged, the grooves stay live (re-segmenting) but the fill goes NEUTRAL (no
            // rainbow); on release we rebuild with the colour back. (Thumb drag events bubble to the slider.)
            slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
                new System.Windows.Controls.Primitives.DragStartedEventHandler((s, e) => { _angleDragging = true; RebuildPieces(); }));
            slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                new System.Windows.Controls.Primitives.DragCompletedEventHandler((s, e) => { _angleDragging = false; RebuildPieces(); }));
            dp.Children.Add(label);
            dp.Children.Add(val);
            dp.Children.Add(slider);
            return dp;
        }

        // Re-seed the editable crease selection from the cached fold angles at the current Crease angle
        // threshold (DISCARDS brush edits), then rebuild the overlay. Called on Propose + the Crease angle
        // slider. No cached angles -> clears.
        // Re-seed the per-FACE region map from the threshold creases (DISCARDS brush paint), then derive the
        // crease set from those region boundaries and rebuild the overlay. The region map is primary; creases
        // are whatever separates two regions. A dangling threshold crease (doesn't split a region) is dropped.
        // Seed a provisional crease set (into the Pattern's CreaseMap) to flood-fill the initial regions, then
        // re-seed the per-FACE region map from it (DISCARDS brush paint) and re-derive the real region-boundary
        // creases, then rebuild the overlay. The region map is primary; creases are whatever separates two
        // regions. A dangling threshold crease (doesn't split a region) is dropped. Piecer inactive (modal).
        void RelabelCreases()
        {
            SeedCreaseEdges();          // provisional crease set into _doc.Pattern.CreaseMap
            _piecer?.ClearSelection();  // ids are renumbered by Seed (matches the old SeedRegions resetting _brushRegion)
            _doc.ClearHistory();        // Chapter reset: the re-partition invalidates the undo/redo deltas
            _doc.Pattern?.Seed();           // flood-fill the regions across non-crease edges
            _doc.Pattern?.Invalidate();     // rot downstreams: CreaseMap regrows to the real region-boundary creases
            RebuildCreaseOverlay();
        }

        // Seed a provisional crease set from the threshold-labeled proposed edges (only used to flood-fill the
        // initial regions in _doc.Pattern.Seed; Invalidate then rots CreaseMap so it regrows the real boundaries).
        void SeedCreaseEdges()
        {
            if (_doc.Pattern == null) return;
            var set = new System.Collections.Generic.HashSet<long>();
            if (_creaseFold != null)
            {
                double thr = _sim.CreaseAngleDeg * Math.PI / 180.0;
                for (int i = 0; i < _creaseFold.Length; i++)
                    if (_creaseFold[i] >= thr) set.Add(Pattern.EdgeKey(_creaseA[i], _creaseB[i]));
            }
            _doc.Pattern.CreaseMap.Supply(set);   // Supply the provisional set; Seed peeks it, then Invalidate marks it stale
        }

        // Mark the crease render stale: Pattern's Grown CreaseLines Transient grows the actual segment buffer on
        // read in OnRender (from CreaseMap + the held mesh). This is now just the "crease render needs re-staging"
        // trigger — no CPU build here. Called from Doc.Changed + the RefreshCreaseOverlay editor hook.
        void RebuildCreaseOverlay()
        {
            var creaseMap = _doc.Pattern?.CreaseMap.Value;   // Transient: lazily derived from PieceMap
            _creaseCount = creaseMap?.Count ?? 0;            // gates ShowCreases + feeds the proposal status/title
            _creaseDirty = true;
            _gl?.InvalidateVisual();
        }

        // (Re)create the Pattern companion so it always wraps the CURRENT live mesh. Called wherever the
        // mesh object changes (load / reset / subdivide); the maps come up null (a fresh partition), which is
        // exactly what ClearProposedCreases would set them to anyway.
        void RebindPattern() => _doc.Rebind(new Pattern(_session?.Mesh));   // Doc re-points + drops history/selection; Pattern lives only on the Doc now

        // Merge the selected pieces as a single undoable transaction. Each ADJACENT cluster fuses into one piece
        // (survivor = min id); isolated selected pieces are left as-is. Refused only if no two selected pieces
        // touch (nothing to merge). The selection collapses to the surviving pieces.
        void TryMerge()
        {
            if (_doc.Pattern == null) return;
            var sel = _doc.Pieces;
            if (sel.Count < 2) { _doc.Comment("merge: select 2+ pieces first"); return; }
            var ids = new System.Collections.Generic.HashSet<int>(); foreach (var p in sel.Items) ids.Add(p.Value);
            var groups = _doc.Pattern.MergeGroups(ids);
            var delta = Commands.Merge(_doc.Pattern, groups);
            if (delta.Empty) { _doc.Comment("merge: no adjacent pieces to merge"); return; }
            var involved = new System.Collections.Generic.HashSet<int>();              // pieces touched (from ∪ to)
            foreach (var o in delta.Ops) { involved.Add(o.From); involved.Add(o.To); }
            _doc.Comment($"merge {involved.Count} pieces, {delta.Ops.Count} faces");   // label the otherwise-opaque setpiece block
            if (_doc.Run(delta))   // self-rejects if the Doc isn't Ready (mid-stroke / baking)
            {
                var survivors = new System.Collections.Generic.HashSet<PieceId>();
                foreach (var g in groups.Values) survivors.Add(new PieceId(g));   // merged survivors + untouched singletons
                _doc.Pieces.Set(survivors);
            }
            else _doc.Comment("merge: busy — finish the current action first");
        }

        // Delete the selected pieces as one undoable transaction: each is healed into its dominant surviving
        // neighbour (the "kill & donate" op — same as a Ctrl-drag with nothing selected, but driven from the
        // selection). Refused when nothing's selected, or when no selected blob has a surviving neighbour to
        // donate to (empty delta — e.g. the whole mesh selected). The selection clears (the pieces are gone).
        void TryDelPiece()
        {
            if (_doc.Pattern == null) return;
            var sel = _doc.Pieces;
            if (sel.Count < 1) { _doc.Comment("delpiece: select a piece first"); return; }
            var ids = new System.Collections.Generic.HashSet<int>(); foreach (var p in sel.Items) ids.Add(p.Value);
            var delta = Commands.DelPiece(_doc.Pattern, ids);
            if (delta.Empty) { _doc.Comment("delpiece: nothing to delete (no surviving neighbour)"); return; }
            _doc.Comment($"delpiece {ids.Count} pieces, {delta.Ops.Count} faces");   // label the otherwise-opaque setpiece block
            if (_doc.Run(delta))   // self-rejects if the Doc isn't Ready (mid-stroke / baking)
                _doc.Pieces.Clear();   // the pieces are gone -> clear the selection
            else _doc.Comment("delpiece: busy — finish the current action first");
        }

        // Drop the cached proposal + overlay + editable selection + preview geometry + apex cache (fresh
        // mesh, or topology/geometry changed). Idempotent.
        void ClearProposedCreases()
        {
            if (_creaseFold == null && _proposedPos.IsStale && (_doc.Pattern == null || _doc.Pattern.CreaseMap.IsStale) && _creaseCount == 0 && _view.Display != DisplaySource.Pieces) return;
            _creaseFold = null; _creaseA = null; _creaseB = null; _proposedPos.Clear();
            if (_doc.Pattern != null) { _doc.Pattern.CreaseMap.Clear(); _doc.Pattern.PieceMap = null; }
            _piecer?.ClearSelection();
            _activeEditor = null;     // no proposal -> the brush is unavailable (was: _faceRegion == null)
            _creaseCount = 0; _creaseDirty = true;   // drop the crease wires (CreaseLines grows empty once cleared)
            _view.Display = DisplaySource.Authoring; _doc.Pattern?.Geometry.Clear(); _pieceDirty = true;   // OnRender turns the piece view off + frees the buffer
        }

        // Identify pieces (face regions bounded by the crease selection + mesh boundaries) and stage a
        // per-piece-tinted SPLIT render buffer (pos / normal / piece colour / world distance-to-boundary).
        // CPU-only here; the GL upload is staged for OnRender. Auto-shown after Propose, refreshed on Crease
        // angle change, on a brush stroke, and on the proposed-mesh preview toggle.
        void RebuildPieces()
        {
            if (_session == null || _doc.Pattern == null)
            { _view.Display = DisplaySource.Authoring; _doc.Pattern?.Geometry.Clear(); _pieceDirty = true; _gl?.InvalidateVisual(); return; }
            var P = _session.Mesh;
            int nF = P.Faces.Count;

            // Pieces ARE the painted face regions (the primary segmentation). Re-seed only if the map is missing
            // or stale for this topology — a CALLER-side Real-state concern, kept OUT of the pure derivation so a
            // piece-geometry .Value read can never re-partition the mesh (pre-I2 untangle; NODE-MODEL-IMPL §3).
            EnsurePieceMap(nF);
            var creaseMap = _doc.Pattern.CreaseMap.Value;   // Transient: derived from the now-ensured PieceMap
            if (creaseMap == null || creaseMap.Count == 0)
            { _view.Display = DisplaySource.Authoring; _doc.Pattern?.Geometry.Clear(); _pieceDirty = true; _gl?.InvalidateVisual(); return; }

            // Inputs the pure derivation needs that come from render/editor/modal state (caller concerns):
            float meshR = (_renderer != null) ? Math.Max(1e-4f, _renderer.Radius) : 1f;
            _activeEditor?.FaceFillBegin();   // precompute the remove-set ONCE so the colour callback stays O(1)
            // Colour source (grooves delineate the pieces in every case; this only sets the FILL tint):
            //   crease review (cam-modal): settled -> full rainbow patchwork (PieceColor); mid-drag -> neutral
            //     white (the crease shader still shows the pieces, but no colour churns while you slide the angle)
            //   brush mode: the active editor's per-face fill (active region light-blue / remove preview red /
            //     null -> white).
            Func<int, int, Vector3> colorOf = (f, pid) =>
                _camModal ? (_angleDragging ? Vector3.One : PieceColor(pid))
                          : (_activeEditor?.FaceFill(f, pid) ?? Vector3.One);

            var (pos, nrm, col, dist, edge) = DerivePieceBuffers(P, _doc.Pattern.PieceMap, creaseMap, meshR, colorOf);
            // SUPPLY the Pattern Real's geometry Transient (the buffers need render/editor inputs, so this is
            // Supplied not Grown — unlike CreaseMap). The View projects Pattern as the Pieces base source. (I2a)
            _doc.Pattern.Geometry.Supply(new RenderData { Kind = RenderKind.Pieces, Pos = pos, Nrm = nrm, Col = col, Dist = dist, Edge = edge });
            _pieceDirty = true;
            _view.Display = pos.Length > 0 ? DisplaySource.Pieces : DisplaySource.Authoring;
            _gl?.InvalidateVisual();
        }

        // Caller-side Real-state ensure: (re)partition only when the PieceMap is missing or stale for this
        // topology. Isolated from RebuildPieces' pure derivation (pre-I2 untangle; NODE-MODEL-IMPL §3).
        void EnsurePieceMap(int nF)
        {
            if (_doc.Pattern.PieceMap == null || _doc.Pattern.PieceMap.Length != nF) _doc.Pattern.Seed();
        }

        // PURE derivation: PieceMap (+ its derived creaseMap) -> the 5 SPLIT render buffers + the groove inset.
        // No Seed, no _renderer/_activeEditor/_camModal reads (those arrive as meshR + colorOf). This IS the I2a
        // Piece-Real Grow recipe; RebuildPieces is the caller that supplies the inputs and stores the result.
        // (node-model pre-I2 untangle; see docs/specs/NODE-MODEL-IMPL.md §3.)
        static (float[] pos, float[] nrm, float[] col, float[] dist, float[] edge) DerivePieceBuffers(
            PlanktonMesh P, int[] pieceId, System.Collections.Generic.HashSet<long> creaseMap, float meshR,
            Func<int, int, Vector3> colorOf)
        {
            int nV = P.Vertices.Count, nF = P.Faces.Count;

            double[] dp = null;   // proposed-preview removed -> live mesh coords
            Vector3 Pos(int v) => dp != null
                ? new Vector3((float)dp[v * 3], (float)dp[v * 3 + 1], (float)dp[v * 3 + 2])
                : new Vector3((float)P.Vertices[v].X, (float)P.Vertices[v].Y, (float)P.Vertices[v].Z);

            // Distance-to-boundary field: Dijkstra from crease vertices over mesh edges (world lengths), capped.
            float cap = 0.25f * meshR;
            var dist = new float[nV]; for (int v = 0; v < nV; v++) dist[v] = cap;
            var pq = new System.Collections.Generic.PriorityQueue<int, float>();
            foreach (long key in creaseMap)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFFL);
                if (a >= 0 && a < nV && dist[a] != 0f) { dist[a] = 0f; pq.Enqueue(a, 0f); }
                if (b >= 0 && b < nV && dist[b] != 0f) { dist[b] = 0f; pq.Enqueue(b, 0f); }
            }
            while (pq.TryDequeue(out int v, out float dv))
            {
                if (dv > dist[v] || dv >= cap) continue;
                Vector3 pv = Pos(v);
                foreach (int u in P.Vertices.GetVertexNeighbours(v))
                {
                    if (u < 0 || u >= nV || P.Vertices[u].IsUnused) continue;
                    float nd = dv + (Pos(u) - pv).Length;
                    if (nd < dist[u]) { dist[u] = nd; if (nd < cap) pq.Enqueue(u, nd); }
                }
            }

            // Averaged vertex normals from the display positions.
            var vN = new Vector3[nV];
            for (int f = 0; f < nF; f++)
            {
                if (pieceId[f] < 0) continue;
                int[] fv = P.Faces.GetFaceVertices(f); if (fv.Length != 3) continue;
                Vector3 fn = Vector3.Cross(Pos(fv[1]) - Pos(fv[0]), Pos(fv[2]) - Pos(fv[0]));
                vN[fv[0]] += fn; vN[fv[1]] += fn; vN[fv[2]] += fn;
            }

            // SPLIT buffers: 3 corners per triangle, each carrying its piece colour, the per-vertex boundary
            // distance, and the per-corner perpendicular distance to each of the triangle's 3 edges (BIG when
            // that edge is NOT a crease). The latter interpolates to the EXACT distance to the tri's own crease
            // edges, fixing the all-corners-on-crease ("V") triangle that the per-vertex field renders solid.
            const float BIG = 1e9f;
            static float PerpDist(Vector3 p, Vector3 a, Vector3 b)
            {
                Vector3 ab = b - a; float L = ab.Length;
                return L < 1e-12f ? (p - a).Length : Vector3.Cross(p - a, ab).Length / L;
            }
            var pos = new System.Collections.Generic.List<float>(nF * 9);
            var nrm = new System.Collections.Generic.List<float>(nF * 9);
            var col = new System.Collections.Generic.List<float>(nF * 9);
            var dst = new System.Collections.Generic.List<float>(nF * 3);
            var edg = new System.Collections.Generic.List<float>(nF * 12);
            for (int f = 0; f < nF; f++)
            {
                if (pieceId[f] < 0) continue;
                int[] fv = P.Faces.GetFaceVertices(f); if (fv.Length != 3) continue;
                Vector3 cc = colorOf(f, pieceId[f]);
                Vector3 p0 = Pos(fv[0]), p1 = Pos(fv[1]), p2 = Pos(fv[2]);
                bool c0 = creaseMap.Contains(Pattern.EdgeKey(fv[0], fv[1]));   // edge 0 = (v0,v1)
                bool c1 = creaseMap.Contains(Pattern.EdgeKey(fv[1], fv[2]));   // edge 1 = (v1,v2)
                bool c2 = creaseMap.Contains(Pattern.EdgeKey(fv[2], fv[0]));   // edge 2 = (v2,v0)
                // A groove belongs only to a triangle that actually CONTAINS a crease edge. Any triangle
                // with no crease edge gets flat-tinted (flag = 1): this kills every spurious groove that the
                // per-vertex field draws along a non-crease edge whose two endpoints are crease vertices
                // (e.g. edge 1-3 between crease-chain vertices, or an all-corners "bridge" triangle).
                float flat = (!c0 && !c1 && !c2) ? 1f : 0f;
                for (int k = 0; k < 3; k++)
                {
                    int v = fv[k]; Vector3 pp = Pos(v);
                    Vector3 nn = vN[v].LengthSquared > 1e-20f ? Vector3.Normalize(vN[v]) : Vector3.UnitZ;
                    pos.Add(pp.X); pos.Add(pp.Y); pos.Add(pp.Z);
                    nrm.Add(nn.X); nrm.Add(nn.Y); nrm.Add(nn.Z);
                    col.Add(cc.X); col.Add(cc.Y); col.Add(cc.Z);
                    dst.Add(dist[v]);
                    edg.Add(c0 ? PerpDist(pp, p0, p1) : BIG);
                    edg.Add(c1 ? PerpDist(pp, p1, p2) : BIG);
                    edg.Add(c2 ? PerpDist(pp, p2, p0) : BIG);
                    edg.Add(flat);
                }
            }
            return (pos.ToArray(), nrm.ToArray(), col.ToArray(), dst.ToArray(), edg.ToArray());
        }

        // (The active-region + remove-preview tints moved to the Piecer editor, which owns the brush colouring.)

        // Per-piece colour: cycle the open-color hues at shade 3 (standardized / accessible). Pieces a full
        // palette-cycle apart in id share a colour, but the deboss grooves always separate them.
        static Vector3 PieceColor(int id)
        {
            int n = OpenColor.Rainbow3.Length;
            return OpenColor.Rainbow3[((id % n) + n) % n];
        }

        // Worker entry (background thread): pure compute, dispatched single vs multi-component.
        void RunBake()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const long budgetMs = 100000;   // Solve bake time budget (100s; was 10s) — caps the develop+subdivide loop
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
            RebindPattern();                                 // 1->4 renumbers verts/faces -> rewrap the partition on the new mesh
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
            if (aligned && _flatRenderer != null)
            {
                _flatRenderer.Upload(_flat);                                  // re-upload the refined M'
                _sim.StrainPct = RelErr(_session.Mesh, _flat) * 100.0;    // strain stays ~constant (subdivide is metric-preserving)
            }
            _meshDirty = true;
            _developed.Rot();         // authoring mesh changed -> stale the developed view (falls back to authoring until re-Solve). Becomes a real cascade edge once the mesh is a node.
            ClearProposedCreases();   // 1->4 renumbers vertices -> labels invalid
            Title = "PieceSolver — subdivided  (" + _session.Mesh.Vertices.Count + " verts)";
            _gl?.InvalidateVisual();
        }

        // Reload the current mesh from disk and reset the flow. Same bounds -> camera kept (no reframe).
        // A true Revert: discard all work and reload the document from the file on disk. The standalone global
        // op behind the Revert button / Ctrl+R / the journal's Revert command. (Solve must NOT call this — it
        // develops a derived clone instead, so the authoring mesh + Pattern survive. See docs/archive/SOLVER-PHASE.md.)
        void Revert()
        {
            if (_meshPath == null || !System.IO.File.Exists(_meshPath)) return;
            try { _session = new FlowSession(MeshIO.Load(_meshPath)); }
            catch (Exception ex) { Title = "PieceSolver — reset failed: " + ex.Message; return; }
            RebindPattern();          // the partition companion now wraps the reloaded mesh
            _developed.Rot();         // authoring mesh changed -> stale the developed view (falls back to authoring until re-Solve). Becomes a real cascade edge once the mesh is a node.
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
        // Returns true once a usable flat map + anchor exist. (Solve's worker-thread bake uses FlattenPure,
        // the GL-free twin of this; this UI-thread variant is the interactive flatten entry point.)
        bool EnsureFlat()
        {
            if (_session == null) return false;
            if (_bffNeeded)
            {
                bool ok = Bff.TryFlatten(_session.Mesh, out var flat, out var log);
                if (!string.IsNullOrWhiteSpace(log)) _doc.Comment(log.TrimEnd());
                if (!ok || flat == null) return false;   // failure logged; don't start the sim
                ShowFlat(flat);
                _bffNeeded = false;
            }
            return _flat != null && _M0 != null;
        }

        // Upload the BFF flat map M' and place it beside M on the z=0 plane (to +X of M by both radii
        // plus a gap). Driven by EnsureFlat's lazy first flatten. Sets _hasFlat.
        void ShowFlat(PlanktonMesh flat)
        {
            if (_renderer == null || _flatRenderer == null) return;   // GL views not built yet (pre-first-render)
            _flat = flat;             // retain M' — the actual mesh the isometric solver mutates
            CaptureM0();              // snapshot M's positions once per mesh (proximity anchor for the solver)
            _flatRenderer.Upload(flat);   // sets _flatRenderer.Center / .Radius from the flat geometry
            PlaceFlat();              // place M' beside M on the z=0 plane

            _hasFlat = true;
            // NOTE: intentionally no camera reframe here — keep the current view when the 2D pattern
            // appears (M' is placed beside M; the user can zoom-extents manually if they want it framed).
            _gl?.InvalidateVisual();
        }

        // Place the flat map M' beside M on the z=0 plane (to +X of M by both radii plus a gap). Called
        // from ShowFlat (first flatten) so M' sits alongside M.
        void PlaceFlat()
        {
            if (_renderer == null || _flatRenderer == null) return;
            float gap = 0.3f * (_renderer.Radius + _flatRenderer.Radius);
            var targetCenter = new Vector3(
                _renderer.Center.X + _renderer.Radius + _flatRenderer.Radius + gap,
                _renderer.Center.Y,
                0f);
            _flatRenderer.ModelOffset = targetCenter - _flatRenderer.Center;
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
            lines.AddRange(_doc.EventLog);                              // the full ordered event log: commands + setpiece + undo/redo
            try { System.IO.File.WriteAllLines(dlg.FileName, lines); _doc.Comment("saved " + _doc.EventLog.Count + " events -> " + System.IO.Path.GetFileName(dlg.FileName)); }
            catch (Exception ex) { _doc.Comment("save failed: " + ex.Message); }
        }

        // File > Import (no shortcut — Ctrl+O is reserved for the future File > Open): pick an STL / FBX / OBJ
        // and load it — same path as the startup/mesh-set load,
        // journaled as a `load` op. FBX preserves Rhino's unwelded seam topology (one component per brep face).
        void ImportMesh()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Mesh (*.stl;*.fbx;*.obj)|*.stl;*.fbx;*.obj|STL (*.stl)|*.stl|FBX (*.fbx)|*.fbx|OBJ (*.obj)|*.obj|All files (*.*)|*.*",
                InitialDirectory = @"C:\Temp"
            };
            if (dlg.ShowDialog() != true) return;
            Execute(StudioCommand.Load(dlg.FileName), record: true);
        }

        void OpenAndReplay()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Journal (*.journal)|*.journal|All files (*.*)|*.*", InitialDirectory = @"C:\Temp" };
            if (dlg.ShowDialog() != true) return;
            var cmds = new System.Collections.Generic.List<StudioCommand>();
            try { foreach (var ln in System.IO.File.ReadAllLines(dlg.FileName)) { var c = StudioCommand.Parse(ln); if (c != null) cmds.Add(c); } }
            catch (Exception ex) { _doc.Comment("load failed: " + ex.Message); return; }
            ReplayJournal(cmds);
        }

        // Replay a command list against the live GUI, one command per timer tick (so the viewport
        // repaints between steps). Each command is timed; runs also log FlowMetrics, giving numbers
        // directly comparable to the CLI baseline - the soft perf/value drift signal across commits.
        void ReplayJournal(System.Collections.Generic.List<StudioCommand> cmds)
        {
            if (cmds == null || cmds.Count == 0) { _doc.Comment("nothing to replay"); return; }
            _replayQueue = cmds; _replayPos = 0;
            _doc.Comment("--- replay: " + cmds.Count + " commands ---");
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
                _doc.Comment(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0,3}/{1}  {2}  [{3:0.0} ms]{4}", _replayPos, _replayQueue.Count, _replaySolveLine, ms, ex));
            }
            if (_replayQueue == null || _replayPos >= _replayQueue.Count) { _replayTimer.Stop(); _doc.Comment("--- replay done ---"); return; }
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
            _doc.Comment(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0,3}/{1}  {2}  [{3:0.0} ms]", _replayPos, _replayQueue.Count, c.Serialize(), sw.Elapsed.TotalMilliseconds));
        }
        bool _replaySolvePending;   // a replayed Solve is in flight; log + advance once _baking clears
        string _replaySolveLine;    // the serialized Solve line, held for the deferred completion log

        void Echo(string line) => _console?.AppendLine(line);          // the Console sink for every Doc-recorded line (op/command OR '#' comment)

        void UpdateStatus()
        {
            if (StatusText == null) return;
            int v = _session != null ? _session.Mesh.Vertices.Count : 0;
            string eIso = _hasFlat
                ? "  ·  E_iso " + _lastEIso.ToString("0.###e+0", System.Globalization.CultureInfo.InvariantCulture)
                : "";
            StatusText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "upload {0:0.0} ms · {1} verts{2}", _lastUploadMs, v, eIso);
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

        // Mouse scheme: right-drag = orbit, Shift+right-drag = pan. Wheel = zoom (wired in ctor). Left-button +
        // hover delegate to the active editor (the Crease brush, when one is active); else they do nothing.
        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _lastMouse = e.GetPosition(_gl);
            _previewDot.Visibility = Visibility.Collapsed;   // hide the footprint preview while dragging
            if (e.ChangedButton == MouseButton.Right)
            {
                _drag = (_heldMods & ModifierKeys.Shift) != 0 ? DragMode.Pan : DragMode.Orbit;
                _rightClickArmed = _drag == DragMode.Orbit;   // a plain (no-Shift) right-click, if it doesn't drag, pops the piece menu
                _rightDownPos = _lastMouse;
            }
            else if (e.ChangedButton == MouseButton.Left)
            {
                _drag = DragMode.Edit;
                if (EditorActive) _activeEditor.OnPointerDown(_lastMouse, _heldMods);
            }
            else return;
            _gl.CaptureMouse();   // keep dragging even if the cursor leaves the viewport
        }

        void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _drag = DragMode.None;
            _gl.ReleaseMouseCapture();
            if (EditorActive) _activeEditor.OnPointerUp(_lastMouse);
            if (e.ChangedButton == MouseButton.Right && _rightClickArmed)
            {
                _rightClickArmed = false;
                var up = e.GetPosition(_gl);
                double dx = up.X - _rightDownPos.X, dy = up.Y - _rightDownPos.Y;
                if (dx * dx + dy * dy <= RightClickPx * RightClickPx && EditorActive) ShowPieceMenu();   // a click, not an orbit -> pop the menu
            }
        }

        // The right-click (no-drag) piece context menu: a disabled count header ("5 Pieces" / "No Pieces
        // Selected"), then Merge (needs 2+ selected) and Delete (DelPiece, needs 1+). Built fresh per pop so the
        // enabled state matches the live selection. Only while the Piecer is the active editor.
        void ShowPieceMenu()
        {
            int n = _doc.Pieces.Count;
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = _gl,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse
            };
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = n == 0 ? "No Pieces Selected" : PieceId.Name(n), IsEnabled = false });
            menu.Items.Add(new System.Windows.Controls.Separator());
            var merge = new System.Windows.Controls.MenuItem { Header = "Merge", IsEnabled = n >= 2 };
            merge.Click += (s, e) => TryMerge();
            menu.Items.Add(merge);
            var del = new System.Windows.Controls.MenuItem { Header = "Delete", IsEnabled = n >= 1 };
            del.Click += (s, e) => TryDelPiece();
            menu.Items.Add(del);
            menu.IsOpen = true;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(_gl);
            if (_rightClickArmed)   // any travel past the threshold cancels the pending menu — even a round trip back to the press point
            {
                double rdx = p.X - _rightDownPos.X, rdy = p.Y - _rightDownPos.Y;
                if (rdx * rdx + rdy * rdy > RightClickPx * RightClickPx) _rightClickArmed = false;
            }
            if (_drag == DragMode.None)
            {
                _lastHover = p;
                if (EditorActive) _activeEditor.OnHover(p);   // footprint preview on hover (no-op when no editor is active)
                return;
            }
            if (_drag == DragMode.Edit)
            {
                if (EditorActive) _activeEditor.OnPointerMove(p);
                _lastMouse = p;
                return;
            }
            float dx = (float)(p.X - _lastMouse.X), dy = (float)(p.Y - _lastMouse.Y);
            _lastMouse = p;
            switch (_drag)
            {
                case DragMode.Orbit:
                    _view.Camera.Orbit(dx, dy);
                    _view.Rot();
                    break;
                case DragMode.Pan:
                    PanCamera(dx, dy);
                    break;
            }
        }

        // Shift+right-drag pan -> the View's camera (speed scales with zoom inside Camera.Pan).
        void PanCamera(float dx, float dy) { _view.Camera.Pan(dx, dy); _view.Rot(); }

        // ===================== Folding 3D|2D split panes =====================
        // The centre holds two panes — the 3D viewport (CenterHost) and the 2D placeholder. Dragging the divider
        // to an edge stashes that pane into a thin gutter tab; clicking the gutter pops it back to the width it had
        // when stashed. Mirrors vzome-playground's createPaneLayout (remembered width = restoreCodeW).
        enum PaneState { Both, Fold3D, Fold2D }
        PaneState _panes = PaneState.Both;
        double _r3D = 0.5;             // 3D-pane share of the split (0..1) when both open — STAR-sized, so the divider
        double _dragR3D;               //   stays proportional and can't be pushed off-screen when the window resizes.
        bool _dividerDragging;         // true between divider mousedown and mouseup (capture is held on SplitGrid)
        const double GutterPx = 30, CollapseAt = 70, DividerPx = 8, MinShare = 0.05;

        void ApplyPanes()
        {
            bool both = _panes == PaneState.Both, f3 = _panes == PaneState.Fold3D, f2 = _panes == PaneState.Fold2D;
            if (f3)      { Col3D.Width = new GridLength(GutterPx);                 Col2D.Width = new GridLength(1, GridUnitType.Star); }
            else if (f2) { Col3D.Width = new GridLength(1, GridUnitType.Star);     Col2D.Width = new GridLength(GutterPx); }
            else         { Col3D.Width = new GridLength(_r3D, GridUnitType.Star);  Col2D.Width = new GridLength(1 - _r3D, GridUnitType.Star); }
            CenterHost.Visibility    = f3 ? Visibility.Collapsed : Visibility.Visible;
            Gutter3D.Visibility      = f3 ? Visibility.Visible   : Visibility.Collapsed;
            View2DPane.Visibility    = f2 ? Visibility.Collapsed : Visibility.Visible;
            Gutter2D.Visibility      = f2 ? Visibility.Visible   : Visibility.Collapsed;
            CenterDivider.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
            if (!f3) InvalidateView();   // viewport showing -> nudge a redraw at the new size
        }

        void DividerDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { _r3D = 0.5; _panes = PaneState.Both; ApplyPanes(); return; }
            _dragR3D = _r3D;               // remember the pre-drag share, so a stash pops back to it
            _dividerDragging = true;
            SplitGrid.CaptureMouse();   // capture the STABLE container, not the divider — hiding it on snap won't drop the drag
        }

        void DividerMove(object sender, MouseEventArgs e)
        {
            if (!_dividerDragging) return;
            double avail = SplitGrid.ActualWidth - DividerPx;
            if (avail < 1) return;
            double nw = e.GetPosition(SplitGrid).X - DividerPx / 2;            // 3D width at the pointer
            if (nw < CollapseAt)              { _r3D = _dragR3D; _panes = PaneState.Fold3D; }   // stash 3D, pop back to pre-stash share
            else if (avail - nw < CollapseAt) { _r3D = _dragR3D; _panes = PaneState.Fold2D; }   // stash 2D, keep 3D's share
            else { _r3D = Math.Max(MinShare, Math.Min(1 - MinShare, nw / avail)); _panes = PaneState.Both; }
            ApplyPanes();
        }

        void DividerUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dividerDragging) return;
            _dividerDragging = false;
            SplitGrid.ReleaseMouseCapture();
        }

        // ===================== Crease brush — host-side helpers (the interaction lives in Piecer) =====================

        // [ / ] grow / shrink the brush (the same VM param the BRUSH Size slider binds to).
        void ResizeBrush(int delta) => _sim.BrushSize = Math.Clamp(_sim.BrushSize + delta, 1.0, 10.0);   // [ / ] step one notch

        // Picking + the brush footprint (PickFace/PickSurface, BrushWorldRadius/ScreenRadiusPx/BrushSpacingPx)
        // now live on the View (IEditorHost).

        void UpdatePreview(System.Windows.Point cursor)
        {
            if (!EditorActive || !_view.PickSurface(cursor, out var hit))
            { _previewDot.Visibility = Visibility.Collapsed; return; }
            double rpx = _view.ScreenRadiusPx(hit);
            _previewDot.Width = _previewDot.Height = 2.0 * rpx;
            _previewDot.Margin = new Thickness(cursor.X - rpx, cursor.Y - rpx, 0, 0);
            _previewDot.Visibility = Visibility.Visible;
        }

        // The View is now the editor's host (IEditorHost) — see PieceSolver/View.cs. MainWindow keeps only the
        // render rebuilds (RebuildPieces / RebuildCreaseOverlay) + the preview dot, wired into the View as hooks.

        void InvalidateView() { _gl?.InvalidateVisual(); }

        void OnRender(TimeSpan delta)
        {
            if (!_glInit)
            {
                _renderer = new MeshView();
                _renderer.SetNoiseVolume(NoiseVolume.Blue(64, 1337), 64);    // solid blue-noise volume for the surface LIC
                _flatRenderer = new MeshView(); _flatRenderer.EnsureProgram();   // BFF flat map, drawn beside M
                _flatRenderer.ShowEdges = true;   // M' is flat (uniform shading) -> reveal its triangulation with edges
                _grid = new GroundGrid();
                _glInit = true;
            }

            // apply a queued matcap texture (GL thread = here); M' shades the same as M
            if (_matcapDirty && _renderer != null && _matcapPx != null)
            {
                _renderer.SetMatcap(_matcapPx, _matcapW, _matcapH);
                _flatRenderer?.SetMatcap(_matcapPx, _matcapW, _matcapH);
                _matcapDirty = false;
            }
            // Default-shading pair (neutral + environment), uploaded to both views like the picked matcap.
            if (_neutralDirty && _renderer != null && _neutralPx != null)
            {
                _renderer.SetNeutralMatcap(_neutralPx, _neutralW, _neutralH);
                _flatRenderer?.SetNeutralMatcap(_neutralPx, _neutralW, _neutralH);
                _neutralDirty = false;
            }
            if (_envDirty && _renderer != null && _envPx != null)
            {
                _renderer.SetEnvMatcap(_envPx, _envW, _envH);
                _flatRenderer?.SetEnvMatcap(_envPx, _envW, _envH);
                _envDirty = false;
            }
            // staged proposed-crease line vertices (GL thread); gate on !_baking like the mesh upload
            // pull the crease geometry from Pattern's Grown CreaseLines Transient + stage it (Real -> RenderData -> GL).
            if (_creaseDirty && !_baking && _renderer != null)
            {
                var creaseRd = _doc.Pattern?.CreaseLines.Value;   // Grown: grows fresh on read
                _renderer.SetCreases(creaseRd?.Segments ?? System.Array.Empty<float>());
                _creaseDirty = false;
            }
            // I2a: pull the Pieces SPLIT geometry from the Pattern Real (Supplied by RebuildPieces) + stage it.
            if (_pieceDirty && !_baking && _renderer != null)
            {
                if (_doc.Pattern != null && _doc.Pattern.Geometry.Peek(out var pieceRd) && pieceRd.Pos != null && pieceRd.Pos.Length > 0)
                    _renderer.SetPieces(pieceRd.Pos, pieceRd.Nrm, pieceRd.Col, pieceRd.Dist, pieceRd.Edge);
                else _renderer.ClearPieces();
                _pieceDirty = false;
            }

            // re-upload when the mesh changed (run / subdivide / reset / load) or shading toggled;
            // re-fit the camera after a load (new bounds). Upload time feeds the perf readout.
            if (_meshDirty && !_baking && _session != null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                UploadForDisplay();   // Display-aware: Developed => governed _developed, else authoring mesh
                sw.Stop();
                _lastUploadMs = sw.Elapsed.TotalMilliseconds;
                _meshDirty = false;
                UpdateStatus();
            }

            // Re-fit the camera (and ground grid) when flagged — after a load/reset, and when the BFF
            // flat map M' first appears (so M' isn't left off to the side). Fits M, plus M' if shown.
            if (_reframe && _renderer != null && _renderer.HasMesh)
            {
                Vector3 fitCenter = _renderer.Center; float fitRadius = _renderer.Radius;
                if (_hasFlat && _flatRenderer != null && _flatRenderer.HasMesh)
                {
                    Vector3 flatCen = _flatRenderer.Center + _flatRenderer.ModelOffset;   // M' placed center
                    float flatRad = _flatRenderer.Radius;
                    Vector3 bbLo = Vector3.ComponentMin(fitCenter - new Vector3(fitRadius), flatCen - new Vector3(flatRad));
                    Vector3 bbHi = Vector3.ComponentMax(fitCenter + new Vector3(fitRadius), flatCen + new Vector3(flatRad));
                    fitCenter = (bbLo + bbHi) * 0.5f; fitRadius = MathF.Max(1e-4f, (bbHi - bbLo).Length * 0.5f);
                }
                _view.Camera.Frame(fitCenter, fitRadius);
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

            GL.ClearColor(30f / 255f, 40f / 255f, 50f / 255f, 1.0f);   // background magic colour (30,40,50)
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            double w = _gl.ActualWidth, h = Math.Max(1, _gl.ActualHeight);
            float aspect = (float)(w / h);

            // Z-up (Rhino convention): elevation raises +Z, up vector is +Z. Matrices come from the View's camera.
            Matrix4 view = _view.Camera.ViewMatrix;
            float r = _renderer != null ? _renderer.Radius : 1f;
            Matrix4 proj = _view.Camera.ProjMatrix(aspect, r);

            _grid?.Draw(view, proj);   // ground reference, behind the mesh (depth-tested)
            if (_renderer != null)
            {
                _renderer.Sharpness = (float)_sim.Facet; _renderer.FacetExp = (float)_sim.FacetExp;   // Facet -> shader
                _renderer.Shine = (float)_sim.Shine; _renderer.UseMatcap = _sim.UseMatcap;            // Shine shading
                _renderer.ShowCreases = _creaseCount > 0;                                          // proposed-crease overlay
                _renderer.ShowPieces = _view.Display == DisplaySource.Pieces; _renderer.InsetWidth = (float)_sim.InsetWidthFrac * Math.Max(1e-4f, _renderer.Radius);   // per-piece view; live inset width (world-relative)
                // Surface-LIC field (modulates the matcap). Recompute only when the mesh/mode changed.
                _renderer.LicMode = _sim.ShowRuling ? 1 : 0;
                if (_sim.ShowRuling && _renderer.HasMesh && _session != null && _rulingsDirty && !_baking)
                {
                    float fmax;
                    var fld = RulingField.ComputeField(_session.Mesh, out fmax);
                    _renderer.SetField(fld, fmax);
                    _rulingsDirty = false;
                }
                // Surface-LIC scale, derived from the noise TEXEL size so consecutive march taps stay
                // CORRELATED (~1 texel/tap) -> coherent streaks. The old 0.03*Radius step was ~4 texels
                // per tap, so the 24-tap convolution sampled decorrelated noise and washed out to a flat
                // grey ("scale massively off"). NoiseVolume is 64^3, repeated LIC_TILES across the model.
                float licTiles = MathF.Max(2f, (float)_sim.LicGrain), NOISE_N = 64f;
                float licExtent = MathF.Max(1e-3f, 2f * _renderer.Radius);
                _renderer.NoiseFreq   = licTiles / licExtent;
                _renderer.LicStep     = licExtent / (licTiles * NOISE_N);   // one noise texel per march tap
                _renderer.LicTaps     = System.Math.Clamp(_sim.LicLength, 2, 64);
                _renderer.LicStrength = MathF.Max(0f, MathF.Min(1f, (float)_sim.LicAlpha));
                _renderer.CurvMin = (float)System.Math.Clamp(_sim.LicCurvMin, 0.0, 1.0);
                _renderer.CurvMax = MathF.Max(_renderer.CurvMin + 0.01f, (float)System.Math.Clamp(_sim.LicCurvMax, 0.0, 1.0));   // keep max > min (smoothstep)
                _renderer.ShowSeams = _sim.FixBSplineEdges;
                if (_sim.FixBSplineEdges && _renderer.HasMesh && _seamsDirty && !_baking)
                {
                    _renderer.SetSeams(_seamLineF ?? System.Array.Empty<float>());
                    _renderer.SetSeamControls(_seamCtrlF ?? System.Array.Empty<float>());
                    _seamsDirty = false;
                }
                _renderer.Draw(view, proj);
            }
            if (_hasFlat && _flatRenderer != null && _flatRenderer.HasMesh)   // BFF flat map M', beside M on the z=0 plane
            {
                _flatRenderer.Sharpness = (float)_sim.Facet; _flatRenderer.FacetExp = (float)_sim.FacetExp;
                _flatRenderer.Shine = (float)_sim.Shine; _flatRenderer.UseMatcap = _sim.UseMatcap;
                _flatRenderer.Draw(view, proj);
            }
        }
    }
}
