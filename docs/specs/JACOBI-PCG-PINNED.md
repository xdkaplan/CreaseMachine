# Jacobi-Preconditioned CG with Dirichlet "Pinned" Boundaries

**Scope.** This spec documents the *linear solver* at the heart of the PieceSolver develop kernel —
the matrix-free **Jacobi-preconditioned Conjugate Gradient (PCG)** that runs once per Levenberg–Marquardt
(LM) outer iteration — and the **Dirichlet "pinned"** mechanism that freezes chosen vertices (the
frozen seam edges) during a solve.

Code: [`PieceSolver/IsometricLM.cs`](../../PieceSolver/IsometricLM.cs), `IsometricLM.Solve(...)`.
This spec does **not** re-derive the developability energy or the LM trust region — see the file's header
comment and [`reference_jiang2020`] for those. It focuses on (1) the linear solve and (2) the pin.

> **Terminology note.** "Jacobi" here means the **diagonal (Jacobi) preconditioner**, `M⁻¹ = diag(JᵀJ + λI)⁻¹`.
> The iterative method itself is **Conjugate Gradient**, not a stationary Jacobi iteration. The user-facing
> name for the whole kernel is **PieceSolver** (the class is `IsometricLM`); in the UI it is just **"Solve"**.

---

## 1. What each LM outer iteration asks the linear solver to do

The develop step is a nonlinear least-squares problem: minimize `E = ‖r(x)‖²`, where `x` stacks the 3-D
mesh **M** (`x,y,z` per vertex) and its flat image **M′** (`x,y` per vertex), and `r` is the residual
vector (iso / fairness / anchor / scale / bending blocks; see the file header).

LM linearizes `r` at the current point and solves a **damped normal-equation system** for the step `δ`:

```
(JᵀJ + λI) δ = −Jᵀr
```

- `J` is the Jacobian of `r`; `λ` is the LM damping (trust region).
- The **left-hand matrix `A = JᵀJ + λI`** is symmetric positive (semi-)definite → CG is the right solver.
- We never assemble `A` or `J`. We apply `J` and `Jᵀ` directly (matrix-free), so a CG iteration costs two
  sparse "applies" plus a few vector ops.

Variable layout (`Solve`, lines ~140–147): `N = 5·nV`. M occupies `x[3v .. 3v+2]`; M′ occupies
`x[oP+2v .. oP+2v+1]` with `oP = 3·nV`.

---

## 2. Matrix-free apply: `ApplyJ` / `ApplyJt`

- `ApplyJ(v) → J·v` (lines ~269–324): linearized residual blocks.
- `ApplyJt(r) → Jᵀ·r` (lines ~329–375): the **exact transpose**, assembled as a **gather-by-vertex** —
  each vertex sums its own incident edges (via the `veStart/veEdge/veSign` CSR) and its 1-ring. Because
  every output slot is written by exactly one iteration, the apply is **race-free and deterministic**, so
  it parallelizes over vertices with no accumulators (gated by `ParThreshold`).
- Correctness of `Jᵀ` is protected by an opt-in finite-difference gate (`DebugGradCheck`, lines ~447–464):
  it central-differences `E` against `2·(Jᵀr)` and must agree to ~1e-9. **Any change to the applies must
  pass this gate** (and the pin must not break it — see §6).

`A·p = JᵀJ·p + λp` is therefore `ApplyJ(p)` then `ApplyJt(...)` then `+ λp` (lines ~482–483).

---

## 3. The Jacobi (diagonal) preconditioner — `DiagJtJ`

CG on this system was **ill-conditioned** — it hit its iteration cap without converging, which made every
outer LM step slow. The fix is a **diagonal preconditioner** `M⁻¹ = 1/(diag(JᵀJ) + λ)`.

`DiagJtJ(dg)` (lines ~383–430) assembles `diag(JᵀJ)` exactly per term — it is the sum of squared Jacobian
column entries per variable:

- **iso** (per edge → both endpoints): `(2·sIso·dM)²` into the M-DOFs, `(2·sIso·dP)²` into the M′-DOFs.
- **scale** (one global row): accumulate the per-vertex gradient, then square it.
- **fairness**: `sFair²` (own) `+ Σ_nbr sFair²/deg²`.
- **anchor (pos)**: `sPos²`.
- **bending**: an estimate `sBend²·(u2)²` where `u2 = 1 + Σ_{k∈nbr} 1/(d_v·d_k)` is the bi-Laplacian
  self-coefficient. **This estimate is load-bearing** — without it, `M⁻¹` blows up on bending-dominated
  vertices and the preconditioner makes things *worse*.

`diag` is computed **once per outer iteration** (it depends on the linearization point, not on `λ`); `λ` is
added per LM try in the `cgMinv` line.

---

## 4. The PCG iteration

Standard preconditioned CG (lines ~470–492), per LM try:

```
cgMinv[k] = 1 / (cgDiag[k] + λ)          # the Jacobi preconditioner  (per try, λ changes)
x = 0
r = b                                    # b = −Jᵀr0  (RHS)
z = Minv ∘ r ;  p = z
rz = ⟨r,z⟩ ;  rr0 = ⟨r,r⟩
repeat up to cgIters, while ⟨r,r⟩ > 1e-18·rr0 :
    Ap = ApplyJt(ApplyJ(p)) + λ·p        # matrix-free A·p
    α  = rz / ⟨p,Ap⟩                      # (break if ⟨p,Ap⟩ ≤ 0)
    x += α·p ;  r −= α·Ap
    z  = Minv ∘ r
    β  = ⟨r,z⟩ / rz ;  p = z + β·p ;  rz = ⟨r,z⟩
```

`cgIters` is the inner cap (CG is intentionally *inexact* — LM tolerates a truncated solve). The resulting
`x` is the trial step `δ`, accepted/rejected by Nielsen gain-ratio damping (lines ~500–514).

---

## 5. Dirichlet "pinned" behavior — the frozen seams

**Goal.** Hold a chosen set of vertices' **3-D positions fixed** for the whole solve, so a panel develops
its *interior* against a *frozen boundary* and adjacent panels stay joined at their shared seam.

**Input.** `Solve(..., bool[] pinned = null)`. `pinned[v] == true` means "freeze vertex `v`'s 3-D position."
This is a **Dirichlet boundary condition**: the value (position) on those DOFs is prescribed, not solved for.

**Setup** (lines ~436–441): build a per-DOF mask `mFixed` (length `N`). For each pinned vertex `v`, mark its
**three M-DOFs** `3v, 3v+1, 3v+2`. Note: only the **M (3-D)** DOFs are pinned — the vertex's **M′ (flat)**
DOFs `oP+2v, oP+2v+1` are **never** marked, so the flat image still moves and the panel flattens freely.

**Mechanism — a filtered/projected PCG.** Rather than deleting the pinned rows/columns from the system (which
is awkward matrix-free), we run the *same* PCG but **zero the pinned DOFs in three places**, which keeps the
step `x` identically zero there:

| Where | Line | What | Effect |
|---|---|---|---|
| RHS | ~466 | `if mFixed[k]: b[k] = 0` | no driving force on pinned DOFs |
| Preconditioner | ~473 | `cgMinv[k] = mFixed[k] ? 0 : 1/(cgDiag[k]+λ)` | `z = Minv∘r` is 0 there → never enters a search direction |
| Matvec | ~484 | `if mFixed[k]: cgAp[k] = 0` | keeps the residual 0 there → clean convergence test |

**Why this freezes the pinned vertices (invariant: `x[mFixed] ≡ 0` throughout CG):**

- `x` starts at 0 (line ~475).
- `r = b`, and `b[mFixed] = 0` → `r[mFixed] = 0`.
- `z = Minv∘r`, and `Minv[mFixed] = 0` → `z[mFixed] = 0`; `p = z` → `p[mFixed] = 0`.
- Each iteration:
  - `Ap[mFixed]` is forced to 0 (line ~484).
  - `⟨p,Ap⟩` is unaffected by pinned DOFs (`p[mFixed]=0`).
  - `x += α·p` → `x[mFixed] += α·0` = unchanged (still 0).
  - `r −= α·Ap` → `r[mFixed] −= α·0` = unchanged (still 0).
  - `z = Minv∘r` → 0 there; `p = z + β·p` → `0 + β·0` = 0 there.
- So `p[mFixed]` and `x[mFixed]` stay 0 for every iteration. The trial step then does
  `tMx[v] = Mx[v] + x[3v]` with `x[3v]=0` → the pinned vertex's 3-D position is **unchanged**.

This is the textbook way to impose Dirichlet conditions in a matrix-free iterative solver: solve in the
subspace of the *free* DOFs while the pinned DOFs sit at their prescribed values.

**Why the matvec-zeroing (line ~484) matters specifically.** Without it, the residual at a pinned DOF would
accumulate the *reaction force* (which does not go to zero), so `⟨r,r⟩` would never reach the stopping
tolerance and CG would always burn its full `cgIters`. Zeroing `Ap` on `mFixed` keeps `r[mFixed] = 0`, so the
convergence test reflects only the free DOFs.

**What it does NOT do.** It does not pin M′ (the flat) — the panel still unfolds. It does not change the
energy or the residual definition — pinned vertices still participate in their neighbours' iso/fairness
residuals (a fixed boundary correctly constrains the interior). It is purely a constraint on which DOFs may
move.

---

## 6. Properties & invariants

- **Backward compatible.** `pinned == null` → `mFixed == null` → all three zeroings are skipped → the solve is
  **bit-for-bit identical** to the un-pinned solver. The FD gradient gate stays at ~1e-9 with `pinned = null`.
- **Deterministic.** The matrix-free apply is gather-by-vertex (no reduction races), so a pinned solve is
  reproducible run-to-run.
- **Hard constraint = ∞ on a spectrum.** This pin is the *hard* (weight = ∞) end of a soft-constraint
  spectrum. A future **relax** simply *un-pins* those DOFs (frees the seam); a **drift/weight** mode would
  replace the hard pin with a finite-weight anchor-to-target. (Architecture note in `project_multipiece_seams`.)
- **Caller contract.** The caller (`PieceSolver` multi-piece path) freezes a piece's boundary by passing
  `pinned = MeshOps.BoundaryVertexMask(piece)`; both sides of a shared seam are pinned at coincident
  positions, so the reassembled solid stays joined.

---

## 7. Map: behavior → code

| Concern | Location (`PieceSolver/IsometricLM.cs`) |
|---|---|
| `Solve` signature (`pinned` param) | ~54–56 |
| Variable / residual layout | ~140–147 |
| Matrix-free `ApplyJ` / `ApplyJt` | ~269–375 |
| FD gradient gate | ~447–464 |
| Jacobi diagonal `DiagJtJ` | ~383–430 |
| Build `mFixed` from `pinned` | ~436–441 |
| Pin: zero RHS `b` | ~466 |
| Pin: zero preconditioner `cgMinv` | ~473 |
| PCG loop | ~470–492 |
| Pin: zero matvec `cgAp` | ~484 |
| Trial step + Nielsen accept/reject | ~494–516 |
| Write positions back + `eIso` readout | ~519–534 |

> Line numbers are approximate — they drift as the file evolves; search the quoted comments/identifiers
> (`mFixed`, `cgMinv`, `DiagJtJ`) if they don't match.
