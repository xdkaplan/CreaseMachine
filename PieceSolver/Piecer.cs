using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using OpenTK.Mathematics;

namespace PieceSolver
{
    // The Editor active during Piecing (after Propose -> Accept). The "Crease brush" — a contextual tool
    // (no on/off toggle): PLAIN-click SELECTS a piece (click empty space / ESC to deselect). SHIFT and CTRL are
    // both moded by selection, and a single dab paints immediately (no min-drag):
    //   SHIFT — no selection: mint a NEW region and paint it (it becomes active); selection: GROW the active
    //           piece (add territory; the crease follows the brush).
    //   CTRL  — no selection: REMOVE whole pieces (healed into the dominant neighbour); selection: CARVE the
    //           active piece (faces donated to a foreign neighbour, or split off as a new island).
    // Edits the Pattern only; no geometry moves. See docs/PIECER-REFACTOR.md.
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
        bool _painting;             // Shift+drag (or a single Shift dab) is GROWING the active piece (add territory)
        double _dabAccum;           // screen-px travelled since the last bump (path-spacing accumulator)
        Point _lastPointer;         // previous pointer position, the start of the current stroke segment

        // ===================== pointer hooks (the left-button brush branches) =====================

        public override void OnPointerDown(Point screen, ModifierKeys mods)
        {
            _lastPointer = screen;
            _dabAccum = 0;
            if ((mods & ModifierKeys.Control) != 0)
            {
                // CTRL is moded by selection: NO piece selected -> REMOVE whole pieces (kill & donate); a piece
                // selected -> CARVE that piece. Either way it just marks faces under the brush.
                _removing = true; _carve = _selection.HasValue; _touched = new HashSet<int>();
                if (_host.PickSurface(screen, out var hit)) MarkFacesUnderBrush(hit);
                _host.RefreshPieces();
            }
            else if ((mods & ModifierKeys.Shift) != 0)
            {
                // SHIFT is moded by selection (mirrors Ctrl): with NO piece selected it MINTS a new region (which
                // becomes active, so the drag grows it); with a piece selected it GROWS the active piece (add
                // territory). Either way it paints under the brush — immediately on the dab (no min-drag) and
                // along the drag. (To mint another new region, deselect first via ESC / empty-canvas click.)
                if (!_selection.HasValue) _selection = _host.Pattern.NewRegionId();
                _painting = true;
                if (_host.PickSurface(screen, out var hit) && _host.Pattern.Paint(hit, _host.BrushWorldRadius, _selection.Value))
                {
                    _host.RefreshCreaseOverlay(); _host.Pattern.SplitDisconnected();
                    if (_host.ShowPieces) _host.RefreshPieces();
                }
            }
            else if (_host.PickFace(screen, out int f0, out _))
            {
                // Plain click = SELECT only (never paints). Click empty canvas (below) to deselect.
                var map = _host.Pattern.PieceMap;
                _selection = (f0 >= 0 && map != null && f0 < map.Length) ? new PieceId(map[f0]) : (PieceId?)null;
                if (_host.ShowPieces) _host.RefreshPieces();   // show the active-selection highlight
            }
            else if (_selection.HasValue) Deselect();   // plain click on empty canvas -> deselect
        }

        public override void OnPointerMove(Point screen)
        {
            if (_removing || _painting) BrushStrokeTo(screen);   // Ctrl -> mark faces; Shift -> grow the active piece. Plain drag does nothing.
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
            else if (_painting && _host.ShowPieces) _host.RefreshPieces();   // final settle of the grow
            _painting = false;
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
                    if (_removing) { if (MarkFacesUnderBrush(hit)) changed = true; }                       // Ctrl gesture: mark faces
                    // Shift gesture: grow the active region. Paint re-derives creases (RegenCrease); the overlay
                    // is a view, so refresh it here per dab.
                    else if (_painting && _host.Pattern.Paint(hit, _host.BrushWorldRadius, _selection ?? new PieceId(-1))) { changed = true; _host.RefreshCreaseOverlay(); }
                }
                pos += spacing;
            }
            _dabAccum = seg - (pos - spacing);
            if (changed)
            {
                if (_painting) _host.Pattern.SplitDisconnected();   // a grow stroke can carve a region into islands -> give the smaller ones new ids
                if (_host.ShowPieces) _host.RefreshPieces();         // live: recompute once per mouse-move (throttle), not per dab
            }
        }

        // One Ctrl-gesture dab: add every face under the brush to the marked set (_touched). Marks ALL faces;
        // Carve filters to the active piece's faces in Pattern.Carve, so the rest are a no-op affordance (shown
        // in the pre-select colour, never removed). Pure marking — the actual remove/carve happens on mouse-up.
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
                    return (_selection.HasValue && region == _selection.Value.Value) ? ToDelete : PreHighlight;
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
        // Active-piece highlight — open-color Indigo 3.
        static readonly Vector3 ActiveRegionColor = OpenColor.Indigo3;
        // Ctrl-gesture preview, on open-color reds (the "marked but not deleting" cue is the SAME in both
        // modes — it does not diverge by context):
        //   PreHighlight (Red 2) = a marked face that will NOT be deleted — the no-selection remove pre-highlight
        //                          AND the carve no-op affordance (non-active faces under the brush).
        //   ToDelete    (Red 5) = a piece/face that WILL be deleted (a wholly-marked piece, or a carved face).
        static readonly Vector3 PreHighlight = OpenColor.Red2;
        static readonly Vector3 ToDelete = OpenColor.Red5;
    }
}
