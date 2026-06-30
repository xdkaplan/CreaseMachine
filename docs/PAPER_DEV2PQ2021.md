# Paper Notes — Verhoeven et al. 2021 (Dev2PQ: ruling-aligned PQ-strip remeshing)

Transcribed reference for

> Floor Verhoeven, Amir Vaxman, Tim Hoffmann, Olga Sorkine-Hornung.
> **"Dev2PQ: Planar Quadrilateral Strip Remeshing of Developable Surfaces."**
> arXiv:2103.00239v1 [cs.GR], 27 Feb 2021. https://arxiv.org/abs/2103.00239

Sibling to [`PAPER_JIANG2020.md`](PAPER_JIANG2020.md) (quad-mesh isometric developables)
and [`PAPER_FORMULAS.md`](PAPER_FORMULAS.md) (Stein/Grinspun/Crane 2018 — the base our
covariance energy derives from). This is a **remeshing / fabrication-output** method, not a
develop-the-shape method: it takes a triangle mesh that already *is* (piecewise) developable
and re-tiles it into curvature-aligned **planar-quad (PQ) strips** whose interior edges are
the rulings. We do **not** implement it; it is the most directly relevant published "rulings →
flat panels" pipeline for a future fabrication-export stage (see [Relevance](#relevance-to-creasemachine)).

> ✅ **All numbered equations (Eq. 1–25, incl. Eq. 6) verified** against PDF page screenshots
> (provided 2026-06-27) — notation and equation forms corrected to match the paper exactly (the
> original draft of this doc was reconstructed from `pdftotext`, which had stripped the math
> symbols; that introduced wrong notation — `φ/u/ρ/q/c(f)/α,β,γ` and a guessed *logistic* for
> Eq. 6 — now fixed to the paper's `u/γ/s/Γ/w(f)/ω_a,ω_d,ω_s` and the actual Gaussian-ramp Eq. 6).
> ⚠️ **One item remains text-reconstructed:** the unnumbered **discrete-gradient** expression in
> §3 (boilerplate from [Brandt et al. 2017]), flagged inline *(reconstructed — verify)*.
> Algorithm 1's *step ordering* is from the text layer, but every sub-solve it calls (Eq. 20–25)
> is verified.

### Notation (the paper's, as verified)

| Symbol | Meaning |
|---|---|
| `u` | the **scalar field** being designed; its level sets are the rulings |
| `w` | the dependent orthogonal coordinate (generating-curve direction) — *not* a confidence |
| `p(u)`, `r(u)` | generating curve / ruling direction in the parameterization (Eq. 1) |
| `γ` | the unit **2-direction field** (defined up to sign), `γ ∥ ∇u`, `γ = ∇u/‖∇u‖` |
| `γ⊥` | `γ` rotated 90° (the ruling-aligned direction) |
| `s(p) > 0` | the **density** of `u`'s level sets; `sγ` is the integrable (curl-free) field |
| `Γ = γ²` | the power representation of `γ` (sign-invariant, complex) |
| `R`, `R⊥` | the ruling power field and its 90° rotation (the alignment target) |
| `w(f) ∈ [0, 0.8]` | per-face **confidence** weight; `m(f)` face area; `m(e)` edge mass |
| `ω_a, ω_d, ω_s` | scalar weights for the align / div / smooth energies (Eq. 16) |
| `μ_a, μ_s` | step-size normalizers (lowest nonzero generalized eigenvalues) in Eq. 20–21 |
| `γ_u, γ_d, γ_c` | the iterate after normalize / after div-free projection / after curl-free projection |

---

## TL;DR

- **Input:** a triangle mesh of a (piecewise) developable surface — *not* necessarily aligned to
  curvature, e.g. a 3D scan or the output of freeform developable modeling.
- **Output:** a curvature-aligned **PQ-strip mesh** — long planar quad strips in the curved
  (torsal) regions whose chordal edges are **exact straight rulings**, plus big planar polygons
  for the flat regions. Curved folds and creases handled without explicit segmentation.
- **How:** fit a scalar field `u` whose level sets are straight and align to the locally-estimated
  rulings. The straight-level-set condition = *the normalized gradient of `u` is divergence-free*
  (`div(∇u/‖∇u‖)=0`). This is nonlinear/high-order, so they **factor it** into (a) a directional-
  field optimization (for `γ`) and (b) fitting the density `s` / integrating to `u` — making it
  tractable.
- **Key trick:** design only one coordinate (`u`, the ruling foliation); let the orthogonal
  coordinate `w` be dependent. Singularities, flat regions, and curved folds emerge automatically
  from the field design — no manual domain decomposition.

---

## 1. Background — developable surfaces as conjugate nets (§3)

A `C²` surface with vanishing Gauss curvature everywhere is a **smooth developable**. A general
developable is a union of patches `S = ∪ Sᵢ`, each either:

- a **torsal patch** — a curved ruled surface with **constant normal along each ruling**, or
- a **planar patch** — vanishing mean curvature, bounded by rulings of torsal patches and the
  surface boundary.

**Non-smooth developables** they also handle:
- **Creases / curved folds** — smooth developable pieces joined along curves with only `C⁰`
  continuity [Huffman 1976]. ("Curved fold" = globally isometric to a plane; "crease" = not.
  They treat both identically and call them *creases*.)
- **Point singularities** (cone apexes, index 1) — locally non-developable. They **remove the
  apex vertex + incident faces** before running, optionally re-adding in post.

### Conjugate-net parameterization (Eq. 1) ✅

A torsal patch `Sᵢ` is parameterized in coordinates `(u, w)` as

```
Sᵢ(u, w) = p(u) + w · r(u)                                          (1)
```

- `p(u): ℝ → ℝ³` — a **generating curve**.
- `r(u): ℝ → S²` — the **ruling direction**.
- Fixing `u` and sweeping `w` traces a straight line → the **ruling**. So **the level sets of `u`
  are the rulings.** Developability ⟺ the Gauss map is constant along each ruling.

The rulings are the **minimum-curvature lines**. Choosing `p(u)` as a max-curvature line makes
`(u,w)` a **principal curvature-line parameterization** (rulings ⟂ generating curves). Enforcing
`u` continuous across patches (and allowing singularities to emerge) gives a single **conjugate
net** for the whole surface; singularities, on a `C²` surface, must sit in **planar regions**.

**Order of symmetry.** They design the **2²-symmetric** variant (the `u` and `w` roles stay
distinct), not the 4-symmetric "let the two coords intermix" used for generic PQ meshing
[Diamanti 2014; Liu 2011]. **They design only `u`** — the ruling foliation — and let `w` be
dependent.

---

## 2. Ruling fields — the core characterization (§3.2) ✅

`γ` is a unit tangent **2-direction field** (defined up to sign). Align it with `r⊥` (the ruling
direction rotated 90°), so that it is parallel to the gradient of the scalar field:

```
∇u ∥ γ                                                              (2)
```

Away from singularities and creases, the straight-level-set condition is then exactly that `γ` is
a **divergence-free unit field**:

```
∇·γ = ∇·( ∇u / ‖∇u‖ ) = 0                                          (3)
```

(`γ` is divergence-free because the level sets of `u` are rulings — extrinsically flat geodesics
on the developable, so they have zero geodesic curvature, and `κ_g = div(∇u/‖∇u‖)`.)

**Integrability up to a scalar (Eq. 4).** To recover `u` from `γ`, there must exist a positive
**density** `s(p) > 0` making `sγ` curl-free (a gradient):

```
∇ × ( s · γ ) = 0,     s(p) > 0   ∀p ∈ S                            (4)
```

`s` is the **density of `u`'s level sets** — it varies naturally where rulings fan out (e.g. a
cone). So the design splits: find a div-free unit field `γ`, and a density `s` making `sγ`
integrable; then integrate to `u`.

**Ruling-alignment via the second fundamental form (Eq. 5).** Being a geodesic field is *not
sufficient* — rulings are extrinsic (they depend on the shape operator), so `γ` must be pinned to
the actual rulings. With second fundamental form `II`:

```
∀p ∈ S,    II( γ⊥(p), γ⊥(p) ) = 0                                   (5)
```

**Singularities & combing.** `γ` is a 2-direction field (defined up to sign), so divergence/curl
need a local *combing* (consistent sign choice) to apply. Singularity indices are integer
multiples of **½**. Only two singularity types occur: **(a)** cone apexes (index 1, removed up
front); **(b)** singularities **inside planar regions** (index ½), reconciling neighboring torsal
patches' orientations. Level sets bend near planar-region singularities — harmless, since planar
regions become one big polygon anyway.

**Creases.** Rulings on the two patches meeting at a crease generally meet at an angle, not a
straight line. So they **do not require `γ` to be divergence-free near creases** — the field is
allowed to *break* across them.

---

## 3. Discretization (§3.3, §4)

Input: triangle mesh `M = (V, E, F)`. `u` is a **piecewise-linear vertex-based function** `u(v),
v ∈ V`; consequently `r`, `r⊥`, and `∇u` are **face-based piecewise-constant** tangent fields
(this space denoted `X`; the unit field `γ ∥ ∇u` lives here too). The mesh is uniformly scaled so
**average edge length = 1**.

Operators (from [Brandt et al. 2017]): conforming gradient `G: V → X`, divergence `D: X → V`,
non-conforming curl `C: X → E`. For a triangle `f = (i,j,k)` with area `m(f)`:

```
(G u)(f) = (1 / 2 m(f)) · ( u_i · eᵢ^⊥ + u_j · eⱼ^⊥ + u_k · eₖ^⊥ )   (reconstructed — verify)
```

A discrete PQ mesh sampled from the level sets of a conjugate net has faces planar **to second
order** [Liu et al. 2006]; curvature-line sampling gives **circular** quads (discrete
curvature-line nets [Bobenko–Suris]). Torsal patches → **PQ strips** (each quad = two boundary
curve segments + two straight rulings). Planar patches → **big flat polygons** (the non-straight
level sets there lie in the plane, so they can be straightened freely). If the singularity-index
sum in a planar region is `s`, the polygon has `4 + 2s` sides (e.g. an index-½ singularity → a
hexagon).

### Ruling estimation (§4)

Per face `f`, the ruling `r(f)` = eigenvector of the **minimum eigenvalue of the face shape
operator** `S(f)` [De Goes et al. 2020]. Known only up to sign → use a **power representation**
[Knöppel 2013; Azencot 2017]: write `r(f)` as a complex number in a local frame and **square it**
to kill the sign:

```
R(f)  = r(f)²                          (sign-invariant ruling power field)
R⊥(f) = ( r(f)⊥ )²                     (the same, rotated 90° — the alignment target in Eq. 7)
```

### Confidence weights (Eq. 6) ✅

Rulings are least reliable in planar/near-planar regions (the min/max curvatures are close and
noisy) and most reliable in strongly-curved regions. A per-face **relative confidence** `w(f) ∈
[0, 0.8]` is attached to each face as a function of the discrete absolute max/min curvatures
`κ₁(f), κ₂(f)` (the absolute largest/smallest eigenvalues of the shape operator `S(f)`):

```
w(f) = θ₁ · ( 1 − exp( θ₂ · ( θ₃ · ( κ₁(f) − κ₂(f) ) )² ) ) ,   θ₁ = 0.8,  θ₂ = −0.9,  θ₃ = 5   (6)
```

This is a Gaussian-type ramp (the paper's prose calls it a "logistic curve"): when `κ₁ ≈ κ₂`
(planar / near-planar) the squared term → 0, so `w(f) → 0`; as the curvatures separate (strongly
curved) it saturates at `θ₁ = 0.8`. The cap **0.8** is effectively reached once `κ₁ − κ₂ ≥ 0.5`
(then `exp(−0.9·(5·0.5)²) = exp(−5.625) ≈ 0.004`), so the method **never fully trusts** a ruling.
Boundary faces and crease faces are set to `w(f) = 0`.

**Crease detection** (optional): collect edges whose adjacent face normals differ by more than a
user threshold; zero the confidence of faces incident to crease vertices. Creases may instead be
prescribed by the user.

---

## 4. Optimization (§5)

Precompute per face: shape operator `S(f)`, ruling `r(f)`, the power fields `R(f), R⊥(f)`, and
confidences `w(f)`. Mass matrices: `M_X` (face areas) and `M_E` (edge masses, each = half the
summed dual-edge lengths). Unknowns: the unit field `γ(f)`, its power representation `Γ(f) =
γ(f)²`, the density `s(f)`, and (after integration) `u`.

### Energy terms ✅ (Eq. 7–15 verified)

**Alignment (Eq. 7–8)** — pull the power field `Γ` to the rotated ruling power field `R⊥`,
weighted by area and confidence:

```
E_a(Γ) = Σ_{f∈F}  m(f) · w(f) · ‖ Γ(f) − R⊥(f) ‖²                    (7)
       = ( Γ − R⊥ )ᴴ  M_X W_X  ( Γ − R⊥ )                           (8)
```

`W_X` = diagonal of per-face confidences; `Γ, R⊥` are `|F|×1` complex; note the conjugate
transpose `ᴴ`.

**Unit-norm, divergence-free (Eq. 9)** — a **Ginzburg–Landau** term [Viertel–Osting 2019;
Sageman-Furnas 2019]:

```
E_d(γ) = Σ_{v∈V} ‖ D γ(v) ‖²  +  (1 / ε²) · Σ_{f∈F} ( ‖γ(f)‖² − 1 )²    (9)
```

*(The PDF render of the double-well term is small; the squared `(‖γ‖²−1)²` form is the standard
Ginzburg–Landau double-well [Viertel–Osting 2019] and is what drives `‖γ‖→1`. Coefficient is
`1/ε²`.)* As `ε → 0`, this minimizes the divergence of a unit-norm field after excising radius-`ε`
balls around singularities → it **naturally locates singularities inside planar regions**.

**Smoothness (Eq. 10–12)** — power-field smoothness across each interior edge `e = (f,g)`:

```
per-edge:   ‖ Γ(f) · ē_f²  −  Γ(g) · ē_g² ‖²                          (10)
E_s(Γ) = Σ_{e∈E} m(e) · ( 1 − w(e) ) · ‖ Γ(f) ē_f² − Γ(g) ē_g² ‖²    (11)
       = Γᴴ L₂ Γ ,     L₂ = G_Eᴴ M_E ( I − W_E ) G_E                 (12)
```

- `ē_f` = conjugate of `e_f`, the complex representation of edge `e` in face `f`'s basis.
- **`w(e) = w(f) + w(g)`** (the **sum** of the two face confidences) — so low-confidence
  (near-planar) regions get *more* smoothing via `(1 − w(e))`.
- `G_E` stacks the per-edge differences `Γ(f)ē_f² − Γ(g)ē_g²` from Eq. (10); `W_E` is the diagonal
  of edge confidences.

**Integrability (Eq. 13–15)** — measure the curl of the **scaled** field `sγ` and constrain it to
zero, with density bounds:

```
(C sγ)(e) = ⟨ s(f)γ(f) − s(g)γ(g) , e ⟩                              (13)
constraint:   C sγ = 0                                              (14)
bounds:       s_low < s < s_high     (they use 0.4 ≤ s ≤ 1.6, Eq. 25) (15)
```

### Full problem (Eq. 16–19) ✅

```
(Γ, γ, s) = argmin   ω_a · E_a(Γ) + ω_d · E_d(γ) + ω_s · E_s(Γ)     (16)
   s.t.   Γ(f) = γ²(f)    ∀f                                        (17)
          C s γ = 0                                                 (18)
          s_low < s < s_high                                       (19)
```

`ω_a, ω_d, ω_s` are scalar weights. Following [Sageman-Furnas 2019], they anneal weights (drive
`ω_d, ω_s → 0`) so the solution converges to a divergence-free unit-norm field aligned to rulings
*away from* planar regions and singularities.

### Algorithm 1 — alternating solve (§5.2)

Separable in `Γ`, `γ`, `s`, so they alternate. The **align and smooth steps run in the power
representation `Γ`**, then convert back to the raw unit field for the projections:

```
Initialize  Γ⁰ = R⊥ (ruling power target),  s = 1,  V* = V \ (V_boundary ∪ V_crease)
repeat  (k = k+1):
    Γ^k      ← ImplicitAlign(Γ^{k-1})       # implicit-Euler step on E_a, in power space  (Eq. 20)
    Γ^{k'}   ← ImplicitSmooth(Γ^k)          # implicit-Euler step on E_s                  (Eq. 21)
    γ_u^k    ← LocalRawRepresentation(Γ^{k'}), then renormalize γ_u/‖γ_u‖   # power → unit field
    V*       ← UpdateSingularities(γ_u^k)   # V*, V_boundary, V_crease ⊆ V
    γ_d^k    ← ProjectDivFree(γ_u^k)         # project onto div-free space                (Eq. 22)
    (γ_c^k, s) ← ProjectCurlFree(γ_d^k)      # convex: scale to curl-free + bound s        (Eq. 23-25)
    Γ        ← PowerRepresentation(·)        # back to power rep for the next iteration
until  max_f ‖ γ^k(f) − γ^{k-1}(f) ‖ < 1e-3
```

*(The loop ordering above is from the text layer; the sub-solves it calls — Eq. 20–25 — are
verified below.)* The linear sub-solves ✅:

```
ImplicitAlign(Γ^{k-1}):
    ( M_X + (ω_a/μ_a)·∇E_a ) Γ^k = M_X Γ^{k-1} + (ω_a/μ_a)·M_X W_X Γ⁰ ,   ∇E_a = M_X W_X    (20)
ImplicitSmooth(Γ^k):
    ( M_X + (ω_s/μ_s)·∇E_s ) Γ^{k'} = M_X Γ^k ,                           ∇E_s = L₂ (Eq. 12) (21)
ProjectDivFree(γ_u^k):
    argmin_{γ_d^k} ‖ γ_d^k − γ_u^k ‖²   s.t.  ( D γ_d^k )(V*) = 0                          (22)
        — D encodes the local principal matching around vertices, applied only at V*.
ProjectCurlFree(γ_c^k):   (convex)
    argmin_{γ_c^k, s} ‖ γ_c^k − s·γ_d^k ‖²   s.t.  C γ_c^k = 0,   0.4 ≤ s ≤ 1.6            (23-25)
```

`Γ⁰` on the Eq. 20 RHS is the ruling power target `R⊥` (what alignment pulls toward); `μ_a, μ_s`
are step-size normalizers (the lowest nonzero generalized eigenvalues).

**Step sizes / convergence.** One step size is fixed at `0.1`; the other starts at `0.005` and
**halves every 30 iterations**; sizes are rescaled by `μ_a, μ_s`. Typical convergence: **10–20
iterations** (fields without singularities), **40–50** (shapes with planar singularities). The
convex `ProjectCurlFree` (CVX [Grant–Boyd]) dominates runtime.

### Integration & meshing (§5.3)

With an integrable `sγ`, integrate to `u` using an **off-the-shelf seamless integrator**
(Directional [Vaxman 2017]): cut the mesh to a topological disk with singularities on the
boundary; extract a corner-based `u`, seamless across cuts via integer translations; configure it
to produce **½ℤ values around singularities** so level sets avoid meeting there (which subdivides
planar polygons). Then **trace the integer level sets of `u`** and **collapse all non-boundary
valence-2 vertices** → this straightens the polylines (negligible in torsal regions where they're
already straight; in planar regions the level sets become chords between boundary vertices).
Result: PQ strips in curved regions, large polygons in flat regions.

---

## 5. Results & evaluation (§6)

- **Implementation:** libigl + Directional; i7-8569U, 16 GB. Typical input ≈ **1800 faces** →
  vector-field design **4–5 s** (mostly the CVX curl-free projection); integration **10–15 s**.
  Scales to **160k faces** (~100 iterations, slower).
- **Planarity metric** [Liu 2006]: distance between a quad's diagonals ÷ their average length, in
  %; RMS over consecutive quads for higher-degree polygons. Stringent tolerance ≈ **1%**. Most
  results meet `< 1%` **without any planarization post-processing** (Table 2). The single worst
  case (`σ = 11.84%`) planarizes to `0.0034%` via ShapeUp with negligible visual change.
- **Resolution:** finer level-set sampling → lower Hausdorff distance to input + lower planarity
  error (already good even at coarsest).
- **Convergence to ground truth:** on an analytical clothoid, the optimized field converges toward
  the analytical max-curvature directions as input resolution rises (Table 1: analytical-vs-output
  mean angular error 2.24° → 1.19° → 0.52° at 10k/40k/160k faces) — *despite* noisy estimated
  input rulings (analytical-vs-input mean ≈ 3.4° → 1.0°).
- **Robustness:** handles random vertex displacement up to **12.5%** of avg edge length cleanly;
  at **25%** needs a larger `β` (some fine detail lost). Robust across a range of `β, γ_w`.
- **Generality demonstrated:** curved folds (two folds, Fig. 18), D-forms & sphericons & other
  creased shapes [Tang 2016; Jiang 2020], glued constructions with cone apexes, non-disk topology,
  interactive editing with **dynamic connectivity** (re-run after each deformation; combinatorics
  change automatically). Physically fabricated by laser-etching the flattened mesh edges into
  cardboard and bending (Fig. 19).

### Limitations (§6)

- **Triangulation-dependent** (Fig. 14): if the input triangulation fights the principal
  directions, ruling estimation degrades (worst near corners with little data). Mitigation:
  triangulate high-valence polygons with a **center vertex + triangle fan**.
- **Thin features** (e.g. in piecewise developables) provide little alignment information.
- **Resolution-dependent**: quality rises with input resolution (still good at low res).

---

## 6. Relevance to CreaseMachine

This is a **downstream / fabrication** method, orthogonal to our develop kernel — worth knowing
precisely because it occupies the niche our pipeline *doesn't*:

- **Different goal.** We **develop** a mesh toward developability (covariance flow in the GH
  component / `IsometricLM` in PieceSolver). Dev2PQ assumes the shape is *already* developable and
  **re-tiles** it into ruling-aligned flat panels. It is the natural **"rulings → PQ strips →
  flat panels"** export stage that sits *after* a develop + piece pass.
- **Rulings as first-class edges.** Their output makes rulings **exact straight edges** and flat
  patches **single polygons** — exactly the panelization a fabrication output of CreaseMachine
  would want. Contrast our current ruling *overlay* (display-only) and the `useMaxCov` input
  (straight-ruling bias in the flow).
- **Ruling estimation.** They use the **min-eigenvector of the per-face shape operator** [De Goes
  2020] + a power representation + a logistic curvature-confidence. We estimate developability via
  the **normal covariance** (Stein/Grinspun/Crane). The shape-operator min-eigenvector is a
  candidate if we ever want a per-face ruling estimate for export or for a straight-ruling term.
- **Creases break the field.** Their handling — *do not enforce div-free across creases; let the
  field break* (and zero the confidence `w(f)` on crease faces) — maps cleanly onto our
  **piece/crease** model (`Pattern`/`CreaseMap`): creases are exactly where the ruling foliation
  should be discontinuous.
- **Planar singularities = polygons.** Their automatic index-½ singularity in flat regions →
  a polygon is the principled answer to "how do flat patches tile," which our piecing currently
  leaves as an arbitrary triangulation.

**We do not implement this.** It is recorded as the reference design for a possible future
**ruling-aligned PQ export** (or a straight-ruling remeshing pass), and as an alternative lens
(field-based, not flow-based) alongside [`PAPER_JIANG2020.md`](PAPER_JIANG2020.md). See also
[`NOTICE.md`](NOTICE.md) for citation/license lineage if any of it is ever ported.

---

## References (selected, from the paper)

- De Goes, Butts, Desbrun. *Discrete Differential Operators on Polygonal Meshes.* TOG 39(4), 2020.
  — the face shape operator used for ruling estimation.
- Knöppel, Crane, Pinkall, Schröder. *Globally Optimal Direction Fields.* TOG 32(4), 2013. — power
  representation + power smoothness.
- Diamanti, Vaxman, Panozzo, Sorkine-Hornung. *Designing N-PolyVector Fields with Complex
  Polynomials.* CGF 33(5), 2014. — principal matching.
- Sageman-Furnas, Chern, Ben-Chen, Vaxman. *Chebyshev Nets from Commuting PolyVector Fields.* TOG
  38(6), 2019. — the alternating GL/projection scheme this follows.
- Viertel, Osting 2019; Brandt et al. 2017 (operators); Vaxman et al. 2017 (Directional, the
  integrator); Liu et al. 2006 (PQ / conjugate-net / planarity metric); Huffman 1976 (creases).
- Jiang et al. 2020 — see [`PAPER_JIANG2020.md`](PAPER_JIANG2020.md) (provides several test models).
