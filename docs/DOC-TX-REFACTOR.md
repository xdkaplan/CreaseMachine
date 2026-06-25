# Doc / transaction (undo-redo) refactor

Stand up the **Doc** orchestrator and a **transaction layer** so piecing edits are
undoable / redoable, and route every mutation through one gate. This is the sequel to
[`PIECER-REFACTOR.md`](archive/PIECER-REFACTOR.md): that pass extracted the Piecer / Pattern / Editor
units; this one gives them a spine — a single mutation chokepoint that records reversible
**Deltas** on an undo/redo stack. **Merge** is the first command built on it (the payoff of
multi-select), and the forcing function that proves the machine.

This doc has two parts: the **Design Note** (durable — the model, the vocabulary, the decisions
and *why*) and the **Implementation Plan** (the ordered, mostly behavior-preserving steps,
Merge-first).

> **Revised 2026-06-24 — see [§ Revision: journaling = the op-log](#revision-2026-06-24-journaling--the-op-log) and [Part 3](#part-3--journaling-op-log-implementation-plan).**
> Read **Parts 1–2 + the [Shortcomings list](#shortcomings--known-gaps-journaling-op-log) as the as-built
> state**: the undo/tx layer **shipped**, and the op-log records every piece op — but, as the Shortcomings
> spell out, it did **not** collapse the two journals into one (piece-op *replay* is unwired, and CLI parity
> for the new verbs is broken). Read **the Revision as the target**: the *one op-log = undo = journal =
> save* model it describes (folding journaling in and dropping the forward/reverse duality, generalizing
> `Op` beyond "the atom inside a delta") is the aspiration the as-built is converging toward, not a
> finished state.

---

# Part 1 — Design Note

## Why

Piecing ops mutate `Pattern.PieceMap` in place with no record, so there is no undo. The
existing `Journal` / `StudioCommand` sink covers only coarse app commands (load/run/solve) as a
**forward** record/replay log — it is not a reverse-delta undo, and piecing bypasses it entirely.
Merge wants undo; "proper CAD editability" wants undo for everything. So we need the spine before
the Real zoo (creases-with-identity, joins, tabs, cone tips) lands on top of it.

## Vocabulary (locked)

These supersede / extend the [`PIECER-REFACTOR.md`](archive/PIECER-REFACTOR.md) glossary.

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
  *applies/inverts* Ops. (Op ≠ Command.) *(Generalized 2026-06-24 — see the Revision: an Op is now
  **any** reversible mutation of Real state, the **sole** Doc-mutation path, and the **journal
  entry** — fine-grained for piece edits, coarse for `load`/`solve`.)*
- **Command** *(our word)* / **Tool** *(the user's word)* — an invokable action that **computes
  an `IDelta`** from the current Selection + Real state. **Pure: it reads, it does not mutate.**
  `Merge`, `Delete`, `Carve`, `Grow`, `Mint`.
- **Run / Undo / Redo** — the Doc's three verbs. `Run(delta)` is the single mutation path
  (the base meaning of "run" in this layer; the CLI/flow `run` is a different layer).
- **Real vs Transient** — **Real** = authoritative state that lives in Deltas / is `ITxAble`
  (`PieceMap`). **Transient** = derived state that is **refreshed** from Real, never in a Delta
  (`CreaseMap`). The line that decides what is undoable.
- **refresh** (this doc's older "regen") — re-derive a Transient from Real. `Apply`/`Invert`
  **rot** the Transient (`RegenCrease`); a *Grown* transient like `CreaseMap` then re-derives
  lazily on read. Never recorded. Full freshness model: [docs/specs/DOC-SPEC.md](specs/DOC-SPEC.md).
- **Real** — an authored node with identity: **Piece**, **Crease**, and later **Join** / **Tab** /
  **Cone tip** / **Control point**. (Retired names: "Element", "entity". The full node model — Reals in
  an ownership tree, Transients in a dependency DAG — is in [docs/specs/DOC-SPEC.md](specs/DOC-SPEC.md).)
- **`Selection<T>`** — a typed selection set, one per Real type, living in the Doc, carrying a
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
    public Pattern Pattern { get; private set; }            // the (only, today) Store
    public Selection<PieceId> Pieces { get; } = new();      // typed selection — NOT undoable
    readonly Stack<IDelta> _undo = new(), _redo = new();
    Tx _open;                                               // the single open transaction (null = none)
    public Busy State { get; private set; } = Busy.None;    // a long op (Calculating/Opening) owns the Doc
    public event Action Changed;
    public bool Ready => State == Busy.None && _open == null;   // is a NEW mutation/tx allowed right now

    public Tx OpenTx();                                     // open the one tx (lease); stale -> warn+cancel; busy -> dead tx
    public bool Run(IDelta d);                              // one-shot = open+apply+commit; self-rejects if !Ready
    public void Undo();  public void Redo();               // self-reject if !Ready
    public void EnterBusy(Busy r); public void ExitBusy(); // a long op (bake/open) takes/releases the Doc
}
```

**Mutating entry points SELF-REJECT when `!Ready`** — the guard lives inside `Run`/`Undo`/`Redo`/`OpenTx`,
not at the call sites, so callers never have to remember to check and a rejected call is a clean no-op
(each delta is all-or-nothing). `Run` is one-shot sugar for `OpenTx → Apply → Commit`.

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
  become Reals with identity — a *later* spec — regen becomes *reconcile*. Out of scope here.)
- **Selection = neither.** Lives in the Doc, fires `Changed`, is **not** undoable (decided).

## Commands — Merge (worked example)

A Command is pure: `(Real state, Selection) → IDelta`. Two equally valid ways to produce the Ops:

- **Direct emit** (simple commands, e.g. Merge): walk the faces, append an `Op` per change.
- **Compute-new-then-diff** (complex commands, e.g. Carve/Grow): run the *existing* labelling
  logic into a scratch `int[]`, then diff scratch vs current → Ops. This reuses the proven
  carve/grow/split logic almost verbatim and bundles a carve **and** its `SplitDisconnected`
  renumber into **one** Delta (one undo step). The undo stack still holds only the cheap diff.

```csharp
// Merge: fuse each connected component of the selection into its survivor (min id). Pattern.MergeGroups
// maps every selected piece -> its component survivor (isolated pieces map to themselves); the command
// just diffs that into ops. Empty if nothing moves (all selected pieces isolated from each other).
static PieceDelta Merge(Pattern p, Dictionary<int,int> groups)   // groups = p.MergeGroups(selection)
{
    var ops = new List<Op>(); var map = p.PieceMap;
    for (int f = 0; f < map.Length; f++)
        if (groups.TryGetValue(map[f], out int surv) && map[f] != surv) ops.Add(new Op{Face=f, From=map[f], To=surv});
    return new PieceDelta(ops);
}
```

Wiring: `M` does `var g = pattern.MergeGroups(ids); var d = Merge(pattern, g); if (doc.Run(d)) doc.Pieces.Set(g.Values)`
— the selection collapses to the survivors (merged clusters + untouched singletons). Undo/Redo on
`Ctrl+Z` / `Ctrl+Y` / `Ctrl+Shift+Z`.

**`CanRun` / availability** is a pure predicate over Selection + Real state (`Merge` ⇔ ≥2 pieces selected
**and** at least one adjacent pair — i.e. a non-empty delta). In v1 commands are **functions returning
`IDelta`** plus a `CanRun`-style guard; a formal `ICommand { CanRun; Compute }` interface is deferred until
chrome/buttons need uniform enablement (YAGNI).

## Transaction scope & the concurrency guard

A gesture brackets its edit with an explicit **transaction scope** so an interleaved command can't corrupt
an in-flight edit (e.g. `Ctrl+Z` mid-carve — plausible, since carve already holds `Ctrl`):

- **`OpenTx()`** opens the **one** transaction (a lease). While it's open the Doc isn't `Ready`, so foreign
  `Run`/`Undo`/`Redo`/`OpenTx` **self-reject**. The tool composes its delta privately during the drag
  (Real state untouched — preview only), then at mouse-up `tx.Apply(delta)` (applies live) + `tx.Commit()`.
- **One transaction at a time** — a stale open tx is a leak → debug-warn + auto-cancel; a `Tx` disposed
  without Commit/Cancel auto-cancels. **ESC** mid-stroke → `tx.Cancel()` (rolls back any applied parts;
  during a brush drag that's zero, since the brush applies only at commit) + disarm the gesture.
- **A tx accumulates** — `Apply` may be called multiple times; `Commit` bundles them into one
  `CompositeDelta` = **one undo unit** (invert runs the parts in reverse). The brush only ever commits one
  delta, but the capability is there for future macro/multi-step tools (and is parallel-friendly).
- **Long ops own the Doc too** — `EnterBusy(Calculating)` around the bake makes `Run`/`Undo`/`Redo`
  self-reject while the worker owns the mesh. `Ready == State == None && no open tx`. (`Save` is an atomic
  read on the UI thread and a gesture leaves Real state at a committed snapshot, so reads never block.)

Single-threaded today, so one-tx-at-a-time costs nothing; when work goes multithreaded, "every tx is
opened and closed" is the seam where a clean ordering guard drops in.

## Decisions locked

1. **Recorded Deltas, not snapshots.** Cheaper on the undo stack; the Command produces the delta.
2. **Undo/redo only in v1.** ~~Journaling piecing is deferred.~~ **Superseded (2026-06-24):**
   journaling is now the **op-log** (see the Revision). No separate forward-command log — the one
   op-log serves undo + redo + journal + replay + persist.
3. **The Command computes the delta; `Doc.Run(delta)` applies it.** `store.Apply` is the sole writer.
4. **Real / Transient** is the undoable boundary. Transient (`CreaseMap`) regens after Apply/Invert.
5. **Selection lives in the Doc, typed (`Selection<T>`), and is NOT undoable.** Nor is the view/camera.
6. **Merge fuses connected components.** Each adjacent cluster of selected pieces merges into one
   (survivor = min id, via `Pattern.MergeGroups`); a selected piece adjacent to no other selected piece
   is left as-is. Merge is refused only if no two selected pieces touch (nothing to merge). No auto-split
   (each cluster is connected by construction). The selection collapses to the surviving pieces.
7. **Minor id churn is acceptable** until stable ids land; the Doc recomputes the affected selection
   after a mutating op.
8. **One transaction at a time, opened-and-closed.** Mutating entry points **self-reject** when the Doc
   isn't `Ready` (open tx or a long op); a rejected call is a clean no-op. A tx **accumulates** and commits
   as one undo unit. This is the concurrency guard (and the future multithread seam).

## Non-goals (explicitly out of scope here)

Crease-with-identity / reconcile-regen · the generalized Real store · the `Creaser` editor ·
Joins / Tabs / Cone tips / Control points · stable `PieceId` GUIDs · composite multi-Store deltas
(the `IDelta` interface is forward-compatible, but only the single-Store `PieceDelta` ships). Each
is its own later spec; this one is just the spine. *(Journaling of piecing was here too; it is now
in scope — see the Revision.)*

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
  `Pieces.Replace/Add/Remove/Clear`; `FaceFill` reads `Pieces.Contains(piece)`; `Deselect` /
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
  (`Apply`/`Invert`) + `RegenCrease` + read-only queries (`FacesUnderBrush`, `NewPieceId`,
  `GrowAssign`, `LargestComponent`, `MostlyMarked`). Mutating logic moves to `Commands`.
- The Doc recomputes the affected selection after each op (decision 7).
- Build 0/0, launch — carve / grow / mint / delete all undo/redo. The app is uniformly undoable.

### Step 6 — revise the project docs *(closing step)*
- Update **`AGENTS.md`** (the Doc/tx layer + the new vocabulary: Doc, Store, `ITxAble`, `IDelta`,
  Op, Command/Tool, Run/Undo/Redo, Real/Transient, `Selection<T>`), **`docs/HANDOFF.md`**,
  and cross-link this doc from **`PIECER-REFACTOR.md`** (its sequel). Note `Remove → Delete` and
  `entity`/`Element` → `Real` renames. Leave README as-is unless a user-facing line changed.
- Refresh the **memory** index entry for the piecing architecture.

## Verification & risks

- **Verification:** each step builds 0/0 and launches; Steps 3–5 are checked against the
  pre-refactor gesture behavior (select/add/remove, carve, grow, mint, delete) plus the new
  undo/redo. No engine/solver code is touched, so the bench checksums are unaffected.
- **Merge components (decision 6).** `Pattern.MergeGroups` union-finds the selection's faces across
  shared borders, so each **connected component** of selected pieces gets one survivor (min id);
  isolated selected pieces map to themselves. Merge fuses every adjacent cluster independently and
  leaves singletons alone (`{A,B,C}`, A|B adjacent, C off on its own → A∪B, keep C). The `M` key no-ops
  with a hint only when *no* two selected pieces touch (empty delta). Each cluster is connected by
  construction, so the relabel needs **no** `SplitDisconnected` and never makes a disjoint-id piece.
- **Selection staleness (decision 7).** Until stable `PieceId` GUIDs exist, an op that renumbers
  pieces leaves the selection pointing at stale ids; the Doc recomputes it post-op (Merge →
  `{keep}`; carve/grow → keep surviving ids, drop vanished). Accepted as minor churn.
- **Half-undoable interim.** After Step 4, Merge is undoable but carve/grow/delete are not (still
  in-place). This is a transient state closed by Step 5; if it reads as confusing during dev, pull
  Step 5 forward before exposing the build.

---

# Revision (2026-06-24): journaling = the op-log

The undo/tx layer in Parts 1–2 shipped. This revision folds **journaling** into it — and in doing
so **collapses** the planned "forward command → journal / reverse delta → undo" duality into a
**single op-log**. The Console becomes a live view of that log: a small DSL of **ops** (replayable)
and **`#` comments** (intent labels / narration), with light syntax highlighting.

## The unifying idea

> An **Op** is the *only* thing that mutates the Doc, always inside a tx.

So the ordered **op-log** *is* the undo history *is* the journal *is* the persisted session — **one
structure + a cursor**, read four ways:

| reading | mechanism |
|---|---|
| **undo** | step the cursor back → `Invert` the op |
| **redo** | step the cursor forward → `Apply` the op |
| **replay** | apply the log from empty |
| **save** | serialize the log |

(The op-log + cursor *replaces* the separate `_undo` / `_redo` stacks: ops left of the cursor are in
effect; ops right of it are undone/redoable; a new op truncates the redo tail and appends.)

## Vocabulary deltas (this revises §Vocabulary)

- **Op** — generalized from "the atom inside a delta" to **any reversible mutation of *Real* Doc
  state**, the **sole** Doc-mutation path, and the **journal entry**. Fine-grained for piece edits
  (`SetPiece(face, from→to)`), **coarse** for app-level (`LoadMesh`, `SolveGeometry`).
- **Command / Tool** — **demoted to transient GUI intent**: the thing the user invokes (the Merge
  tool) that *emits* ops into a tx. **Not journaled** — only its ops persist; the intent survives
  only as a `#` comment label (`# merge 2 pieces`).
- **Journal** — now = **the op-log** (was the separate `StudioCommand` forward-log), **owned by the
  Doc**. The Console renders it.
- **`#` comment** — a non-replayable line: intent labels, op summaries, status. Full-line *or*
  trailing (`carve … # note`). Ignored on replay.

## Real-stores / Transient-regens (the storage bound)

The op-log carries **Real** mutations only. **Transient** is never stored — it's a pure function of
Real, regen'd after every `Apply`/`Invert`/replay. This bounds the log and keeps derived state
always-consistent:

| state | in the op-log? | restored on apply/replay by |
|---|---|---|
| **Real** (`PieceMap`, geometry) | **yes** — the op carries it | applying the op (the op *is* the change) |
| **Transient** (`CreaseMap`, rulings, distance field) | **never** | regen from Real |

The sharp consequence — **Solve must store its result.** The developed vertex positions are **Real**
(not derivable) *and* the bake is **not bit-deterministic** (FP / parallel-reduction drift), so
re-running on replay would diverge. So the `SolveOp` **carries its result geometry** (a code comment
at the store site spells out the FP reason). `load` is cheap to re-apply; only coarse,
non-deterministic ops pay the store cost. (If an op touched only Transient it would store nothing.)

## The journal DSL (Console = a live `.journal`)

```
load bunny.obj                  # coarse op — Real: swaps the mesh
solve acc=0.2 subdiv=2          # coarse op — Real: stores the developed geometry
# merge piece(3,7)              # intent label (the GUI command — comment, ignored on replay)
SetPiece(47, 3→2)               # the merge's OUTPUT ops (Real: PieceMap)
SetPiece(48, 3→2)
# carve piece(4)                # gesture intent label (optional, not critical)
SetPiece(50, 4→9)               # the gesture's OUTPUT ops == the committed delta
SetPiece(51, 4→9)
```

- **Every command journals as its OUTPUT ops** (= the committed delta) — discrete (`merge`) and
  gesture (`carve`) alike. The command/intent is only ever an **optional `#` label**, never a
  replayable line. So the **committed delta *is* the op-log entry** — the undo stack and the journal
  are the same data.
- **bare line = op** (replayable, reversible); **`#` line / trailing `#` = comment**.
- `StudioCommand.Parse` already treats a leading `#` as a comment; add: **strip from the first `#`**
  so inline comments work.
- **CLI parity preserved** — `load` / `solve` lines are byte-identical to today's; they're just
  reclassified as (coarse) ops.

## Accepted trade-off

**No parametric replay.** Replay reproduces *exact state* (re-applies ops); it does **not** re-run
command logic against a changed base. GUI transients (selection, camera, hover, brush preview) are
not ops and are never journaled or replayed. For our goals that's a feature, not a loss.

---

# Part 3 — Journaling (op-log) implementation plan

Same cadence (build 0/0 + launch + one commit per slice). Builds on the shipped tx layer.
**Status (revised 2026-06-24, post master-merge):** Slice A shipped; Slice C shipped *differently* (one
unified `EventLog` drives Save, but the `_undo` / `_redo` stacks were kept rather than replaced by a
cursor); Slice B is parked for the solver-refactor agent.

### Slice A — Doc owns the journal; piece edits journal as ops; Console = the op-log — **DONE**
- The **Doc** owns the op-log (`_log` / `EventLog` / `Record` / `Comment`). On `Run` / `tx.Commit` the
  committed delta's ops serialize to `setpiece {faces} from to` (grouped by `from→to` transition) and
  stream into the log via `Record`.
- **`#`-comment split:** `Doc.Comment` is the comment channel (auto-`#`); op/command echoes are bare.
  `Parse` strips inline comments (from the first ` #`).
- The Console renders the log via the Doc's `Recorded` event → `Echo` → `ConsoleWindow.AppendLine`
  (ops highlighted, `#` comments dimmed, ISO timestamps). Piece edits show as `setpiece` ops with
  `# merge N pieces, M faces` labels. *(Replaying those lines back is still unwired — Shortcoming 1.)*

### Slice B — `load` / `solve` become coarse ops — **PENDING** (parked for the solver-refactor agent)
- `LoadOp` (Real: swap mesh; cheap re-apply) and `SolveOp` (**stores its result geometry** — with the
  FP-determinism comment). Serialization stays `load …` / `solve …` (CLI parity); replay applies the
  stored result instead of re-running the non-deterministic bake.
- **Not built:** `solve` / `SolveOp` belong to the solver-refactor agent (AGENTS carve-out). `load` /
  `revert` are recorded as coarse command-lines through the Doc today, but neither is yet a reversible op.

### Slice C — collapse undo/redo + journal into one op-log — **DONE (differently)**
- **Shipped:** every command routes through `Doc.Record`; undo/redo append **bare `undo` / `redo`
  verbs** into the single `_log`; **Save serializes the whole `EventLog`**. One journal now drives Save
  + Console, in lockstep with undo (the Console shows `undo` / `redo` lines; Console == saved journal).
- **Not done as literally specced:** the `_undo` / `_redo` stacks were **kept** (not replaced by a
  log + cursor). They already act as the op-log + cursor, so the rewrite was judged needless churn; the
  user-visible goal — one unified journal — is met.

### Closing — docs — **PARTIAL**
- `AGENTS.md` carries the Doc/tx vocabulary + the op-log model; this doc's Revision + Part 3 are the
  record; `HANDOFF.md` and the memory entry are updated. The remaining fold-in (`Op` / `Command` /
  `Journal` as the architecture of record) is complete except where Slice B (solve-as-op) is pending.

---

# Shortcomings / known gaps (journaling op-log)

Updated 2026-06-24 (post master-merge). The op-log shipped: every command routes through the Doc's single
`EventLog` (`load` / `revert` / `run` / `subdivide` / `matcap` command-lines + `setpiece` piece ops + bare
`undo` / `redo` verbs + `#` comment labels), the Console renders it with ISO timestamps + syntax
highlighting, and **Save = the `EventLog`**. Remaining gaps, roughly worst-first:

1. **Piece-op replay is not wired (the big one).** A saved journal's `setpiece` lines can't be replayed
   back. Two reasons: **(a) Propose isn't journaled** (it's a button, not a `StudioCommand`), so a bare
   journal can't rebuild the *seeded partition* the ops apply to; and **(b)** ops serialize into the log
   (`Doc.LinesFor` / `RecordOps`) but there's no parse-and-apply path — `StudioCommand.Parse` has no
   `setpiece` case, so `OpenAndReplay` handles app-commands only. Save is a one-way trip today.
2. **`solve` / `SolveOp` deferred** (parked for the solver-refactor agent). Solve isn't an op, so a
   journal's `solve` line re-runs the (non-deterministic) bake rather than restoring a stored result. The
   **Solve-stores-result** design (Real geometry, FP-determinism) is unbuilt.
3. **Ops are face-index + piece-id based, hence session-local.** `setpiece <face> <from> <to>` uses dense
   face indices + piece ids that renumber (`SplitDisconnected`). A journal is only valid for the *same
   mesh/session*; cross-session / post-remesh replay needs **stable ids** (deferred GUID work). `from` is
   informational (Apply uses only `to`).
4. **CLI parity broken for the new verbs.** PieceSolver emits `revert` and `setpiece`; the headless CLI
   (`crease.exe`) knows neither (it has `reset`, no piecing). A modern journal won't round-trip through the
   CLI. (`Parse` accepts `reset` as a legacy alias, so the reverse direction still works.) Inline-comment
   stripping is also simplistic (strips from the first ` #`; a `#` with no leading space, or a path
   containing ` #`, is mishandled).
5. **No test coverage.** The bench / `GradCheck` + CLI checksums don't touch the piecing/journaling layer
   (the CLI is a flow-only fossil). None of this is regression-guarded — verification was build-0/0 +
   manual launch only. (This is what makes the DoD's "test-protected" aspirational for this layer.)

## Resolved since the first cut (2026-06-24)

- **Journal unified** (was gaps 3 & 4). Every command now routes through `_doc.Record`, and **Save is
  `_doc.EventLog`** — not a concatenation of a separate `StudioCommand` store and a piece-op stream.
  Undo/redo emit bare `undo` / `redo` verbs into the *same* log, so the Console reflects undo and
  Console == saved journal again (the earlier "no `# undo` marker / Console ≠ saved" gap is closed).
- **Saved ops carry labels** (was gap 5). Save serializes the whole `EventLog`, which interleaves the
  `# merge N pieces, M faces` / gesture-summary `#` comments with the ops. (There's still no first-class
  "command label precedes its ops" wiring, but a saved journal is no longer unlabeled.)
- **Merge debt cleared** (was gap 9). The master merge landed: the method is `Revert()` (the branch's
  `CmdKind.Revert` command + master's `Revert()` method reconciled), and `Pattern.CreaseMap` is now a
  `Transient<HashSet<long>>` — the op-log is no longer on a pre-`Transient<T>` base.
