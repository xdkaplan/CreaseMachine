# CreaseMachine

A Grasshopper (Rhino 8) component — **CreaseMachine** — that flows a triangle mesh
toward a piecewise-developable (creasable) sheet using the covariance
("hinge") developability energy of **Stein, Grinspun & Crane**, *"Developability
of Triangle Meshes"* (ACM TOG 37(4), 2018), with optional paper-faithful B.2 /
B.4 / B.5.1 extensions and a practical L1 dihedral sparsity term for sub-panel
consolidation.

The component evaluates the developability energy and its analytic gradient,
flows the mesh down that gradient (Nesterov-accelerated gradient descent), and
— on demand — applies one 1→4 subdivision so creases can sharpen at higher
resolution. No remeshing, no projection, no smoothing.

The flow loop is multicore-parallel (per-vertex independent work, per-task
gradient accumulators, reduce at the end) and can run on a background thread
decoupled from the Grasshopper solve cycle.

## The `CreaseMachine` component

Category: **Mesh → CreaseMachine**.

### Inputs

| Input | Nick | Type | Default | Meaning |
|-------|------|------|---------|---------|
| Mesh | Mesh | Mesh | — | Triangle mesh to develop. Connectivity is preserved (no remeshing); quads are triangulated on input. |
| Step | Step | Number | 0.05 | Step size as a fraction of edge length — the most-curved vertices move about this fraction of an edge per iteration. Applied internally as `Step·L²` so it behaves identically at any mesh scale and after Subdivide. ~0.05 descends cleanly; raise for speed, lower if the surface shimmers. Live-tunable. |
| Momentum | Mom | Number | 0.9 | Nesterov momentum (0–0.95). 0 = plain gradient descent; 0.9 reaches a developable state in roughly 5× fewer iterations. Higher is faster but lowers the stable `Step` ceiling. Resets on Reset and Subdivide. Live-tunable. |
| Iterations | Iter | Integer | 1 | Flow steps taken per solve. Connect a timer for continuous flow (or use `Running` to run continuously on a background thread regardless of the timer). |
| Subdivide | Subdiv | Boolean | false | Rising edge (false→true) applies one in-place 1→4 (midpoint) subdivision to the live mesh. Per the paper: subdivide after the flow settles to get hi-res creases, then keep flowing. |
| Reset | Reset | Boolean | true | True to (re)initialize from the input mesh, false to run. Connect a timer for continuous flow. |
| deBranch | deBranch | Number | 0 | Weight of the **B.5.1 branching penalty** — the squared *minimum* width of the convex hull of `±` signed face normals. The covariance energy penalises the *sum* of squared widths; this term penalises the min directly, strictly anti-branching by construction. Useful when seams craze along curved seams. Live-tunable. |
| deConsolidate | deConsolidate | Number | 0 | Weight of the **B.2 combinatorial / consolidation** penalty — for each vertex, the minimum within-cluster pair-sum `Σ‖N_s − N_t‖²` over connected 2-partitions of its 1-ring. Penalises within-patch normal spread while leaving real seams alone — merges piecewise developability into global. Live-tunable. |
| useMaxCov | useMaxCov | Boolean | false | Replace the default sum-covariance (smaller eigenvalue of `Σθ N Nᵀ`, Eq 5) with the **B.4 max-covariance** `λ_max = min_u max_f ⟨u,N_f⟩²`. The sum form lets rulings branch into V's at seams; the max form forces every normal onto a single 1-D arc → straight rulings. Live-tunable. |
| Sharpness | Sharpness | Number | 4.0 | Corner-preservation exponent. Per-vertex energy and gradient are multiplied by `w(d) = 1 / (1 + (d / (π/4))^Sharpness)`, where `d` is the Gauss–Bonnet angle defect. At 0 the falloff is off (corners get pulled flat); higher values preserve sharper junctions (cube corner at Sharpness=4 keeps ~6% weight). Live-tunable. |
| deCraze | deCraze | Number | 0 | Weight of an **L1 dihedral sparsity** penalty (`Σ|φ_e| × weight`). Sparse-promoting: within-patch edges drop their dihedral to exactly zero (so adjacent patches merge) while real seams keep theirs. Corner-weighted by `Sharpness` so sharp junctions are still preserved. Not from the paper — see `NOTICE.md` for the Lasso / He&Schaefer L0-mesh-denoising citation. Live-tunable. |
| Running | Running | Boolean | false | `true` = decouple compute from the GH solve cycle. The flow runs continuously on a background worker thread, snapshotting the mesh + energy out on each timer tick. All other inputs stay live-tunable. `false` = legacy behaviour: one flow step per GH solve. See [Running mode](#running-mode) below. |
| DetMix | DetMix | Number | 0.0 | Continuous blend in `[0, 1]` between the paper-faithful `λ_min(M)` energy (`DetMix=0`) and the symmetric `det(M_tangent) = λ_min·λ_max` energy (`DetMix=1`). `λ_min` is genuinely non-smooth at degenerate vertices (icosahedral corners, symmetric quads) — the picked eigenvector is direction-arbitrary there, which can produce visible twist on symmetric meshes. Mixing in a small amount (try 0.05–0.2) restores symmetry by combining both tangent-plane eigenvectors. Live-tunable. |
| MomFix | MomFix | Integer | 4 | Momentum-restart mode for near-isotropic vertices whose gradient direction is arbitrary (the source of "racking" on symmetric meshes like geodesic spheres). `1` = none (paper behaviour, racks ~iter 27). `2` = DegenZeroMom: zero velocity where eigenvalue separation `sep < 0.1`. `3` = GradRestart: zero velocity when `dot(grad, vel) > 0` **and** `DetMix < 0.5` (velocity heading uphill; delays racking to ~iter 61). `4` = Combined 2+3 **plus a global adaptive momentum restart** (O'Donoghue–Candès: reset all velocity on any step that overshoots uphill in aggregate) that prevents the fold→collapse cascade which destroyed meshes under sustained high momentum — makes `Momentum = 0.9` stable *and* convergent (default). Live-tunable. |
| CrazeBand | CrazeBand | Number | 0.1 | **deCraze smoothing band, in radians (Huber).** `deCraze` penalises `|φ|` on the *unsigned* dihedral, whose force holds constant magnitude as `φ → 0` and reverses direction across the flat (`φ=0`) cusp — a non-vanishing, flipping force that makes `deCraze` *vibrate/jitter* under momentum instead of flattening cleanly. `CrazeBand` replaces `|φ|` with a quadratic below the band, so the force tapers smoothly to 0 at flat (near-flat edges **settle**) while edges above the band keep the full L1 pull (real creases untouched). ~0.1 rad (~5.7°, default) calms the jitter while preserving creases; raise toward 0.2–0.3 if it still buzzes, lower if real creases soften. `0` = off (original pure-L1 behaviour). Only active when `deCraze > 0`. Live-tunable. |

### Outputs

| Output | Nick | Type | Meaning |
|--------|------|------|---------|
| Mesh | Mesh | Generic (Plankton mesh) | The developing mesh, as a `PlanktonMesh`. Snapshotted under the worker mesh-lock so it is consistent with the energy + brush outputs. |
| Energy | Energy | Number (list) | Per-vertex developability energy (smaller eigenvalue of the 1-ring normal covariance — or the `DetMix`-blended energy when `DetMix > 0`), parallel to the mesh vertices. ~0 where developable, higher at residual non-developable spots (seam corners). Colour the mesh by it to inspect crease structure. |
| BrushWeights | Brush | Number (list) | Per-vertex brush-paint state (an additive local `deCraze` boost) accumulated by the experimental drag-paint UX. Parallel to the mesh vertices, 0 where untouched, up to ~2.0 saturated. Useful as a vertex-colour overlay during development. (The brush UX itself is still prototyping — its behaviour and accumulation curve may change.) |

### Notes on the method

- The **analytic gradient** of the covariance developability energy is used
  (re-derived from the paper). The optimizer — Nesterov-accelerated gradient
  descent — is our own choice, not the paper's.
- The **raw** gradient is used (no magnitude normalization), so the velocity
  self-damps as the gradient vanishes and the flow settles rather than orbiting
  the minimum.
- Boundaries are held fixed.
- Slivers and severe folds are healed by simple, manifold-safe edge collapses
  (Plankton's `CollapseEdge` primitive) — this is **not** an adaptive remesher.
- Faces that fold back past the vertex normal (an inverted/overhang
  configuration) are dropped from the gradient sum, whose amplifier term would
  otherwise spike toward a numerical explosion. A legitimate convex sharp edge
  never reaches that threshold, so it is preserved.
- A **kink-outlier filter** zeros any vertex whose gradient magnitude exceeds
  ~8× the per-vertex median. Eigenvalue crossings of the in-plane covariance
  produce genuine subgradient jumps; without the filter, those single vertices
  fly off and corrupt the flow at moderate-to-large `Step`.
- **DetMix** smoothly trades paper-faithfulness for symmetry. At `DetMix = 0`
  the energy is exactly the paper's `λ_min(M)`, non-smooth at degenerate
  vertices — fine on most meshes, visibly twisty on icosahedra / symmetric
  quads. Raising `DetMix` adds in `λ_min·λ_max`, whose gradient combines both
  tangent-plane eigenvectors and is basis-invariant. Small values (0.05–0.2)
  fix the symmetric-mesh twist without changing the rest of the flow's
  character.
- **Energy** is exposed per vertex so you can colour the mesh and inspect where
  curvature concentrates (seam corners stay hot; developed regions go to ~0).

### Running mode

When `Running = true`, the flow detaches from the GH solve cycle and runs on a
background worker thread as fast as the engine can deliver. The component still
solves on the GH timer / input changes — those solves just snapshot the
*current* mesh + energy + brush state for display, instead of stepping the flow
themselves. Practical implications:

- A connected timer paces the **display refresh**, not the compute rate.
- All inputs (`Step`, `Momentum`, weights, `Sharpness`, `deCraze`, `DetMix`)
  remain live-tunable while the worker iterates — the worker reads them once
  per iteration under a shared lock.
- Multicore parallelism is capped at `ProcessorCount − 2` so the Rhino viewport,
  the GH UI thread, and the OS keep cycles for responsiveness.
- `Reset = true`, removing the component, or closing the document stops the
  worker cleanly.

When `Running = false` the component behaves as it did originally: one flow
step per GH solve.

## Building

Requires the .NET SDK and **Rhino 8** installed at the default location
(`C:\Program Files\Rhino 8`), which provides the Grasshopper, GH_IO and
RhinoCommon SDK assemblies referenced by the project.

```sh
dotnet build src/CreaseMachine.csproj -c Release
```

This produces the Grasshopper plug-in at:

```
src/bin/Release/net48/CreaseMachine.gha
```

To install, copy that `.gha` into your Grasshopper Libraries folder
(`%APPDATA%\Grasshopper\Libraries`) and unblock it.

## Tests / bench

A Rhino-free console bench (`GradCheck`) compiles the energy, vector and
mesh-ops sources directly and runs a battery of checks: finite-difference
gradient verification, developability classification, scale-invariance,
momentum/collapse/degeneracy sanity, plus a per-config CHA microbench on a
representative input.

```sh
dotnet build test/GradCheck.csproj -c Release
test/bin/Release/net48/GradCheck.exe          # full suite
test/bin/Release/net48/GradCheck.exe perf     # fast: FD gate + CHA perf + value checksums only
```

(`GradCheck.exe` is a net48 executable — run it directly, not via `dotnet`.)
Some diagnostics look for hardcoded STL files under `C:\Temp`; if they are
absent those lines are skipped — that is expected.

The `perf` mode times the CHA per-config (per-phase tick breakdown via `CHAStats`)
on three meshes — `C:\Temp\Bunny {2.5k,5k,20k}.stl` — and prints **value-preservation
checksums** (`sumE`, `sum|g|`, an index-weighted gradient probe) for the flow config.
`sumE` is fully deterministic; the gradient sums carry ~1e-13 relative parallel-
reduction jitter. When optimizing CHA, capture the checksums first and require every
change to reproduce them to ~1e-9 relative — a real value bug lands far above that.
See [`docs/PERF-CHA.md`](docs/PERF-CHA.md) for the optimization history and numbers.

## Headless tooling (Rhino-less)

Two front-ends over the same engine, for development / fabrication exploration without Rhino:

- **`cli/`** (`CreaseCLI` → `crease.exe`): a stateful REPL. `load` a mesh, then `run N [params]`
  to bake it incrementally (Nesterov velocity persists across runs for continuity). Numeric
  params accept linear ramps over a run: `run 150 deCraze=0.1>0.0`. Other commands: `subdivide`,
  `reset`, `stats`, `zero-momentum`, `export <file.obj|.ply>` (PLY carries per-vertex
  developability energy as vertex colour). Metrics per run: developability `sumE` (regularizers
  excluded), `maxGrad`, `panels`, `crazeRMS`, `maxDih` (crease cutoff = `CrazeBand`).
- **`gui/`** (`CreaseGUI` → `CreaseGUI.exe`): a basic WinForms control panel that **drives
  `crease.exe` as a subprocess** (composes commands → stdin, streams output → log). No 3D viewer;
  Export writes PLY/OBJ for MeshLab/Blender/Rhino, with an optional auto-export-to-watch-file for
  live-reload review. Locates `crease.exe` in `cli/bin` (Browse fallback).

```sh
dotnet build cli/CreaseCLI.csproj -c Release && cli/bin/Release/net48/crease.exe
dotnet build gui/CreaseGUI.csproj -c Release && gui/bin/Release/net48/CreaseGUI.exe
```

Both compile the Rhino-free engine sources directly (like the bench).

**Shared flow step:** the canonical Nesterov developability step lives once in
[`src/Session.cs`](src/Session.cs) as `FlowSession.NesterovStep` (look-ahead → CHA →
momentum-restart + trust-region cap), with thin `CollapseShort/CollapseSliver/HealFolds`
helpers over `MeshOps`. Both the GH component (`CreaseMachine.DoFlowStep`) and the CLI drive
the same `FlowSession`, so the intricate flow logic cannot drift between them — verified by the
CLI reproducing fixed bunny checksums bit-for-bit after the extraction. Collapse *cadence* is
still the caller's choice (GH collapses once per `Iter`-step solve; the CLI collapses every
step) — legitimate per-host policy, not shared math. The bench's `FlowAndWatch` and `repro/`
keep their own loop copies on purpose: diagnostic harnesses, not under a sync contract
(`FlowAndWatch` omits the momFix restarts; `repro/` is a frozen racking experiment). The
in-process GUI will hold a persistent `FlowSession` rather than the per-step transient the
plugin/CLI wrap today.

## Vendored dependencies

`lib/PlanktonGh.dll` is stock, unmodified upstream
[Plankton](https://github.com/meshmash/Plankton) (0.4.3) by Daniel Piker and
Will Pearson. `lib/Plankton.dll` is that same unmodified 0.4.3 source recompiled
to **netstandard2.0** (zero code changes, same `0.4.3.0` version) so one assembly
serves both the net48 plugin and a future net8 headless GUI; net48 loads it
transparently and `PlanktonGh.dll` binds to it unchanged. Vendored so the project
builds without a separate Plankton checkout. Plankton is LGPL — see
[`docs/NOTICE.md`](docs/NOTICE.md) for the netstandard2.0 rebuild recipe.

## License

This project is released under the **GNU General Public License, version 2
(GPL-v2)**. See `LICENSE` for the full text, and `NOTICE.md` for the upstream
attributions and how the license decision was reached.

GPL-v2 matches the license of the reference implementation released by the
authors of the Stein/Grinspun/Crane paper this code re-derives, keeps the
lineage clean, and is compatible with the LGPL-licensed Plankton dependency.
Commercial use is permitted under the GPL-v2 terms: you may distribute and
sell builds of this `.gha`, provided you also make the source available and
preserve the license on any derivative work.
