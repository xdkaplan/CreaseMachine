using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PieceSolver
{
    // A typed selection set (one per Real type), living in the Doc. The Editor mutates it; the view and
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
    // open it holds the lease (a foreign OpenTx/Undo/Redo self-rejects); the tool composes its delta(s) and Apply()s
    // them (live), then Run() bundles the lot into one undo unit + journals it, or Cancel() rolls them back. Disposing an
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
        public void Run()    { if (_closed) return; _closed = true; if (_alive) _doc.CloseTx(this, commit: true); }   // finalize the tx: bundle the parts into one undo unit + journal (the canonical tool shape: OpenTx -> Apply -> Run)
        public void Cancel() { if (_closed) return; _closed = true; if (_alive) _doc.CloseTx(this, commit: false); }
        public void Dispose()
        {
            if (_closed) return;
            Debug.WriteLine("Tx disposed without Run/Cancel — auto-cancelling.");
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

        // The session EVENT LOG: every committed op (`setpiece {faces} <from> <to>`), `undo` / `redo`, and app
        // command (load/revert/…) in order, as bare REPLAYABLE lines + `#` comment narration. THIS is the journal
        // — Save serializes it, and replaying it re-executes each line to faithfully reproduce the session (incl.
        // the undo/redo dance). The Console renders each line via Recorded. See docs/DOC-TX-REFACTOR.md.
        readonly List<string> _log = new List<string>();
        public IReadOnlyList<string> EventLog => _log;
        public event Action<string> Recorded;                        // fired per emitted line (the Console renders it)

        Tx _open;                                                    // the single open transaction (null = none)
        int _nextId;                                                 // monotonic id source for ALL Reals (see MintId) — only ever rises, never reused
        public Busy State { get; private set; } = Busy.None;         // a long op (Calculating / Opening) owns the Doc

        public event Action Changed;                                 // Real/Transient changed
        public bool Ready => State == Busy.None && _open == null;    // is a NEW mutation/tx allowed right now
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        // Re-point at a fresh Store (mesh load / subdivide / reset) and drop the now-meaningless history +
        // selection + any open tx — deltas reference old face indices, so they cannot survive a re-mesh.
        public void Rebind(Pattern pattern) { _open?.Cancel(); Pattern = pattern; _undo.Clear(); _redo.Clear(); _open = null; Pieces.ClearSilent(); }   // F-9: Cancel (not orphan) any open tx — first, while the OLD Pattern is still in place so its rollback inverts the right mesh (in practice _open is null here: Rebind runs between gestures)

        // The single monotonic id source for ALL Reals (today: Pattern pieces; future: Creases, Splines). Floors
        // itself above the current partition's max, so a fresh mint never collides with a Seed (ids 0..N-1) or a
        // loaded/replayed partition; and because the counter only ever rises, it never REUSES a freed id — that
        // stability is what lets the int double as identity. int is ~2.1B and never reused; promote to long/Guid
        // only if that ever bites (it won't in a session). Reals draw from this via the Func injected at construction.
        public int MintId()
        {
            var pm = Pattern?.PieceMap;
            if (pm != null) { int m = -1; for (int i = 0; i < pm.Length; i++) if (pm[i] > m) m = pm[i]; if (_nextId <= m) _nextId = m + 1; }
            return _nextId++;
        }

        // Drop the undo/redo history without re-pointing the Store — for a Chapter reset (Seed re-partitions the
        // SAME mesh, so old deltas reference invalid piece ids). Selection is cleared separately by the caller.
        public void ClearHistory() { _undo.Clear(); _redo.Clear(); }

        // A long op (bake / open) takes/releases the Doc. While busy, Run/OpenTx/Undo/Redo self-reject.
        public void EnterBusy(Busy reason) { State = reason; }
        public void ExitBusy() { State = Busy.None; }

        // Append a line to the event log + emit it to listeners (the Console). Record = a bare REPLAYABLE line
        // (op / command / undo / redo); Comment = a `#` narration line (skipped on replay).
        public void Record(string line) { _log.Add(line); Recorded?.Invoke(line); }
        public void Comment(string text) => Record("# " + text);
        public void ClearLog() => _log.Clear();   // the Console's Clear empties the event log

        // Open the single transaction. One at a time: a stale open tx is a leak -> warn + cancel it; opening while
        // a long op owns the Doc returns a refused (dead) tx whose Apply/Commit no-op.
        public Tx OpenTx()
        {
            // Gate on Busy FIRST: the stale-tx cancel below calls InvertInternal (mutates Real), so cancelling
            // before the gate let a rollback slip through during a bake (Busy.Calculating). (review F-8)
            if (State != Busy.None) { Debug.WriteLine($"OpenTx during {State} — refused."); return new Tx(this, alive: false); }
            if (_open != null) { Debug.WriteLine("OpenTx while a tx is open — cancelling the stale one."); _open.Cancel(); }
            _open = new Tx(this, alive: true);
            return _open;
        }

        // (No Doc.Run one-shot — removed. EVERY mutation is a transaction: open a Tx, Apply the delta(s), Run()
        //  it. Button commands (Merge, DelPiece) use the same explicit shape as a gesture:
        //      using var tx = doc.OpenTx(); tx.Apply(delta); tx.Run();
        //  Gate on `doc.Ready` first if you need to branch on busy/mid-gesture.)

        public void Undo() { if (!Ready || _undo.Count == 0) return; var d = _undo.Pop(); InvertInternal(d); _redo.Push(d); Record("undo"); Changed?.Invoke(); }
        public void Redo() { if (!Ready || _redo.Count == 0) return; var d = _redo.Pop(); ApplyInternal(d); _undo.Push(d); Record("redo"); Changed?.Invoke(); }

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
    }
}
