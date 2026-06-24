using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PieceSolver
{
    // A typed selection set (one per Element type), living in the Doc. The Editor mutates it; the view and
    // command-availability react to Changed. NOT on the undo stack — selection isn't undoable. See
    // docs/DOC-TX-REFACTOR.md.
    sealed class Selection<T>
    {
        readonly HashSet<T> _set = new HashSet<T>();
        public event Action Changed;
        public bool Contains(T x) => _set.Contains(x);
        public int Count => _set.Count;
        public IEnumerable<T> Items => _set;
        public void Replace(T x) { _set.Clear(); _set.Add(x); Changed?.Invoke(); }
        public void Set(IEnumerable<T> items) { _set.Clear(); foreach (var x in items) _set.Add(x); Changed?.Invoke(); }
        public void Add(T x)     { if (_set.Add(x))    Changed?.Invoke(); }
        public void Remove(T x)  { if (_set.Remove(x)) Changed?.Invoke(); }
        public void Clear()      { if (_set.Count > 0) { _set.Clear(); Changed?.Invoke(); } }   // user deselect -> notify
        public void ClearSilent() { _set.Clear(); }   // programmatic reset (the caller drives its own rebuild)
    }

    // Why the Doc is busy (only one reason at a time — single transaction at a time). Editing = a Tx is open;
    // Calculating / Opening = a long op owns the Doc. Ready == None && no open Tx.
    enum Busy { None, Calculating, Opening }

    // An open transaction — the scope a tool brackets its edit with. ONE at a time (enforced by the Doc). While
    // open it holds the lease (foreign Run/Undo/Redo self-reject); the tool composes its delta(s) and Apply()s
    // them (live), then Commit() bundles the lot into one undo unit, or Cancel() rolls them back. Disposing an
    // un-closed Tx warns + auto-cancels (so a leaked / ESC-mashed gesture can't strand the Doc). See
    // docs/DOC-TX-REFACTOR.md.
    sealed class Tx : IDisposable
    {
        readonly Doc _doc;
        readonly bool _alive;                 // false = a refused tx (opened while busy) — all ops no-op
        readonly List<IDelta> _parts = new List<IDelta>();
        bool _closed;
        internal Tx(Doc doc, bool alive) { _doc = doc; _alive = alive; }
        internal List<IDelta> Parts => _parts;

        // Apply a delta now (live, so the view updates) and record it in this transaction.
        public void Apply(IDelta d)
        {
            if (!_alive || _closed || d == null) return;
            if (d is PieceDelta { Empty: true } || d is CompositeDelta { Empty: true }) return;
            _doc.ApplyLive(d);
            _parts.Add(d);
        }
        public void Commit() { if (_closed) return; _closed = true; if (_alive) _doc.CloseTx(this, commit: true); }
        public void Cancel() { if (_closed) return; _closed = true; if (_alive) _doc.CloseTx(this, commit: false); }
        public void Dispose()
        {
            if (_closed) return;
            Debug.WriteLine("Tx disposed without Commit/Cancel — auto-cancelling.");
            Cancel();
        }
    }

    // The orchestrator: owns the Store(s) + Selection(s) + the undo/redo stacks, and gatekeeps all mutation
    // through Run / OpenTx / Undo / Redo. (Short for Document; "Project" is reserved for a future on-disk
    // workspace.) Mutating entry points SELF-REJECT when !Ready, so callers never have to check — a rejected
    // call is a clean no-op (each delta is all-or-nothing). One transaction at a time; every tx is opened and
    // closed, which is what makes a future multithreaded ordering guard clean. See docs/DOC-TX-REFACTOR.md.
    sealed class Doc
    {
        public Pattern Pattern { get; private set; }                 // the (only, today) Store
        public Selection<PieceId> Pieces { get; } = new Selection<PieceId>();

        readonly Stack<IDelta> _undo = new Stack<IDelta>();
        readonly Stack<IDelta> _redo = new Stack<IDelta>();

        // Op-log emit: the Doc streams committed ops (`setpiece {faces} <from> <to>`) + `#` comments to whoever
        // listens (the Console renders them). The persisted journal is DERIVED on demand — OpLines (the undo
        // stack) + the StudioCommand log — not stored here. See docs/DOC-TX-REFACTOR.md (Revision: the op-log).
        public event Action<string> Recorded;                        // fired per emitted op/comment line

        Tx _open;                                                    // the single open transaction (null = none)
        public Busy State { get; private set; } = Busy.None;         // a long op (Calculating / Opening) owns the Doc

        public event Action Changed;                                 // Real/Transient changed
        public bool Ready => State == Busy.None && _open == null;    // is a NEW mutation/tx allowed right now
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        // Re-point at a fresh Store (mesh load / subdivide / reset) and drop the now-meaningless history +
        // selection + any open tx — deltas reference old face indices, so they cannot survive a re-mesh.
        public void Rebind(Pattern pattern) { Pattern = pattern; _undo.Clear(); _redo.Clear(); _open = null; Pieces.ClearSilent(); }

        // Drop the undo/redo history without re-pointing the Store — for a Chapter reset (Seed re-partitions the
        // SAME mesh, so old deltas reference invalid region ids). Selection is cleared separately by the caller.
        public void ClearHistory() { _undo.Clear(); _redo.Clear(); }

        // A long op (bake / open) takes/releases the Doc. While busy, Run/OpenTx/Undo/Redo self-reject.
        public void EnterBusy(Busy reason) { State = reason; }
        public void ExitBusy() { State = Busy.None; }

        // Emit a bare op/command line, or a `#` comment, to the op-log listeners (the Console).
        public void Record(string line) => Recorded?.Invoke(line);
        public void Comment(string text) => Record("# " + text);

        // Open the single transaction. One at a time: a stale open tx is a leak -> warn + cancel it; opening while
        // a long op owns the Doc returns a refused (dead) tx whose Apply/Commit no-op.
        public Tx OpenTx()
        {
            if (_open != null) { Debug.WriteLine("OpenTx while a tx is open — cancelling the stale one."); _open.Cancel(); }
            if (State != Busy.None) { Debug.WriteLine($"OpenTx during {State} — refused."); return new Tx(this, alive: false); }
            _open = new Tx(this, alive: true);
            return _open;
        }

        // One-shot mutation = open + apply + commit, atomically (for button commands like Merge — no gesture).
        // Returns false if rejected (not Ready) or empty, so the caller can skip its follow-up (e.g. reselect).
        public bool Run(IDelta d)
        {
            if (!Ready || d == null || (d is PieceDelta { Empty: true }) || (d is CompositeDelta { Empty: true })) return false;
            ApplyInternal(d); _undo.Push(d); _redo.Clear(); RecordOps(d); Changed?.Invoke();
            return true;
        }

        public void Undo() { if (!Ready || _undo.Count == 0) return; var d = _undo.Pop(); InvertInternal(d); _redo.Push(d); Changed?.Invoke(); }
        public void Redo() { if (!Ready || _redo.Count == 0) return; var d = _redo.Pop(); ApplyInternal(d); _undo.Push(d); Changed?.Invoke(); }

        // ---- internals driven by an open Tx ----

        internal void ApplyLive(IDelta d) { ApplyInternal(d); Changed?.Invoke(); }   // a tx part: apply now, repaint

        internal void CloseTx(Tx tx, bool commit)
        {
            if (!ReferenceEquals(tx, _open)) return;   // stale / already replaced
            _open = null;
            var parts = tx.Parts;
            if (commit)
            {
                if (parts.Count == 1) { _undo.Push(parts[0]); _redo.Clear(); }                          // single delta -> push as-is
                else if (parts.Count > 1) { _undo.Push(new CompositeDelta(new List<IDelta>(parts))); _redo.Clear(); }   // bundle into one undo unit
                // 0 parts -> a lease-only tx (e.g. an empty/refused gesture): record nothing
                foreach (var p in parts) RecordOps(p);   // journal the committed ops
            }
            else
            {
                for (int i = parts.Count - 1; i >= 0; i--) InvertInternal(parts[i]);   // roll back applied parts, newest first
                if (parts.Count > 0) Changed?.Invoke();
            }
        }

        // Recurse composites; route leaf deltas to the (single, today) Store. Multi-store routing lands here later.
        void ApplyInternal(IDelta d)  { if (d is CompositeDelta c) { foreach (var p in c.Parts) ApplyInternal(p); } else Pattern?.Apply(d); }
        void InvertInternal(IDelta d) { if (d is CompositeDelta c) { for (int i = c.Parts.Count - 1; i >= 0; i--) InvertInternal(c.Parts[i]); } else Pattern?.Invert(d); }

        // Serialize a delta's ops to op-lines, grouping faces that share a transition so a merge/relabel reads as
        // ONE line per `from -> to`: `setpiece {47,51,54,61,62} 2 0`. No spaces in the brace list (one CLI token).
        static void LinesFor(IDelta d, List<string> sink)
        {
            if (d is CompositeDelta c) { foreach (var p in c.Parts) LinesFor(p, sink); return; }
            if (!(d is PieceDelta pd)) return;
            var order = new List<(int from, int to)>();                       // groups in first-seen order
            var byKey = new Dictionary<(int, int), List<int>>();              // (from,to) -> faces
            foreach (var o in pd.Ops)
            {
                var key = (o.From, o.To);
                if (!byKey.TryGetValue(key, out var faces)) { faces = new List<int>(); byKey[key] = faces; order.Add(key); }
                faces.Add(o.Face);
            }
            foreach (var k in order) sink.Add($"setpiece {{{string.Join(",", byKey[k])}}} {k.from} {k.to}");
        }
        void RecordOps(IDelta d) { var ls = new List<string>(); LinesFor(d, ls); foreach (var l in ls) Record(l); }

        // The in-effect piece ops as op-lines (the undo stack, oldest first) — the piece half of a saved session.
        // Read-only and reflects undo BY CONSTRUCTION: undone deltas aren't on the stack, so they aren't emitted.
        public List<string> OpLines()
        {
            var lines = new List<string>(); var arr = _undo.ToArray();      // Stack.ToArray = top..bottom
            for (int i = arr.Length - 1; i >= 0; i--) LinesFor(arr[i], lines);   // walk bottom..top = oldest first
            return lines;
        }
    }
}
