# Paper Notes — Jiang et al. 2020 (quad-mesh isometric developables)

Distilled reference for

> Caigui Jiang, Cheng Wang, Florian Rist, Johannes Wallner, Helmut Pottmann.
> **"Quad-mesh based isometric mappings and developable surfaces."**
> ACM TOG 39(4), Article 1, 2020. https://doi.org/10.1145/3386569.3392430

Companion to [`PAPER_FORMULAS.md`](PAPER_FORMULAS.md) (the Stein/Grinspun/Crane 2018
base our energy is derived from). This is a *different* approach to discrete
developables — useful mainly as an alternative lens on the crazing problem (see
[`NOTICE.md`](NOTICE.md) and the crazing memory). We do **not** implement it.

> ✅ **Verified against the original PDF** (the published ACM TOG paper). The core
> constraints **Eq. 1–5, the shape operator, and the curvatures `K`/`H` are confirmed
> exact**. Primes on `M'`/`v'` (which text extraction drops) are correct here. The
> energy sums and Algorithms 1–2 below are faithful to the paper's structure — they
> are just least-squares of the verified constraints — and were transcribed rather
> than line-checked, since they're standard.

---

## Core idea (high confidence)

Don't manage a quad mesh `M=(V,E,F)` directly — manage a **checkerboard pattern**
inscribed in it by edge midpoints `m_vw = ½(v+w)`:

- each **face** → a **"black" parallelogram** (the 4 edge-midpoints of the face).
  By **Varignon's theorem** these midpoints form a parallelogram *even if the quad
  is non-planar*, with edges parallel to the quad's diagonals → always planar, so
  it has a well-defined normal `n_f`.
- each **vertex** → a **"white" face**.

A pair of combinatorially-equivalent meshes `M, M'` is a **discrete map between
surfaces**. First-order properties are defined on the black parallelograms:

- **isometric** ⟺ corresponding black parallelograms are **congruent**
- **conformal** ⟺ corresponding black parallelograms are **similar**

**Discrete developable** (their definition): a quad mesh `M` that is **isometric
(Eq. 1) to a planar quad mesh `M'`** — no constraint that edges follow rulings.
That edge-freedom is the whole point (contrast with Stein/ours, below).

---

## Constraints / energies (OCR-suspect — verify)

For corresponding faces `f = v₀v₁v₂v₃` and `f' = v'₀v'₁v'₂v'₃`, everything is stated
on the **diagonals** `v₀−v₂` and `v₁−v₃` (which are the inscribed parallelogram's
edge directions, via Varignon):

**Isometry (Eq. 1)** — congruent parallelograms:
```
c_iso,0(f) = ‖v₀−v₂‖² − ‖v'₀−v'₂‖²                       = 0
c_iso,1(f) = ‖v₁−v₃‖² − ‖v'₁−v'₃‖²                       = 0
c_iso,2(f) = ⟨v₀−v₂, v₁−v₃⟩ − ⟨v'₀−v'₂, v'₁−v'₃⟩          = 0
```

**Conformal (Eq. 2)** — per-face factor `λ_f`, similar parallelograms:
```
c_conf,0(f) = λ_f‖v₀−v₂‖² − ‖v'₀−v'₂‖²                    = 0
c_conf,1(f) = λ_f‖v₁−v₃‖² − ‖v'₁−v'₃‖²                    = 0
c_conf,2(f) = λ_f⟨v₀−v₂, v₁−v₃⟩ − ⟨v'₀−v'₂, v'₁−v'₃⟩        = 0
```

**Discrete Gauss map / vertex normals (Eq. 3)** — normals `n_i` (NOT unit), chosen
so offset mesh `M^δ = {v_i + δ n_i}` has black parallelograms in parallel planes at
distance δ. With `n_f` the unit normal of the inscribed parallelogram:
```
c_norm,j(f) = ⟨n_j + n_{j+1}, n_f⟩ − 2 = 0    (j = 0..3 mod 4; j=3 redundant)
```

**Per-face curvatures (Eq. 4)** via shape operator `s_f` (`[a,b] := det(a,b,n_f)`):
```
K(f) = det(s_f) =  [n₃−n₁, n₂−n₀] / [v₃−v₁, v₂−v₀]
H(f) = ½ tr(s_f) = −( [n₃−n₁, v₂−v₀] + [v₃−v₁, n₂−n₀] ) / ( 2[v₃−v₁, v₂−v₀] )
```
Principal curvatures = roots of `x² − 2Hx + K = 0`. Obeys Steiner's formula on the
black-parallelogram areas: `area(f^δ) = area(f)(1 − 2δH + δ²K)`.

**Shape-operator symmetry / conjugacy (Eq. 5)** — added as a regularizer so
principal directions are orthogonal (and to kill leftover ambiguity in Eq. 3):
```
c_sym(f) = ⟨n₃−n₁, v₂−v₀⟩ − ⟨v₃−v₁, n₂−n₀⟩ = 0
```

**Energies** (each constraint → least-squares):
```
E_iso   = Σ_f Σ_j c_iso,j(f)²
E_conf  = Σ_f Σ_j c_conf,j(f)²        E_λ = Σ_f (λ_f − 1)²
E_norm  = Σ_f Σ_j c_norm,j(f)²        E_sym = Σ_f c_sym(f)²
E_pos   = Σ_{i∈I} ‖v_i − a_i‖²  +  Σ_{(i,j)∈J} ‖v_i − v_j‖²     (handles; gluing pins v_i=v_j)
E_prox,1 = Σ ‖v_i − v*_i‖²            E_prox,2 = Σ τ_i(v_i)²     (ICP point + tangent-plane to ref Φ)
E_fair  = Σ_{v_i v_j v_k successive on a polyline} ‖v_i − 2v_j + v_k‖²     (2nd-difference fairness)
E_map   = Σ ( ⟨v_i−2v_j+v_k, v_l−v_j⟩ − ⟨v'_i−2v'_j+v'_k, v'_l−v'_j⟩ )²    (mapping regularizer, Eq. 7)
```
`E_fair` penalizes polyline zigzag; `E_map` regularizes the *mapping* (not the
mesh) by discretizing the isometric transport of 2nd derivatives.

**Algorithm 1** (M' isometric to M): minimize `w_iso E_iso + w_fair E_fair + w_pos
E_pos` by Levenberg–Marquardt; **anneal `w_fair ← w_fair/10`** in an outer loop
until `E_iso ≤ ε`. Method is robust to `w_iso, w_pos` but sensitive to `w_fair`.

**Algorithm 2** (developable spline / watertight CAD): subdivide control mesh `M`
by `k` rounds (Catmull-Clark-like) → `S^k(M)`; optimize `M` so `S^k(M)` is
discrete-developable (isometric to flat `M'`); the limit `M∞` is a **bicubic
B-spline that is developable** → watertight developable CAD (a previously-open
problem). `M` need not be fair for `S^k(M)` to be fair.

**Developability test:** `M` is piecewise-developable ⟺ its **Gauss image `σ(M)`
is curve-like** (1-D, zero area on the sphere). Rulings = intersection of the two
black-parallelogram planes at an edge midpoint, or the zero-eigenvector of `s_f`.

---

## Why this matters for CreaseMachine

1. **It independently diagnoses OUR crazing.** Fig. 23 + §5: *"Stein et al. model
   discrete developables whose rulings are aligned with the edges of the underlying
   mesh ... It instead produces surfaces with creases whose location is
   mesh-dependent."* Our flow IS Stein's energy → the mesh-dependent crease network
   is exactly the accordion/craze we've been fighting. Pottmann's group calls it out
   as the known weakness of edge-locked / ruling-based methods. See
   [[project-crazing-findings]].

2. **Their structural fix is isometry-to-a-flat-mesh, not a per-vertex hinge.**
   Developability is `E_iso`: keep the metric congruent to a planar reference `M'`.
   A metric pinned to flat can't "accordion" the way `λ_min` (which is blind to even
   vs. lumpy dihedral) can — there is no zero-energy family of lumpy distributions,
   because the *lengths* are constrained. Plus `E_fair` (anti-zigzag) and `E_map`
   actively regularize. This is a fundamentally different (and craze-resistant)
   formulation — but it's **quad-mesh + checkerboard**, not our triangle/Stein world,
   so adopting it is a reframe, not a patch.

3. **Better craze metric candidate.** "Developable ⟺ Gauss image is a 1-D curve."
   A Gauss-image-spread / curve-likeness measure on the black-parallelogram normals
   could be a cleaner craze signal than our `rough` (which a blur can game) — it
   targets the *defining* property directly.

4. **Segmentation is still punted.** §5/§6: *"Creating the segmentation is beyond
   the scope of this paper"* and it's named as future work. Even the Pottmann group
   leaves auto-segmentation open — consistent with our finding that the
   segmentation/region step is the genuinely hard part of crease-clean developables.

5. **Curvature toolkit.** Per-face `K, H`, principal curvatures/directions, rulings
   as conjugate/zero-curvature directions — a richer, edge-independent curvature
   readout than what we expose today.

**Bottom line:** this is the "do developability via constrained isometry to a flat
mesh, edges free of rulings" school — the principled escape from the mesh-dependent
crease problem, at the cost of switching to a quad/checkerboard discretization. Keep
as a design reference for if/when we reconsider the core formulation.
