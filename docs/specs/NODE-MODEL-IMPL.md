# Node-Model Implementation Plan — building Real / Transient

**Status:** **I1 + pre-I2 untangle + I2a + I3 all BUILT & committed (2026-06-24/25)** — see the increment
list for commit hashes; I4+ is still plan. Design = [DOC-SPEC.md](DOC-SPEC.md). **Revised 2026-06-24 after a
read-only audit** (the audit caught three show-stoppers in the first draft — see the change log at the
end). Filename provisional.

## 0. Two hard constraints the audit surfaced (read first)

1. **The viewport is a one-of-three switch, NOT a draw-list.** `View.Display` is
   `enum DisplaySource { Authoring, Pieces, Developed }` — exactly one *base* source draws per frame, by
   design (it's what made the piece-over-developed occlusion *unrepresentable*). So **we do NOT introduce a
   "draw every Real with geometry" loop** — that would reintroduce the occlusion. The model is: the **active
   base source** (one Real) + **always-on overlay Reals** (grid, crease lines, seam wires) compose the frame.
2. **Rendering is PUSH, not PULL.** GL uploads must run inside `OnRender` (GL-thread), gated by dirty flags
   (`_meshDirty`/`_pieceDirty`/…). So a Real's geometry is a two-step thing: `Real.Geometry` **yields a
   `RenderData` on the UI thread**; the actual `MeshView.Set*`/`Upload` GL call **stays staged in `OnRender`**.
   A Real never calls GL directly.

## 1. Concrete shapes (post-audit)

```csharp
abstract class Real
{
    public Real Parent { get; }                  // single owner ⇒ ownership TREE (composition, not derivation)
    public IReadOnlyList<Real> Children { get; }
    public abstract string Name { get; }         // facet: Name (always)
    public virtual Transient<RenderData> Geometry => null;   // null ⇒ nothing to draw (uniform; View no-ops)
}

// A typed DISPATCH TOKEN that names which existing MeshView path to call — NOT a re-abstraction of vertex
// data (MeshView keeps owning GL + its 3 shader programs). Mirrors MeshView's 3 upload entry points:
enum RenderKind { Mesh, Pieces, Lines }
sealed class RenderData {
    public RenderKind Kind;
    public PlanktonMesh Mesh; public double[] PosOverride;                 // Mesh  -> MeshView.Upload
    public float[] Pos, Nrm, Col, Dist, Edge;                              // Pieces -> MeshView.SetPieces (5 arrays; dist+edge REQUIRED by PIECE_FRAG)
    public float[] Segments; public Vector3 LineColor;                     // Lines -> MeshView.SetCreases/SetSeams
}
```
*(No `RenderKind.Points` — no consumer exists. `Pieces` is its own case, distinct from `Mesh`: 5 parallel
arrays incl. `Dist`/`Edge`, own shader.)*

- **Store relationship (unchanged, frozen-safe):** a Real's authored data stays in its Store; a `Piece : Real`
  is a handle/projection over `(PieceId, Pattern)`, **not** a replacement for `PieceMap`. No `Pattern`/
  `PieceDelta` change.
- **Scene root lives on the `Doc`** (`Doc.Scene`), so it outlives any single View (panels are peer consumers).
  Additive field — *not* routed through the tx primitives, so within the freeze.

## 2. Increment order (each: build 0/0, app-runs, user-smoke-test, commit)

- **I1 ✓ DONE (`45909a1`) — the spine, on an always-on overlay.** Introduced `Real` + `RenderData`/
  `RenderKind`; made the **crease overlay** a `Real` whose `Geometry` yields a `Lines` `RenderData`;
  `OnRender` `Peek`s it and stages `SetCreases`. Proves "Real → RenderData → staged GL upload" on a node
  that is always drawn, never occludes — sidesteps `DisplaySource` entirely.
- **pre-I2 untangle ✓ DONE (`33886d4`)** — pure `DerivePieceBuffers` extracted; see §3.
- **I2a ✓ DONE (`458cfc3`) — the partition `Pattern` IS the Pieces Real.** (User's call: no separate
  `PieceView` / per-`Piece` class — *"don't shatter the abstraction until we need it"*.) `Pattern : Real`
  gained a **Supplied** `Geometry` Transient (beside its **Grown** `CreaseMap`); `RebuildPieces` runs the
  pure `DerivePieceBuffers` and Supplies it; `OnRender` pulls it for the `Pieces` base source. ONE Real,
  ONE buffer — per-piece identity stays the I4 gateway.
- **I3 ✓ DONE (`375f503`) — the rot cascade.** (Frozen-layer; built with user sign-off.) `Node` base holds
  **Downstream** edges + `RotDownstream()` (the flood); `Transient.Rot`/`Supply` cascade; `Real : Node`;
  `Pattern` wires `CreaseMap` + `Geometry` as downstreams and `RegenCrease() → RotDownstream()`, so every
  PieceMap change rots both. `Apply`/`Invert` bodies byte-identical (only `RegenCrease`'s reach widened) —
  undo math untouched. No frame-glitch artifacts in testing.
- **I4+ — frozen / later:** Crease-with-identity (`regen`→`reconcile`), per-`Piece` identity + selection,
  Spline + its Store, the property panel as a second consumer, stable `PieceId` GUIDs.

## 3. Pre-I2 untangle (REQUIRED — `RebuildPieces` is not pure)

`RebuildPieces` (MainWindow.xaml.cs ~966–1075) **cannot** be lifted into a Grown recipe as-is: it
(a) **calls `_doc.Pattern.Seed()`** (~:975) — *mutates Real state*; a `.Value` read must never re-partition
the mesh; (b) reads `_renderer.Radius`, `_activeEditor.FaceFill`, `_camModal`, `_angleDragging` — editor/
render/modal coupling. **Before I2:** split it into a *pure* `PieceMap → Pieces RenderData` derivation
(the Grow recipe) and leave the `Seed()` + editor-fill as caller-side concerns. This is its own small,
freeze-safe step.

## 4. Scene inventory (what actually renders today)

Base sources (one at a time via `DisplaySource`): **authoring mesh**, **piece view**, **developed mesh**.
Always-on overlays: **ground grid**, **crease lines**, **seam wires + control polygon**. Plus a **second
render path**: the **BFF flat map M′** (`_flatRenderer` — its *own* `MeshView` instance, drawn beside the
model). The single-View model must decide whether M′ is a Real (a Piece's developed-flat geometry) or a
separate consumer; **deferred**, flagged so it isn't forgotten.

## 5. Freeze check (corrected)

| Increment | Frozen layer? |
|---|---|
| Pre-I2 untangle (`RebuildPieces` → pure) ✓ | No (relocated a derivation; removed a read-side mutation) |
| I1 (Real + RenderData; crease adopter) ✓ | **No** |
| I2a (`Pattern : Real`; Supplied `Geometry`) ✓ | **No** (additive to `Pattern`; no `Apply`/`Invert`/delta change) |
| I3 (rot cascade) ✓ | **Touched frozen `Pattern` — done with sign-off**; `RegenCrease → RotDownstream`, `Apply`/`Invert` bodies byte-identical |
| I4+ (Crease-identity, Spline Store, …) | **YES — sign-off** |

## 6. Verification per increment

Build 0/0 (PieceSolver; `.gha`+bench only if `src/` touched — it shouldn't be). App launches; user
smoke-test (the relevant view renders; piecing/brush/camera unaffected). No behavior change beyond display
routing through Reals — in particular **the occlusion stays impossible** (one base source at a time).

## Change log

- **2026-06-24 (post-audit):** I1 adopter changed developed-sheet → grid/crease overlay (the sheet is a
  degenerate write-once Transient). Added §0 (DisplaySource is one-of-three; render is PUSH). I3 relabeled
  frozen-layer/sign-off (rot cascade hits `Pattern.Apply/Invert`). I2 pinned to I2a (re-materialize; no
  identity) with I2b → I4. `RenderData` reshaped to a 3-case dispatch token mirroring `MeshView` (incl.
  `Dist`/`Edge`; dropped `Points`). Added §3 (`RebuildPieces` `Seed()` side-effect must be removed first)
  and §4 (scene inventory incl. the M′ second render path).
