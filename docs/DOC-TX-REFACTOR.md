# Doc / transaction (undo-redo) refactor

Stand up the **Doc** orchestrator and a **transaction layer** so piecing edits are
undoable / redoable, and route every mutation through one gate. This is the sequel to
[`PIECER-REFACTOR.md`](PIECER-REFACTOR.md): that pass extracted the Piecer / Pattern / Editor
units; this one gives them a spine — a single mutation chokepoint that records reversible
**Deltas** on an undo/redo stack. **Merge** is the first command built on it (the payoff of
multi-select), and the forcing function that proves the machine.

This doc has two parts: the **Design Note** (durable — the model, the vocabulary, the decisions
and *why*) and the **Implementation Plan** (the ordered, mostly behavior-preserving steps,
Merge-first).

---

# Part 1 — Design Note

## Why

Piecing ops mutate `Pattern.PieceMap` in place with no record, so there is no undo. The
existing `Journal` / `StudioCommand` sink covers only coarse app commands (load/run/solve) as a
**forward** record/replay log — it is not a reverse-delta undo, and piecing bypasses it entirely.
Merge wants undo; "proper CAD editability" wants undo for everything. So we need the spine before
the entity zoo (creases-with-identity, joins, tabs, cone tips) lands on top of it.

## Vocabulary (locked)

These supersede / extend the [`PIECER-REFACTOR.md`](PIECER-REFACTOR.md) glossary.

- **Doc** — the **orchestrator**. Owns the Stores, the Selections, and the undo/redo stacks;
  gatekeeps all mutation through `Run` / `Undo` / `Redo`. (Short for Document. "Project" is
  deliberately *reserved* for a future on-disk workspace/file concept — do not use it here.)
- **Store** — a holder of **Real** state that implements `ITxAble` and defines its own `IDelta`.
  Today there is exactly one: `Pattern`.
- **`ITxAble`** — the contract a Store wears to take part in transactions:
  `void Apply(IDelta)` / `void Invert(IDelta)`. No `Checkpoint` — we record **deltas, not
  snapshots**. Implemented by Stores; **driven by** the Doc (the Doc is *not* `ITxAble`).
- **`IDelta`** — one reversible change. **Opaque to the Doc** (it only ever holds/forwards it),
  **concrete to the Store** (which applies/inverts it). An `IDelta` is a list of **Ops**.
  Designed to **compose** (a future composite `IDelta` bundles per-Store deltas for a
  multi-Store gesture); v1 ships a single-Store delta only.
- **Op** — the **primitive, invertible mutation that a Delta is composed of** — e.g.
  `SetPiece(face, from→to)`. A Command *generates* Ops; an `IDelta` *is a list of* Ops; the Store
  *applies/inverts* Ops. (Op ≠ Command.)
- **Command** *(our word)* / **Tool** *(the user's word)* — an invokable action that **computes
  an `IDelta`** from the current Selection + Real state. **Pure: it reads, it does not mutate.**
  `Merge`, `Delete`, `Carve`, `Grow`, `Mint`.
- **Run / Undo / Redo** — the Doc's three verbs. `Run(delta)` is the single mutation path
  (the base meaning of "run" in this layer; the CLI/flow `run` is a different layer).
- **Real vs Transient** — **Real** = authoritative state that lives in Deltas / is `ITxAble`
  (`PieceMap`). **Transient** = derived state that is **regen'd**, never in a Delta
  (`CreaseMap`). The line that decides what is undoable.
- **regen** — re-derive Transient from Real. Runs *after* every `Apply` / `Invert`. Never
  recorded. (`RegenCrease`.)
- **Element** — any selectable/editable thing with identity: **Piece**, **Crease**, and later
  **Join** / **Tab** / **Cone tip** / **Control point**. (Was "entity".)
- **`Selection<T>`** — a typed selection set, one per Element type, living in the Doc, carrying a
  **Changed** event. **Not** `ITxAble` — selection is **not** on the undo stack (nor is the view
  / camera).
- **Editor** — a tool's gesture **grammar** + view preview (`Piecer`, future `Creaser`). Produces
  Commands; sets the **tx boundary** (one gesture = one tx = one Delta).
- **tx / transaction** — one undoable unit. **One gesture = one tx.**
- **Chapter** — a whole-partition reset boundary (`Seed`). (Unchanged.)

## The five roles (the law)

```
Editor      gesture grammar + preview. Produces a Command; bounds the tx (gesture start → commit).
  │  emits
Command     reads Selection + Real state, COMPUTES an IDelta. Pure — never mutates.   (Tool, to the user)
  │  hands the delta to
Doc         Run(delta): store.Apply(delta) → push undo, clear redo → fire Changed.    (the orchestrator)
  │  drives                                   Undo/Redo: pop/push between the two stacks, Invert/Apply.
Store       Apply(IDelta)/Invert(IDelta): mutate Real, then regen Transient.          (Pattern; ITxAble)
  ▲  composed of
Op          one invertible atom: SetPiece(face, from→to). The Store knows how to apply/invert it.
```

Single mutation path: **`store.Apply` is the only thing that writes Real state**, and it is only
ever reached via `Doc.Run` / `Doc.Redo` (or `Invert` via `Doc.Undo`). Nothing else mutates.

Why the Command computes the delta (and we `Run(delta)`, not `Run(command)`): the Command already
knows the selection and can read Real state, so it produces the exact change up front. The Doc then
applies a *finished* delta — it never has to open a tx, let an op mutate, and capture. That keeps
the Doc trivial and the mutation path single. Storing the delta ("N faces: from→to") is also far
cheaper on the undo stack than snapshotting a 50k-int `PieceMap`.

## Data shapes (v1)

```csharp
interface IDelta { }                                  // opaque to the Doc

readonly struct Op { public int Face, From, To; }     // one invertible atom

sealed class PieceDelta : IDelta                       // Pattern's delta = a list of Ops
{
    public readonly List<Op> Ops;
    public bool Empty => Ops.Count == 0;
}

interface ITxAble
{
    void Apply (IDelta d);   // mutate Real forward, then regen Transient
    void Invert(IDelta d);   // mutate Real back,    then regen Transient
}
```

`Pattern : ITxAble`:

```csharp
public void Apply(IDelta d)  { var pd=(PieceDelta)d; foreach (var o in pd.Ops) PieceMap[o.Face]=o.To;   RegenCrease(); }
public void Invert(IDelta d) { var pd=(PieceDelta)d; foreach (var o in pd.Ops) PieceMap[o.Face]=o.From; RegenCrease(); }
```

`Doc` (the orchestrator):

```csharp
sealed class Doc
{
    public Pattern Pattern { get; }                         // the (only, today) Store
    public Selection<PieceId> Pieces { get; } = new();      // typed selection — NOT undoable
    readonly Stack<IDelta> _undo = new(), _redo = new();
    public event Action Changed;                            // Real/Transient changed → view + CanRun react
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Run(IDelta d) { if (d is PieceDelta { Empty: true }) return;
                                Pattern.Apply(d); _undo.Push(d); _redo.Clear(); Changed?.Invoke(); }
    public void Undo() { if (_undo.Count==0) return; var d=_undo.Pop(); Pattern.Invert(d); _redo.Push(d); Changed?.Invoke(); }
    public void Redo() { if (_redo.Count==0) return; var d=_redo.Pop(); Pattern.Apply (d); _undo.Push(d); Changed?.Invoke(); }
}
```

`Selection<T>` (lives in the Doc; the Editor mutates it; **not** on the undo stack):

```csharp
sealed class Selection<T>
{
    readonly HashSet<T> _set = new();
    public event Action Changed;
    public bool Contains(T x) => _set.Contains(x);
    public int Count => _set.Count;
    public IEnumerable<T> Items => _set;
    public void Replace(T x){ _set.Clear(); _set.Add(x); Changed?.Invoke(); }
    public void Add(T x)    { if (_set.Add(x))    Changed?.Invoke(); }
    public void Remove(T x) { if (_set.Remove(x)) Changed?.Invoke(); }
    public void Clear()     { if (_set.Count>0){ _set.Clear(); Changed?.Invoke(); } }
}
```

## Real vs Transient — what goes where

- **`PieceMap` = Real.** Its changes are the Ops in a `PieceDelta`. Undoable.
- **`CreaseMap` = Transient.** Never in a Delta; **regen'd** inside `Apply`/`Invert`. (When creases
  become Real Elements with identity — a *later* spec — regen becomes *reconcile*. Out of scope here.)
- **Selection = neither.** Lives in the Doc, fires `Changed`, is **not** undoable (decided).

## Commands — Merge (worked example)

A Command is pure: `(Real state, Selection) → IDelta`. Two equally valid ways to produce the Ops:

- **Direct emit** (simple commands, e.g. Merge): walk the faces, append an `Op` per change.
- **Compute-new-then-diff** (complex commands, e.g. Carve/Grow): run the *existing* labelling
  logic into a scratch `int[]`, then diff scratch vs current → Ops. This reuses the proven
  carve/grow/split logic almost verbatim and bundles a carve **and** its `SplitDisconnected`
  renumber into **one** Delta (one undo step). The undo stack still holds only the cheap diff.

```csharp
// Merge: relabel every selected piece to the survivor (min id). Direct emit; no SplitDisconnected
// (adjacent selections — the normal case — yield one connected piece). Returns an empty delta if <2.
static PieceDelta Merge(Pattern p, Selection<PieceId> sel)
{
    var ops = new List<Op>();
    if (sel.Count < 2) return new PieceDelta(ops);
    int keep = int.MaxValue; foreach (var id in sel.Items) keep = Math.Min(keep, id.Value);
    var map = p.PieceMap;
    for (int f = 0; f < map.Length; f++)
        if (sel.Contains(new PieceId(map[f])) && map[f] != keep) ops.Add(new Op{Face=f, From=map[f], To=keep});
    return new PieceDelta(ops);
}
```

Wiring: a keybind (`M`) / button handler does `var d = Merge(doc.Pattern, doc.Pieces); doc.Run(d);`
then collapses the selection to `{keep}`. Undo/Redo on `Ctrl+Z` / `Ctrl+Y`.

**`CanRun` / availability** is a pure predicate over Selection + Real state (`Merge` ⇔ ≥2 pieces
selected — see the *Merge adjacency* note in Risks). In v1 commands are **functions returning
`IDelta`** plus a `CanRun`-style guard; a formal `ICommand { CanRun; Compute }` interface is
deferred until chrome/buttons need uniform enablement (YAGNI).

## Decisions locked

1. **Recorded Deltas, not snapshots.** Cheaper on the undo stack; the Command produces the delta.
2. **Undo/redo only in v1.** Journaling piecing (forward commands + CLI parity) is deferred.
3. **The Command computes the delta; `Doc.Run(delta)` applies it.** `store.Apply` is the sole writer.
4. **Real / Transient** is the undoable boundary. Transient (`CreaseMap`) regens after Apply/Invert.
5. **Selection lives in the Doc, typed (`Selection<T>`), and is NOT undoable.** Nor is the view/camera.
6. **Merge is adjacent-oriented** (the survivor keeps the id; no auto-split) — see Risks.
7. **Minor id churn is acceptable** until stable ids land; the Doc recomputes the affected selection
   after a mutating op.

## Non-goals (explicitly out of scope here)

Crease-with-identity / reconcile-regen · the generalized Element store · the `Creaser` editor ·
Joins / Tabs / Cone tips / Control points · stable `PieceId` GUIDs · journaling of piecing ·
composite multi-Store deltas (the `IDelta` interface is forward-compatible, but only the
single-Store `PieceDelta` ships). Each is its own later spec; this one is just the spine.

---

# Part 2 — Implementation Plan

Ordered, build-green at every step (`dotnet build PieceSolver -c Release` → 0/0, launch, verify),
one commit per step (traditional cadence). **Merge-first:** Steps 0–4 stand up the machine and make
Merge undoable end-to-end; Step 5 converts the remaining piece ops; Step 6 revises the docs.

### Step 0 — Delta scaffolding *(pure addition, no behavior change)*
- Add `IDelta`, `Op`, `PieceDelta`, `ITxAble` (new file `PieceSolver/Tx.cs`).
- Ensure `PieceId : IEquatable<PieceId>` (+ `GetHashCode`) so `Selection<PieceId>` / `HashSet`
  hash well — add if missing.
- Build 0/0. (Nothing references these yet.)

### Step 1 — `Pattern` implements `ITxAble` *(pure addition)*
- Add `Apply` / `Invert` (apply/revert Ops on `PieceMap`, then `RegenCrease`).
- Build 0/0. Still unused.

### Step 2 — the `Doc` *(new orchestrator; wire ownership)*
- Add `PieceSolver/Doc.cs` (`Pattern` + `Selection<PieceId> Pieces` + undo/redo stacks +
  `Run`/`Undo`/`Redo` + `Changed`/`CanUndo`/`CanRedo`) and `Selection<T>` (new file or in `Doc.cs`).
- `MainWindow` constructs the `Doc` (it currently owns the `Pattern`; the `Doc` now owns it, and
  `MainWindow` holds the `Doc`). No editor change yet.
- Build 0/0, launch — behavior identical (Doc is dormant).

### Step 3 — hoist selection into the Doc *(behavior-preserving)*
- Add `Doc Doc { get; }` to `IEditorHost`; `MainWindow` returns its `Doc`.
- `Piecer._selection` → `_host.Doc.Pieces`. Replace the tap logic's set mutations with
  `Pieces.Replace/Add/Remove/Clear`; `FaceFill` reads `Pieces.Contains(region)`; `Deselect` /
  `ClearSelection` → `Pieces.Clear()`.
- Subscribe `MainWindow` to `Doc.Pieces.Changed` → `RebuildPieces` (replaces the selection-driven
  imperative `RefreshPieces` calls in `Piecer`; brush-preview refreshes stay for now).
- Build 0/0, launch — select/add/remove/deselect behave exactly as today.

### Step 4 — Merge command + undo/redo UI *(first undoable op, end-to-end)*
- Add `Merge` (Commands location — `PieceSolver/Commands.cs`) + `CanMerge`.
- Keybind `M` → `Run(Merge(...))` then collapse selection to `{keep}`; `Ctrl+Z`/`Ctrl+Y` →
  `Doc.Undo`/`Redo`. (Optionally a Merge button gated by `CanMerge`.)
- Build 0/0, launch — select 2+ adjacent pieces, `M` merges, `Ctrl+Z` restores, `Ctrl+Y` re-merges.
  **Milestone: the machine is proven.**

### Step 5 — convert the rest to Commands *(Phase 2 — everything undoable)*
- Convert `Delete` (renamed from `Remove`), `Carve`, `Grow`, `Mint` from in-place `Pattern`
  mutators into Commands that compute a `PieceDelta` (compute-new-then-diff; carve/grow bundle
  their `SplitDisconnected` renumber into the same delta). Route each through `Doc.Run`.
- `Pattern` slims to a **pure Store**: Real (`PieceMap`) + Transient (`CreaseMap`) + `ITxAble`
  (`Apply`/`Invert`) + `RegenCrease` + read-only queries (`FacesUnderBrush`, `NewRegionId`,
  `GrowAssign`, `LargestComponent`, `FullyMarked`). Mutating logic moves to `Commands`.
- The Doc recomputes the affected selection after each op (decision 7).
- Build 0/0, launch — carve / grow / mint / delete all undo/redo. The app is uniformly undoable.

### Step 6 — revise the project docs *(closing step)*
- Update **`AGENTS.md`** (the Doc/tx layer + the new vocabulary: Doc, Store, `ITxAble`, `IDelta`,
  Op, Command/Tool, Run/Undo/Redo, Real/Transient, Element, `Selection<T>`), **`docs/HANDOFF.md`**,
  and cross-link this doc from **`PIECER-REFACTOR.md`** (its sequel). Note `Remove → Delete` and
  `entity → Element` renames. Leave README as-is unless a user-facing line changed.
- Refresh the **memory** index entry for the piecing architecture.

## Verification & risks

- **Verification:** each step builds 0/0 and launches; Steps 3–5 are checked against the
  pre-refactor gesture behavior (select/add/remove, carve, grow, mint, delete) plus the new
  undo/redo. No engine/solver code is touched, so the bench checksums are unaffected.
- **Merge adjacency (decision 6).** v1 `Merge` relabels all selected pieces to the survivor with
  **no** `SplitDisconnected`, so an *adjacent* selection (the normal case) yields one connected
  piece, while a *non-adjacent* selection yields a single disjoint-id piece. Two ways to honour
  "adjacent-only": (a) gate `CanMerge` on the selected pieces being mutually connected (a BFS over
  the piece-adjacency graph), or (b) accept the disjoint result for now (minor churn). **Plan: ship
  (b), add (a) if the disjoint case bites** — flag for review.
- **Selection staleness (decision 7).** Until stable `PieceId` GUIDs exist, an op that renumbers
  pieces leaves the selection pointing at stale ids; the Doc recomputes it post-op (Merge →
  `{keep}`; carve/grow → keep surviving ids, drop vanished). Accepted as minor churn.
- **Half-undoable interim.** After Step 4, Merge is undoable but carve/grow/delete are not (still
  in-place). This is a transient state closed by Step 5; if it reads as confusing during dev, pull
  Step 5 forward before exposing the build.
