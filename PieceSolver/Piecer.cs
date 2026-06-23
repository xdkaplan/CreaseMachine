using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using OpenTK.Mathematics;

namespace PieceSolver
{
    // The Editor active during Piecing (after Propose -> Accept). The "Crease brush" — a contextual tool
    // (no on/off toggle): PLAIN-click SELECTS a piece (click empty space / ESC to deselect). SHIFT and CTRL are
    // both moded by selection and PROVISIONAL — they preview during the stroke and commit on release:
    //   SHIFT — no selection: MINT a new region; selection: GROW the active piece. Candidates preview in green
    //           (5 = will be added, 2 = a disconnected no-op affordance until it connects). Commits on release.
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
        bool _growActive;           // a Shift gesture is in progress: PROVISIONAL (nothing applied until release) — mint or grow
        bool _growMint;             // this Shift gesture is a MINT (no selection at start) rather than a grow
        HashSet<int> _growTouched;  // candidates the brush has passed over (grow: faces not already in the active piece; mint: all)
        HashSet<int> _growConnected;// of _growTouched, the subset that will be added (Green 5); the rest preview Green 2
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
                // SHIFT is moded by selection, and BOTH modes are PROVISIONAL -- nothing is committed to the mesh
                // until release. NO selection -> MINT a new region (every candidate previews Green 5, committed
                // as a new region on release). A selection -> GROW the active piece (candidates connected to it
                // preview Green 5 = will add; disconnected ones preview Green 2 = a no-op affordance unless they
                // connect by release).
                _growActive = true; _growMint = !_selection.HasValue;
                _growTouched = new HashSet<int>(); _growConnected = new HashSet<int>();
                if (_host.PickSurface(screen, out var hit)) AccumulateGrow(hit);
                UpdateGrowConnected();
                if (_host.ShowPieces) _host.RefreshPieces();
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
            if (_removing) BrushStrokeTo(screen);        // Ctrl -> mark faces along the path
            else if (_growActive) GrowStrokeTo(screen);  // Shift (mint or grow) -> accumulate + preview (provisional)
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
            else if (_growActive)
            {
                // Commit on release. MINT: allocate a new region, apply ALL candidates to it, make it active.
                // GROW: apply only the connected (Green 5) candidates; disconnected (Green 2) ones were never
                // written -> a no-op. Either way re-split any neighbour the change carved, and refresh.
                if (_growMint)
                {
                    var id = _host.Pattern.NewRegionId();
                    _host.Pattern.ApplyGrow(_growTouched, id);
                    _selection = id;
                }
                else
                {
                    var connected = _host.Pattern.GrowConnected(_growTouched, _selection ?? new PieceId(-1));
                    _host.Pattern.ApplyGrow(connected, _selection ?? new PieceId(-1));
                }
                _host.Pattern.SplitDisconnected();
                _host.RefreshCreaseOverlay();
                _growActive = false; _growMint = false; _growTouched = null; _growConnected = null;
                if (_host.ShowPieces) _host.RefreshPieces();
            }
        }

        public override void OnHover(Point screen) => _host.ShowBrushPreview(screen);

        // ===================== brush stroke (path-length spaced dabs) =====================

        // The Ctrl stroke: mark faces under the brush every `spacing` screen-px along the path (the actual
        // remove/carve happens on mouse-up). Tracks path LENGTH; _dabAccum carries the leftover across moves.
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
                if (_host.PickSurface(new Point(_lastPointer.X + dx * t, _lastPointer.Y + dy * t), out var hit) && MarkFacesUnderBrush(hit)) changed = true;
                pos += spacing;
            }
            _dabAccum = seg - (pos - spacing);
            if (changed && _host.ShowPieces) _host.RefreshPieces();
        }

        // A Shift stroke segment (mint or grow): accumulate the faces under the brush as candidates, then
        // recompute which will be added (Green 5) vs are a disconnected affordance (Green 2). Provisional —
        // nothing is applied to the mesh; the commit happens on mouse-up.
        void GrowStrokeTo(Point b)
        {
            double dx = b.X - _lastPointer.X, dy = b.Y - _lastPointer.Y;
            double seg = Math.Sqrt(dx * dx + dy * dy);
            if (seg < 1e-6) return;
            double spacing = _host.BrushSpacingPx(b);
            double pos = spacing - _dabAccum;
            bool added = false;
            while (pos <= seg)
            {
                double t = pos / seg;
                if (_host.PickSurface(new Point(_lastPointer.X + dx * t, _lastPointer.Y + dy * t), out var hit) && AccumulateGrow(hit)) added = true;
                pos += spacing;
            }
            _dabAccum = seg - (pos - spacing);
            if (added)
            {
                UpdateGrowConnected();
                if (_host.ShowPieces) _host.RefreshPieces();
            }
        }

        // Add the faces under the brush to the candidate set. For a grow, faces already in the active piece are
        // skipped; for a mint there is no active piece (act = -1) so every face is a candidate.
        bool AccumulateGrow(Vector3 center)
        {
            if (_growTouched == null) return false;
            int act = _selection?.Value ?? -1;
            var map = _host.Pattern.PieceMap;
            bool added = false;
            foreach (int f in _host.Pattern.FacesUnderBrush(center, _host.BrushWorldRadius))
                if (map != null && f >= 0 && f < map.Length && map[f] != act && _growTouched.Add(f)) added = true;
            return added;
        }

        // Recompute the "will be added" (Green 5) subset: a MINT adds every candidate; a GROW adds only those
        // connected to the active piece (the rest stay Green 2).
        void UpdateGrowConnected()
        {
            _growConnected = _growMint ? new HashSet<int>(_growTouched)
                                       : _host.Pattern.GrowConnected(_growTouched, _selection ?? new PieceId(-1));
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
            // Shift preview (mint or grow): a candidate that will be added reads Green 5; a disconnected grow
            // candidate reads Green 2 (a no-op affordance until it connects). Provisional — mesh unchanged until release.
            if (_growActive)
            {
                if (_growConnected != null && _growConnected.Contains(face)) return GrowAdd;
                if (_growTouched != null && _growTouched.Contains(face)) return GrowPreview;
            }
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
        // Shift+grow preview: Green 5 = connected (will be added), Green 2 = disconnected (no-op affordance).
        static readonly Vector3 GrowAdd = OpenColor.Green5;
        static readonly Vector3 GrowPreview = OpenColor.Green2;
    }
}
