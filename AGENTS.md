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
- **`gui/`** (`CreaseGUI` → `CreaseGUI.exe`, *legacy — superseded by the in-process PieceSolver app*): a basic WinForms control panel that **drives
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
in-process PieceSolver app holds a persistent `FlowSession` (`_session`); note its **Solve** develops
via the IsometricLM patch-solver, a separate path from this shared `NesterovStep`.

## In-process app — `PieceSolver/` (the active studio)

`PieceSolver/` (`PieceSolver` → `PieceSolver.exe`) is the active in-process interactive app: load a
mesh, flatten it, and develop it into a piecewise-developable sheet, all in one net8 process. (It was
renamed from `patchsolver/CreasePatchSolver`. `studio/` is a fork of the same WPF + OpenTK + engine
scaffolding — the **CreaseStudio** app, the Nesterov covariance-flow front-end where the crease-proposer
Solve lives — developed in parallel, not a dead copy.) **Direction:** PieceSolver will become a *module* within the eventual
**CreaseStudio** — its standalone window goes away and its GUI is fully consumed into CreaseStudio, with
PieceSolver providing the per-piece flatten/develop solver + workflow. Build:

```sh
dotnet build PieceSolver/PieceSolver.csproj -c Release && PieceSolver/bin/Release/net8.0-windows/PieceSolver.exe
```

### The Solve workflow

- **Load** OBJ / STL / **FBX**. Binary FBX is read by `src/FbxIO.cs` preserving Rhino's *unwelded* seam
  topology, so a 6-sided solid loads as one connected component per brep face. STL can't carry that
  (triangle soup → re-welded), so FBX is the piecing-friendly format.
- **Solve** is an **async, cancelable, modal bake** (a background worker behind a progress + cancel
  overlay; it is the *single* develop path — the old hold-Space live-step and the SubD button were
  removed). It develops to the selected **Accuracy** (allowable in-plane strain %, by material) and then,
  per **Subdivision level**, subdivides + re-develops. **Solve develops a *derived* mesh, never the
  authoring mesh** (so the authoring mesh + its `Pattern` survive — `OnSolveAsync` no longer calls
  `Revert`; it bakes a clone/unweld on a temporary session and keeps the result as the `_developed`
  Transient the view shows; see [`docs/SOLVER-PHASE.md`](docs/SOLVER-PHASE.md)). The derived mesh: a
  **pieced** mesh (>1 painted region) is **unwelded along its creases** (`MeshOps.UnweldByRegion`) so each
  painted piece is its own connected component; an FBX solid arrives multi-component already; a single open
  patch is a plain clone. The multi-component path then splits (`MeshOps.SplitComponents`), BFF-flattens +
  isometric-develops each piece with its boundary **frozen (Dirichlet)**, and reassembles — flat panels
  laid out beside the model, worst-panel-strain GO gate. *(Known limitation: a fully-frozen per-piece
  boundary over-constrains painted pieces → wrinkly panels; loosening seams to a soft constraint is the
  named seam-relaxation follow-up, not built.)*
- **Develop solver:** `PieceSolver/IsometricLM.cs` — Levenberg–Marquardt + matrix-free Jacobi-
  preconditioned CG, co-refining M (3-D) and its flat image M′ toward isometry (= developability). The
  paper's developability solver, NOT the covariance flow. Perf-tuned (preconditioner, per-edge
  precompute, Nielsen damping, a parallel gather-by-vertex apply) and gated by an opt-in finite-
  difference gradient check (`DebugGradCheck`). An optional `pinned` Dirichlet holds chosen vertices
  fixed — the seam pin.
- **Flatten:** `PieceSolver/Bff.cs` — Boundary First Flattening via the external `bff-command-line.exe`,
  per patch.
- **B-spline seams:** `src/BSpline.cs` — a low-DOF periodic cubic "bent wire" fit to a boundary loop; the
  **Fix B-spline edges** toggle pins the boundary onto it (Dirichlet) and draws the curve + control
  polygon. The MVP freezes seams; *relaxing* them (control points move under the same `E_iso`, a soft-
  constraint spectrum) is a deliberate later feature.

### Architecture (settled — don't drift from it without reason)

- **net8 WPF, engine in-process.** Compiles the SAME Rhino-free engine sources
  (`Vec3`/`DevelopabilityEnergy`/`MeshOps`/`MeshIO`/`Session`/`BSpline`/`FbxIO`) **plus the patch solver
  `IsometricLM.cs`** directly — no subprocess, no IPC. This is why Plankton was retargeted to
  `netstandard2.0` (loads in both the net48 plugin and net8 here).
- **3D via OpenTK + GLWpfControl** (`MeshView.cs`): MatCap shader, Z-up orbit camera, own per-frame depth
  renderbuffer (GLWpfControl's framebuffer is colour-only). Overlays: ruling lines + B-spline seam wires.
- **MVVM** (`SimSettings.cs`): panel controls two-way bind to a view-model, not code-behind. 4-panel
  docked layout (fixed top/bottom bars, drag-resizable left/right panels, centre viewport).
- **Command sink + journal** (`Journal.cs`): every action routes through one `Execute(StudioCommand,
  record)` chokepoint — the spine for record/replay (and, later, undo). The async **Solve** is journaled
  as a `solve` command carrying a full `BakeParams` snapshot; replay gates on the bake's completion
  before advancing. The `.journal` grammar is a **superset of the CLI's**, so the same file drives both
  the headed app and the headless `crease.exe` (the CLI maps `solve` to its Nesterov bake equivalent).
  Camera orbit is intentionally not journaled.
- **Piecing data model + transactions + editors** (`Pattern.cs` / `PieceId.cs` / `Tx.cs` / `Doc.cs` /
  `Commands.cs` / `Editor.cs` / `Piecer.cs`): the Piecing partition, its undo/redo transaction layer, and
  its interaction all live outside the `MainWindow` god-file. See
  [`docs/PIECER-REFACTOR.md`](docs/PIECER-REFACTOR.md) (the unit extraction) and
  [`docs/DOC-TX-REFACTOR.md`](docs/DOC-TX-REFACTOR.md) (the Doc / undo-redo layer) for the models +
  glossary + roadmap. The vocabulary below is shared by both.
  - **`Doc`** (`Doc.cs`) — the **orchestrator**. Owns the Store(s), the typed `Selection<T>`(s), and the
    undo/redo stacks, and gatekeeps every piece mutation through `Run` / `OpenTx` / `Undo` / `Redo`
    (`Run(delta)` = open + `Store.Apply` + push undo + clear redo → `Changed`; it's one-shot sugar over a
    transaction). A gesture brackets its edit with **`OpenTx()` … `Tx.Apply`/`Commit`/`Cancel`** — **one tx
    at a time**, accumulating into one undo unit (`CompositeDelta`). Mutating entry points **self-reject when
    `!Ready`** (a `Tx` is open, or a long op set `Busy` — e.g. the bake's `EnterBusy(Calculating)`), so a
    competing `Ctrl+Z` mid-stroke is a clean no-op; ESC cancels an in-flight stroke (`Editor.CancelGesture`).
    Short for Document; "Project" is reserved for a future on-disk workspace. `Selection<T>` is typed per
    Element, carries a `Changed` event, and is **NOT** on the undo stack (nor is the view/camera).
  - **`Pattern`** — the (only, today) **Store**: a THIN companion over one `PlanktonMesh` (Plankton has no
    per-face attribute storage). Holds **Real** state — the authoritative **`PieceMap`** (`int[]`, per-face
    piece id) — and a **Transient**, derived **`CreaseMap`** (`HashSet<long>` packed edge keys, edges
    between differing pieces) that `RegenCrease` re-derives (lossy; runs after every Apply/Invert).
    Implements `ITxAble` (`Apply`/`Invert` a `PieceDelta` — the single persistent `PieceMap` writer);
    `ComputeDelta(mutate)` runs an in-place op, captures the net change as a delta, and rolls back (so the
    intricate in-place engines — `Delete`/`Carve`/`Grow`/`Mint` + `SplitDisconnected` — are reused as
    delta-producing Commands). `Seed` flood-fill = a whole-partition **Chapter** reset; queries are
    read-only (`NewRegionId`, `FullyMarked`, `FacesUnderBrush`, `GrowAssign`, `LargestComponent`,
    `RegionsConnected`).
  - **`Tx`** (`Tx.cs`) — the transaction primitives: **`IDelta`** (one reversible change, opaque to the
    Doc, concrete to the Store), **`Op`** (its invertible atom — a face's label `From → To`), `PieceDelta`
    (a list of Ops), and **`ITxAble`** (`Apply`/`Invert`).
  - **`Commands`** (`Commands.cs`) — pure functions that read Selection + Real state and **compute** an
    `IDelta` (never mutate; the Doc applies it). The user calls these **Tools**; we call them Commands.
    First: `Merge` (fuse each connected component of the selection into its survivor via
    `Pattern.MergeGroups`; isolated selected pieces are left as-is).
  - **`PieceId`** — a zero-cost `readonly struct` handle over the int piece id (an **Element** id at the
    selection boundary). Ints stay dense in `PieceMap` (hot path).
  - **`Editor` / `Piecer`** — `Editor` is the abstract base (lifecycle + pointer hooks + a per-face
    `FaceFill` tint). **`Piecer : Editor`** is the editor active during Piecing (after Propose → Accept):
    selection is a **set** of pieces (in `Doc.Pieces`); each modifier splits at a ~10px threshold into a
    **tap** and a **drag** — plain tap = replace selection; Shift tap = add, Shift drag = grow (mint when
    empty); Ctrl tap = remove from selection, Ctrl drag = carve (delete whole pieces when empty). `M`
    merges; `Ctrl+Z` / `Ctrl+Y` undo/redo. Every committing gesture is one `Doc.Run` transaction; the
    Piecer computes deltas, no geometry moves.
  - **`IEditorHost`** — the narrow interface `MainWindow` implements so an editor talks to its host (mesh,
    `Pattern`, **`Doc`**, picking, brush footprint, view-refresh hooks) rather than the whole window — the
    wall that keeps the god-file from regrowing. `MainWindow` owns the `Doc` (which owns the `Pattern`) +
    the active `Editor`, reacts to `Doc.Changed` / `Pieces.Changed`, and keeps the render loop / camera /
    picking / crease-review modal.

  **Shared vocabulary** (see `DOC-TX-REFACTOR.md`): **Doc** (orchestrator) · **Store** (`ITxAble`, holds
  Real state) · **IDelta** / **Op** · **Command** (= the user's "Tool") · **Real / Transient / Ephemeral**
  (defined below) · **Element** (Piece, Crease, … — was "entity") · **Selection<T>** · **regen** ·
  **Chapter** (reset boundary) · **tx** (one gesture = one transaction). Out of scope today: crease-identity
  / reconcile-regen, the Creaser, Joins / Tabs / Cone tips, stable GUIDs, journaling of piecing.

  **Naming & vocabulary are the user's call.** The user owns term, class, method, field, and command
  naming — and the conceptual model behind a name. **Propose** any new name or rename and get **explicit
  acceptance** before introducing it; never coin or rename unilaterally. This whole vocabulary was settled
  exactly that way (propose → debate → accept), and that is the expected workflow for the next one.

  **Real / Transient / Ephemeral** — one distinction that governs undo, regen, *and* save:
  - **Real** — authored source-of-truth (mesh, `Pattern`, params, future crease types / seams). Undoable
    (mutated only via a tx) and the *only* state written to file.
  - **Transient** — *derived* from Real (`CreaseMap`, the unwelded PieceMesh, developed geometry, overlays).
    Not undoable, not saved: carries a derives-from dependency + dirty bit and is **regenerated** from Real
    (eagerly if cheap, lazily if expensive). Computed-*from* Real, never a live alias of it.
  - **Ephemeral** — computed once and thrown away when its scope ends: a **transaction** (a gesture's
    preview accumulators) or an **editor** (the selection, cleared when the editor deactivates), plus view
    state (camera). Not undoable, not saved, and *not* regenerated — just discarded.

The brush-to-freeze-creases north star and the CreaseStudio consolidation plan live in the user's memory.

## Vendored dependencies

`lib/PlanktonGh.dll` is stock, unmodified upstream
[Plankton](https://github.com/meshmash/Plankton) (0.4.3) by Daniel Piker and
Will Pearson. `lib/Plankton.dll` is that same unmodified 0.4.3 source recompiled
to **netstandard2.0** (zero code changes, same `0.4.3.0` version) so one assembly
serves both the net48 plugin and the net8 in-process app (PieceSolver); net48 loads it
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
