# App consolidation — `studio/` + `PieceSolver/` → CreaseStudio

Status: findings + sequencing for the merge. Companion to
[`BRUSH-SCOPE.md`](BRUSH-SCOPE.md) (the brush part of the merge) and the engine
boundary plan (below).

## TL;DR

- **`PieceSolver/` is the active app** (`AGENTS.md` "In-process app —
  PieceSolver/"); **`studio/` is a stale earlier fork**, frozen since the
  `patchsolver/ → PieceSolver/` rename (commit `fed8043`). No commits touch
  `studio/` after the rename.
- They are **not symmetric dupes** — they share a WPF + OpenTK *shell idiom* but
  have **drifted hard** and have **disjoint feature sets**. So the merge is a
  **port, not a dedupe**: adopt PieceSolver as the base, port `studio/`'s brush
  *chassis* (per `BRUSH-SCOPE.md`), delete `studio/`.
- **Sequence: merge the apps first, then extract the engine library.** Doing the
  engine extraction first would mean plumbing a library into `studio/`, an app
  about to be deleted.

## How "dupe" they actually are (line drift)

| File | studio | PieceSolver | diff lines | verdict |
|---|--:|--:|--:|---|
| `Converters.cs` | 36 | 36 | 2 | true dupe — delete one |
| `GroundGrid.cs` | 95 | 95 | 2 | true dupe — delete one |
| `App.xaml.cs` | 6 | 6 | 2 | true dupe — delete one |
| `Journal.cs` | 95 | 150 | 67 | drifted (command bus) |
| `SimSettings.cs` | 84 | 218 | 164 | PieceSolver superset |
| `MeshView.cs` | 230 | 465 | 253 | PieceSolver superset (+LIC) |
| `MainWindow.xaml.cs` | 1053 | 1156 | **1259** | effectively different code |

Only 3 small files are genuine dupes. `MainWindow.xaml.cs` has **1259 diff lines**
on ~1100-line files — not reconcilable as peers. **Do not line-merge it**; rebase
`studio/`'s (culled) brush code onto PieceSolver's window.

Disjoint features:
- **`studio/`-only:** brush system (101 `brush` refs in `MainWindow.xaml.cs`) +
  `Perlin.cs`.
- **`PieceSolver/`-only:** `Bff.cs`, `IsometricLM.cs`, `IsometricSmoothers.cs`,
  `RulingField.cs`, `LicField.cs`, `NoiseVolume.cs` (the solver + the LIC viz).
- Totals: `studio/` ≈ 2096 app lines, `PieceSolver/` ≈ 3648 — PieceSolver is the
  bigger, more capable app.

## Merge plan (port, not dedupe)

1. **Base = PieceSolver** (active, bigger, has LIC + solver).
2. Delete `studio/`'s copies of the 3 true dupes; keep PieceSolver's.
3. **Port the brush chassis** from `studio/` per `BRUSH-SCOPE.md` (size/hotkeys/
   preview/stroke/falloff); cull all 14 experimental brushes; build the one
   Freeze brush. The engine hook (`FlowSession.BrushWeights`) is already shared.
4. **Unify the command bus** — keep PieceSolver's `Journal`, fold in the (single)
   brush command. This pre-consolidates the command layer the engine plan wants.
5. Rename `PieceSolver/` → CreaseStudio; delete `studio/`.

## Then: engine boundary (after the merge)

Current state: there is **no engine library**. `src/CreaseMachine.csproj` is the
*Grasshopper plugin* (net48 → `.gha`). Every other front-end shares the engine by
**source-copying** (`<Compile Include="..\src\*.cs">`: cli 7, PieceSolver 8,
studio 6, repro 3); **zero `ProjectReference`s exist**. The command bus
(`StudioCommand` / `Journal` / `Execute`) is duplicated per app. One external
process seam already exists and works: BFF (`PieceSolver/Bff.cs` shells out to
`bff-command-line.exe`).

Staged boundary plan (each step enables the next):

1. **Extract `src/` → a `CreaseEngine` class library, multi-targeted
   `net48;net8.0`** (the Rhino plugin pins net48 and can never be containerised;
   the engine must multi-target). Replace `<Compile Include="..\src">` with
   `ProjectReference`. Define an explicit public API (`FlowSession`,
   `DevelopabilityEnergy`, `FlowParams`, `MeshIO`). Doing this **after** the app
   merge means only one app + cli + plugin + repro to repoint — not two apps.
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
interactive rates; the LIC field recomputes per mesh change). Putting HTTP/gRPC
between the viewport and `FlowSession` would inject serialise + IPC latency into
the interactive loop. Package the engine (in-process API); reserve the network /
container boundary for the headless CLI.
