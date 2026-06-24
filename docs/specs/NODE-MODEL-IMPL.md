# Node-Model Implementation Plan — building Real / Transient

**Status:** implementation plan (design = [DOC-SPEC.md](DOC-SPEC.md)). *Nothing built yet.* This is the
plan to be **audited** before the build. The conceptual model is settled; this is the *how* + the
*increment order*. Filename provisional — rename if you prefer.

## 0. The target (from DOC-SPEC)

> **`Real`** — authored node; **ownership tree**; never stale; undo via its **Store** (`ITxAble` + delta).
> **`Transient`** — derived node; **dependency DAG**; fresh/stale; `Grow` | `Supply`.
> **Consumers project a Real:** the View pulls a Real's geometry Transient and draws it (or no-ops);
> a panel pulls its values. No "Element" — a Real with no geometry just renders nothing.

The build realizes this **incrementally**, smallest-safest first, never cracking the **frozen** Doc/tx
layer without an explicit checkpoint.

## 1. Concrete shapes (proposed — the audit should challenge these)

```csharp
// The authored node. Lives in the ownership tree; never stale.
abstract class Real
{
    public Real Parent { get; private set; }                 // single owner ⇒ tree
    public IReadOnlyList<Real> Children => _children;          // composition, not derivation
    public abstract string Name { get; }                      // facet: Name (always)

    // Optional viewable geometry — a Transient the View pulls. null ⇒ nothing to draw.
    public virtual Transient<RenderData> Geometry => null;

    // (later) Selectable: identity key for Selection<T>; Properties: get/set for the UX.
}

// What the View knows how to draw. The geometry KIND varies; the View type-dispatches.
sealed class RenderData { public RenderKind Kind; public float[] Positions, Normals, Colors, …; }
enum RenderKind { Mesh, Lines, Points }
```

- **Store relationship (unchanged, frozen-respecting):** a Real's *authored data* stays in its Store
  (`Pattern` for Pieces — `PieceMap`). A `Piece : Real` is a **handle/projection** over `(PieceId, Pattern)`,
  not a replacement for `PieceMap`. Its `Geometry` is a **Grown** `Transient<RenderData>` that derives the
  tint buffer from `PieceMap` (today's `RebuildPieces` body, relocated). **No change to `Pattern`/`PieceDelta`.**
- **View consumes Reals:** the render loop iterates the scene's Reals: `if (r.Geometry?.Peek(out var g))
  DrawByKind(g)`. Uniform; no-ops on null/stale. Replaces the ad-hoc per-buffer dispatch.

## 2. Increment order (each builds green, app-runs, user-smoke-tested, committed)

- **I1 — the spine, proven on the developed sheet (lowest risk; no frozen layer).**
  Introduce `Real` + `RenderData`/`RenderKind`. Make the **developed mesh** a `Real` (`DevelopedSheet`)
  whose `Geometry` is the existing `_developed` **Supplied** `Transient`. Route the View to draw it *through*
  the Real. Proves "Real owns geometry Transient; View consumes Real" end-to-end on a node that's already a
  Transient — `Pattern`/`PieceMap` untouched.
- **I2 — `Piece : Real` (the substantive migration).** Materialize a `Piece` Real per distinct `PieceMap`
  label (identity = `PieceId`, `Name` = `PieceId.Name`), `Geometry` = a Grown `Transient` deriving its tint
  from `PieceMap`. The View renders the Piece-Reals instead of the monolithic piece buffer. `Selection<PieceId>`
  now selects Piece-Reals. Backed by `PieceMap` — no `PieceDelta`/undo changes.
- **I3 — the dep-graph wiring.** Real-mutation (via the Store tx) rots the affected Reals' geometry
  Transients (the `rotChildren` cascade), replacing the shell `RefreshPieces`/`RefreshCreaseOverlay` hooks.
- **I4+ — frozen-layer / later:** Crease-as-Real (the identity gateway → `regen`→`reconcile`), Spline +
  its Store, the property/Settings panel as a second consumer. Each its own checkpoint.

## 3. What this does NOT touch (the freeze)

- `Doc` / `Tx` / `IDelta` / `Op` / undo-redo / the op-log — untouched through I3.
- `Pattern` stays the Store; `PieceMap`/`PieceDelta` unchanged (Piece-Reals are a projection).
- Crease identity (`regen`→`reconcile`) is **I4**, explicitly gated on a separate checkpoint.

## 4. Verification per increment

- Build 0/0 (PieceSolver net8; `.gha` + bench if `src/` touched — it shouldn't be).
- App launches clean; user smoke-test (the relevant view renders; piecing/brush/camera still work).
- No behavior change beyond the intended (display routes through Reals).

## 5. Open questions for the audit

1. Is **I1 (developed-sheet-first)** the right smallest proof, or should I1 be the `Real` base + the View
   loop with *Piece* as the first adopter (bigger, but the "real" node)?
2. `RenderData` as a tagged union vs a small `IRenderable` per kind — which fits the existing `MeshView`
   draw paths (`Upload`/`SetPieces`/`SetCreases`) with least churn?
3. Does a `Piece : Real` **projection** over `PieceMap` (not owning data) actually stay coherent across
   `Seed`/relabel, or does it force premature per-Piece identity (the I4 gateway) sooner than planned?
4. Where does the scene's Real-tree root live — on the `Doc` (alongside the Stores) or the `View`?
5. Any hidden coupling that makes I1 secretly touch the frozen layer?
