# CreaseMachine

A Grasshopper (Rhino 8) component — **SheetBender** — that flows a triangle mesh
toward a piecewise-developable (creasable) sheet using the covariance
("hinge") developability energy of **Stein, Grinspun & Crane**, *"Developability
of Triangle Meshes"* (ACM TOG 37(4), 2018).

The component does only three things: evaluate the developability energy and its
analytic gradient, flow the mesh down that gradient (Nesterov-accelerated
gradient descent), and — on demand — apply one 1→4 subdivision so creases can
sharpen at higher resolution. No remeshing, no projection, no smoothing.

## The `SheetBender` component

Category: **Kangaroo → Mesh**.

### Inputs

| Input | Nick | Type | Default | Meaning |
|-------|------|------|---------|---------|
| Mesh | Mesh | Mesh | — | Triangle mesh to develop. Connectivity is preserved (no remeshing); quads are triangulated on input. |
| Step | Step | Number | 0.05 | Step size as a fraction of edge length — the most-curved vertices move about this fraction of an edge per iteration. Applied internally as `Step·L²` so it behaves identically at any mesh scale and after Subdivide. ~0.05 descends cleanly; raise for speed, lower if the surface shimmers. Live-tunable. |
| Momentum | Mom | Number | 0.9 | Nesterov momentum (0–0.95). 0 = plain gradient descent; 0.9 reaches a developable state in roughly 5× fewer iterations. Higher is faster but lowers the stable Step ceiling. Resets on Reset and Subdivide. Live-tunable. |
| Iterations | Iter | Integer | 1 | Flow steps taken per solve. Connect a timer for continuous flow. |
| Subdivide | Subdiv | Boolean | false | Rising edge (false→true) applies one in-place 1→4 (midpoint) subdivision to the live mesh. Per the paper: subdivide after the flow settles to get hi-res creases, then keep flowing. |
| Reset | Reset | Boolean | true | True to (re)initialize from the input mesh, false to run. Connect a timer for continuous flow. |

### Output

| Output | Nick | Type | Meaning |
|--------|------|------|---------|
| Mesh | Mesh | Generic (Plankton mesh) | The developing mesh, as a `PlanktonMesh`. |
| Energy | Energy | Number (list) | Per-vertex developability energy (smaller eigenvalue of the 1-ring normal covariance), parallel to the mesh vertices. ~0 where developable, higher at residual non-developable spots (seam corners). Colour the mesh by it to inspect crease structure. |

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
- **Energy** is exposed per vertex so you can colour the mesh and inspect where
  curvature concentrates (seam corners stay hot; developed regions go to ~0).

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
gradient verification, developability classification, scale-invariance, and
momentum/collapse/degeneracy sanity.

```sh
dotnet build test/GradCheck.csproj -c Release
test/bin/Release/net48/GradCheck.exe
```

(`GradCheck.exe` is a net48 executable — run it directly, not via `dotnet`.)
Some diagnostics look for a hardcoded `C:\Temp\AboutToExplode.stl`; if it is
absent those lines are skipped — that is expected.

## Vendored dependencies

`lib/Plankton.dll` and `lib/PlanktonGh.dll` are stock, unmodified upstream
[Plankton](https://github.com/meshmash/Plankton) (0.4.3) by Daniel Piker and
Will Pearson. They are committed to this repo so the project builds without a
separate Plankton checkout.

## License — OPEN TODO

**No license has been chosen for this project yet.** This is deliberate and
must be resolved by the author before any distribution.

The developability energy and its gradient are **re-derived from the reference
implementation** accompanying Stein, Grinspun & Crane (2018), which is licensed
**GPL-v2**. That lineage has licensing implications for this repository that need
to be assessed before a license is selected. See `NOTICE.md` for full
attribution.
