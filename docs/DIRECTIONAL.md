# Using Directional — don't hand-roll what the library gives us for free

The developability / **Dev2PQ** PQ-strip-remeshing pipeline runs on **[Directional](https://github.com/avaxman/Directional)**,
Amir Vaxman's directional-field-processing library. Directional provides — **out of the box** — the
ruling/field design, the matching, the **singularity detection**, the seamless integration, and the
polygon meshing that the Dev2PQ paper needs.

> **Rule for agents:** before you hand-roll a covariance flow, a Poisson integrator, a winding-number
> singularity finder, a level-set tracer, a cut-to-disk, or a curl/div projection — **check here first.**
> It is almost certainly a one-line OOTB Directional call. The whole `relax/` prototype history was
> re-implementing these in C# and fighting regression-after-regression; the lesson, paid for in weeks,
> is to **use the library**. The paper (`docs/PAPER_DEV2PQ2021.md`) is *by Vaxman* — its stages map
> almost 1:1 onto Directional tutorials.

## Where it lives / how we call it

- **External clone:** `C:\Repo\avaxman\Directional` (header-only; Eigen vendored in `external/eigen`;
  GMP via vcpkg). **Not** in this repo, and **not** vendored. Build patches + flags are documented in the
  dev2pq source's `BUILD.md`.
- We drive it as a **subprocess**, not via P/Invoke: `dev2pq.exe in.off out.off [flags]` — a small C++
  program (`dev2pq.cpp`) that wraps the library — called exactly like `bff-command-line.exe`. The C#
  side is `PieceSolver/Dev2PQ.cs`; the integration contract is `DEV2PQ-INTEGRATION.md`.

## The pipeline → Directional tool map

Tutorials at `Directional/tutorial/NNN_*`, headers at `Directional/include/directional/`. `dev2pq.cpp`
is literally tutorials **304 + 501 + 505** composed over our ruling estimate + the paper's Eq.6 confidence.

| Paper stage | OOTB Directional | Tutorial |
|---|---|---|
| Ruling estimate (zero-curvature dir) | `TriMesh.min/maxVertexPrincipalDirections` + `vertexPrincipalCurvatures` | 106 |
| Field design (smooth + integrable) | `polyvector_field` + `polyvector_iterate(soft_rosy, curl_projection)` | 303 / 304 |
| Matching + **singularities** | `principal_matching` → `effort_to_indices` | 201 / 401 |
| Seamless integration (cut-to-disk, ½ℤ at singularities) | `setup_integration` + `integrate` (`IntegrationData`) | 501 / 503 |
| PQ-mesh (isolines → polygons + valence-2 collapse) | `setup_mesher` + `mesher` | 505 |
| Isolines only (robust, no DCEL) | `branched_isolines` | 503 |

## Singularities — the canonical "don't hand-roll" example

You do **not** write a winding-number singularity finder. `principal_matching(field)` computes the
per-edge matching + "effort," then calls **`effort_to_indices(field)`**, which derives the per-cycle
**singularity indices** by a Gauss–Bonnet winding count — the vertices where the field can't be combed
consistently. The field then carries them in `field.singLocalCycles` / `singLocalIndices`. For an
**N=2 line field** (rulings) the indices come out as multiples of **½** (the paper's index-½ defects).

They are then **consumed** OOTB: `setup_integration` *cuts the disk at the singularities* and combs them
onto the seams; `integrate` (with `roundSeams=false`) pins/rounds them so the integer level sets route
*around* them. The `relax/` C# prototype hand-rolled all of this (`UpdateSingularities` winding walk +
its own cut + a ½ℤ Poisson penalty) — entirely redundant with the library.

## `IntegrationData` knobs (set after `setup_integration`, before `integrate`)

- `integralSeamless = true` — integer-seamless `u` (required for clean closing strips).
- `roundSeams = false` — round **singularities** (not seams); the Dev2PQ default.
- `lengthRatio` (default 0.02) — strip density / parameterization scale; raise to coarsen. Scales both
  the integrate-rounding and the mesher cost.
- `periodMat` (default integer identity) — the singularity period lattice (where genuine ½-integer
  routing would be configured).

## Gotchas (each one cost real time)

- **Ruling = the ZERO-curvature direction = eigenvector of the *min-absolute* shape-operator eigenvalue**,
  **not** `minVertexPrincipalDirections` (the min-*signed* one). Signed-min returns the *profile* on
  negatively-curved patches → a per-figure 90° "iso/ruling flip." This was a week-long bug; the fix is
  one line (pick the principal dir whose `|curvature|` is smaller).
- **The mesher (`mesher`/`NFunctionMesher`) is exact-arithmetic (GMP) and *asserts* (`check_consistency`,
  DCEL) on heavily-branched / multi-singularity arrangements** (fig15/16/18/19/20/21) — a *contained*
  crash, not solved. `branched_isolines` recovers the rulings there (no DCEL), but yields lines, not
  polygons. fig16_1/fig18_3 fail even there → upstream (integration), genuinely open.
- **Never build with `/DNDEBUG`** — it strips the mesher's DCEL asserts, turning a clean abort into an
  *infinite hang*. `/O2` alone keeps the asserts and gives the bulk of the speed (~10×).
- **GMP + `/O2`** are the speed; see `BUILD.md`. The Directional repo needs ~6 small local patches
  (asserts, GMP `.num/.den`) — all documented there.
- An **N=2 line field** integrates to **one** scalar: `integrate` returns `|V|×2` but `col1 == −col0`.
- `dev2pq.cpp`'s `main()` calls `_set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT)` so a
  mesher assert **fails fast and silently** (no WER dialog) — required for the subprocess caller to get
  a clean failure instead of a blocking modal box.

## Using a Directional tool directly (C++)

Include the header and follow the matching tutorial — they're short and canonical (the integration +
meshing flow is ~10 lines: `principal_matching → setup_integration → integrate → setup_mesher → mesher`).
There's also a whole DEC / Hodge / curvature / subdivision half of the library (tutorials 6xx/7xx) we
don't use yet — reach for it before re-deriving discrete operators by hand.
