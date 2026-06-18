# CHA performance optimization

Optimization pass on `ComputeHingeEnergyAndGrad` (the Covariance Hinge Algorithm —
the per-solve hot path). Goal: drive per-call ms down without changing results.

**Method:** measure-first with the `CHAStats` per-phase tick breakdown
(`GradCheck.exe perf`), one change at a time, each gated on a **value-preservation
checksum** (`sumE`, `sum|g|`, an index-weighted gradient probe over the flow config
`covariance + deCraze=0.5, CrazeBand=0.1`) plus the FD gradient bench. `sumE` stayed
**bit-identical** through every commit; `sum|g|` matches to ~15 sig figs (the
pre-existing parallel-reduction ULP jitter); `deCraze` FD stayed `0.6% PASS` with
`BUG=0`; flow descent / scale-invariance / degeneracy / collapse all unchanged.

Bench host: 20 logical cores (CHA caps parallelism at `ProcessorCount − 2 = 18`).
Numbers are ms/call, mean of 20 timed iterations after warmup. ±~10% run-to-run.

## Results (ms/call, baseline → final)

| Mesh (verts) | covariance + grad (flow) | deCraze + grad (user flow) | energy-only (display tick) |
|---|---|---|---|
| Bunny 2.5k (1250)  | 2.93 → **1.33**  (2.2×) | 3.99 → **1.38**  (2.9×) | 2.08 → **0.45**  (4.6×) |
| Bunny 5k (2537)    | 6.75 → **1.91**  (3.5×) | 8.08 → **2.37**  (3.4×) | 4.15 → **0.89**  (4.7×) |
| Bunny 20k (10142)  | 24.02 → **8.09** (3.0×) | 31.94 → **10.37** (3.1×) | 16.82 → **2.38** (7.1×) |

Per-phase on Bunny 20k, covariance + grad (`facePc / vN / perV / L1 / other`):
baseline `10.5 / 1.7 / 9.8 / 0 / 2.1` → final `1.6 / 0.1 / 5.1 / 0 / 1.3`.

## What changed (each value-preserving)

1. **Halved face-loop cross products.** `dNdp` and `dTheta` shared the same three
   cross products computed twice, since `Cross(N,e) = −Cross(e,N)`. Compute each
   once (6 → 3 per face), bit-identical.
2. **Skip gradient scaffolds on the display path.** `dNdp`/`dTheta` are gradient-only
   but were computed *and allocated* when `wantGrad=false` (`EmitSnapshot`, every GH
   tick). Guarded behind `wantGrad` — the bulk of the energy-only speedup.
3. **Parallelized the precompute.** Face precompute, the two-pass vertex→face
   adjacency build, and the vertex-normal phase were serial (an Amdahl anchor in
   front of the already-parallel per-vertex loop). All three are scatter-free, so
   `Parallel.ForEach` over a range partitioner is fully deterministic. `facePc` on
   20k: 9.0 → 2.1 ms.
4. **Removed redundant per-face work in the covariance gradient.**
   `vertNormalsRaw[vert].Length` (a sqrt) was recomputed every face though it is
   loop-invariant — reuse `rawLenV` from the fold guard. `2·xNfw·θ` was computed
   twice (fvec, factorv) — compute once.
5. **Single-sqrt kink filter.** `RejectKinkOutliers` recomputed `grad[v].Length`
   ~4× per vertex; compute once into a `len[]` array.

## Considered and deliberately NOT done

- **Parallelizing the L1 (deCraze) edge loop.** It scatters to 2 endpoints' energy
  and 6 face-vertices' gradient, so it needs per-task accumulators. At these mesh
  sizes the ~18×nV accumulator allocation+zeroing would likely negate the ~2ms
  saving, and it would break `sumE`'s bit-identical determinism. Left serial.
- **Sourcing the tangent seed (`t1Hint`) from cached adjacency instead of Plankton
  halfedge calls.** Bit-identical on clean meshes and architecturally cleaner
  (decouples the hot loop from Plankton), but perf-neutral here and introduces a
  theoretical divergence at sliver-adjacent vertices during flow. Reverted.
- **Pooling per-task `gradLocal` arrays across calls.** Would cut GC pressure (helps
  Running-mode frame smoothness) but not raw ms (the zeroing cost remains), and the
  bench masks GC via `GC.Collect` before timing. Left for a future flat-array pass.
- **Caching the topology-invariant connectivity across iterations** (rebuild only on
  subdivide/collapse). Measured the prize first: the vertex->face adjacency build is
  only **0.1 / 0.2 / 0.4 ms** (2.5k / 5k / 20k) once parallelized — ~6% of the 20k flow
  call. Caching it would need a cache object + topology *and* sliver-flip dirty
  detection (the filtered adjacency excludes position-dependent slivers) threaded
  through the hot path, for ~0.4 ms. Bad trade post-parallelization; not done. `fvFlat`
  is pure topology but is rebuilt cheaply inside the face loop (3 StartVertex reads/face).

## Next levers (for a future pass)

- The per-vertex loop (`perV`, ~5 ms on 20k) is the floor now — already parallel and
  micro-optimized. Bigger wins need a **flat-array / SoA mesh** rewrite (drop the
  Plankton half-edge indexer + `List<int>` gather from the hot loop), which is also
  the prerequisite layout for a GPU port. See the standalone-solver discussion.
- L1 parallelization becomes worthwhile *with* the flat-array layout (per-task
  accumulators get cheap when reused across iterations).
