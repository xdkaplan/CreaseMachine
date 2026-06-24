using System;

namespace PieceSolver
{
    // Which geometry the viewport shows — ONE value at a time, so the old _showPieces-over-developed
    // occlusion is unrepresentable. Ephemeral view state (like the camera): not Real, not a Transient,
    // not saved / not undoable. See docs/specs/DOC-SPEC.md (Real / Transient / Ephemeral).
    enum DisplaySource { Authoring, Pieces, Developed }

    // The interactive viewport, bound to a Doc — the home for state that has been smeared across the
    // MainWindow god-file. Today it owns the DISPLAY state (the single source of truth for what's rendered)
    // and the repaint request (Rot). The MeshView/render-loop, the IEditorHost role, Editor hosting, and the
    // future stack of Elements drain in next. One View now, but a real abstraction (N later); MainWindow
    // shrinks to the WPF chrome shell that hosts it. See docs/specs/DOC-SPEC.md + AGENTS.md.
    sealed class View
    {
        readonly Action _rot;   // poke the OS paint scheduler — a screen frame can't be lazily pulled on read

        public Doc Doc { get; }
        public DisplaySource Display { get; set; } = DisplaySource.Authoring;
        public Camera Camera { get; } = new Camera();   // the orbit camera — Ephemeral view state (see Camera.cs)

        public View(Doc doc, Action rot) { Doc = doc; _rot = rot; }

        // Mark the rendered frame stale and schedule its re-grow. The frame is itself a Transient (derived from
        // Doc + camera + Display); since rendering is PUSH (not a .Value pull), Rot both invalidates and pokes
        // the paint tick. Later, rotting the viewport may cascade to its 2D-element children — same machinery.
        public void Rot() => _rot?.Invoke();
    }
}
