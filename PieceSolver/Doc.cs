using System;
using System.Collections.Generic;

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
        public void Add(T x)     { if (_set.Add(x))    Changed?.Invoke(); }
        public void Remove(T x)  { if (_set.Remove(x)) Changed?.Invoke(); }
        public void Clear()      { if (_set.Count > 0) { _set.Clear(); Changed?.Invoke(); } }
    }

    // The orchestrator: owns the Store(s) + Selection(s) + the undo/redo stacks, and gatekeeps all mutation
    // through Run / Undo / Redo. (Short for Document; "Project" is reserved for a future on-disk workspace.)
    //   Run(delta)  : Store.Apply -> push undo, clear redo -> fire Changed.   (the single persistent writer)
    //   Undo/Redo   : move the delta between the two stacks, Invert / Apply.
    // The Doc never inspects a delta (opaque IDelta); it owns the Store it routes to. See DOC-TX-REFACTOR.md.
    sealed class Doc
    {
        public Pattern Pattern { get; private set; }                 // the (only, today) Store
        public Selection<PieceId> Pieces { get; } = new Selection<PieceId>();

        readonly Stack<IDelta> _undo = new Stack<IDelta>();
        readonly Stack<IDelta> _redo = new Stack<IDelta>();

        public event Action Changed;                                 // Real/Transient changed (Run/Undo/Redo)
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        // Re-point at a fresh Store (mesh load / subdivide / reset) and drop the now-meaningless history +
        // selection — the deltas reference the old face indices, so they cannot survive a re-mesh.
        public void Rebind(Pattern pattern) { Pattern = pattern; _undo.Clear(); _redo.Clear(); Pieces.Clear(); }

        // Drop the undo/redo history without re-pointing the Store — for a Chapter reset (Seed re-partitions the
        // SAME mesh, so old deltas reference invalid region ids). Selection is cleared separately by the caller.
        public void ClearHistory() { _undo.Clear(); _redo.Clear(); }

        public void Run(IDelta d)
        {
            if (d == null || (d is PieceDelta pd && pd.Empty) || Pattern == null) return;
            Pattern.Apply(d); _undo.Push(d); _redo.Clear(); Changed?.Invoke();
        }
        public void Undo()
        {
            if (_undo.Count == 0 || Pattern == null) return;
            var d = _undo.Pop(); Pattern.Invert(d); _redo.Push(d); Changed?.Invoke();
        }
        public void Redo()
        {
            if (_redo.Count == 0 || Pattern == null) return;
            var d = _redo.Pop(); Pattern.Apply(d); _undo.Push(d); Changed?.Invoke();
        }
    }
}
