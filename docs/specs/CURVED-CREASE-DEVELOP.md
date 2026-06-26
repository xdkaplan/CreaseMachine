# Curved-crease develop — implementation handoff

Design handoff for implementing the **piecewise-developable curved-crease develop** against the real
solver (`PieceSolver` + `IsometricLM`). Worked out in the `investigate/seam-tension` study prototype
(`relax/Relax.cs`); this captures the conclusions so the implementation can be built against the real app.

The seam between panels is a **curved crease — a real fold** (a normal/tangent-plane discontinuity, K=0 on
each side), not a smooth merge and not a frozen seam. The crease **settles** (finds its developable
position) as the panels develop.

## Ship default

**Fully Settled + Fully Unbent**, no kite. The develop always produces the Settled (reconciled, free-crease)
mesh; the miter overlay is the fully-unbent clean line. Retain **Bent** (miter bend) only as an **Advanced**
slider, default = fully unbent. `Original↔Settled` is *not* a user control — you always want Settled.

(The study prototype's kite — `Original↔Settled × Bent↔Unbent` — was an exploration tool; production ships
the chosen defaults, not the kite.)

## 1. The crease is a FOLD that settles

The mesh is already unwelded along creases (`MeshOps.UnweldByRegion`), so the seam can carry a normal jump —
keep that. The display unwelds for rendering the fold; the *solve* is described below.

## 2. Core engine change — ONE coupled solve, not per-piece

A free crease vertex is shared by ≥2 panels (the **fan**), and developability is a **panel-wide** property
(a panel's iso constraint spans its whole boundary at once). So you **cannot** isolate-solve a piece with a
free crease — a central panel couples *all* its creases, and `{A,C}` vs `{B,C}` solves disagree on the
shared panel `C`. The coupling propagates through shared panels, so the whole connected pieced mesh is one
coupled problem. (Only a literal 2-panel sheet — one crease, free ends, no junctions — decomposes.)

**Therefore: solve the whole pieced mesh at once**, crease vertices **shared** (welded *for position*) → the
topology assembles every fan automatically.

### Keeping the fold free — DO NOT mask "crease edges" (the original error in this spec)

`IsometricLM`'s two smoothing blocks are **per-VERTEX over the 1-ring, NOT per-edge** (`IsometricLM.cs:23,
:165`):
- `fairM(v) = M_v − mean(M neighbours)` — uniform Laplacian fairness (and `fairP` for M′),
- `bending(v)` = bi-Laplacian `U²`, `U(v) = mean_nbr(v) − v`,

both pulling `v` toward the mean of `GetVertexNeighbours(v)`. At a **welded** crease vertex the 1-ring spans
**both** panels, so *both* terms average **across the seam → they flatten the fold.** A per-edge bending
mask does nothing, and masking *bending only* still leaves the fairness Laplacian smoothing the crease away.

To keep the fold, do **one** of:

- **Unweld along the crease** (what the prototype does, `BuildSub` → per-panel meshes) — each crease vertex's
  1-ring becomes **one-sided**, so neither term can cross the seam *by construction*. Simplest, guaranteed.
  (You lose the shared-position fan coupling — fine for a single 2-panel sheet; for a real fan use the welded
  option below.)
- **Welded + per-VERTEX exclusion:** keep the crease verts shared (so the per-edge `iso` + the shared
  position still couple the fan) but **zero the residual rows at crease vertices for `fairM`, `fairP`, AND
  `bending`.** The crease vertex stops being smoothed → the kink survives; the fan stays coupled.

**Engine ask:** a **per-vertex "don't-smooth" mask** on `fairM`/`fairP`/`bending` — `IsometricLM.Solve`
already takes a per-vertex `pinned` (Dirichlet); add the same-shape mask for "don't apply the smoothing
residuals here." Also keep `wFair` low/off near the seam (the prototype ran `wFair = 0`, `wBend = 0.6`).

**Do not** keep the current per-piece frozen-boundary path for the free-crease case: `DevelopPiece` →
`BoundaryVertexMask` freezes the *whole* piece boundary, which over-constrains and **cannot free the
crease** (`PieceSolver/MainWindow.xaml.cs`, `RunBakeMulti`/`DevelopPiece`).

**Cone points** (≥3 creases meet, angle defect) can't flatten to one sheet — **lock them** (and/or cut a
slit to the boundary). Locking junction vertices removes the cone singularities; it does *not* decouple the
problem (the panels still couple), but it's worth doing and matches fixing corners you'd fix anyway.

## 3. Subdivision — sub0 crease pinned, new midpoints free

Reuse `SubdivideCompute` (`PieceSolver/MainWindow.xaml.cs`) — it already refines M, M′ (BFF flat), and the
anchor M0 **in lockstep** (so midpoints interpolate identically and the metric is preserved), rescales
`wIso` for the resolution change (`_isoResFactor`), and resets the LM trust region. Keep all of that.

**Change the pin** from "whole boundary frozen" to:

| boundary vertex | frozen? |
|---|---|
| outer boundary (model edge) — original *and* new midpoints | **frozen** |
| crease, original (`v < nV0`) — the control points | **frozen** (at the crease) |
| crease, new midpoints (`crease ∩ v ≥ nV0`) | **free** ← the change |

You need **both** signals: `SeamVerts` / component-boundary (crease vs outer) **and** `v < nV0`
(original vs new — `MeshOps.UniformSubdivide` preserves original vertex indices and appends midpoints, so
this is free; capture `nV0` before the subdivide). The new crease points then relax to a smooth developable
curve **through the fixed sub0 control points** — the polygon converging to the real curved crease, which
is the whole point (don't lock the new points to their linear-midpoint positions).

**Note — PieceMap does not survive subdivide today.** `SubdivideCompute` → `RebindPattern` makes a fresh
`Pattern` (PieceMap null → re-seeded by flood-fill) and `ApplySubdivide` → `ClearProposedCreases` (labels
invalid). The bake survives via connected-component split (an unwelded pieced mesh = one component per
piece), but if you need the labels/crease identity to persist, **propagate child←parent**:
`PieceMap_new[4·f .. 4·f+3] = PieceMap_old[f]` over used triangles, in face order (deterministic in
`UniformSubdivide`; you'd expose/reconstruct the parent map).

## 4. The miter (Bent/Unbent) is GRAPHICAL only

The panels keep their own develop — the miter is an **overlay**, never a develop target. It's the
**bending-minimal elastica of the crease inside a tolerance tube**, computed **geodesic-ish**: project the
smoothing *tangential* to the surface normal so the curve stays on the surface (don't 3D-chord-straighten —
that lifts it off and over-folds the panels). The **Bent** Advanced slider is the unbend amount (tube
radius); default = fully unbent.

**Gotcha:** use **gradient descent on bending** (move against the bilaplacian / 4th-difference,
`step < 2/16` for stability), **not** a Jacobi step toward zero 4th-difference — that *amplifies* the
high-frequency zigzag instead of removing it. Reference implementation: `BentWireV` in `relax/Relax.cs`.

(The tolerance tube here is the *cosmetic smoothing* budget of the miter, distinct from the **panel gap** =
the A↔B *fabrication* tolerance. Don't conflate them.)

## 5. Architecture — Relax is its own pass

The developed mesh is a **Supplied Transient**: a piece edit **rots** it (marks stale → falls back to the
authoring mesh) but does **not** auto-recompute. The develop is globally coupled, so a Grown
(recompute-on-read) transient would re-solve the whole mesh on every brush stroke — not viable. Relax is
**on-demand**, which is exactly the existing Solve-as-bake shape. Keep piece editing (Real `Pattern`/
`PieceMap`) local, cheap, undoable; develop separately.

## 6. Interactive perf (phase 2, optional)

Warm **incremental** regen instead of a cold whole-mesh re-solve:
- Force-propagate the edit's delta from the **prior developed positions** (carry the Nesterov velocity —
  the flow is already stateful), rippling outward.
- **Stop** when the develop residual on the artificial frozen ring drops below tolerance (freeing those
  vertices would move them < ε → past the influence radius).
- Cost ∝ **edit magnitude** (smaller perturbation → shorter ripple *and* fewer iterations) → continuous
  drag ≈ real-time; a big discrete jump pays one bounded ripple.
- Ripple is **anisotropic** (runs far along rulings, decays fast across) and **jumps creases** (into
  neighbour panels via the coupling) — both still bounded.
- The warm regen reads its own prior value, so it's **path-dependent**, and the develop is non-convex →
  warm regens **drift**. Keep a **cold full-Relax** (drops the warm state) as the canonical re-anchor.

This turns the develop Transient's rot from all-or-nothing into a **distance-bounded ripple** — a Supplied
transient with local incremental refresh, exact-on-demand via the cold Relax.

## Adjacent / future

- **Panel gap = the fabrication tolerance** (A↔B; e.g. 0.1 mm). Each panel may spend it toward its own
  developable-want; the seam opens up to it (the gap is clamped, you spend smoothness/developability).
- **Trimback / constructability gap** (future, not specified here): a clean both-sides offset of the unbent
  miter at a *wider* construction distance (e.g. 4 mm), trimming the jig-jaggedy mesh edges back to that
  clean offset — distinct from the assembly tolerance.

## References

- Study prototype: `relax/Relax.cs` (worktree `investigate/seam-tension`) — `CornerAt`, `DevSubAtCrease`,
  `BentWireV`, the unweld/U build.
- Engine subdivide: `src/MeshOps.cs` `UniformSubdivide`.
- Develop kernel: `PieceSolver/IsometricLM.cs` (the `pinned` Dirichlet; the *per-vertex* don't-smooth mask
  on `fairM`/`fairP`/`bending` is the ask — see §2; `fairM`/`bending` are per-vertex 1-ring at `:23`/`:165`).
- Solve / subdivide flow: `PieceSolver/MainWindow.xaml.cs` — `RunBakeMulti`, `SubdivideCompute`,
  `DevelopPiece`, `RebindPattern`.
