# Solver phase — make the painted pieces drive Solve (the unweld handoff)

> **Archived (2026-06-24).** The unweld-handoff Solve described here **shipped** — this is the design
> record. The present-tense "Today Solve…" framing describes the *pre-implementation* state, and the
> bracketed source line numbers predate the change.

Today **Solve develops the mesh's connected components** (`MeshOps.ComponentCount`/`SplitComponents`) —
i.e. the *weld topology* the file shipped with (FBX seams), **ignoring the painted `Pattern`** — and on
top of that it *destroys* the Pattern (`OnSolveAsync` calls `ApplyReset`). This spec wires the two together:
the **painted partition becomes what Solve develops**, via an *unweld along the creases* before the bake.

Sequel to [`DOC-TX-REFACTOR.md`](../DOC-TX-REFACTOR.md): it builds directly on Real / Transient / Ephemeral
and the Doc model. Two parts: the **Design Note** (model + decisions + *why*) and the **Implementation
Plan** (ordered, build-green steps).

---

# Part 1 — Design Note

## The one idea

**Solve develops a *derived* mesh — the Solver's Transient — never the authoring Real mesh.** For a pieced
mesh that derived mesh is the authoring mesh **unwelded along its creases** so each painted piece is its own
connected component; the existing per-component bake then develops them. The authoring mesh + `Pattern`
never move, so they **survive Solve** and you can keep piecing.

This is the [`DOC-TX-REFACTOR.md`](../DOC-TX-REFACTOR.md) model applied to geometry:

- **Real:** mesh + `Pattern` (`PieceMap`) + bake params.
- **Transient (the Solver's):** the cut/developed mesh. **Derived** from Real (unweld-along-`CreaseMap`, then
  develop), **lazy** (expensive → regen on entering the Solver phase), **rotted** when `Pattern`/params
  change. Computed-*from* Real, never a live alias. Not undoable, not saved (regenerated on load).
- **Piecer ↔ Solver = swap the active representation.** The Real document is untouched by the switch.

The unweld is a **regen of that Transient**, not a tx (it creates/destroys a Transient, mutates no Real) —
so it lives *outside* the undo/tx world, exactly like `RegenCrease`.

## Why this also fixes the "Solve wipes the Pattern" bug

`OnSolveAsync` currently calls `ApplyReset` ([:593](../PieceSolver/MainWindow.xaml.cs)) — which *is* the
global **Reset** command ([:367](../PieceSolver/MainWindow.xaml.cs)): reload the file, `RebindPattern`
(wipes the Pattern), drop flat/anchor/BFF, `ClearProposedCreases`. It's there only to give the develop a
clean base (pristine input, base resolution). Once **Solve develops a derived copy**, it inherently never
touches the authoring mesh, so re-Solve can't compound subdivisions and there's nothing to "reset" — the
`ApplyReset` call simply goes away, and **Revert goes back to being a standalone global op** (the Reset
button / `Ctrl+R` / journal) that the Solve workflow no longer reaches into.

## How it composes — almost no new bake code

`RunBake` routes on `ComponentCount` → `RunBakeMulti` already **flattens + develops each connected
component with its boundary frozen, then reassembles** ([:1101](../PieceSolver/MainWindow.xaml.cs)). So once
the derived mesh is unwelded into per-piece components, **the existing multi-piece bake develops the painted
pieces unchanged.** `UnweldByRegion` is the only genuinely new code; the develop is reuse. (The FBX-seam
path and the painted-piece path thereby *unify* — both are "develop the components of the derived mesh.")

## The op — `MeshOps.UnweldByRegion`

A pure engine function (Rhino-free), next to `SplitComponents`/`ComponentCount`:

```
MeshOps.UnweldByRegion(PlanktonMesh M, int[] pieceMap, out int[] vertexMap) -> PlanktonMesh M'
```
- **Rebuild, don't surgically tear** (Plankton's Euler ops are fiddly): emit a fresh mesh where vertices are
  duplicated by **(original vertex, piece id)**. Same-piece faces sharing a vertex → one copy (welded inside
  the piece); different-piece faces sharing a vertex → coincident separate copies (unwelded along the
  crease). A vertex where 3 pieces meet → 3 copies. Faces stay 1:1; face count preserved.
- Returns a `vertexMap` (M′ vert → original vert) — like `SplitComponents` — so colours/energy/seam-loops
  map back, and (later) stable piece identity can ride through it.
- Result: one connected component per painted piece; geometry identical (coincident verts at seams).

## Decisions

1. **Solve develops a derived mesh, never the authoring Real mesh.** Single patch → a clone; pieced →
   unwelded-by-`CreaseMap`. (`TopologyClone` already exists for the clone.) This is what lets the Pattern
   survive and removes the `ApplyReset` coupling by construction. **Honest caveat:** today `_session.Mesh` is
   one object playing *authoring + develop-target + displayed* — so "never touch the authoring mesh" requires
   pulling a **minimal slice of the representation split forward** (Decision 6 is otherwise deferred): the bake
   runs on a temporary develop-session over the derived mesh, the authoring `_session` is restored afterward,
   and the developed result is a separate mesh (`_developed`) the view shows. The Piecer↔Solver round-trip
   stays **deliberately messy** in v1 (the brush still targets the now-hidden authoring mesh; no clean phase
   toggle yet) — the viewer needs a refactor regardless. The full clean split is Decision 6.
2. **Revert stays a standalone global op** (rename `ApplyReset → Revert`, keep it the Reset button/`Ctrl+R`/
   journal target). Solve no longer calls it.
3. **`UnweldByRegion` is a pure `MeshOps` op** (vertex,piece duplication + `vertexMap`). Reusable from Solve,
   export, CLI, fab.
4. **The Solver's developed mesh is a Transient** — lazy (regen on entering the Solver phase), rotted on
   `Pattern`/params change. **v1:** held pragmatically by `MainWindow` (the cut copy the bake produces).
   **Later:** a formal `Transient<T>` on the Doc, once the Doc owns the mesh (Decision 6).
5. **Reuse `RunBakeMulti`.** Unweld → multi-component → existing per-component develop. Minimal new bake code.
6. **Doc-owns-mesh + the formal Piecer↔Solver representation swap is DEFERRED** to its own spec. v1 ships the
   user-visible win (paint pieces → Solve develops them) with the mesh still in `MainWindow`; the cut mesh is
   a Transient *in concept* but not yet a `Transient<T>` on the Doc. This is the "Doc is a whole document"
   consolidation and it's big — name it now, don't do it here.
7. **All creases are cuts in v1.** **Joins** (a crease that folds rather than separates) and the general
   *unweld-along-an-edge-set / corner-fan* op are deferred — they need the per-crease `separate`/`join` type,
   which is the crease-identity gateway from `DOC-TX-REFACTOR.md`'s non-goals.

## Non-goals (explicitly out of scope here)

Joins / cut-vs-fold per-crease type + the corner-fan general unweld · the full **Doc-owns-mesh** refactor and
the formalized representation swap · stable `PieceId` GUIDs carried through the `vertexMap` · seam relaxation
(seams stay frozen, as today) · persisting the developed Transient to file (it regenerates).

---

# Part 2 — Implementation Plan

Build-green at every step (`dotnet build PieceSolver -c Release` → 0/0, launch, verify), one commit per step.
The payoff (paint pieces → Solve develops them) lands at the end of Phase C **without** the Doc-owns-mesh
refactor.

### Phase A — Solve develops a derived mesh (decouple from Revert)
- **Rename `ApplyReset → Revert`** (keep it the Reset button / `Ctrl+R` / `CmdKind.Reset` target). Pure rename.
- **Remove the `ApplyReset()` call from `OnSolveAsync`.**
- **Solve operates on a clone, not in place:** at the start of the bake, derive the working mesh from the
  authoring mesh (`TopologyClone` for a single patch; Phase C swaps in the unweld for a pieced mesh), develop
  *that*, display it; the authoring mesh + `Pattern` are untouched. Re-Solve re-derives a fresh clone → no
  compounding, no disk reload.
- **Verify:** paint/propose state survives a Solve; re-Solve doesn't compound subdivisions; the standalone
  Reset still reloads the file. (Single-patch develop result identical to today.)

### Phase B — `MeshOps.UnweldByRegion` (pure op)
- Implement the (vertex, piece) rebuild + `vertexMap` next to `SplitComponents`.
- **Headless sanity** (GradCheck/console): components(M′) == distinct pieces; face count preserved; seam
  vertices coincident; each piece manifold-with-boundary; `vertexMap` round-trips. No app deps.

### Phase C — the handoff (paint pieces → Solve develops them)
- When the mesh is **pieced** (the `Pattern` has >1 region / a non-empty `CreaseMap`), Solve's derived mesh =
  `UnweldByRegion(authoringMesh, PieceMap)`; otherwise the clone from Phase A.
- The derived mesh is multi-component → **`RunBakeMulti` develops it unchanged** (per-piece flatten + frozen
  seams + reassemble).
- **Verify:** paint a few pieces on a single welded patch → Solve → it develops as those pieces (laid out as
  panels), seams frozen, worst-strain gate as today; the authoring mesh + Pattern remain for re-piecing.

### Phase D — docs
- Revise **`AGENTS.md`** (Solve now develops the painted partition via `UnweldByRegion`; the Solver's mesh is
  a Transient; Revert is standalone) and **`docs/HANDOFF.md`**; cross-link from `DOC-TX-REFACTOR.md`; refresh
  memory. Note the `ApplyReset → Revert` rename.

### Deferred (own specs — named, not built here)
- **Doc-owns-mesh** → the Solver's developed mesh becomes a formal `Transient<T>` on the Doc, and Piecer↔Solver
  is a clean representation swap (Decision 6).
- **Joins** → per-crease `separate`/`join` type + the corner-fan unweld (Decision 7), gated on crease-identity.

## Verification & risks

- **Verification:** each step builds 0/0 and launches; Phase A is checked against today's single-patch Solve
  (identical result) + "Pattern survives a Solve"; Phase C against "painted pieces develop as panels." No
  engine/solver math changes, so the bench checksums are unaffected.
- **Re-Solve compounding (Phase A):** the reason `ApplyReset` existed. Developing a fresh derived clone each
  Solve removes it by construction (the authoring mesh never accrues subdivisions). The risk is *missing* a
  spot that still mutates the authoring mesh — audit the bake's mesh writes.
- **Which mesh is displayed:** when Solve develops a copy, the *developed copy* becomes the shown 3D while the
  authoring mesh is hidden — an informal representation swap even in v1. Keep `Reset` (reload) as the way back
  to the authoring view until the formal phase swap (deferred) exists.
- **Unweld correctness (Phase B):** coincident seam verts + per-piece manifoldness; the headless check is the
  gate. The op is pure, so it's cheap to test in isolation before any wiring.
- **Single-component path:** still needs a clone (not in-place) so the authoring mesh survives — don't leave
  `RunBakeSingle` mutating `_session.Mesh` directly.
