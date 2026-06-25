using System;
using System.Windows;
using System.Windows.Input;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using Plankton;

namespace PieceSolver
{
    // Which geometry the viewport shows — ONE value at a time, so the old _showPieces-over-developed
    // occlusion is unrepresentable. Ephemeral view state (like the camera): not Real, not a Transient,
    // not saved / not undoable. See docs/specs/DOC-SPEC.md (Real / Transient / Ephemeral).
    enum DisplaySource { Authoring, Pieces, Developed }

    // The interactive viewport, bound to a Doc — and now the editor's host (IEditorHost). Owns the DISPLAY
    // state, the orbit Camera, picking (screen -> mesh), the brush footprint, and the repaint poke (Rot).
    // The render rebuilds (RefreshPieces / RefreshCreaseOverlay) and the brush-preview dot still live on the
    // shell (the render-loop hasn't drained yet), so View delegates just those to injected hooks; everything
    // else is View-native. MainWindow is now only the WPF chrome shell + render loop. One View today, but a
    // real abstraction (N later); the stack of Reals is the next shape. See docs/specs/DOC-SPEC.md + AGENTS.md.
    sealed class View : IEditorHost
    {
        readonly GLWpfControl _gl;            // the GL surface — size + repaint
        readonly Func<PlanktonMesh> _mesh;    // the live mesh, for picking (bridges session-ownership for now)
        readonly Func<double> _brushSize;     // the brush-size notch (SimSettings.BrushSize), for the footprint
        // Shell hooks — the un-drained render/preview services; they collapse to View-native when the
        // render-loop + preview dot move onto View.
        readonly Action _refreshPieces, _refreshCreaseOverlay, _hidePreview;
        readonly Action<Point> _showPreview;
        readonly Func<bool> _baking, _camModal;   // transient blocking states — injected so EditorActive can gate on them
        // Pointer-pipeline shell hooks: the held-modifier state (tracked from key events on the shell, NOT read live)
        // and the right-click piece context menu (a WPF ContextMenu the shell owns). Injected so the moved pointer
        // handlers reach them without the whole window.
        readonly Func<ModifierKeys> _heldMods;
        readonly Action _showPieceMenu;

        public Doc Doc { get; }
        public DisplaySource Display { get; set; } = DisplaySource.Authoring;
        public Camera Camera { get; } = new Camera();   // the orbit camera — Ephemeral view state (see Camera.cs)

        // ---- POINTER pipeline (Ephemeral): the mouse-gesture state machine ----
        Point _lastMouse;
        enum DragMode { None, Orbit, Pan, Edit }
        DragMode _drag = DragMode.None;   // right-drag = orbit, Shift+right-drag = pan, left-drag = Crease brush
        Point _rightDownPos;   // where a right-button press started — to tell a click from an orbit-drag
        bool _rightClickArmed; // a plain right-press that, released without dragging, pops the piece menu
        const double RightClickPx = 6.0;   // right-release within this of the press = a click (menu); beyond = an orbit
        Point _lastHover;      // last hover position, for the footprint preview
        public Point LastHover => _lastHover;   // the shell's brush-resize keys re-render the preview at this point

        // EDITOR (Ephemeral): the active interaction the View hosts. The Piecer instance is retained so its
        // selection persists across activations; ActiveEditor is non-null only once a proposal is accepted.
        readonly Piecer _piecer;
        public Piecer Piecer => _piecer;
        public Editor ActiveEditor { get; set; }
        // The brush is LIVE only when an editor is bound AND we're not in a transient blocking state (a bake or a
        // camera-modal) — the old BrushAvailable / EditorActive gate, now expressed on the host.
        public bool EditorActive => ActiveEditor != null && !_baking() && !_camModal();

        public View(Doc doc, GLWpfControl gl, Func<bool> baking, Func<bool> camModal,
                    Func<ModifierKeys> heldMods, Action showPieceMenu,
                    Func<PlanktonMesh> mesh, Func<double> brushSize,
                    Action refreshPieces, Action refreshCreaseOverlay, Action<Point> showPreview, Action hidePreview)
        {
            Doc = doc; _gl = gl; _baking = baking; _camModal = camModal; _mesh = mesh; _brushSize = brushSize;
            _heldMods = heldMods; _showPieceMenu = showPieceMenu;
            _refreshPieces = refreshPieces; _refreshCreaseOverlay = refreshCreaseOverlay;
            _showPreview = showPreview; _hidePreview = hidePreview;
            _piecer = new Piecer(this);   // the Piecing editor talks to the View (its host); ctor doesn't use the host
        }

        // Mark the rendered frame stale and schedule its re-grow. The frame is itself a Transient (derived from
        // Doc + Camera + Display); since rendering is PUSH (not a .Value pull), Rot both invalidates and pokes
        // the paint tick. Later, rotting the viewport may cascade to its 2D-element children — same machinery.
        public void Rot() => _gl?.InvalidateVisual();

        // ---- IEditorHost: identity ----
        public PlanktonMesh Mesh => _mesh();
        public Pattern Pattern => Doc.Pattern;
        public bool ShowPieces => Display == DisplaySource.Pieces;

        // ---- IEditorHost: picking (Camera + the live mesh; stateless ray math in Picker) ----
        public bool PickRay(Point screen, out Vector3 eye, out Vector3 rd)
        {
            eye = default; rd = default;
            if (_mesh() == null) return false;
            return Camera.PickRay(screen, _gl.ActualWidth, _gl.ActualHeight, out eye, out rd);
        }
        public bool PickSurface(Point screen, out Vector3 hit)
        {
            hit = default;
            return PickRay(screen, out var eye, out var rd) && Picker.RayMeshHit(eye, rd, _mesh(), out hit, out _);
        }
        public bool PickFace(Point screen, out int face, out Vector3 hit)
        {
            face = -1; hit = default;
            return PickRay(screen, out var eye, out var rd) && Picker.RayMeshHit(eye, rd, _mesh(), out hit, out face);
        }

        // ---- IEditorHost: brush footprint (world radius, projected to screen px) ----
        // Brush Size is a 1..10 notch indexing a Fibonacci table of world radii (fine low end, fast growth up top).
        static readonly double[] BrushRadii = { 0.1, 0.2, 0.3, 0.5, 0.8, 1.3, 2.1, 3.4, 5.5, 8.9 };
        public double BrushWorldRadius => BrushRadii[Math.Clamp((int)Math.Round(_brushSize()), 1, 10) - 1];

        // The brush's world radius projected to screen pixels at the given surface point's depth.
        public double ScreenRadiusPx(Vector3 hit)
        {
            Vector3 dir = Camera.Dir;
            Vector3 eye = Camera.Eye;
            double dist = Math.Max(1e-4, Vector3.Dot(hit - eye, -dir));   // depth along the view axis
            double h = Math.Max(1, _gl.ActualHeight);
            double tanH = Math.Tan(MathHelper.DegreesToRadians(45f) * 0.5);
            return BrushWorldRadius * h / (2.0 * dist * tanH);
        }

        // Dab spacing ~ half the brush's on-screen radius, so spacing scales with the brush and zoom.
        public double BrushSpacingPx(Point screen)
        {
            if (PickSurface(screen, out var hit)) return Math.Max(1.0, 0.5 * ScreenRadiusPx(hit));
            return 8.0;
        }

        // ---- IEditorHost: render / preview, delegated to the shell (drain with the render-loop) ----
        public void RefreshPieces() => _refreshPieces?.Invoke();
        public void RefreshCreaseOverlay() => _refreshCreaseOverlay?.Invoke();
        public void ShowBrushPreview(Point screen) => _showPreview?.Invoke(screen);
        public void HideBrushPreview() => _hidePreview?.Invoke();
        public void Invalidate() => Rot();

        // ---- POINTER handlers (wired by the shell onto the GL surface) ----
        // Mouse scheme: right-drag = orbit, Shift+right-drag = pan. Wheel = zoom. Left-button +
        // hover delegate to the active editor (the Crease brush, when one is active); else they do nothing.
        public void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _lastMouse = e.GetPosition(_gl);
            HideBrushPreview();   // hide the footprint preview while dragging
            if (e.ChangedButton == MouseButton.Right)
            {
                _drag = (_heldMods() & ModifierKeys.Shift) != 0 ? DragMode.Pan : DragMode.Orbit;
                _rightClickArmed = _drag == DragMode.Orbit;   // a plain (no-Shift) right-click, if it doesn't drag, pops the piece menu
                _rightDownPos = _lastMouse;
            }
            else if (e.ChangedButton == MouseButton.Left)
            {
                _drag = DragMode.Edit;
                if (EditorActive) ActiveEditor.OnPointerDown(_lastMouse, _heldMods());
            }
            else return;
            _gl.CaptureMouse();   // keep dragging even if the cursor leaves the viewport
        }

        public void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _drag = DragMode.None;
            _gl.ReleaseMouseCapture();
            if (EditorActive) ActiveEditor.OnPointerUp(_lastMouse);
            if (e.ChangedButton == MouseButton.Right && _rightClickArmed)
            {
                _rightClickArmed = false;
                var up = e.GetPosition(_gl);
                double dx = up.X - _rightDownPos.X, dy = up.Y - _rightDownPos.Y;
                if (dx * dx + dy * dy <= RightClickPx * RightClickPx && EditorActive) _showPieceMenu?.Invoke();   // a click, not an orbit -> pop the menu
            }
        }

        public void OnMouseMove(object sender, MouseEventArgs e)
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
                if (EditorActive) ActiveEditor.OnHover(p);   // footprint preview on hover (no-op when no editor is active)
                return;
            }
            if (_drag == DragMode.Edit)
            {
                if (EditorActive) ActiveEditor.OnPointerMove(p);
                _lastMouse = p;
                return;
            }
            float dx = (float)(p.X - _lastMouse.X), dy = (float)(p.Y - _lastMouse.Y);
            _lastMouse = p;
            switch (_drag)
            {
                case DragMode.Orbit:
                    Camera.Orbit(dx, dy);
                    Rot();
                    break;
                case DragMode.Pan:
                    PanCamera(dx, dy);
                    break;
            }
        }

        // Shift+right-drag pan -> the camera (speed scales with zoom inside Camera.Pan).
        public void PanCamera(float dx, float dy) { Camera.Pan(dx, dy); Rot(); }

        public void OnMouseWheel(object sender, MouseWheelEventArgs e) { Camera.Zoom(e.Delta); Rot(); }
        public void OnMouseLeave(object sender, MouseEventArgs e) => HideBrushPreview();
    }
}
