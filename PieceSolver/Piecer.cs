using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using OpenTK.Mathematics;

namespace PieceSolver
{
    // The Editor active during Piecing (after Propose -> Accept). A contextual tool (no on/off toggle).
    // Selection is a SET of pieces, living in the Doc (typed, shared, not undoable). Each modifier splits into
    // a TAP (release under StrokeThresholdPx of the press) and a DRAG (past it -> the brush):
    //   PLAIN — tap: REPLACE the selection with the one piece under the cursor (empty canvas / ESC -> deselect).
    //           Plain never brushes.
    //   SHIFT — tap: ADD that piece to the selection (union). Drag: GROW the whole selection (when nothing is
    //           selected, MINT a new region from the largest connected blob). Provisional — candidates preview
    //           Green 5 (will be added) / Green 2 (a disconnected no-op affordance); commits on release.
    //   CTRL  — tap: REMOVE that piece from the selection. Drag: CARVE faces out of the whole selection (donated
    //           to a neighbour outside the selection, or split off as an island), or — nothing selected — DELETE
    //           whole pieces (healed into the dominant neighbour). Marked faces preview red; commits on release.
    // Every committing gesture goes through Doc.Run as one transaction (undoable). The Piecer computes the change
    // and never mutates Real state directly. See docs/DOC-TX-REFACTOR.md and PIECER-REFACTOR.md.
    sealed class Piecer : Editor
    {
        readonly IEditorHost _host;
        public Piecer(IEditorHost host) { _host = host; }

        public override string Name => "Piece";

        // ---- selection lives in the Doc (typed, shared, NOT undoable); the Piecer drives it via this handle ----
        Selection<PieceId> Sel => _host.Doc.Pieces;
        bool Selected(int region) => region >= 0 && Sel.Contains(new PieceId(region));
        HashSet<int> SelIds() { var s = new HashSet<int>(); foreach (var p in Sel.Items) s.Add(p.Value); return s; }

        // ---- gesture arming (tap vs drag, decided by travel from the press point) ----
        const double StrokeThresholdPx = 10.0;   // press-to-brush threshold: a Shift/Ctrl release under it is a TAP
        ModifierKeys _downMods;                   // modifiers latched at press (a brush keys off these, not live mods)
        Point _downScreen;                        // press position (the gesture origin)
        bool _armed;                              // a Shift/Ctrl gesture is in progress (plain acts on press, never arms)
        bool _dragging;                           // travel crossed the threshold -> the brush is live
        int _tapPiece;                            // piece under the press point, for a tap select/add/remove (-1 = none)

        // ---- Ctrl brush (only live once _dragging) ----
        bool _removing;             // Ctrl drag in progress (carve when a piece is selected, else delete pieces)
        bool _carve;                // this Ctrl gesture is a CARVE (the selection is non-empty) rather than a delete
        HashSet<int> _touched;      // faces marked during the current Ctrl gesture

        // ---- Shift brush (only live once _dragging) — PROVISIONAL (nothing applied until release) ----
        bool _growActive;           // a Shift gesture is in progress: mint or grow
        bool _growMint;             // this Shift gesture is a MINT (nothing selected at start) rather than a grow
        HashSet<int> _growTouched;  // candidates the brush passed over (faces not already in the selection)
        HashSet<int> _growConnected;// of _growTouched, the subset that will be added (Green 5); the rest preview Green 2
        Dictionary<int, int> _growAssign;  // grow: face -> selected piece its front reached (null for a mint)
        Tx _tx;                     // the open Doc transaction for the live brush stroke (lease + commit vehicle)

        double _dabAccum;           // screen-px travelled since the last dab (path-spacing accumulator)
        Point _lastPointer;         // previous pointer position, the start of the current stroke segment

        // ===================== pointer hooks =====================

        public override void OnPointerDown(Point screen, ModifierKeys mods)
        {
            _downScreen = screen; _downMods = mods; _lastPointer = screen; _dabAccum = 0;
            _armed = false; _dragging = false;
            _removing = false; _carve = false; _touched = null;
            _growActive = false; _growMint = false; _growTouched = null; _growConnected = null; _growAssign = null;

            // The piece under the press point (for a tap select / add / remove). -1 if the press missed a face.
            var map = _host.Pattern?.PieceMap;
            _tapPiece = (_host.PickFace(screen, out int f0, out _) && f0 >= 0 && map != null && f0 < map.Length) ? map[f0] : -1;

            if ((mods & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            {
                _armed = true;   // Shift/Ctrl: defer — a release under the threshold edits the selection; a drag runs the brush
                return;
            }

            // PLAIN press = SELECT one (replace the whole selection), or deselect on empty canvas. Acts immediately;
            // plain has no brush, so there is nothing to defer. (Selection.Changed drives the view refresh.)
            if (_tapPiece >= 0) Sel.Replace(new PieceId(_tapPiece));
            else if (Sel.Count > 0) Deselect();
        }

        public override void OnPointerMove(Point screen)
        {
            if (_armed && !_dragging)
            {
                double dx = screen.X - _downScreen.X, dy = screen.Y - _downScreen.Y;
                if (dx * dx + dy * dy <= StrokeThresholdPx * StrokeThresholdPx) { _lastPointer = screen; return; }   // still a tap
                BeginBrush();   // crossed the threshold -> the Shift/Ctrl brush goes live (seeded from the press point)
            }
            if (_removing) BrushStrokeTo(screen);        // Ctrl -> mark faces along the path
            else if (_growActive) GrowStrokeTo(screen);  // Shift -> accumulate + preview (provisional)
            _lastPointer = screen;
        }

        public override void OnPointerUp(Point screen)
        {
            if (_dragging)
            {
                if (_removing) CommitRemove();
                else if (_growActive) CommitGrow();
            }
            else if (_armed && _tapPiece >= 0)
            {
                // A TAP with a modifier (plain already acted on press): edit the selection SET by the press piece.
                if ((_downMods & ModifierKeys.Control) != 0) Sel.Remove(new PieceId(_tapPiece));   // Ctrl tap = remove from selection
                else if ((_downMods & ModifierKeys.Shift) != 0) Sel.Add(new PieceId(_tapPiece));   // Shift tap = add to selection (union)
            }
            _armed = false; _dragging = false;
        }

        public override void OnHover(Point screen) => _host.ShowBrushPreview(screen);

        // The Shift/Ctrl brush crossed the press threshold: go live and seed a first dab at the press point so the
        // span from press to here is covered. Keys off the modifiers LATCHED at press.
        void BeginBrush()
        {
            _dragging = true;
            _tx = _host.Doc.OpenTx();   // hold the lease for the whole stroke — foreign Run/Undo/Redo now self-reject
            _lastPointer = _downScreen; _dabAccum = 0;
            if ((_downMods & ModifierKeys.Control) != 0)
            {
                // CTRL drag: a non-empty selection -> CARVE it; nothing selected -> DELETE whole pieces. Just marks.
                _removing = true; _carve = Sel.Count > 0; _touched = new HashSet<int>();
                if (_host.PickSurface(_downScreen, out var hit)) MarkFacesUnderBrush(hit);
                _host.RefreshPieces();
            }
            else
            {
                // SHIFT drag (provisional): nothing selected -> MINT a new region (the largest connected blob);
                // a selection -> GROW it (candidates a selected front reaches -> Green 5; the rest Green 2, a no-op
                // affordance until they connect). Commits on release.
                _growActive = true; _growMint = Sel.Count == 0;
                _growTouched = new HashSet<int>(); _growConnected = new HashSet<int>(); _growAssign = null;
                if (_host.PickSurface(_downScreen, out var hit)) AccumulateGrow(hit);
                UpdateGrowConnected();
                if (_host.ShowPieces) _host.RefreshPieces();
            }
        }

        // ===================== commit (mouse-up) — one transaction through the Doc =====================

        void CommitRemove()
        {
            // Carve out of the selection, or (nothing selected) delete wholly-marked pieces. Both reuse the
            // intricate in-place ops, captured as ONE delta (carve bundles its SplitDisconnected renumber).
            string log = null;
            PieceDelta delta;
            if (_carve)
            {
                var sel = SelIds(); var touched = _touched;
                delta = _host.Pattern.ComputeDelta(() =>
                {
                    log = _host.Pattern.Carve(touched, sel);
                    if (log != null) _host.Pattern.SplitDisconnected();   // carving a strip can split a piece into islands
                });
            }
            else
            {
                var touched = _touched;
                delta = _host.Pattern.ComputeDelta(() => { log = _host.Pattern.Delete(touched); });
            }
            bool empty = delta.Empty;
            _removing = false; _carve = false; _touched = null;          // drop preview state before the rebuild
            if (log != null) _host.Log(log);
            _tx?.Apply(delta); _tx?.Commit(); _tx = null;               // apply (fires Changed -> rebuild) + close the stroke's tx
            if (empty && _host.ShowPieces) _host.RefreshPieces();        // refused / no-op: no Changed fired -> drop the red preview
        }

        void CommitGrow()
        {
            PieceDelta delta; PieceId minted = default; bool didMint = _growMint;
            if (_growMint)
            {
                // MINT: a brand-new region from the largest connected blob (Green 5); strays were never written.
                var connected = _growConnected;
                delta = _host.Pattern.ComputeDelta(() =>
                {
                    if (connected != null && connected.Count > 0)
                    {
                        var id = _host.Pattern.NewRegionId();
                        _host.Pattern.ApplyGrow(connected, id);
                        _host.Pattern.SplitDisconnected();
                        minted = id;
                    }
                });
            }
            else
            {
                // GROW: apply the connected candidates, each to the selected piece its front reached (Green 5).
                var touched = _growTouched; var assign = _growAssign; var sel = SelIds();
                delta = _host.Pattern.ComputeDelta(() =>
                {
                    var a = assign ?? _host.Pattern.GrowAssign(touched, sel);
                    _host.Pattern.ApplyGrowMap(a);
                    _host.Pattern.SplitDisconnected();
                });
            }
            bool empty = delta.Empty;
            _growActive = false; _growMint = false; _growTouched = null; _growConnected = null; _growAssign = null;
            _tx?.Apply(delta); _tx?.Commit(); _tx = null;               // apply (fires Changed -> rebuild) + close the stroke's tx
            if (didMint && !empty) Sel.Replace(minted);                  // mint -> the new piece becomes the selection
            else if (empty && _host.ShowPieces) _host.RefreshPieces();   // no-op: drop the green preview
        }

        // ===================== brush stroke (path-length spaced dabs) =====================

        // The Ctrl stroke: mark faces under the brush every `spacing` screen-px along the path (the actual
        // delete/carve happens on mouse-up). Tracks path LENGTH; _dabAccum carries the leftover across moves.
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

        // Add the faces under the brush to the candidate set. A face already in the selection is skipped (it is
        // already "ours"); for a mint the selection is empty so every face qualifies.
        bool AccumulateGrow(Vector3 center)
        {
            if (_growTouched == null) return false;
            var map = _host.Pattern.PieceMap;
            bool added = false;
            foreach (int f in _host.Pattern.FacesUnderBrush(center, _host.BrushWorldRadius))
                if (map != null && f >= 0 && f < map.Length && !Selected(map[f]) && _growTouched.Add(f)) added = true;
            return added;
        }

        // Recompute the "will be added" (Green 5) set: a MINT adds only the LARGEST connected blob of candidates
        // (strays stay Green 2, dropped on release — no stray single-triangle pieces); a GROW adds only candidates
        // a selected front reaches (GrowAssign), the rest stay Green 2.
        void UpdateGrowConnected()
        {
            if (_growMint) { _growConnected = _host.Pattern.LargestComponent(_growTouched); _growAssign = null; }
            else { _growAssign = _host.Pattern.GrowAssign(_growTouched, SelIds()); _growConnected = new HashSet<int>(_growAssign.Keys); }
        }

        // One Ctrl-gesture dab: add every face under the brush to the marked set (_touched). Marks ALL faces;
        // Carve/Delete filter to the relevant faces, so the rest are a no-op affordance (shown in the pre-select
        // colour, never removed). Pure marking — the actual delete/carve happens on mouse-up.
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
        HashSet<int> _marked;        // the delete-gesture marked set (null when not removing)
        HashSet<int> _fullyMarked;   // regions wholly marked -> will be deleted (dark red)

        public override void FaceFillBegin()
        {
            _marked = (_removing && _touched != null && _touched.Count > 0) ? _touched : null;
            // "fully-marked piece -> dark red" only applies to the no-selection DELETE preview; a carve marks
            // faces (not whole pieces), so a marked selected-piece face is the delete colour.
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
            //   CARVE  -> a SELECTED piece's faces read the DELETE colour (dark red); other faces under the brush
            //             can't be carved, shown in the lighter PRE-SELECT colour as a no-op affordance.
            //   DELETE -> marked faces light red, a wholly-marked piece dark red.
            if (_marked != null && _marked.Contains(face))
            {
                if (_carve)
                    return Selected(region) ? ToDelete : PreHighlight;
                return (_fullyMarked != null && _fullyMarked.Contains(region)) ? ToDelete : PreHighlight;
            }
            // Every selected piece -> the active highlight.
            if (Selected(region)) return ActiveRegionColor;
            return null;   // caller defaults to white
        }

        // ---- gesture lifecycle ----
        // A brush stroke is mid-composition iff its tx lease is open. ESC routes here instead of to Deselect.
        public override bool GesturePending => _tx != null;

        // Abort the in-flight stroke (ESC): cancel the tx (nothing was applied during the drag, so it rolls back
        // zero parts) and disarm so the pending mouse-up does nothing; drop the provisional preview.
        public override void CancelGesture()
        {
            _tx?.Cancel(); _tx = null;
            bool wasBrush = _removing || _growActive;
            _removing = false; _carve = false; _touched = null;
            _growActive = false; _growMint = false; _growTouched = null; _growConnected = null; _growAssign = null;
            _armed = false; _dragging = false;
            if (wasBrush && _host.ShowPieces) _host.RefreshPieces();
        }

        // ---- selection lifecycle ----
        public void ClearSelection() { Sel.ClearSilent(); }            // programmatic (Seed/mesh change/teardown); the caller drives the rebuild
        public override void Deselect() { Sel.Clear(); }               // user deselect (ESC / empty-canvas click); Changed drives the rebuild

        // ---- colours ----
        // Selected-piece highlight — open-color Indigo 3.
        static readonly Vector3 ActiveRegionColor = OpenColor.Indigo3;
        // Ctrl-gesture preview, on open-color reds (the "marked but not deleting" cue is the SAME in both modes —
        // it does not diverge by context):
        //   PreHighlight (Red 2) = a marked face that will NOT be deleted — the no-selection delete pre-highlight
        //                          AND the carve no-op affordance (faces outside the selection under the brush).
        //   ToDelete    (Red 5) = a piece/face that WILL be deleted (a wholly-marked piece, or a carved face).
        static readonly Vector3 PreHighlight = OpenColor.Red2;
        static readonly Vector3 ToDelete = OpenColor.Red5;
        // Shift preview: Green 5 = will be added (mint blob, or a connected grow), Green 2 = disconnected (no-op affordance).
        static readonly Vector3 GrowAdd = OpenColor.Green5;
        static readonly Vector3 GrowPreview = OpenColor.Green2;
    }
}
