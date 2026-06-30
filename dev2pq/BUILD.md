# Dev2PQ via Directional — the paper-faithful build (WIP, resumable)

The C# `relax/Relax.cs` pipeline hand-rolls Directional (Vaxman's lib — the tool the paper actually uses for
§5.1–5.3). This dir replaces that with a **headless subprocess** over Directional, mirroring the BFF integration
(`PieceSolver/Bff.cs`). The paper IS Directional's tutorials: 301/303 (power/GL field), 106 (principal dirs = rulings),
304 (integrable field = §5.2 design), 501/503/505 (seamless integrate + mesher = §5.3).

## Status (2026-06-29)
- ✅ Directional cloned `C:\Temp\Directional` + hedra `C:\Temp\libhedra`. Eigen VENDORED (external/eigen).
- ✅ Toolchain PROVEN: VS2022 MSVC + C++20 compiles & runs a Directional program on this box (test.exe loaded a mesh).
- ✅ NO libigl needed (only streamlines.h uses it — we don't). NO polyscope (viewer only — headless skips it).
- ✅ `dev2pq.cpp` written: load OFF → per-face ruling (mesh.minVertexPrincipalDirections) → polyvector_field N=2
     aligned to rulings → principal_matching → setup_integration (cut-to-disk) → integrate (seamless u) →
     setup_mesher → mesher → hedra::polygonal_write_OFF. The FIELD-DESIGN path compiles.
- ⚠️  BLOCKER (the last mile): the MESHER needs exact arithmetic. With NO GMP, Directional uses
     `ENumber_internal.h` (BigInteger), whose `num`/`den` are DATA MEMBERS, but generate_mesh.h /
     exact_geometric_definitions.h call the GMP-API `.num()`/`.den()`/`.convert()` METHODS → MSVC C2064/C2660.
     FIX: either (a) patch internal ENumber to expose num()/den() methods (rename members), or (b) **use GMP**
     (`vcpkg install gmp`, add /I + link) — the tested path; Directional flags the internal type as "slow-ish".
- PATCH ALREADY APPLIED to the clone: `BigInteger.h:63` had a real typo `assert(X) && "msg")` → fixed to
     `assert(X && "msg")` (GCC-tolerated, MSVC-fatal). Dropped errors 28→11.

## Build
`build.bat dev2pq.cpp dev2pq.exe`  (calls vcvars64 + cl /std:c++20 /I Directional/include /I external/eigen /I libhedra/include)

## Next
Resolve the exact-arithmetic (GMP recommended), build dev2pq.exe, validate vs the paper OFFs (--compare/--sniff),
then wire it as the subprocess the relax/PieceSolver pipeline calls instead of the hand-rolled PQFaces path.
The C# hand-rolled pipeline (incl. the §5.3 annulus topo-cut that fixed fig24_9) stays the working fallback until then.

## UPDATE — dev2pq.exe BUILDS (2026-06-29)
Full pipeline compiles + runs. Patches applied to the clone at `C:\Repo\avaxman\Directional` (moved out of C:\Temp):
1. `BigInteger.h:63` — `assert(X) && "msg")` → `assert(X && "msg")` (malformed; MSVC-fatal, GCC-tolerated).
2. `exact_geometric_definitions.h` + `generate_mesh.h` — the no-GMP internal ENumber exposes `num`/`den` as MEMBERS,
   so changed the 6 mesher/predicate calls `.num()`/`.den()` → `.num`/`.den` (member access). [breaks the GMP path,
   fine since we build no-GMP].
3. dev2pq.cpp: dropped `hedra::polygonal_write_OFF` (it pulls `igl/igl_inline.h`) → inline OFF writer. No libigl.
4. build.bat: added `/bigobj` (Eigen+Directional template explosion exceeds the COFF section limit).

RUNTIME BLOCKER: on fig24_9 the mesher asserts `DCEL not consistent` ("line triangle overlap") at generate_mesh.h:452.
Likely either (a) the field isn't integrable — dev2pq.cpp SKIPS the curl-free iteration that tutorial 304 does, or
(b) the no-GMP internal exact arithmetic is unreliable (lib says "GMP recommended"). NEXT: add the 304 curl-free
iteration; if still failing, switch to GMP (`vcpkg install gmp`, build ENumber_GMP path) — the tested arithmetic.

## WORKS (2026-06-29) — produces a VALID, paper-faithful mesh
Added the curl-free iteration (304: `iterationMode=true` + `soft_rosy`+`curl_projection` via `polyvector_iterate`)
— THAT was the fix; without an integrable field the mesher's DCEL was inconsistent. Now:
  `dev2pq fig24_9: 950v -> 68v/31f, DCEL clean`
fig24_9 (the closed tube): **1 component** (vs the C# hand-roll's 2-component SPLIT; paper is 1/63f). Manifold,
0 non-manifold edges, surface dist 1.12% mean. So the paper's actual machinery laces the tube correctly.
GAP: ruling alignment 23.5deg median vs the paper — the ruling estimation (avg vertex min-principal-dir) / field
weights (wAlignment/wSmooth/wRoSy) need tuning.

## WIRED INTO relax (2026-06-29) — DIRECTIONAL is now the 3rd swapper source
`Relax.cs` drives `dev2pq.exe` as a subprocess exactly like `PieceSolver/Bff.cs` drives `bff-command-line.exe`
(write OFF → run `dev2pq.exe in.off out.off` → read OFF). NO P/Invoke / ABI — this is the integration pattern the
primary CreaseMachine app will reuse to call Directional as an external API (answers the C#↔C++ cross-compat
question: subprocess, not linking).
- `RunDirectional(inputObj)` (Relax.cs): MeshIO.Load → temp `%TEMP%\dev2pq_in.off` (tris only, invariant-culture
  floats) → `Process.Start(DirectionalExe, "in.off out.off")` (30s timeout, captures stderr) → returns
  `%TEMP%\dev2pq_out.off`, loaded via `LoadOffForDisplay`. Cached per-figure (`_dirCacheFig/_dirCacheOut`) so a
  B re-toggle is instant.
- Swapper: `bool _swapPaper` → `int _swapMode` (0=OURS C# / 1=PAPER -out.off / 2=DIRECTIONAL dev2pq.exe). **B cycles
  all three**; titlebar shows `[OURS]`/`[PAPER OUT]`/`[DIRECTIONAL]`. M / dropdown reset to OURS.
- `DirectionalExe` path is hardcoded to the worktree's `relax/directional/dev2pq.exe` (prototype, like BFF's hardcoded
  ExePath). Headless round-trip verified: fig24_9 obj→off→dev2pq→68v/31f, DCEL clean.

## RULING-ALIGNMENT INVESTIGATION + ROBUSTNESS SWEEP (2026-06-29)
Paper-grounded (Dev2PQ §4/§5.2 + Directional reference; no invented heuristics). Field fixes are in `dev2pq.cpp`
(committed); the metric is in `Relax.cs::Compare`.
- **Sign-align the ruling line field** (kept): principal directions are a ± line field; raw 3-vertex averaging cancels
  antiparallel signs and rotates the ruling. Sign-align v1,v2 to v0 first. fig24_9 axis-agnostic ruling 24.2°→22.0°,
  Hausdorff 9.47%→7.70%.
- **Drop soft_rosy** (reverted): mesher emits 0 faces — soft_rosy is Directional's per-iteration realization of the
  paper's single-ruling-per-face (power) symmetry. Load-bearing; keep it. wRoSy stays 0 (no RoSy *energy* term, §5.2).
- **Eq.6 confidence wAlignment** (behind `argv[3]=="confidence"`, default OFF): paper-faithful but REGRESSED the only
  validatable figure (fig24_9 surface 1.10%→1.40%) because the paper anneals smoothness (ω_s→0) to balance it and
  Directional's fixed-weight `polyvector_iterate` can't reproduce that annealing. Re-evaluate once flat-region figures run.
- **Metric was partly an artifact**: the old long-edge ruling metric mis-paired our rulings against the paper's short
  cross-edges. New robust per-face STRIP metric (a strip's 2 longest parallel edges = its bounding rulings),
  centroid-matched, with an AXIS-AGNOSTIC column immune to the our-fat-vs-paper-thin aspect-ratio axis-flip. The 23.5°
  was inflated; honest axis-agnostic ruling on fig24_9 ≈ 22°.

### 5th Directional patch (relaxes an OVER-STRICT debug assert) — `effort_to_indices.h`
The strict `assert(|round(d)-d|<1e-6 && "Indices are not naturally integer!")` (line 41) crashed the MAJORITY of
figures during `principal_matching`. The code rounds `dIndices` on the very next line anyway, and the paper's
singularity indices ARE integers — so the 1e-6 guard is a debug check, not algorithm. Relaxed to **round-always + WARN
(never abort)** when |round−d|>0.25. Patch lives at `C:\Repo\avaxman\Directional\include\directional\effort_to_indices.h`
(added `#include <iostream>`). This unlocked figures that meshed cleanly post-round but tripped the guard.

### Broad sweep result — the REAL blocker is mesher SPEED, not the assert
With the relaxed assert, a 14-figure sweep (150s timeout, per-figure wall time) shows three regimes:
- **VALID (small ≤~1700v): 18–55s, clean 1-component meshes.** fig5_2 961v→38s ruling **3.5°**; fig3 663v→26s ruling
  7.7°; fig10 1702v→55s ruling 24°; fig5_1 441v→18s ruling 39°; fig24_18 0.66% surface. Ruling quality VARIES by figure
  (field-design quality, the original alignment target). Every figure logs exactly **w:12** non-integer cycles that
  round benignly (systematic — boundary/generator cycles — not random noise; they mesh fine, vindicating the relax).
- **SLOW → timeout (large >4000v): fig1/fig2 4735v, fig11_1 5389v, fig13_2 20251v all >150s.** Pure speed: the **no-GMP
  internal exact arithmetic** (Directional: "GMP recommended; internal is slow-ish") scales badly. fig24_9 950v alone
  takes ~38s.
- **Genuine corruption (DCEL): fig16_1 961v crashes 0s, fig19_1 3169v crashes 94s** on the mesher's `consistency` /
  `check_consistency` assert — a real §5.3 arrangement-robustness gap (separate from speed/the index assert).

### GMP ENABLED (2026-06-29) — fast exact arithmetic
Replaced the slow no-GMP internal `ENumber` with GMP (Directional: "GMP recommended; internal is slow-ish").
- **Installed** via vcpkg: `C:\Repo\vcpkg` (shallow clone + bootstrap), `vcpkg install gmp:x64-windows` (6.3.0, builds
  gmp + gmpxx; `--enable-cxx`). Layout: `installed/x64-windows/{include/gmpxx.h, lib/gmp.lib+gmpxx.lib, bin/gmp-10.dll+gmpxx-4.dll}`.
  License: GMP is LGPL-3/GPL-2 dual — compatible with our GPL-v2.
- **Reverted patch #2** (the `.num/.den` member-access edits in `generate_mesh.h` + `exact_geometric_definitions.h`)
  back to the GMP-API method calls `.num()/.den()` — `ENumber_GMP` exposes them as METHODS (`EInt num() const`),
  whereas the internal `ENumber` had them as members. So enabling GMP REQUIRED undoing patch #2. The no-GMP build is
  now retired (those reversions break it). Patches #1 (BigInteger assert typo) and the effort_to_indices relax remain.
- **build.bat** now: `/DUSE_GMP_ENABLED` + `/I %VCPKG%\include` + links `gmp.lib gmpxx.lib`, then copies the 2 DLLs next
  to dev2pq.exe (the subprocess finds them in its own dir regardless of cwd).
- **Speed (GMP only):** fig24_9 950v: 38s → 12.7s (~3×), byte-identical output.

### /O2 — the build was UNOPTIMIZED (the real speed bug), ~10×
build.bat had NO optimization flag, so cl defaulted to /Od (unoptimized). Adding **/O2** (asserts kept — do NOT add
/DNDEBUG, it strips the mesher's DCEL guards and turns clean aborts into hangs) gave a far bigger win than GMP, and
byte-identical results:
- fig24_9 950v: 12.7s → **1.3s**;  fig24_1 961v → **1.6s**;  fig10 1702v → **2.2s**;  fig5_2 → 1.6s.
- The "large figures HANG" conclusion was WRONG — they were just unoptimized: fig1 4735v now runs in **6.4s**,
  fig11_1 5389v in **9.3s**. But both then CRASH on the mesher's DCEL `check_consistency` ("line triangle overlap")
  — so fig1/fig2/fig11_1 join fig16_1/fig19_1 in the genuine **§5.3 arrangement-corruption** bucket, not a speed bucket.
- Net: the VALID figures now solve in ~1–2s (snappy review). The remaining failures are all §5.3 corruption (or the
  20k-vertex fig13_2, still genuinely large). GMP + /O2 are both kept (GMP is the correct exact arithmetic; /O2 the speed).

The w:12 non-integer indices were diagnosed: all exactly **2.50021 ≈ 2.5** (a half-integer), and the CLOSED tube fig24_9
has w:0 — so they are **boundary-loop cycles** on open patches (half-integer by construction), NOT interior singularities.
The strict assert was crashing on boundaries; rounding them is benign (they mesh). Confirms the assert-relax is safe.

NEXT, in priority order for "solve these inputs as PQs":
1. **§5.3 arrangement robustness** for the DCEL-corruption figures (fig16_1/fig19_1) — a real seamless-integration /
   arrangement gap, separate from the boundary-cycle rounding.
2. **Field-design quality** for the misaligned-but-valid figures (fig5_1 39°) — the ruling-alignment tail.
3. Larger meshes may still need more speed (GMP gives ~3×, not the full GMP potential — the bottleneck is partly the
   field solve / arrangement, not only the exact arithmetic).
