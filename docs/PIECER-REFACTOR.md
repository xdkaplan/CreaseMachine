# Piecer / Pattern refactor

Extract the Piecing interaction and its data model out of the `MainWindow` god-file
(2023 lines, ~8 responsibilities) into cohesive units, and stand up an **Editor**
abstraction — without changing any behavior. This is the first concrete step toward
proper CAD editability (today we select **Pieces**; later, **Creases**).

This doc has two parts: the **Design Note** (durable — the model and the decisions and
*why*) and the **Implementation Plan** (this pass — the ordered, behavior-preserving steps).

---

# Part 1 — Design Note

## The layered model

```
PlanktonMesh   geometry + topology. The SSOT for shape. Pure half-edge: no per-element
               attribute storage, no weld "state" — but it DOES have the Euler primitives
               (SplitVertex / MergeVertices, SplitFace / MergeFaces, SplitEdge).
   │
Pattern        a THIN companion over one PlanktonMesh: the partition + the ops. Holds
               PieceMap + (derived) CreaseMap. NOT a mesh — it stores no geometry.
   │
Editor / Piecer  interaction. The Piecer is the Editor active during Piecing
                 (after Propose -> Accept). It edits the Pattern via ops.
   │
PieceMesh      (FUTURE) the committed, UNWELDED PlanktonMesh whose connected components
               ARE the pieces — produced at the Solve handoff by UnweldByRegion. A *state*
               of PlanktonMesh, not a new type.
```

Why `Pattern` must exist as a companion (not data on the mesh): reflection confirms Plankton
has **no per-face attribute storage and no weld-state flag**, so the piece labels have
nowhere to live *on* the mesh. They live in `Pattern`, index-coupled to that mesh.

## Glossary (approved terms)

- **tx** — transaction: one atomic, recorded, reversible edit. **One gesture (mouse-down ->
  up) = one tx.** The unit of the undo stack. *(future)*
- **op** — an operation that mutates the *authoritative* partition (Paint, Remove, Split,
  Seed). The recordable verbs (cf. Blender "operator"). **Only ops produce tx deltas.**
- **regen** — a derivation: recompute a derived/cached view from authoritative state.
  Idempotent, **never recorded** (re-runs on undo). e.g. `RegenCrease`.
- **query** — read-only interrogation (NewRegionId, FullyMarked, FacesUnderBrush). No
  mutation, never in tx.
- **Chapter** — a reset boundary that starts a fresh piecing epoch (Seed / Propose /
  Crease-angle change). The undo stack **checkpoints** here; no undo across a Chapter.
- **undo stack** — ordered committed tx, each reversed via its sparse delta. *(future)*
- **GUID** — stable entity identity, surviving edits/renumbering. **Not built yet** — the
  future element layer. Distinct from today's ephemeral int piece id.
- **Piece** — a panel/region: a connected set of faces sharing a piece id. Today an
  *ephemeral int* id; future first-class entity (GUID + attributes).
- **PieceId** — `readonly struct` over the int region id; a zero-cost typed handle. The
  *id*, distinct from the future *Piece* entity. (`Id`, not `ID` — .NET treats "Id" as a
  word, cf. `Process.Id`.)
- **Crease** — a piece boundary: the edge(s) between two different pieces. Today *derived*
  (no identity); future first-class entity.
- **PieceMap** — the authoritative per-face partition: `int[] faceRegion`, where
  `faceRegion[i]` = the piece id of face `i`. A *field of* `Pattern`, not its own class.
- **CreaseMap** — the derived crease set: `HashSet<long>` of packed edge keys. A
  materialized view of `PieceMap`. A *field of* `Pattern`.
- **Pattern** — the thin companion over a `PlanktonMesh` holding `PieceMap` + `CreaseMap`
  + the ops. (Papercraft/sewing sense: pieces + cut/fold lines.)
- **Piecer** — the **Editor** for the Piecing phase. Invokes ops, runs regens, reads via
  queries.
- **Editor** — abstract base for editors (lifecycle + pointer hooks + per-face tint).
- **IEditorHost** — narrow interface the host (MainWindow) implements, exposing only what an
  editor needs. The wall that keeps the god-file from regrowing. (`I` = C# interface.)

## Op roles (this is what scopes the future tx layer)

Three buckets, behaving differently under tx:

| Role | Members | Surfaces (now) | Future manip (tx) |
|---|---|---|---|
| **mutating op** | Paint, Remove, Split | `bool changed` / counts | sparse `{face -> old,new}` + entity births/deaths |
| **mutating op (reset)** | Seed | whole `PieceMap` | whole partition — an **epoch reset** (a Chapter), not a delta |
| **regen** | RegenCrease | new `CreaseMap` | — never recorded; re-runs on undo |
| **query** | NewRegionId, FullyMarked, FacesUnderBrush | a value | — never in tx |

Only the **mutating ops** are ever recorded. `Mark` (accumulating the remove gesture's
touched faces) is **not** a Pattern op — it mutates the Piecer's transient; its *consequence*
(`Remove`) is the recorded op.

## Decisions & rationale

- **Pattern-primary, CreaseMap derived (today).** `RegenCrease` recomputes the creases from
  the partition. The authority *may flip* to crease-primary when direct crease editing lands
  (then regions derive from creases via flood-fill). The seam to leave: keep `CreaseMap`
  clearly tagged *derived*.
- **`RegenCrease` is lossy for now, deliberately.** It rebuilds the whole set, discarding any
  per-crease identity. Fine until creases carry identity/attributes — at which point it
  becomes a *reconciling* regen (or is replaced by the authority flip). We do **not** change
  this logic during the refactor (one change at a time).
- **Pattern updates every stroke** — live during the drag (for the patch diagram), with the
  per-stroke tx as the future undo unit. **Select / deselect are not tx** and don't touch the
  Pattern; they only change the Piecer's active selection.
- **One gesture = one tx; sparse deltas via copy-on-first-write.** The undo record stores
  authoritative deltas only (`{face -> old,new}` + entity births/deaths), never the derived
  `CreaseMap` (re-derive on undo). Redo must reinstate the *same* ids. Records are
  topology-epoch-scoped (a Chapter checkpoints them). *(future)*
- **PieceId handle.** A `readonly struct` over the int — ints stay in the dense `PieceMap`
  array (hot path); the typed handle appears only at the API/selection boundary. The
  "selection is a Piece" guarantee is free via the type. The polymorphic `Selection`
  container (Piece-or-Crease, multi-select) is deferred until there's a second selectable
  entity — today it's an honest single-select `PieceId?`.
- **IEditorHost is the wall.** The Piecer depends on the narrow host contract, not the 2000-
  line class, so it can't drag the god-file back in (and could run in a test harness).
- **Identity today is ephemeral.** A piece is an `int` allocated `max+1`; re-seed renumbers.
  Not stable -> can't back undo or carry attributes -> which is exactly why the GUID/entity
  layer is its own later step, not bolted onto the ints.
- **Weld/unweld is an operation, not a state.** Plankton has `SplitVertex`/`MergeVertices`;
  `UnweldByRegion` (Solve handoff) and weld-FBX-on-import are built on those, later.
- **Refactor and behavior change are separate passes.** This pass is a pure relocation,
  verified by build + a run that behaves bit-identically.

## Deferred roadmap (later passes, rough order)

1. **tx + undo/redo stack** — per-stroke, sparse delta, Chapter checkpoints.
2. **Element/entity DB** — `Piece`/`Crease` entities with stable ids + attributes; the
   `localInt <-> id` map reconciled per tx; `RegenCrease` becomes reconciling.
3. **`Picker.cs`** — lift picking out (incl. edge-picking for crease selection).
4. **Crease editor + polymorphic `Selection` + select-modes** (Piece / Crease / Junction).
5. **`UnweldByRegion` / PieceMesh / weld-FBX-on-import** (the Solve-handoff representation).
6. **Perf** — dirty-set incremental updates, spatial index (brush + picking), partial GL
   uploads, buffer reuse. The "return your manips" hook unifies the undo and perf needs.

None of these are touched in the refactor pass.

---

# Part 2 — Implementation Plan (this pass)

Branch: `viz/creative-2`. **Behavior-preserving relocation only.** Build green after each
step; do not change any logic.

### Step 1 — `PieceId`
Add `readonly struct PieceId { int Value }` (own small file, or top of `Pattern.cs`). Zero-
cost; `PieceMap` stays `int[]`.

### Step 2 — `Pattern.cs`
Move out of `MainWindow`, retargeting `_faceRegion` -> `PieceMap`, `_creaseEdges` ->
`CreaseMap`:
- **Data:** `int[] PieceMap`, `HashSet<long> CreaseMap`, a `PlanktonMesh` reference (ctor).
- **Ops:** `Seed`, `Paint(center, radius, PieceId)`, `Remove(touched, PieceId active)`,
  `SplitDisconnected()`.
- **Regen:** `RegenCrease()` (renamed from `DeriveCreaseEdges`, logic unchanged — lossy).
- **Queries:** `NewRegionId() -> PieceId`, `FullyMarked(touched, PieceId active)`,
  `FacesUnderBrush(center, radius)` (factor the shared centroid loop out of Paint + Mark).
- **Helpers:** `EdgeKey`, `DictGet` move with it.
- In `MainWindow`, replace the bodies with calls to `_pattern.X(...)`. **Build green here** —
  this alone proves the data/ops moved cleanly with no behavior change.

### Step 3 — `Editor.cs` + `IEditorHost`
- `abstract class Editor`: `Name`; `Activate()/Deactivate()`; `OnPointerDown(pt, mods)`,
  `OnPointerMove(pt)`, `OnPointerUp(pt)`, `OnHover(pt)`; `FaceFill(face, region) -> Vector3?`.
- `interface IEditorHost`: `Mesh`, `Pattern`, `PickFace`, `PickSurface`, `BrushWorldRadius`,
  `ScreenRadiusPx`, `RefreshPieces()`, `ShowPreview(pt)/HidePreview()`, `Invalidate()`.

### Step 4 — `Piecer.cs : Editor`
Move the brush gesture code out of `MainWindow`'s mouse handlers:
- **State:** `PieceId? _selection`, `_removing`, `_touched`, `_stroking`, `_strokeStart`.
- **Hooks:** the OnMouseDown/Move/Up brush branches (select-then-paint, Shift = new region,
  Ctrl = remove, click-vs-drag threshold), `BrushStrokeTo`, `MarkFacesUnderBrush`, hover
  preview.
- **FaceFill:** remove-red (marked, via `host.Pattern.FullyMarked`) / active-blue
  (`region == _selection?.Value`) / null.
- Calls `host.Pattern` ops + `host` for picking / refresh / preview / invalidate.

### Step 5 — wire `MainWindow` as host
- Implement `IEditorHost`.
- Hold `Pattern _pattern` (recreated/reseeded on mesh load/subdivide/reset) and
  `Editor _activeEditor`.
- Mouse handlers: route left-button + hover to `_activeEditor`; **right-drag camera stays in
  MainWindow**.
- `_activeEditor = _piecer` after Propose -> Accept; cleared otherwise (`BrushAvailable` ≡
  "Piecer active").
- `RebuildPieces` stays here (view): reads `_pattern`; colors by — `if (_camModal)` modal
  rainbow/neutral, `else _activeEditor?.FaceFill(face, region) ?? Vector3.One`.
- Review flow (`OnProposeAsync` finally, `OpenCreaseReview`, Crease-angle handler) calls
  `_pattern.Seed`/`RegenCrease` directly (pre-Accept; Piecer inactive).
- Picking stays in `MainWindow` (Picker.cs deferred), exposed via `IEditorHost`.

### Verification
Build 0 warnings / 0 errors. Launch. Exercise:
- Propose -> camera-modal review (rainbow; drag Crease-angle = neutral grooves; Accept).
- Paint a piece (select; drag to grow), Shift+click (new region / bullseye), Ctrl+drag remove
  (light/dark red preview; dominant-neighbour heal; active selection protected).
- Re-Propose. Confirm **identical** behavior throughout. Commit.

### Do NOT touch (deferred)
tx / undo / dirty-set returns / GUID-entity table / reconciling RegenCrease / `Picker.cs` /
`UnweldByRegion` / PieceMesh / weld-on-import / polymorphic `Selection` / authority flip.
