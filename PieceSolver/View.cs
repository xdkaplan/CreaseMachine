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

    // The interactive viewport, bound to a Doc — the home for state that has been smeared across the
    // MainWindow god-file. Owns the DISPLAY state, the orbit Camera, the repaint poke (Rot), and picking
    // (screen -> mesh). It holds the GL surface (size + invalidate) and a live-mesh accessor; the full
    // render-loop, the IEditorHost role, Editor hosting, and the stack of Elements drain in next. One View
    // now, but a real abstraction (N later); MainWindow shrinks to the WPF chrome shell that hosts it.
    // See docs/specs/DOC-SPEC.md + AGENTS.md.
    sealed class View
    {
        readonly GLWpfControl _gl;          // the GL surface — size + repaint (View is starting to own its viewport)
        readonly Func<PlanktonMesh> _mesh;  // the live mesh, for picking (bridges session-ownership for now)

        public Doc Doc { get; }
        public DisplaySource Display { get; set; } = DisplaySource.Authoring;
        public Camera Camera { get; } = new Camera();   // the orbit camera — Ephemeral view state (see Camera.cs)

        public View(Doc doc, GLWpfControl gl, Func<PlanktonMesh> mesh) { Doc = doc; _gl = gl; _mesh = mesh; }

        // Mark the rendered frame stale and schedule its re-grow. The frame is itself a Transient (derived from
        // Doc + Camera + Display); since rendering is PUSH (not a .Value pull), Rot both invalidates and pokes
        // the paint tick. Later, rotting the viewport may cascade to its 2D-element children — same machinery.
        public void Rot() => _gl?.InvalidateVisual();

        // ---- picking: screen point -> mesh, via the Camera + the live mesh (stateless ray math in Picker) ----

        public bool PickRay(Point screen, out Vector3 eye, out Vector3 rd)
        {
            eye = default; rd = default;
            if (_mesh() == null) return false;
            return Camera.PickRay(screen, _gl.ActualWidth, _gl.ActualHeight, out eye, out rd);
        }

        // Nearest hit point on the mesh.
        public bool PickSurface(Point screen, out Vector3 hit)
        {
            hit = default;
            return PickRay(screen, out var eye, out var rd) && Picker.RayMeshHit(eye, rd, _mesh(), out hit, out _);
        }

        // Nearest hit point + the FACE index (for seeding the brush's active region).
        public bool PickFace(Point screen, out int face, out Vector3 hit)
        {
            face = -1; hit = default;
            return PickRay(screen, out var eye, out var rd) && Picker.RayMeshHit(eye, rd, _mesh(), out hit, out face);
        }
    }
}
