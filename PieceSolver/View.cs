using System;
using System.Windows;
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

        public Doc Doc { get; }
        public DisplaySource Display { get; set; } = DisplaySource.Authoring;
        public Camera Camera { get; } = new Camera();   // the orbit camera — Ephemeral view state (see Camera.cs)

        public View(Doc doc, GLWpfControl gl, Func<PlanktonMesh> mesh, Func<double> brushSize,
                    Action refreshPieces, Action refreshCreaseOverlay, Action<Point> showPreview, Action hidePreview)
        {
            Doc = doc; _gl = gl; _mesh = mesh; _brushSize = brushSize;
            _refreshPieces = refreshPieces; _refreshCreaseOverlay = refreshCreaseOverlay;
            _showPreview = showPreview; _hidePreview = hidePreview;
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
    }
}
