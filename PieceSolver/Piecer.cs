using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using OpenTK.Mathematics;

namespace PieceSolver
{
    // The Editor active during Piecing (after Propose -> Accept). The "Crease brush" — a contextual tool
    // (no on/off toggle) that paints PIECE MEMBERSHIP: left-click selects a piece (click empty space to
    // deselect), drag grows the active selection into its neighbours (the crease follows the brush),
    // Shift+click mints a new region. Ctrl+drag is moded by selection: with NO piece selected it REMOVES whole
    // pieces (healed into the dominant neighbour); with a piece selected it CARVES that piece (its faces leave
    // it — donated to a foreign neighbour, or split off as a new island). Edits the Pattern only; no geometry
    // moves. See docs/PIECER-REFACTOR.md.
    sealed class Piecer : Editor
    {
        readonly IEditorHost _host;
        public Piecer(IEditorHost host) { _host = host; }

        public override string Name => "Piece";

        // ---- interaction state (was MainWindow fields) ----
        PieceId? _selection;        // active region being painted with (was _brushRegion; -1 -> null)
        bool _removing;             // Ctrl+drag destructive gesture in progress (remove pieces, OR carve when a piece is selected)
        bool _carve;                // this Ctrl gesture is a CARVE (a piece was selected at gesture start) rather than a remove
        HashSet<int> _touched;      // faces marked during the current Ctrl gesture
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
                // CTRL+drag is moded by selection: with NO piece selected it REMOVES whole pieces (kill &
                // donate); with a piece selected it CARVES that piece (marks only ITS faces, in the delete
                // colour, healed into a foreign neighbour or split off as a new island on release). Either way
                // it just marks faces under the brush — no region painting.
                _removing = true; _carve = _selection.HasValue; _touched = new HashSet<int>();
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
            else if (_selection.HasValue) Deselect();   // plain click on empty canvas -> deselect (next Ctrl is the no-selection remove mode)
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
                // carve the active piece, or (no selection) remove wholly-marked pieces — both heal + re-derive creases.
                string log = _carve ? _host.Pattern.Carve(_touched, _selection ?? new PieceId(-1))
                                    : _host.Pattern.Remove(_touched);
                if (log != null)
                {
                    _host.Log(log);
                    if (_carve) _host.Pattern.SplitDisconnected();   // carving a strip can split the active piece into islands
                    _host.RefreshCreaseOverlay();
                }
                _removing = false; _carve = false; _touched = null;
                if (_host.ShowPieces) _host.RefreshPieces();        // drop the red preview, show the result
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
        // Mark ALL faces under the brush (both modes). Carve filters to the active piece's faces in
        // Pattern.Carve; the other marked faces are a no-op affordance (shown in the pre-select colour, never
        // carved). Pure marking — the actual remove/carve happens on mouse-up.
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
            // "fully-marked piece -> dark red" only applies to the no-selection REMOVE preview; a carve marks
            // faces (not whole pieces), so every marked face is the delete colour.
            _fullyMarked = (_marked != null && !_carve) ? _host.Pattern.FullyMarked(_touched) : null;
        }

        public override Vector3? FaceFill(int face, int region)
        {
            // Ctrl-gesture preview on marked faces:
            //   CARVE  -> the active piece's faces read the DELETE colour (dark red); other faces under the
            //             brush can't be carved, shown in the lighter PRE-SELECT colour as a no-op affordance.
            //   REMOVE -> marked faces light red, a wholly-marked piece dark red.
            if (_marked != null && _marked.Contains(face))
            {
                if (_carve)
                    return (_selection.HasValue && region == _selection.Value.Value) ? ToDelete : CarveAffordance;
                return (_fullyMarked != null && _fullyMarked.Contains(region)) ? ToDelete : PreHighlight;
            }
            // Active paint region -> light blue.
            if (_selection.HasValue && region == _selection.Value.Value)
                return ActiveRegionColor;
            return null;   // caller defaults to white
        }

        // ---- selection lifecycle ----
        public void ClearSelection() { _selection = null; }   // silent (programmatic — a RebuildPieces follows, e.g. Seed/mesh change)
        public override void Deselect() { _selection = null; if (_host.ShowPieces) _host.RefreshPieces(); }   // user deselect (ESC / empty-canvas click)

        // ---- colours ----
        // Active-piece highlight — light blue (not yet on the open-color palette; see the colour audit).
        static readonly Vector3 ActiveRegionColor = new Vector3(0.485f, 0.7954f, 0.97f);
        // Ctrl-gesture preview, on open-color reds:
        //   no-selection REMOVE -> PreHighlight (Red 3) for a marked face, ToDelete (Red 5) for a wholly-marked piece.
        //   CARVE               -> ToDelete (Red 5) for the active piece's faces, CarveAffordance (Red 1) for the
        //                          other (non-carvable) faces under the brush — a no-op affordance.
        static readonly Vector3 PreHighlight = OpenColor.Red3;
        static readonly Vector3 ToDelete = OpenColor.Red5;
        static readonly Vector3 CarveAffordance = OpenColor.Red1;
    }
}
