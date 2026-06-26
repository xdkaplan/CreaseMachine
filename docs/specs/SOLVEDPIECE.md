# SolvedPiece — the Dev chain (developed results as Supplied Transients)

**Status:** *design / target.* Not built. This realizes the **`SolvedPiece`** concept deferred in
[DOC-SPEC.md](DOC-SPEC.md) §9 (and the self-kicking **Supplied** flavor in §6), and builds directly on the
node-model convergence ([NODE-MODEL-CONVERGENCE.md](NODE-MODEL-CONVERGENCE.md)) — the rot cascade, the
governed `_developed`, and TAB. **Vocabulary here was settled live (propose→accept) and is the user's; treat
it as locked.** Today's form is **Pattern-level**; the per-`Piece` form is the I4 gateway (§7).

## 1. Vocabulary

| Term | Meaning |
|---|---|
| **Authoring** *(= "unDev")* | the raw input mesh, **undeveloped**. Always valid. |
| **Pattern** | the partition painted on Authoring — **Pieces** (face regions) separated by **Creases** (boundaries). A Real (`PieceMap`). Edited by the **Piecer**. *(The **Creaser** — creases-with-identity — is a future editor; today creases are derived.)* |
| **Dev0 … DevN** | the Authoring mesh **developed** at **N subdivisions** (N = the subdivision count). `Dev0` = developed, *zero* subdivisions — it **is** solved, just not subdivided (`Dev0 ≠ Authoring`). |
| **Developed** | the live result shown opposite Authoring = `Dev{selected level}`. |

## 2. The dependency chain

```
Authoring (mesh, upstream of Pattern)
   └─ Pattern  (Real; PieceMap)
        └─ Dev0  (developed @ 0 subdiv)
             └─ Dev1  (subdivide Dev0 + re-develop)
                  └─ Dev2 … DevN
```

- The bake **already** computes this chain in order (`RunBakeSingle`: `SolveToAccuracy()`→`Dev0`, then
  `for lvl: SubdivideCompute() + SolveToAccuracy()`→`Dev1…DevN`); `Dev{n}` is built **from** `Dev{n-1}`.
- **Authoring is upstream of Pattern** — re-piecing mutates the Pattern, **not** the Authoring mesh. So
  Authoring (unDev) is unaffected by Pattern edits and is *always* valid.

## 3. The Devs are **Supplied Transients** (self-kicking)

- **Supplied, not Grown.** Developing is an expensive **async** multi-pass LM solve; you must never run it
  synchronously on a `.Value` read. The Solve bake **produces** each Dev and `Supply`s it; readers `Peek`.
  This is exactly DOC-SPEC §6's "self-derivable-but-expensive ⇒ Supplied" case. `_developed` is **already**
  one such Supplied Transient — SolvedPiece **generalizes the single `_developed` into the `Dev0…DevN`
  chain.**
- **Self-kicking via `Ensure()`.** A Dev owns an `Ensure()` that, when stale, launches its bake and `Supply`s
  on completion. (DOC-SPEC §6 leaves self- vs. external-kick as a sub-detail; self-kicking fits the
  Solve-button-as-TAB shape below.)
- **Born early, born stale.** The `Dev` chain are fields on `Pattern`, constructed *with* it. A
  freshly-constructed Supplied Transient is **already `IsStale`** (no value, `IsFresh=false`) — so there is
  **no "pre-Rot"**; "never fed" and "fed-then-rotted" are the *same observable state*, `IsStale`. When
  `Pattern` is rebound (mesh change), the new Pattern gets fresh-born-stale Devs.

## 4. Invalidation: a Pattern edit rots the whole Dev chain

```
Pattern.Invalidate()  →  Dev0.Rot()  →  Dev1.Rot()  →  …  →  DevN.Rot()
```

After a Pattern edit, **only Authoring (unDev) is fresh**; every Dev is stale until re-Solved. This is the
ordinary rot cascade — wiring the Devs downstream of `Pattern` is what makes it automatic (no special-casing,
and it closes the same stale-result class of bug we fixed for `_developed`).

**No warm-start (re-develop from Authoring).** You cannot seed the new solve from the old Dev across a Pattern
change: the develop co-refines M + M′ toward isometry **per-piece with the seams frozen (Dirichlet)**; change
the Pattern and the seams + piece-decomposition change, so the old developed M no longer satisfies the new
constraints. Because the solve is **non-convex**, seeding from it doesn't just cost iterations — it can settle
into a *different/worse local minimum* (mesh "drift"). The canonical, reliable start is the **Authoring mesh**
(re-BFF-flatten the new pieces, then develop). So Authoring↔Dev is **back-and-forth, re-baked each time.**

- **Future exception (gated behind the Creaser, not built):** *adjusting a crease* is the one edit where
  warm-starting `Ensure()` from the prior Dev (instead of re-baking from unDev) may be worth it — and even
  then only behind a strain-gate that verifies it didn't drift. Out of scope until the Creaser exists.

## 5. TAB is the Solve button

TAB flips the base view **Authoring ↔ Developed**, and **flipping to Developed *is* the Solve**:

```
TAB → Developed :  show( Dev{level}.Ensure() )   // if stale → bake, then show; the single trigger is IsStale
TAB → Authoring :  show( unDev )                 // always valid, instant
```

- The standalone **Solve button retires into TAB** (one fewer top-level control — in line with the
  "converge on fewer options" preference).
- Because `Ensure()` is async, this **requires DOC-SPEC §9's generation guard**: a Pattern edit *during* a
  bake cancels the in-flight solve and re-rots, so a late result can't land stale. TAB-as-Solve is what
  *forces* the gen-guard (the one piece §9 deferred).

## 6. Show the Pattern *on* the Dev

When TAB shows a Dev, the **Pattern overlay (piece colours + crease wires) renders on the developed mesh.**
Develop preserves face-topology, so the `PieceMap` maps straight onto `Dev0`. For a subdivided `DevN`, the
`PieceMap` is **propagated through the 1→4 subdivision** (each face's 4 children inherit the id) — itself a
small derived Transient. So "Pattern on a Dev" is a display *composition* (developed geometry +
`Pattern.Geometry`/`Pattern.CreaseLines` re-mapped), fully supported by the model — no new authored state.

## 7. Per-`Piece` future (I4)

Today the Devs hang off **`Pattern`** because `Piece` is not a Real yet (it's an int in `PieceMap`). When
`Piece` becomes a Real (I4), a Dev can become **per-Piece** (each piece's own developed geometry, its own
`Ensure()`), which is the literal "`SolvedPiece`" of DOC-SPEC §9 and enables per-piece re-solve / partial
invalidation. Until then, **Pattern-level Dev chain** is the correct, simpler form.

## 8. Optional capstone — progressive `Dev0→…→DevN` display

Surface each level as it bakes (coarse→fine refine in real time), per DOC-SPEC §9 "progressive loading = several
`Supply` calls." **Feasibility (code-grounded):** the bake already loops the levels and already marshals
worker→UI via `_bakeProgress` (a `Progress<T>`); the MVP is ~15–25 LOC — widen that callback to carry a mesh
**snapshot** and, in the UI-thread handler, `Supply` the current Dev + upload. **Costs (the care, not the LOC):**
(a) a **clone per level** — the worker keeps mutating `_session` (next `SubdivideCompute`), so the UI must get a
stable snapshot (#2-hazard discipline); upload+discard each (don't retain all — subdiv-4 is 256× faces);
(b) the **generation guard** — but TAB-as-Solve needs it anyway, so progressive adds ≈0 over the base work.
**So it's a small rider on the SolvedPiece build, gated behind the gen-guard — not a separate feature.**

## 9. Build order (when this is scheduled)

Builds on the convergence's P4 (developed-as-Transient) — do that first, then:
1. **Dev chain as Supplied Transients on `Pattern`** (`Dev0…DevN`), downstream of `Pattern` (cascade rots them).
2. **Generation guard** (DOC-SPEC §9) — epoch/cancel for the async bake.
3. **`Ensure()`** (self-kicking) + **TAB-as-Solve** (retire the Solve button); TAB→unDev always valid.
4. **Pattern-on-Dev** display composition (incl. PieceMap-through-subdivision for `DevN`).
5. *(optional)* **progressive Supply** (§8).
6. *(I4, later)* per-`Piece` Devs.

## 10. Out of scope / open

- Warm-start (crease adjustment) — behind the unbuilt Creaser (§4).
- Keeping all `Dev0…DevN` simultaneously for instant TAB-between-*levels* — today only Authoring↔Developed(=Dev{level}); per-level retention is a memory tradeoff, defer.
- The `Pattern.Invalidate` cascade reaching the Devs assumes the Devs are registered downstream of `Pattern`
  (additive, frozen-safe — same as `CreaseMap`/`Geometry`/`CreaseLines`).
