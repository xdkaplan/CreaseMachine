# CreaseMachine — Agent Handoff

Context for an agent picking up this repo cold. The README covers *what the
component is and how to build it*; this doc covers *the current state, the open
problem, and the hard-won lessons that aren't obvious from the code.*

> **Why this file exists:** this repo was just split out from a working fork.
> A lot of the project's accumulated reasoning lived in the previous session's
> notes, which do **not** travel to this repo automatically. Everything load-
> bearing has been distilled below — read it before changing the energy,
> gradient, flow, or degeneracy handling.

---

## 1. What this is

**SheetBender** — a Grasshopper (Rhino 8) component that flows a triangle mesh
toward a piecewise-developable ("creasable") sheet, implementing the covariance
developability energy of **Stein, Grinspun & Crane, "Developability of Triangle
Meshes" (ACM TOG 37(4), 2018)** on the Plankton half-edge library. It evaluates
the energy + its **analytic** gradient, flows down the gradient (Nesterov), and
on demand applies one 1→4 subdivision. No remeshing, no projection, no
smoothing. Motivating use case: turn a smooth geodesic sphere ("baseball") into
clean creased developable panels.

**Status:** the core works and is the clean, paper-faithful version the author
wanted. The one big unsolved thing is **sub-panel crazing** (§5).

## 2. Orientation

| File | Role | Rhino types? |
|------|------|--------------|
| `src/SheetBender.cs` | The GH component (I/O, the flow loop, subdivision). | yes |
| `src/DevelopabilityEnergy.cs` | Energy + analytic gradient + per-vertex `VertexEnergy`. The math core. | **no** |
| `src/MeshOps.cs` | Manifold-safe cleanup: `CollapseShortEdges`, `CollapseFolds`. | **no** |
| `src/Vec3.cs` | Minimal double-precision vector (mirrors Rhino's `Vector3d` ops). | **no** |
| `test/Program.cs` | The bench: finite-difference gradient checker + flow/scale/degeneracy sanity. | **no** |
| `lib/Plankton*.dll` | Upstream Plankton 0.4.3 (vendored, unmodified). | — |

The three core files are deliberately **Rhino-free** so the bench can compile
them directly without Rhino. **Keep it that way** — Rhino types belong only in
`SheetBender.cs`.

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
classification. The analytic-energy line shows `maxRel ≈ 45% -> FAIL`; that is a
**known artifact** of finite-difference-checking a non-smooth (eigenvalue-kink)
energy, *not* a real error. Flow tests should show energy descending
monotonically (e.g. 2.36 → 0.04). Some diagnostics look for hardcoded
`C:\Temp\*.stl` files; if absent those lines self-skip — expected.

## 4. How it works (enough to be dangerous)

- **Energy.** Per vertex, build the normal covariance `A = Σ_f θ_f N_f N_fᵀ`
  over incident faces (θ = corner angle weight). Developability energy = the
  **smaller tangent eigenvalue** of `A`. It is 0 iff the 1-ring face normals lie
  on a great circle (a hinge / developable vertex).
- **The eigensolve.** The area-weighted vertex normal `Nv` is the *exact* null
  vector of `A`. So the smaller eigenvalue (and its eigenvector) live in the
  **2×2 tangent block** of `A` in the plane ⊥ `Nv` — solved in closed form
  (`MinTangentEigenpair`). Do **not** go back to a general 3×3 eigensolver
  (see §6, lesson 2).
- **Gradient (analytic, re-derived from the paper).** `factorf` (face-normal
  derivative, carries `phi/sinPhi`) + `factorv` (vertex-normal derivative,
  carries `phi/tanPhi`, divided by `|rawNormal|`), all reducing to `Vec3`
  dot/cross. No matrix class.
- **Optimizer (our choice, not the paper's).** Nesterov-accelerated gradient
  descent: sample grad at the lookahead `x + β·v`, then `v = β·v − t·grad`,
  `x += v`. **Raw** gradient (no normalization), `t = Step·L²`, velocity capped
  at one edge length. β≈0.9 ≈ 5× faster than plain descent. Boundaries held.

## 5. THE OPEN PROBLEM — sub-panel crazing

The flow produces developable panels, but **inside** a panel that should be one
smooth developable region it grows spurious high-frequency creases — *crazing* —
which we want gone, while keeping the **true seams** between panels sharp. Goal:
smooth *within* panels, sharp *at* seams.

**This is the paper's own central problem** (they call it "crumpling"), and they
admit it is worst and ill-posed in **exactly our case**: near-spherical regions
(κ₁≈κ₂, no preferred ruling direction — a baseball). §4.5 / §6: "crumpled
solutions arbitrarily close"; rulings aren't clean "except on fairly simple
meshes." Their levers are all **prevention**, and we have not used most of them:
1. **Input tessellation** decides the seams — for a sphere you break the
   symmetry with the initial mesh (Fig 2). *Most paper-faithful untried lever.*
2. **Intrinsic energy** (sphere exp-map, §4.1.2 / App B.5) reduces spikes.
3. **λ_max** (max instead of sum, Eq 6) straightens rulings / kills branching.
4. **L-BFGS** (not our Nesterov).
The paper has **no cure once crazed** — it avoids crazed minima by construction.

**Discriminators tried and ruled out** (all leak — crazes and seams overlap on
each): per-edge **angle** (a craze can share a seam's defect angle); crease-curve
**connectivity / length** (fooled by junctions, collinear chords, crazes that run
parallel to the panel outline); patch **size** (merges legitimate small end-caps);
per-vertex **energy magnitude** (~2× separation at best); **local-energy-peak**
(seam *corners* are energy peaks too). **Coarse-to-fine prevention was tested by
the author and does NOT fix it** — don't re-suggest it.

**The reframe that still looks right:** craze-vs-seam may be undecidable
*statically* but is decidable *dynamically*. On an intrinsically-flat panel a
clean developable has energy ≈ 0, so a craze is **removable** energy (clean is
strictly lower) trapped in a local minimum. A true seam is **irreducible** — a
closed surface owes 4π of Gaussian curvature (Gauss–Bonnet) that must live on
seams / cone points; no perturbation removes it. A process that merely lowers
energy therefore drains crazes and *cannot* touch seams.

**Thermal-noise idea — prototyped, bench-validated, then deliberately ROLLED
BACK (not in the code):** add a concentration-gated normal jitter ("Temperature")
that kicks isolated energy peaks (crazes) out of their local minima; the dev
flow is the relaxation that recaptures the clean basin; the user is the cooling
schedule. The gate fired correctly on the bench (craze peaks high, seam ridges
and flat regions ~0) but it was pulled to keep the core clean and rethink the
approach. If you revisit it: the working **ridge-aware gate** was
`max(0, energy − MAX-neighbour-energy) / (energy + 0.001·maxEnergy)`, clamped to
[0,1] — key on the **max** neighbour (the *mean* wrongly flags seam corners) and
**relative** to the vertex's own energy (crazes run ~50× lower energy than seams,
so an absolute-scaled gate is backwards). Full Metropolis accept/reject was
judged unnecessary (the dev flow is the accept step).

**Where it stands:** the author rejected "it's just the tessellation / an
accepted limitation" as too tidy, and pulled the thermal-noise complexity back
to rethink from a clean base. **Open and actively being reconsidered.** Most
promising untried directions: seeding the input tessellation, and the paper's
λ_max / intrinsic energies.

## 6. Hard-won lessons (do NOT relearn these the hard way)

1. **The dev step must be RAW-proportional.** Never normalize the step by the
   median/max gradient — it *diverges* (bench: 2.36 → 29 normalized, vs
   2.36 → 0.04 monotonic raw). The self-damping that makes the flow settle comes
   precisely from the raw gradient vanishing near the minimum.
2. **Energy + eigenvector come from the 2×2 tangent block, never a 3×3
   eigensolve.** An earlier "gradient bug" (a handful of bad vertices) was a
   fragile 3×3 eigenvector/energy selection, *not* a derivation error. Using the
   tangent block (`Nv` is the known null vector) fixed it cleanly.
3. **Step is `Step·L²`, not `Step·L`.** L² makes it invariant to mesh *scale*
   **and** to Subdivide (which halves L and doubles the 1/length gradient).
   Verified across 1×/10× scale and across subdivision.
4. **Four degeneracy guards are all load-bearing** (without them a degenerate
   triangle's 1/area term spikes ~26000× and corrupts the mesh): **sliver**
   (face aspect < 1%), **fold** (vertex-normal coherence `|rawNormal|/ΣdA < 0.1`),
   **inverted-face** (`cosPhi = Nv·Nf < −0.85`, i.e. phi > ~148°), and the
   **velocity cap** (≤ 1 edge). Plus short-edge collapse and a coherence-<0.05
   fold-heal collapse (both via Plankton's manifold-safe `CollapseEdge`). A
   *legitimate* convex sharp edge tops out ~106° from `Nv` and is preserved;
   only a surface folding under itself exceeds 148°.
5. **Performance budget is real:** the inner ops run on the order of 10⁵×
   per frame at ~10 fps during interactive flow. Keep everything 1-ring-local;
   avoid per-vertex allocations in hot paths.
6. **The reference implementation is GPL-v2; the project has no license yet.**
   Re-derive math from the **paper**, do not copy reference code. See
   `NOTICE.md` and the README's "License — OPEN TODO". Reference impl (math
   reference only): `C:\Repo\odedstein\DevelopabilityOfTriangleMeshes`
   (`hinge_energy.cpp`, `triangle_dTheta.cpp`, `triangle_dN.cpp`).

## 7. Deliberately dropped (don't re-add without a reason)

- **Unbend / `CreaseBlendForce`** — a crease-blend de-craze lever. It smooths,
  then the dev force rebuilds the same creases. Found unhelpful; removed.
- **`MeshEdgeAngle`** — an edge-angle diagnostic component. Its purpose (testing
  whether angle separates craze from seam) is spent.
- **Thermal noise / `Temperature`** — see §5. Prototyped, validated, rolled back.
- **The old MeshMachine `remesher` node flow** — never extracted into this repo
  (see `NOTICE.md`).

## 8. Conventions

- Run the bench after touching energy/gradient/flow/degeneracy code — it is the
  regression net, and it is fast.
- Keep `DevelopabilityEnergy` / `MeshOps` / `Vec3` Rhino-free.
- The component GUID is `078039c1-4b2e-4e4f-8c72-e909a9b5c8f7`; keep it stable so
  existing Grasshopper definitions keep their wiring.
- Prior working fork (legacy, being culled — not this repo):
  `C:\Repo\xdkaplan\CreaseMachine`.
