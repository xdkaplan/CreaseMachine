# CreaseMachine — Agent Handoff

Context for an agent picking up this repo cold. The README covers *what the
component is and how to build it*; this doc covers *the current state, the
extensions beyond the paper, the runtime architecture, and the hard-won lessons
that aren't obvious from the code.*

> **Why this file exists:** much of the project's load-bearing reasoning lives
> in commit history + the math/perf decisions taken across multiple sessions
> and does **not** travel automatically. Everything you need before changing
> the energy, gradient, flow, threading, or degeneracy handling is below.

---

## 1. What this is

**CreaseMachine** — a Grasshopper (Rhino 8) component that flows a triangle mesh
toward a piecewise-developable ("creasable") sheet, implementing the covariance
developability energy of **Stein, Grinspun & Crane, "Developability of Triangle
Meshes" (ACM TOG 37(4), 2018)** on the Plankton half-edge library. It evaluates
the energy + its **analytic** gradient, flows down the gradient
(Nesterov-accelerated), and on demand applies one 1→4 subdivision. No
remeshing, no projection, no smoothing. Motivating use case: turn a smooth
geodesic sphere ("baseball") into clean creased developable panels.

**Status.** The core works. The flow is correct, scale-invariant, FD-verified
(BUG=0 across all energy formulations except the known kink-class on max-cov),
and runs multicore-parallel on a background thread. The paper-faithful
extensions (B.2 / B.4 / B.5.1) are implemented and individually FD-checked. A
practical L1 dihedral sparsity term (`deCraze`) is added on top — not from the
paper, but the most reliable lever we have for sub-panel consolidation. A
continuous symmetry blend (`DetMix`) fixes the eigenvector-arbitrariness twist
on degenerate meshes (icosahedra, symmetric quads). An interactive drag-paint
**brush** UX is wired (still prototype) that paints a local `deCraze` boost.

Sub-panel crazing is still the open *aesthetic* question (§5), but it now has
multiple practical levers including a per-vertex weight map. License is
**GPL-v2** (see `LICENSE`, `NOTICE.md`).

## 2. Orientation

| File | Role | Rhino types? |
|------|------|--------------|
| `src/CreaseMachine.cs` | The GH component: I/O, the flow loop, subdivision, the background worker, the brush MouseCallback. | yes |
| `src/DevelopabilityEnergy.cs` | Energy + analytic gradient + per-vertex `VertexEnergy`. The math core. Includes a per-call `CHAStats` profiler. | **no** |
| `src/MeshOps.cs` | Manifold-safe cleanup: `CollapseShortEdges`, `CollapseSliverEdges`, `CollapseFolds`. | **no** |
| `src/Vec3.cs` | Minimal double-precision vector (mirrors Rhino's `Vector3d` ops). | **no** |
| `test/Program.cs` | The bench: finite-difference gradient checker + flow/scale/degeneracy sanity + `PerfBench` microbench. | **no** |
| `lib/Plankton*.dll` | Upstream Plankton 0.4.3 (vendored, unmodified). | — |
| `PAPER_FORMULAS.md` | Transcription of the App-B formulas used (B.1 Eq 7/8/9, B.2, B.3 Eq 10, B.4, B.5.1). | — |

The three core engine files are deliberately **Rhino-free** so the bench
compiles them directly without Rhino. **Keep it that way** — Rhino types belong
only in `CreaseMachine.cs`. This is also the path to a future standalone /
headless / web-service deployment.

## 3. Build / deploy / test loop

```sh
# Build the plug-in  ->  src/bin/Release/net48/CreaseMachine.gha
dotnet build src/CreaseMachine.csproj -c Release

# Deploy: copy the .gha into %APPDATA%\Grasshopper\Libraries
#   *** CLOSE RHINO FIRST *** — Rhino locks the loaded .gha and the copy fails.

# Bench (run after ANY energy/gradient/flow/degeneracy change):
dotnet build test/GradCheck.csproj -c Release
test/bin/Release/net48/GradCheck.exe        # net48 EXE — run directly, NOT via `dotnet`
```

Reading the bench: the line you care about is the **`BUG(gradient error)=0`**
classification per energy variant. The `maxRel ≈ 45% -> FAIL` lines are
**known artifacts** of finite-difference-checking a non-smooth (eigenvalue-kink)
energy, *not* real errors. Flow tests should show energy descending monotonically
(e.g. 2.36 → 0.04). Max-cov shows `BUG=1` at the same kink vertex every run —
also known. Some diagnostics look for hardcoded `C:\Temp\*.stl` files; if absent
those lines self-skip — expected.

`PerfBench` reports ms/call broken down by block (face precompute, vertex
normals, per-vertex loop, L1) on a representative bunny input. Use it to spot
regressions after performance-touching changes.

## 4. How it works (enough to be dangerous)

- **Energy (default).** Per vertex, build the normal covariance
  `M = Σ_f θ_f Nfw_f Nfw_fᵀ` over incident faces (`Nfw_f = muvf · phi`,
  `phi` = angle between vertex normal `Nv` and face normal `N_f`, `theta` = the
  corner angle). The default developability energy is the **smaller tangent
  eigenvalue** of `M`. It is 0 iff the 1-ring face normals lie on a great
  circle (a hinge / developable vertex).
- **The eigensolve.** `Nv` is the *exact* null vector of `M`. So the smaller
  eigenvalue (and its eigenvector) live in the **2×2 tangent block** of `M` in
  the plane ⊥ `Nv` — solved in closed form (`TangentEigenpairs`). Do **not** go
  back to a general 3×3 eigensolver (see §7, lesson 2).
- **DetMix blend.** With `DetMix ∈ [0, 1]`, the energy is
  `(1 − a) · λ_min + a · λ_min · λ_max`. At `a = 0` this is the paper's
  `λ_min`; at `a = 1` it's `det(M_tangent) = λ_min · λ_max`. The gradient is
  the standard chain rule `dE/dp = (dE/dλ_min) · grad_λ_min +
  (dE/dλ_max) · grad_λ_max`; each `grad_λ_i` uses the existing per-face chain
  rule with the corresponding eigenvector. **Why this exists:** the paper's
  `λ_min` is genuinely non-smooth wherever the two tangent eigenvalues meet
  — at degenerate vertices the picked eigenvector is direction-arbitrary and
  spatially asymmetric meshes (icosahedra, symmetric quads) twist visibly under
  it. Mixing in any `λ_min · λ_max` contribution forces both eigenvectors to
  contribute, restoring basis-invariance and killing the twist.
- **Gradient (analytic, re-derived from the paper).** `factorf` (face-normal
  derivative, carries `phi/sinPhi`) + `factorv` (vertex-normal derivative,
  carries `phi/tanPhi`, divided by `|rawNormal|`), all reducing to `Vec3`
  dot/cross. No matrix class. Heavily precomputed: `dNdp[3f+i]` (Eq 8), `dTheta[3f+i]`,
  `faceEdge[3f+i]`, `faceSliver[f]`, `fvFlat[3f+i]` — all built once per CHA
  call so the per-vertex loops become dot products into pre-built tables.
- **Optimizer (our choice, not the paper's).** Nesterov-accelerated gradient
  descent: sample grad at the lookahead `x + β·v`, then `v = β·v − t·grad`,
  `x += v`. **Raw** gradient (no normalization), `t = Step·L²`, velocity
  capped at one edge length. β ≈ 0.9 ≈ 5× faster than plain descent.
  Boundaries held.
- **Kink-outlier filter.** At eigenvalue crossings the picked subgradient
  representative can be enormous and direction-arbitrary. After the analytic
  gradient is computed, any vertex whose `|grad|` exceeds 8× the per-vertex
  median is zeroed. This used to live only in the numerical-grad FD harness;
  it is now in the live analytic path too, otherwise `Step > ~0.01` jitters
  visibly at kink vertices.

## 4.5 Runtime architecture

- **Threading model.** When `Running = true`, a background worker thread runs
  `DoFlowStep()` in a tight loop. `SolveInstance` no longer steps the flow —
  it just snapshots the live mesh + energy + brush state for output. Two locks:
  - `meshLock` guards the `PlanktonMesh P` + the `vel[]` + the `brushWeights[]`.
    The worker holds it for the duration of one flow step; `SolveInstance`
    grabs it briefly to deep-copy `P` via `new PlanktonMesh(P)`, then computes
    energy *outside* the lock so the worker keeps iterating.
  - `sharedLock` guards `SharedParams` (Step / Momentum / weights / `DetMix` /
    subdivRequest). `SolveInstance` writes on every tick, the worker reads
    once per iteration. Live-tuning happens through this.
  - `RemovedFromDocument`, `DocumentContextChanged(Close)`, and `Reset` all
    stop the worker cleanly.
  - Worker calls `OnPingDocument().ScheduleSolution(...)` after each iteration
    (coalesced via an `Interlocked` flag) to nudge GH to re-solve and refresh
    the output mesh. Without this nudge, no GH timer + `Running=true` = the
    output mesh would visually never update even while the worker iterates.
- **CHA parallelism.** The per-vertex loop in `ComputeHingeEnergyAndGrad` is a
  `Parallel.ForEach` over a `Partitioner.Create(0, nV, chunkSize)`. Each task
  gets its own scratch (`PerTaskScratch`: faces/locIdx Lists, FaceTrig[] cache,
  branch/cons buffers, and a thread-local `gradLocal[nV]`). After the parallel
  block the gradLocals reduce sequentially into the master `energyGrad[]` — no
  lock contention during the parallel pass. `MaxDegreeOfParallelism` is capped
  at `ProcessorCount − 2` so the Rhino viewport + GH UI stay responsive while
  `Running = true`.
- **Plankton-free hot path.** `GetFaceVertices` and `GetVertexFaces` are
  *never* called inside CHA — instead the face precompute walks
  `Face.FirstHalfedge → Next → Next` directly, and the vertex→faces lookup is
  built by walking `Vertex.OutgoingHalfedge → pair.Next` CCW. Both eliminate
  Plankton's per-call `int[]` allocations entirely (formerly ~25K + ~13K
  allocations per CHA call on the bunny). `PerfBench` reports `GFV` and `GVF`
  block costs as 0.0 ms now — those are honest zeros.
- **Brush mouse callback.** A `Rhino.UI.MouseCallback` is registered while the
  worker is running. Ctrl+LMB drag paints into `brushWeights[]` with a cubic
  falloff (radius defaulted at ~8 edge-lengths). CHA's L1 block reads
  `brushWeights[va] + brushWeights[vb]` averaged per edge and **adds** it to
  the global `crazeWeight` for that edge — painted regions get a much stronger
  normal-smoothing pull while the rest of the mesh stays at the global setting.

## 5. Sub-panel crazing — what we tried and where we landed

The flow produces developable panels, but **inside** a panel that should be one
smooth developable region it grows spurious high-frequency creases — *crazing* —
which we want gone, while keeping the **true seams** between panels sharp. Goal:
smooth *within* panels, sharp *at* seams. **This is the paper's own central
problem** (they call it "crumpling"), worst exactly in our case
(near-spherical, no preferred ruling direction).

Discriminators tried *statically* (all leak — crazes and seams overlap on each
one): per-edge **angle**, crease-curve **connectivity / length**, patch **size**,
per-vertex **energy magnitude**, **local-energy-peak**. The earlier reframe —
*"craze vs seam is undecidable statically but decidable dynamically"* — still
looks right: on an intrinsically-flat panel a clean developable has energy ≈ 0,
so a craze is **removable** energy trapped in a local minimum; a true seam is
**irreducible** (Gauss-Bonnet curvature has to live somewhere).

What we built since the earlier handoff, in order:

1. **`deBranch` (paper, App B.5.1).** Squared *minimum* width of the convex
   hull of `±` signed face normals. Strictly anti-branching by construction;
   useful at curved seams where covariance accepts smeared normals.
2. **`deConsolidate` (paper, App B.2).** Min within-cluster pair-sum
   `Σ‖N_s − N_t‖²` over connected 2-partitions of the 1-ring. Pulls
   within-patch normals together while leaving between-cluster differences
   alone — paper-faithful "merge piecewise developability into global."
3. **`useMaxCov` (paper, App B.4).** Replace sum-covariance with
   `min_u max_f ⟨u, N_f⟩²` — forces normals onto a 1-D arc → straight
   rulings. Heavier per-vertex compute but well-defined behaviour.
4. **`deCraze` (NOT paper).** Per-interior-edge L1 dihedral sparsity —
   `Σ |φ_e| · weight` with corner-weighting by `Sharpness`. L1 is
   sparse-promoting: small dihedrals (within-patch) collapse to exactly zero,
   real seams stay. Citation in `NOTICE.md` (Tibshirani 1996; He&Schaefer 2013).
   Empirically the most reliable lever on real workloads, at the cost of
   stiffness (its constant-magnitude subgradient drops the stable `Step`
   ceiling — see §7 lesson 7).
5. **The brush (prototype).** A per-vertex `brushWeights[]` buffer that adds
   *locally* to `deCraze` only where the user has painted. The CHA L1 block
   reads it; everything else flows through the existing pipeline. Lets the
   user mark "smooth this region harder" interactively. Behaviour still being
   tuned — radius/falloff/cap are constants in `CreaseMachine.cs`.
6. **`DetMix`.** Not for crazing per se but for the *related* symmetry issue:
   `λ_min` is non-smooth at degenerate vertices, where the picked eigenvector
   is direction-arbitrary, which produces visible spatial twist on symmetric
   meshes (icosahedra). Blending in `det(M_tangent)` restores basis-invariance.
   Small values (0.05-0.2) fix the symmetric twist without changing the rest
   of the flow's character.

### Open observation: geodesic-sphere oblong distortion

On a high-symmetry **geodesic icosphere** the flow drives the mesh toward a
visibly **oblong ellipsoid** rather than holding the symmetric shape (or
flattening symmetrically). The two obvious explanations turn out to be
**at most partial** answers:

1. **Eigenvector ambiguity at the 12 valence-5 pole vertices.** Predicted to
   cause it; `DetMix > 0` is the proposed fix. **Not yet confirmed empirically
   on the geodesic case** — the user was about to test this when we hit a
   stopping point. Try `DetMix = 1.0` on a geodesic icosphere; if it stays
   round, diagnosis is confirmed for that mechanism.
2. **Nesterov-momentum amplification of small per-frame asymmetries.** Tested
   — running with `Momentum = 0` **does NOT significantly reduce the oblong
   distortion**. So momentum amplification is **not** the dominant cause.

This is a real open finding. Candidates left to investigate, in roughly
descending order of likelihood:

- **Per-vertex trust-region cap (`capLen = L`)** in `CreaseMachine.cs`'s flow
  step. The cap is applied independently per vertex; vertices whose
  `step * grad` exceeds `L` get scaled back, but others don't. On a mesh where
  the gradient magnitude varies (because of eigenvector ambiguity at high-
  symmetry verts), this gives the *appearance* of "racing in different
  directions at different speeds" — the magnitude clamp is uneven across the
  surface even when the gradient direction is roughly OK.
- **Asymmetric guards firing per vertex**: the inverted-face guard
  (`cosPhi < -0.85`), the sliver guard, the fold guard, the corner-weight
  falloff. None *should* fire on a clean geodesic sphere, but if any do (e.g.,
  during the first few iterations before things settle), they fire on a
  subset of the symmetric vertices and break global symmetry.
- **Floating-point order-of-summation** inside the Parallel.ForEach reduce
  step (chunking is by index ranges, so per-vertex accumulator sums are
  deterministic but vertex *ordering* in the reduce sum may bias along
  index-order axes). Probably small but non-zero.

Suggested next steps when you next pick this up:

1. Run `DetMix = 1.0` on a Geodesic sphere with `Momentum = 0`. If still
   oblong → eigenvector ambiguity is not the primary cause (or DetMix isn't
   fixing it the way the math predicts).
2. Disable the per-vertex velocity cap (or replace with a global cap) and
   retest. Will say whether the cap is responsible.
3. Add a programmatic regression test in `PerfBench` / a new `SymmetryTest`:
   load a geodesic icosphere, run N iterations, measure principal-axis ratio
   of the bounding ellipsoid, assert it stays close to 1. Fixing the bug
   without something like this risks regressing it later.

**Practical recipe today:** for sub-panel crazing on real workloads, the
working stack is **covariance + a modest `deCraze` (0.1-0.5) + brush over any
remaining crazed regions**. `deConsolidate` is partly redundant with `deCraze`;
`deBranch` matters only on curved seams. `useMaxCov` matters only when you want
straight rulings; it is expensive without the per-vertex parallelism (and even
with it, ~5-7× the covariance baseline). The brush is what you actually reach
for during interactive work.

**Still legitimately untried at the paper-faithful level:** seeding the input
tessellation (paper §4.1, Fig 2) — break the symmetry with the initial mesh
*before* the flow runs. Most paper-faithful direction still unexplored.

**Discriminators that don't work** are catalogued in the earlier handoff and
remain ruled out — don't re-suggest **coarse-to-fine prevention** (tested and
does NOT fix it), or per-edge angle / patch-size / energy-magnitude
discriminators (all leak).

## 6. Performance

`PerfBench` (in `test/Program.cs`, runs as part of the test bench) reports
per-config ms/call on BunnyScraped.stl (13K verts / 26K faces). At session
start the flow + output cycle was ~134 ms/solve; current ~37 ms/solve
(~3.6×). Roughly half of that came from algorithmic / memory-layout work
(flat `fvFlat`, `dNdp`/`dTheta`/`faceEdge` precomputes, halfedge walking,
List hoists), the other half from the multicore parallel pass.

`deBranch` was the worst hotspot at session start (~1000 ms/call). The
algorithmic reduction was: replace the inner `max_(c,d) (X[c]·u − X[d]·u)²`
loop (O(m²)) with a single `argmax`/`argmin` scan (O(m)) — same answer,
6.4× faster (~150 ms). `deConsolidate` got the same treatment: the O(n⁴)
within-cluster enumeration was replaced with an O(n²) triangle-table
precompute + clusterRowSum running update (~30% faster). Both also got their
scratch buffers hoisted (`branchX`, `consPd`, `consRowPref`, `consTri`).

`PerfBench` is the regression net for perf-touching changes — anything that
regresses the **flow + output** line by >10% should be flagged.

## 7. Hard-won lessons (do NOT relearn these the hard way)

1. **The dev step must be RAW-proportional.** Never normalize the step by the
   median/max gradient — it *diverges* (bench: 2.36 → 29 normalized, vs
   2.36 → 0.04 monotonic raw). The self-damping that makes the flow settle comes
   precisely from the raw gradient vanishing near the minimum.
2. **Energy + eigenvector come from the 2×2 tangent block, never a 3×3
   eigensolve.** An earlier "gradient bug" (a handful of bad vertices) was a
   fragile 3×3 eigenvector/energy selection, *not* a derivation error. Using the
   tangent block (`Nv` is the known null vector) fixed it cleanly. The new
   `TangentEigenpairs` extends this to return *both* eigenpairs for `DetMix`.
3. **Step is `Step·L²`, not `Step·L`.** L² makes it invariant to mesh *scale*
   **and** to Subdivide (which halves L and doubles the 1/length gradient).
   Verified across 1×/10× scale and across subdivision.
4. **Four degeneracy guards are all load-bearing** (without them a degenerate
   triangle's 1/area term spikes ~26000× and corrupts the mesh): **sliver**
   (face aspect < 1%, now precomputed into `faceSliver[]`), **fold** (vertex-
   normal coherence `|rawNormal|/ΣdA < 0.1`), **inverted-face**
   (`cosPhi = Nv·Nf < −0.85`, i.e. phi > ~148°), and the **velocity cap**
   (≤ 1 edge). Plus short-edge collapse, aspect-based sliver collapse, and a
   coherence-<0.05 fold-heal collapse (all via Plankton's manifold-safe
   `CollapseEdge`). A *legitimate* convex sharp edge tops out ~106° from `Nv`
   and is preserved; only a surface folding under itself exceeds 148°.
5. **Performance budget is real:** the per-vertex inner work runs on the order
   of 10⁵× per frame at ~30 fps during `Running=true`. Keep everything
   1-ring-local; avoid per-vertex allocations in hot paths. `PerfBench` will
   catch regressions; run it.
6. **License is GPL-v2.** Re-derive math from the **paper**, do not copy the
   reference implementation's code. The reference impl
   (`hinge_energy.cpp`, `triangle_dTheta.cpp`, `triangle_dN.cpp` — same
   GPL-v2 as us) can be cross-checked for math correctness but its variable
   names / structure / decomposition should NOT be mirrored in commits.
   See `NOTICE.md`.
7. **L1 (`deCraze`) is stiff.** The subgradient is constant-magnitude in φ
   near 0 — fundamental to the lasso property, not a bug. Practical
   consequence: turning on `deCraze` lowers the safe `Step` ceiling by ~2-5×
   compared to pure covariance. If you ever want this not to feel that way,
   per-term gradient normalization (each term divided by its 95th-percentile
   magnitude) is the right surgery, but mind the FD bench afterward.
8. **Kink filter belongs in the analytic path too.** The earlier project had
   `RejectKinkOutliers` only in the numerical-grad FD harness. The live flow
   inherited the eigenvalue-crossing kinks unmasked → jitter at `Step > 0.01`.
   The fix (port the filter to the analytic path) is now in `CHA`. Don't
   remove it without thinking about it.
9. **`Running=true` requires the `ScheduleSolution` nudge.** Without it the
   worker iterates invisibly — the output mesh doesn't refresh because GH has
   no reason to re-solve. The coalesced `Interlocked`-gated nudge in
   `RequestRedraw` is what makes the threaded mode actually usable.
10. **Don't snapshot the live mesh by reference.** GH downstream consumers
    read whatever you hand them, and the worker mutates `P` in place. Always
    snapshot via `new PlanktonMesh(P)` under `meshLock`. The deep-copy cost
    (~few ms on a 13K mesh) is real but unavoidable given the threading model.

## 8. Deliberately dropped (don't re-add without a reason)

- **`FlipDiagonals`** — diagonal-flip step inside the flow loop. Was added,
  found to interact badly with sliver/fold guards (flips would create
  immediately-flagged folds), and stripped back to paper-faithful in the same
  session. The aspect-based sliver collapse covers the cases it was meant to
  handle.
- **Unbend / `CreaseBlendForce`** (legacy fork) — a crease-blend de-craze lever.
  Smooths, then the dev force rebuilds the same creases. Found unhelpful;
  removed before this repo split.
- **`MeshEdgeAngle`** (legacy fork) — an edge-angle diagnostic component. Its
  purpose (testing whether angle separates craze from seam) is spent.
- **Thermal noise / `Temperature`** — prototyped in an earlier session,
  bench-validated, rolled back. The working ridge-aware gate was
  `max(0, energy − MAX-neighbour-energy) / (energy + 0.001·maxEnergy)`. The
  brush ultimately covered the same user-intent ("kick this region out of its
  local minimum") in a more direct, user-controllable way.
- **Coarse-to-fine prevention** — tested by the author in the earlier project,
  does NOT fix sub-panel crazing. Don't re-suggest.
- **The MeshMachine `remesher` node flow** — never extracted into this repo
  (see `NOTICE.md`). Lives in the legacy `CreaseMachine-old` sibling repo as
  the all-rights-reserved-by-default code from the original Piker fork.

## 9. Conventions

- Run the bench after touching energy / gradient / flow / threading code —
  it is the regression net, and it is fast (~seconds).
- Keep `DevelopabilityEnergy` / `MeshOps` / `Vec3` Rhino-free. This is what
  keeps a future standalone / headless / web-service deployment cheap.
- The component GUID is `078039c1-4b2e-4e4f-8c72-e909a9b5c8f7`; keep it stable
  so existing Grasshopper definitions keep their wiring.
- Live deploy target is `%APPDATA%\Grasshopper\Libraries\CreaseMachine.gha`.
  Close Rhino before copying.
- Legacy fork (preserved, not built or deployed from this repo):
  `C:\Repo\xdkaplan\CreaseMachine-old`.
