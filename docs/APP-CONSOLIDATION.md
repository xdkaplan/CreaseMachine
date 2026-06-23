# App consolidation — `studio/` + `PieceSolver/` → CreaseStudio

Status: findings + sequencing for the merge. Companion to
[`BRUSH-SCOPE.md`](BRUSH-SCOPE.md) (the brush part of the merge) and the engine
boundary plan (below). **Revised 2026-06-22** after the crease-proposer + Shine
shading landed in `studio/` and merged to master — the port list is now wider
than brush-only, and the "stale fork" framing is dropped (see TL;DR).

## Target workflow (the UX the consolidation serves)

The one consolidated app is a linear pipeline, not a pile of modes:

1. **Rough import mesh** — OBJ / STL / FBX (FBX preserves unwelded seams).
2. **Crease lines, auto-placed** — the **crease proposer** (Nesterov flow →
   `EdgeDihedrals` → edges past the Crease∠ threshold) marks likely piece
   boundaries. *Built in `studio/` this session; must port to the base app.*
3. **Manual discernment** — a **brush** isolates patches (refine the proposed
   creases / freeze regions). One brush, per `BRUSH-SCOPE.md`.
4. **Patch solver** — **IsometricLM** flattens + develops each patch, with
   Bézier (B-spline) seam edges. PieceSolver's existing kernel.
5. **Convert to quad-strip mesh** — *future dev.*
6. **Modify as a quad-strip mesh** — *future dev (a quad-strip paper, not yet read).*

Stages 2–4 are the near-term target; 5–6 are placeholders. This is exactly why
the merge must carry studio's **stage 2 (proposer)** and **stage 3 (brush)** onto
PieceSolver's **stage 4 (solver)** base — porting the brush alone would strand
the proposer.

## Design principle — converging on FEWER options

We are deliberately **shrinking surface area**: one brush, not 14; good defaults
over knobs. When a secondary control must survive, **hide it behind an "Advanced"
disclosure** instead of adding a top-level option — the Shine shading's
**Use Matcap → Advanced** toggle is the template. Prefer deleting an option to
demoting it; demote to Advanced only when it earns its keep.

## TL;DR

- **`PieceSolver/` is the base app** — bigger and more capable (BFF flatten +
  the IsometricLM develop solver + the ruling/LIC viz). **`studio/` is a parallel
  fork** (CreaseStudio, the Nesterov front-end), *not* a stale/abandoned one: as
  of 2026-06-22 it carries the **crease proposer** (stage 2) and **Shine
  shading**, both merged to master. The consolidation adopts PieceSolver as the
  base and ports studio's *unique* work onto it.
- They are **not symmetric dupes** — they share a WPF + OpenTK *shell idiom* but
  have **drifted hard** and have **disjoint feature sets**. So the merge is a
  **port, not a dedupe**: adopt PieceSolver as the base; port from `studio/` the
  **brush chassis** (per `BRUSH-SCOPE.md`), the **crease proposer** (Solve +
  `EdgeDihedrals` + crease overlay), and the **Shine shading**; then delete
  `studio/`.
- **Sequence: merge the apps first, then extract the engine library.** Doing the
  engine extraction first would mean plumbing a library into `studio/`, an app
  about to be deleted.

## How "dupe" they actually are (line drift)

Counts are current as of the post-merge master (both sides moved: studio gained
the proposer + Shine; PieceSolver gained the ruling/LIC rework).

| File | studio | PieceSolver | diff lines | verdict |
|---|--:|--:|--:|---|
| `Converters.cs` | 32 | 32 | 2 | true dupe — delete one |
| `GroundGrid.cs` | 86 | 86 | 2 | true dupe — delete one |
| `App.xaml.cs` | 6 | 6 | 2 | true dupe — delete one |
| `Journal.cs` | 88 | 141 | 67 | drifted (command bus) |
| `SimSettings.cs` | 89 | 211 | 188 | PieceSolver superset; studio adds Shine + Crease∠ |
| `MeshView.cs` | 292 | 454 | 352 | PieceSolver superset (+LIC); studio adds the crease overlay |
| `MainWindow.xaml.cs` | 1136 | 1088 | **1417** | effectively different code |

Only 3 small files are genuine dupes. `MainWindow.xaml.cs` now has **1417 diff
lines** on ~1100-line files — and studio's is even *larger* than PieceSolver's
after the proposer + Shine landed — so it is not reconcilable as peers. **Do not
line-merge it**; rebase studio's (culled) brush code **+ the proposer + Shine**
onto PieceSolver's window.

Disjoint features:
- **`studio/`-only:** brush system (101 `brush` refs in `MainWindow.xaml.cs`) +
  `Perlin.cs`; the **crease proposer** (`StartSolve` no-collapse flow +
  `MeshOps.EdgeDihedrals` + the crease GL_LINES overlay); **Shine shading**
  (neutral + environment matcap blend with a `Use Matcap` Advanced toggle).
- **`PieceSolver/`-only:** `Bff.cs`, `IsometricLM.cs`, `IsometricSmoothers.cs`,
  `RulingField.cs`, `NoiseVolume.cs` — the solver + the ruling/LIC viz.
  (`LicField.cs` was folded into `RulingField` + `MeshView`.)
- PieceSolver stays the bigger, more capable base; studio's value is now the
  **proposer + brush chassis + Shine**, all of which port onto it.

## Merge plan (port, not dedupe)

1. **Base = PieceSolver** (active, bigger, has the solver + ruling viz).
2. Delete `studio/`'s copies of the 3 true dupes; keep PieceSolver's.
3. **Port the brush chassis** from `studio/` per `BRUSH-SCOPE.md` (size/hotkeys/
   preview/stroke/falloff); cull all 14 experimental brushes; build the one
   Freeze brush. The engine hook (`FlowSession.BrushWeights`) is already shared.
4. **Port the crease proposer** (stage 2): studio's `StartSolve` (no-collapse
   Nesterov flow-in-place → `MeshOps.EdgeDihedrals` → label edges ≥ Crease∠) +
   the crease GL_LINES overlay + the live Crease∠ threshold. `EdgeDihedrals` is
   already in the shared engine (`src/MeshOps.cs`), so this is a UI/orchestration
   port, not engine work.
5. **Port Shine shading** — the neutral + environment matcap blend + the
   `Use Matcap` Advanced toggle into PieceSolver's `MeshView`. PieceSolver
   already bundles the CCC5C9 neutral matcap, so it's a small lift.
6. **Unify the command bus** — keep PieceSolver's `Journal`, fold in the single
   brush command + the proposer's command. Pre-consolidates the command layer the
   engine plan wants.
7. **Rename to the final scheme** (delete `studio/` first so the CreaseStudio
   name is free), then delete the fork — see *Naming end-state* below.

## Naming end-state

The "PieceSolver" name migrates **from the app down to the solver module**, and
the app inherits the "CreaseStudio" name. After the merge completes:

| Now | After | What it is |
|---|---|---|
| `studio/` (`CreaseStudio` assembly) | **deleted** | parallel fork — delete only after its proposer + brush chassis + Shine are ported out |
| `PieceSolver/` app (`PieceSolver.exe`) | **CreaseStudio** | the one consolidated app — dir / project / assembly / namespace / window title / `x:Class` all → CreaseStudio |
| `PieceSolver/IsometricLM.cs` (`IsometricLM`) | **PieceSolver** | the per-piece flatten/develop solver module *inside* CreaseStudio |

Order of operations to avoid a name clash: (a) delete the old `studio/`
CreaseStudio fork first (once its work is ported) so the name is unused; (b)
rename the `IsometricLM` solver → `PieceSolver`; (c) rename the app
(dir/project/assembly/namespace/titles) `PieceSolver` → `CreaseStudio`. This
realises the `AGENTS.md` direction — *"PieceSolver will become a module within
the eventual CreaseStudio"* — with PieceSolver now naming the solver module, not
the app.

## Then: engine boundary (after the merge)

Current state: there is **no engine library**. `src/CreaseMachine.csproj` is the
*Grasshopper plugin* (net48 → `.gha`). Every other front-end shares the engine by
**source-copying** (`<Compile Include="..\src\*.cs">`: cli 7, PieceSolver 8,
studio 7, repro 3); **zero `ProjectReference`s exist**. The command bus
(`StudioCommand` / `Journal` / `Execute`) is duplicated per app. One external
process seam already exists and works: BFF (`PieceSolver/Bff.cs` shells out to
`bff-command-line.exe`).

Staged boundary plan (each step enables the next):

1. **Extract `src/` → a `CreaseEngine` class library, multi-targeted
   `net48;net8.0`** (the Rhino plugin pins net48 and can never be containerised;
   the engine must multi-target). Replace `<Compile Include="..\src">` with
   `ProjectReference`. Define an explicit public API (`FlowSession`,
   `DevelopabilityEnergy`, `FlowParams`, `MeshIO`, `MeshOps`). Doing this
   **after** the app merge means only one app + cli + plugin + repro to repoint —
   not two apps.
2. **Lift the command bus into the engine** (`CreaseEngine.Commands`). It is
   already a latent protocol (commit `c7040ef`: "CLI consumes the shared `solve`
   command"); the journal is a request log, commands serialise naturally — this
   becomes the RPC/REST surface for free later.
3. **Containerise the headless path, where it pays** — a WPF/GL-free `crease`
   CLI (engine + command bus + BFF) for batch baking, CI gradient/perf gates,
   and cloud-offload solves. Wire format already exists (`MeshIO` / `FbxIO`).
   **Do not containerise the interactive app** (needs GPU + display).
4. **Network API / WASM only when there is a remote/web/multi-client need.** It
   sits on steps 1–2, so none of the work is throwaway.

Why a library, not a service, first: the engine is a hot in-process numeric
library (`FlowSession.NesterovStep` mutates a `PlanktonMesh` under a lock at
interactive rates; the ruling/LIC field recomputes per mesh change). Putting
HTTP/gRPC between the viewport and `FlowSession` would inject serialise + IPC
latency into the interactive loop. Package the engine (in-process API); reserve
the network / container boundary for the headless CLI.
