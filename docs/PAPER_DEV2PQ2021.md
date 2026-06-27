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

> ⚠️ **Transcribed from the arXiv text layer, NOT visually verified.** The PDF image
> renderer (poppler) was unavailable in this environment, so equations were reconstructed
> from `pdftotext -layout` output (which **drops Unicode math, sub/superscripts, primes, and
> operator glyphs**) plus standard discrete-differential-geometry conventions. **The prose
> and algorithm structure are faithful; the equations are best-effort reconstructions and
> must be re-checked against the PDF before being relied on for implementation.** Equation
> numbers match the paper. Uncertain spots are flagged inline with *(reconstructed — verify)*.

---

## TL;DR

- **Input:** a triangle mesh of a (piecewise) developable surface — *not* necessarily aligned to
  curvature, e.g. a 3D scan or the output of freeform developable modeling.
- **Output:** a curvature-aligned **PQ-strip mesh** — long planar quad strips in the curved
  (torsal) regions whose chordal edges are **exact straight rulings**, plus big planar polygons
  for the flat regions. Curved folds and creases handled without explicit segmentation.
- **How:** fit a scalar field `φ` on the mesh whose level sets are straight and align to the
  locally-estimated rulings. The straight-level-set condition = *the normalized gradient of `φ`
  is divergence-free*. This is nonlinear/high-order, so they **factor it** into (a) a directional-
  field optimization and (b) fitting `φ` to that field — making it tractable.
- **Key trick:** design only one coordinate (`φ`, the ruling foliation); let the orthogonal
  coordinate be dependent. Singularities, flat regions, and curved folds emerge automatically
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

### Conjugate-net parameterization (Eq. 1)

A torsal patch is parameterized as

```
S(u, v) = c(u) + v · r(u)                                            (1)
```

- `c(u): ℝ → ℝ³` — a **generating curve**.
- `r(u): ℝ → S²` — the **ruling direction**.
- Fixing `u` and sweeping `v` traces a straight line → the **ruling**. So **the level sets of `u`
  are the rulings.** Developability ⟺ the Gauss map is constant along each ruling: `N(u,v) = N(u)`.

The rulings are the **minimum-curvature lines**. Choosing `c(u)` as a max-curvature line makes
`(u,v)` a **principal curvature-line parameterization** (rulings ⟂ generating curves). Enforcing
`φ` continuous across patches (and allowing singularities to emerge) gives a single **conjugate
net** for the whole surface `S`; singularities, on a `C²` surface, must sit in **planar regions**.

**Order of symmetry.** They design the **2²-symmetric** variant (the `u` and `v` roles stay
distinct — `u`-level-sets are rulings, `v` the dependent generating coordinate), rather than the
4-symmetric "let `u,v` intermix" used for generic PQ meshing [Diamanti 2014; Liu 2011]. **They
design only `φ` (≈ `u`)** — the ruling foliation — and let the orthogonal coordinate be dependent.

---

## 2. Ruling fields — the core characterization (§3.2)

Let `φ` be the scalar field whose level sets are the rulings. `∇φ` is orthogonal to those level
sets. The **geodesic curvature** of the level sets is

```
κ_g(level set) = div( ∇φ / |∇φ| )            [do Carmo 1976]
```

Because the level sets are rulings — extrinsically flat, hence **geodesics** on the developable —
their geodesic curvature is zero. Define the **unit field** `u = ∇φ / |∇φ|` (orthogonal to the
ruling directions). Then, away from singularities and creases:

```
div(u)  =  ∇·( ∇φ / |∇φ| )  =  0                                    (3)
```

→ **`u` is a divergence-free unit vector field.** This is the central condition: *straight level
sets ⟺ divergence-free normalized gradient.*

**Integrability up to a scalar (Eq. 4).** To recover `φ` from `u`, there must exist a positive
**density** `ρ(p) > 0` such that `ρ·u` is a gradient (curl-free):

```
curl( ρ · u ) = 0,     ρ(p) > 0                                     (4)   (reconstructed — verify)
```

`ρ` is the **density of the level sets** at `p` — it varies naturally where rulings fan out (e.g.
a cone). So the design splits into: find a div-free unit field `u`, and a density `ρ` making `ρu`
integrable; then integrate to `φ`.

**Ruling-alignment via the second fundamental form (Eq. 5).** Being a geodesic field is *not
sufficient* — rulings are extrinsic (they depend on the shape operator), so `u` must be pinned to
the actual rulings. With second fundamental form `II`:

```
⟨ II · ∇φ , ∇φ ⟩  =  0                                              (5)   (reconstructed — verify)
```

**Singularities & combing.** `u` is a **2-direction field** (defined up to sign), so divergence/
curl need a local *combing* (consistent sign choice) to apply. Singularity indices are integer
multiples of **½**. Only two singularity types occur: **(a)** cone apexes (index 1, removed up
front); **(b)** singularities **inside planar regions** (index ½), where they reconcile the
orientations of neighboring torsal patches. Level sets bend near planar-region singularities —
harmless, since planar regions become one big polygon anyway.

**Creases.** Rulings on the two patches meeting at a crease generally meet at an angle, not a
straight line. So they **do not require `u` to be divergence-free near creases** — the field is
allowed to *break* across them.

---

## 3. Discretization (§3.3, §4)

Input: triangle mesh `M = (V, E, F)`. `φ` is a **piecewise-linear vertex function** `φ: V → ℝ`;
`u`, `∇φ`, and the ruling field are **face-based piecewise-constant** tangent fields (this space
denoted `X`). The mesh is uniformly scaled so **average edge length = 1**.

Operators (from [Brandt et al. 2017]): conforming gradient `G: V → X`, divergence `D: X → V`,
non-conforming curl `C: X → E`. For a triangle `f = (i,j,k)` with area `a(f)`:

```
(G φ)(f) = (1 / 2 a(f)) · ( φ_i · eᵢ^⊥ + φ_j · eⱼ^⊥ + φ_k · eₖ^⊥ )    (reconstructed — verify)
```

A discrete PQ mesh sampled from the `u,v` level sets of a conjugate net has faces planar **to
second order** [Liu et al. 2006]; curvature-line sampling gives **circular** quads (discrete
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
q_r(f) = r(f)²                          (sign-invariant ruling power field)
q_r^⊥(f) = ( r(f)^⊥ )²                  (the same, rotated 90°)
```

### Confidence weights (Eq. 6)

Rulings are least reliable in planar/near-planar regions (the min/max curvatures are close and
noisy) and most reliable in strongly-curved regions. A per-face confidence `c(f) ∈ [0, 0.8]` is a
**logistic** in the absolute max/min curvatures `|κ₁(f)|, |κ₂(f)|` (eigenvalues of `S(f)`):

```
c(f) = 0.8 · σ( a₃ · ( |κ₂(f)| / |κ₁(f)| − ... ) ) ,   a₁=0.8, a₂=−0.9, a₃=5     (6)  (reconstructed — verify)
```

*(The exact logistic argument is garbled in extraction — only the constants `a₁=0.8, a₂=−0.9,
a₃=5` and the cap are certain. Verify against the PDF.)* By design `c(f)` caps at **0.8** (reached
when `|κ₁|, |κ₂|` differ by ≥ 0.5), so the method **never fully trusts** a ruling. Boundary faces
and crease faces are set to `c(f) = 0`.

**Crease detection** (optional): collect edges whose adjacent face normals differ by more than a
user threshold; zero the confidence of faces incident to crease vertices. Creases may instead be
prescribed by the user.

---

## 4. Optimization (§5)

Precompute per face: shape operator `S(f)`, ruling `r(f)`, the power fields `q_r(f) = r(f)²` and
`q_r^⊥(f)`, and confidences `c(f)`. Mass matrices: `M_X` (face areas) and `M_E` (edge masses,
each = half the summed dual-edge lengths). The unknowns are the unit field `u(f)`, its power
representation `q(f) = u(f)²`, the density `ρ(f)`, and (after integration) `φ`.

### Energy terms

**Alignment (Eq. 7–8)** — pull the power field to the (rotated) ruling power field, weighted by
area and confidence:

```
E_align(q) = Σ_f  a(f) · c(f) · | q(f) − q_r(f) |²
           = ( q − q_r )ᴴ  C_X  ( q − q_r )                          (7,8)
```

(`C_X` = diagonal of per-face confidences; `q, q_r` are `|F|×1` complex; note the conjugate
transpose `ᴴ`.)

**Unit-norm, divergence-free (Eq. 9)** — a **Ginzburg–Landau** term [Viertel–Osting 2019;
Sageman-Furnas 2019]:

```
E_div(u) = Σ_V | (D u)(·) |²  +  (1 / 2ε²) · Σ_f a(f) · ( |u(f)|² − 1 )²     (9)  (reconstructed — verify)
```

As `ε → 0`, this minimizes the divergence of a unit-norm field after excising radius-`ε` balls
around singularities → it **naturally locates singularities inside planar regions**.

**Smoothness (Eq. 10–12)** — power-field smoothness across each interior edge `e = (f,g)`:

```
per-edge:   | q(f) · ē_f²  −  q(g) · ē_g² |²                          (10)
E_smooth(q) = Σ_e ( 1 − c(e) ) · | q(f) ē_f² − q(g) ē_g² |²           (11)
            = ‖ S q ‖²                                               (12)
```

where `ē_f` is the conjugate complex representation of edge `e` in face `f`'s basis, and
`c(e) = ½( c(f) + c(g) )`. Low-confidence (near-planar) regions get *more* smoothing.

**Integrability (Eq. 13–15)** — measure curl of the **scaled** field `ρu` and constrain it to
zero, with density bounds:

```
E_int / constraint:   C( ρ · u ) = 0                                 (13,14)
bounds:               ρ_low < ρ < ρ_high     (they use 0.4 ≤ ρ ≤ 1.6) (15,25)
```

### Full problem (Eq. 16–19)

```
(u, ρ, q) = argmin   α · E_align(q) + β · E_div(u) + γ · E_smooth(q)   (16)
   s.t.   q(f) = u(f)²    ∀f                                          (17)
          curl( ρ · u ) = 0                                          (18)
          ρ_low < ρ < ρ_high                                         (19)
```

`α, β, γ` scalar weights. Following [Sageman-Furnas 2019], they drive `ε → 0` and `β → 0` so the
solution converges to a div-free unit field aligned to rulings *away from* planar regions and
singularities.

### Algorithm 1 — alternating solve (§5.2)

The problem is separable in `u`, `ρ`, `q`, so they alternate:

```
Initialize  u₀ = r (estimated rulings),  ρ = 1,  V_s = V \ (V_boundary ∪ V_crease)
repeat  (k = k+1):
    u ← ImplicitAlign(u_{k-1})        # implicit-Euler decrease of E_align       (Eq. 20)
    u ← ImplicitSmooth(u)             # implicit-Euler decrease of E_smooth       (Eq. 21)
    u(f) ← u(f) / |u(f)|              # pointwise renormalize (the GL unit term)
    LocalRawRepresentation(u)
    V_s ← UpdateSingularities(u)      # find current singularities; V_s ⊆ V
    u ← ProjectDivFree(u)             # project onto div-free space               (Eq. 22)
    ρ ← ProjectCurlFree(ρ)            # convex project ρu onto curl-free + bounds (Eq. 23-25)
    q ← PowerRepresentation(u)        # q = u²
until  max_f | u_k(f) − u_{k-1}(f) | < 1e-3
```

The linear sub-solves:

```
ImplicitAlign:   (M_X + τ_a · A) u = M_X u_{k-1} + τ_a · C_X q_r          (20)  (reconstructed — verify)
ImplicitSmooth:  (M_X + τ_s · L_s) u = M_X u ,   L_s = the matrix of (12)  (21)
ProjectDivFree:  argmin_{u'} ‖u' − u‖²   s.t.  (D u')|_{V_s} = 0          (22)
ProjectCurlFree: argmin_ρ   ‖ρ − ...‖²   s.t.  C(ρu)=0,  0.4 ≤ ρ ≤ 1.6    (23-25, convex)
```

**Step sizes / convergence.** One step size is fixed at `0.1`; the other starts at `0.005` and
**halves every 30 iterations** (to make the alternation with renormalization converge); sizes are
rescaled by the lowest nonzero generalized eigenvalues of the align/smooth operators. *(Which
size is which is ambiguous in extraction — verify.)* Typical convergence: **10–20 iterations**
(fields without singularities), **40–50** (shapes with planar singularities). The convex
`ProjectCurlFree` (CVX [Grant–Boyd]) dominates runtime.

### Integration & meshing (§5.3)

With an integrable `ρu`, integrate to `φ` using an **off-the-shelf seamless integrator**
(Directional [Vaxman 2017]): cut the mesh to a topological disk with singularities on the
boundary; extract a corner-based `φ`, seamless across cuts via integer translations; configure it
to produce **½ℤ values around singularities** so level sets avoid meeting there (which subdivides
planar polygons). Then **trace the integer level sets of `φ`** and **collapse all non-boundary
valence-2 vertices** → this straightens the polylines (negligible effect in torsal regions where
they're already straight; in planar regions the level sets become chords between boundary
vertices). Result: PQ strips in curved regions, large polygons in flat regions.

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
  at **25%** needs a larger `β` (some fine detail lost). Robust across a range of `β, γ`.
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
  field break* — maps cleanly onto our **piece/crease** model (`Pattern`/`CreaseMap`): creases are
  exactly where the ruling foliation should be discontinuous.
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
