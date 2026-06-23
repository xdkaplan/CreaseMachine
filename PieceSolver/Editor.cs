using System.Windows;
using System.Windows.Input;
using OpenTK.Mathematics;
using Plankton;

namespace PieceSolver
{
    // The narrow contract the host (MainWindow) exposes to an editor — the wall that keeps the editor
    // from depending on the whole 2000-line window. Picking, the brush footprint, the Pattern, and the
    // view-refresh hooks live on the host; the editor reaches them only through here. See PIECER-REFACTOR.md.
    interface IEditorHost
    {
        PlanktonMesh Mesh { get; }            // the live mesh the Pattern is index-coupled to (= _session.Mesh)
        Pattern Pattern { get; }              // the partition + crease companion the editor mutates
        bool ShowPieces { get; }              // is the per-piece split view currently shown (gates live re-pieces)

        bool PickFace(Point screen, out int face, out Vector3 hit);   // nearest hit face + point
        bool PickSurface(Point screen, out Vector3 hit);             // nearest hit point only
        double BrushWorldRadius { get; }      // brush footprint radius in world units
        double ScreenRadiusPx(Vector3 hit);   // that radius projected to screen px at a surface point
        double BrushSpacingPx(Point screen);  // dab spacing along a stroke path (px)

        void RefreshPieces();                 // rebuild the per-piece split render buffers (= RebuildPieces)
        void RefreshCreaseOverlay();          // rebuild the GL_LINES crease wires from CreaseMap (= RebuildCreaseOverlay)
        void ShowBrushPreview(Point screen);  // place/show the footprint preview dot (= UpdatePreview)
        void HideBrushPreview();              // hide the footprint preview dot
        void Invalidate();                    // request a viewport repaint
        void Log(string msg);                 // append a line to the session Console (the Remove op's summary)
    }

    // Abstract base for editors: lifecycle + pointer hooks + an optional per-face fill tint the view
    // queries while building the piece buffers. Today only the Piecer exists; a Crease editor is the
    // next entity (deferred). Pure relocation — no behaviour added here.
    abstract class Editor
    {
        public abstract string Name { get; }

        public virtual void Activate() { }
        public virtual void Deactivate() { }
        public virtual void Deselect() { }   // clear any active selection (ESC / empty-canvas click); no-op by default

        public virtual void OnPointerDown(Point screen, ModifierKeys mods) { }
        public virtual void OnPointerMove(Point screen) { }
        public virtual void OnPointerUp(Point screen) { }
        public virtual void OnHover(Point screen) { }

        // Per-face FILL tint for the piece view (grooves always delineate pieces; this only sets the fill).
        // null => the caller defaults to white. Called once per face during the buffer build, so it must be
        // O(1); FaceFillBegin precomputes any per-build state first.
        public virtual Vector3? FaceFill(int face, int region) => null;
        public virtual void FaceFillBegin() { }
    }
}
