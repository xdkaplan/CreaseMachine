using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using OpenTK.Mathematics;

namespace PieceSolver
{
    // The Editor active during Piecing (after Propose -> Accept). The "Crease brush" — a contextual tool
    // (no on/off toggle) that paints PIECE MEMBERSHIP: left-click selects a piece, drag grows the active
    // selection into its neighbours (the crease, a region boundary, follows the brush), Shift+click mints a
    // new region, Ctrl+drag marks faces for removal (healed into the dominant neighbour on release). Edits
    // the Pattern only; no geometry moves. Pure relocation from MainWindow's mouse handlers. See PIECER-REFACTOR.md.
    sealed class Piecer : Editor
    {
        readonly IEditorHost _host;
        public Piecer(IEditorHost host) { _host = host; }

        public override string Name => "Piece";

        // ---- interaction state (was MainWindow fields) ----
        PieceId? _selection;        // active region being painted with (was _brushRegion; -1 -> null)
        bool _removing;             // Ctrl+drag "remove pieces" gesture in progress
        HashSet<int> _touched;      // faces marked during the current remove gesture
        bool _stroking;             // a paint drag passed the click-vs-drag threshold -> painting (a bare click only SELECTS)
        Point _strokeStart;         // mouse-down position, to measure the drag threshold
        double _dabAccum;           // screen-px travelled since the last bump (path-spacing accumulator)
        Point _lastPointer;         // previous pointer position, the start of the current stroke segment

        const double StrokeThresholdPx = 6.0;   // drag distance (px) before a paint stroke begins (a click within this just selects)

        // ===================== pointer hooks (the left-button brush branches) =====================

        public override void OnPointerDown(Point screen, ModifierKeys mods)
        {
            _lastPointer = screen;
            _dabAccum = 0;
            if ((mods & ModifierKeys.Control) != 0)
            {
                // CTRL+drag = remove pieces: mark faces under the brush (light red); a wholly-marked piece
                // reads dark red and is removed on mouse-up (its faces heal into the dominant neighbour). No
                // region painting happens during a remove gesture.
                _removing = true; _touched = new HashSet<int>();
                if (_host.PickSurface(screen, out var hit)) MarkFacesUnderBrush(hit);
                _host.RefreshPieces();
            }
            else if (_host.PickFace(screen, out int f0, out var hit))
            {
                if ((mods & ModifierKeys.Shift) != 0)
                {
                    // SHIFT: mint a brand-NEW region and seed it on the click itself (the bullseye needs the dab
                    // now). Dragging then grows it; each Shift+click mints another new region.
                    _selection = _host.Pattern.NewRegionId(); _stroking = true;
                    if (_host.Pattern.Paint(hit, _host.BrushWorldRadius, _selection.Value)) { _host.RefreshCreaseOverlay(); _host.Pattern.SplitDisconnected(); }   // Paint re-derives creases -> refresh the overlay; seeding can also pinch a region in two
                    if (_host.ShowPieces) _host.RefreshPieces();
                }
                else
                {
                    // Plain click: SELECT this piece only -- NO paint dab. Painting begins only once the drag
                    // passes StrokeThresholdPx, so a bare click (or A/B/A/B clicking) just re-highlights the
                    // active selection and never nudges a boundary.
                    var map = _host.Pattern.PieceMap;
                    _selection = (f0 >= 0 && map != null && f0 < map.Length) ? new PieceId(map[f0]) : (PieceId?)null;
                    _strokeStart = screen; _stroking = false;
                    if (_host.ShowPieces) _host.RefreshPieces();   // show the active-selection highlight
                }
            }
        }

        public override void OnPointerMove(Point screen)
        {
            if (_removing) BrushStrokeTo(screen);   // remove: mark faces along the path
            else
            {
                if (!_stroking)
                {
                    double mx = screen.X - _strokeStart.X, my = screen.Y - _strokeStart.Y;
                    if (mx * mx + my * my >= StrokeThresholdPx * StrokeThresholdPx) _stroking = true;   // click -> drag
                }
                if (_stroking) BrushStrokeTo(screen);   // paint only once past the click-vs-drag threshold
            }
            _lastPointer = screen;
        }

        public override void OnPointerUp(Point screen)
        {
            if (_removing)
            {
                string log = _host.Pattern.Remove(_touched, _selection ?? new PieceId(-1));   // remove the wholly-marked pieces, heal into dominant neighbours
                if (log != null) { _host.Log(log); _host.RefreshCreaseOverlay(); }   // Remove re-derives creases (when it removed something) -> refresh the overlay
                _removing = false; _touched = null;
                if (_host.ShowPieces) _host.RefreshPieces();        // drop the red preview, show the healed regions
            }
            else if (_stroking && _host.ShowPieces) _host.RefreshPieces();   // final settle ONLY if a paint stroke actually ran (a bare click already re-highlighted on down)
            _stroking = false;
        }

        public override void OnHover(Point screen) => _host.ShowBrushPreview(screen);

        // ===================== brush stroke (path-length spaced dabs) =====================

        // Place a bump every `spacing` screen-pixels along the stroke path (lastPointer -> b), so it tracks
        // path LENGTH, not time. _dabAccum carries the leftover distance across moves.
        void BrushStrokeTo(Point b)
        {
            double dx = b.X - _lastPointer.X, dy = b.Y - _lastPointer.Y;
            double seg = Math.Sqrt(dx * dx + dy * dy);
            if (seg < 1e-6) return;
            double spacing = _host.BrushSpacingPx(b);
            double pos = spacing - _dabAccum;
            bool changed = false;
            while (pos <= seg)
            {
                double t = pos / seg;
                if (_host.PickSurface(new Point(_lastPointer.X + dx * t, _lastPointer.Y + dy * t), out var hit))
                {
                    if (_removing) { if (MarkFacesUnderBrush(hit)) changed = true; }                       // remove gesture: mark faces
                    // paint gesture: grow active region. Paint re-derives creases (RegenCrease); the overlay is a
                    // view, so refresh it here per dab (matches the old PaintRegionUnderBrush rebuilding it inline).
                    else if (_host.Pattern.Paint(hit, _host.BrushWorldRadius, _selection ?? new PieceId(-1))) { changed = true; _host.RefreshCreaseOverlay(); }
                }
                pos += spacing;
            }
            _dabAccum = seg - (pos - spacing);
            if (changed)
            {
                if (!_removing) _host.Pattern.SplitDisconnected();   // a stroke can carve a region into islands -> give the smaller ones new ids
                if (_host.ShowPieces) _host.RefreshPieces();         // live: recompute once per mouse-move (throttle), not per dab
            }
        }

        // One remove-brush dab: add every face whose centroid is within the brush radius to the marked set
        // (_touched). Pure marking -- no region change; the actual removal happens on mouse-up.
        bool MarkFacesUnderBrush(Vector3 center)
        {
            if (_touched == null) return false;
            bool changed = false;
            foreach (int f in _host.Pattern.FacesUnderBrush(center, _host.BrushWorldRadius))
                if (_touched.Add(f)) changed = true;
            return changed;
        }

        // ===================== per-face FILL tint (the non-modal piece colouring) =====================

        // Precomputed once per buffer build so FaceFill is O(1) per face (FullyMarked is O(F)).
        HashSet<int> _marked;        // the remove-gesture marked set (null when not removing)
        HashSet<int> _fullyMarked;   // regions wholly marked -> will be removed (dark red)

        public override void FaceFillBegin()
        {
            _marked = (_removing && _touched != null && _touched.Count > 0) ? _touched : null;
            _fullyMarked = _marked != null ? _host.Pattern.FullyMarked(_touched, _selection ?? new PieceId(-1)) : null;
        }

        public override Vector3? FaceFill(int face, int region)
        {
            // Remove-gesture preview: marked faces tint red; a wholly-marked piece tints darker (it will be
            // removed). The active selection is protected -> never tinted red.
            if (_marked != null && _marked.Contains(face) && (!_selection.HasValue || region != _selection.Value.Value))
                return _fullyMarked.Contains(region) ? RemoveDark : RemoveLight;
            // Active paint region -> light blue.
            if (_selection.HasValue && region == _selection.Value.Value)
                return ActiveRegionColor;
            return null;   // caller defaults to white
        }

        // ---- selection lifecycle ----
        public void ClearSelection() { _selection = null; }

        // ---- colours (the active-region highlight + the remove-gesture preview tints) ----
        // Active paint region: light blue (= HSV 0.56,0.5,0.97, picked from the rainbow piece palette so it
        // sits naturally among the piece colours). Remove preview: light red = marked, dark red = wholly-marked.
        static readonly Vector3 ActiveRegionColor = new Vector3(0.485f, 0.7954f, 0.97f);
        static readonly Vector3 RemoveLight = new Vector3(0.96f, 0.62f, 0.60f);
        static readonly Vector3 RemoveDark = new Vector3(0.80f, 0.16f, 0.16f);
    }
}
