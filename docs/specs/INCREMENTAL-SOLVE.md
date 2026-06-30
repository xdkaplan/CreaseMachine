# Incremental Solve — only re-develop the pieces that changed

**Status:** *design / target.* Not built. Realizes the **per-`Piece` Dev** that
[SOLVEDPIECE.md §7](SOLVEDPIECE.md) deferred to "I4 (Piece-as-Real)", and supersedes its §4
("a Pattern edit rots the *whole* Dev chain") **for Free-float**. **Unblocked by stable per-piece
identity** (`Doc.MintId`, shipped 2026-06-30 — `efae03e`/`08128bc`): "which pieces changed" is only
well-defined because a surviving piece keeps its id across every op (verified by the per-op
identity-stability sweep). **Vocabulary settled live (propose→accept); treat as the user's.**

## 1. Goal

A piecing session is an **edit → Solve → look → edit → Solve** loop. Today every Solve re-develops the
**whole** model (split → BFF-flatten → IsometricLM → reassemble, ×subdivision levels) even if the user
moved one seam. Incremental Solve re-develops **only the pieces whose content changed since their last
develop** and **reuses the cached developed geometry** (and its flat-panel layout slot) for the rest —
turning an N-piece edit→Solve from `O(all pieces)` into `O(changed pieces)`.

## 2. Why now / why it's safe

- **Develop is already per-piece.** Free-float (`RunBakeFreeFloat`) unwelds along creases,
  `SplitComponents`, then BFF-flattens + isometric-develops **each piece independently with its boundary
  frozen (Dirichlet)** before `CombineMeshes`. So a piece's developed result is a pure function of *its own
  face-set + authoring geometry* — nothing from its neighbours. That independence is what makes per-piece
  caching correct (see §6).
- **Identity is stable.** A piece that survives an op keeps its id; only genuinely-new pieces mint fresh
  ids. So a `SolvedPiece` keyed by id is a stable cache slot across edits.

## 3. Vocabulary

| Term | Meaning |
|---|---|
| **SolvedPiece** | the developed geometry of ONE piece, at the active subdivision level. A **Supplied Transient** (DOC-SPEC §6) keyed by the piece's stable id. Retains its last-baked developed mesh, the **input hash** that produced it, and its layout slot. The per-`Piece` form of `Dev{level}` from [SOLVEDPIECE.md](SOLVEDPIECE.md). |
| **rot / stale** | a `SolvedPiece` marked invalid (`Transient.Rot()`), so the next Solve **re-checks** it — and re-develops it only if its **input hash** changed (§7). `Rot()` keeps the cached mesh (`Peek` semantics), so an unchanged-input stale piece can revalidate for free. "Never developed" and "developed-then-rotted" are the same observable state (`IsStale`) — a born-stale Transient. |
| **touched ids** | the set of piece ids a delta affects = the union over its `Op`s of `{From, To}`. The delta-driven rot target. |
| **input hash** | a cheap fingerprint of everything a piece's develop consumes: its **face-set** (sorted face ids carrying that id) + the **global salt** (Subdivision level, Accuracy/target-strain, IsometricLM weights, seam-pin, **Authoring-mesh version**). Authoring vertex positions are constant within a Pattern (a mesh change bumps the version + rebinds → all reborn), so they fold into the salt rather than being hashed per piece. Stored on the `SolvedPiece` at each successful bake; compared on re-check. |
| **layout slot** | the placement of a piece's flat panel beside the model. Held per id so an unchanged panel doesn't jump when a *different* piece re-develops. |

## 4. The model — per-piece `SolvedPiece` Transients on `Pattern`

`Pattern` gains a keyed collection of Supplied Transients:

```
Pattern (Real; PieceMap)
   ├─ CreaseMap / Geometry / CreaseLines     (wholesale Transients — rot on ANY edit, as today)
   └─ Solved : Dictionary<int, SolvedPiece>  (one Supplied Transient per piece id; rot PER-id)
```

- `Solved` is **lazily populated**: the first time an id is seen (Solve/reassembly or a mint `Op`'s `To`),
  a **born-stale** `SolvedPiece` is created for it (no mesh, no hash → always bakes the first time).
- A `SolvedPiece` is **Supplied**, never Grown — developing is the expensive async LM bake; readers `Peek`,
  the bake `Supply`s (exactly DOC-SPEC §6). It carries, from its last successful bake, the **developed mesh**
  (active level), the **input hash** that produced it, and its **layout slot**. `Rot()` marks it stale but
  **keeps the cached mesh** (`Peek` still returns it) — that retention is what lets an unchanged-input stale
  piece revalidate without re-baking (§7).

This is the per-`Piece` generalization SOLVEDPIECE §7 named; the **stable int id is the key**, so it needs
**no full `Piece` Real** — it's the bridge that brings the deferred feature forward.

## 5. Invalidation — delta-driven per-piece rot (the Store handles it)

The rot is driven by the **delta**, in the **Store's own `ITxAble`** — `Pattern.Apply`/`Invert` already
iterate the delta's `Op`s to write `PieceMap`. They additionally collect the **touched ids** (`{From,To}`
per Op) and **rot `Solved[id]` for exactly those ids**:

```
Pattern.Apply(delta) / Invert(delta):
    write PieceMap[op.Face]              (as today)
    touched ∪= {op.From, op.To}
  → Invalidate()                         (wholesale: CreaseMap/Geometry/CreaseLines rot, as today)
  → foreach id in touched: Solved[id]?.Rot()   (NEW: per-piece rot)
```

- **Coarse trigger, exact gate.** The delta-rot is deliberately *coarse* — it may rot a piece whose develop
  input turns out unchanged (and a global rot, §5a, rots all). The **input hash** (§7) is the exact gate: a
  rotted piece re-bakes only if its hash actually changed, else it revalidates for free. So the rot is
  allowed to be conservative — correctness comes from the hash, and **no bake is ever wasted.**
- **Frozen Doc primitives untouched.** `Run`/`OpenTx`/`CloseTx`/`Undo`/`Redo`/`Record` are unchanged — they
  call `Pattern.Apply`/`Invert` exactly as before. Per-Store derived-state rot is the Store's job, the same
  place `Invalidate()` already lives. (This is the one delta-aware addition; it is *additive* and frozen-safe,
  the same shape as the existing `CreaseMap` rot.)
- **Neighbour coupling is captured for free.** Moving face `f` from piece 3→5 yields an `Op(f,3,5)`, so BOTH
  3 (shrank) and 5 (grew) are touched → both rot. A piece whose face-set is genuinely untouched appears in
  no `Op` → stays fresh → reused. (In Free-float its frozen boundary loop is then identical, so reuse is
  exact — §6.)
- **Mint / delete.** A mint's `To` is a new id → its (born-stale) `SolvedPiece` develops. A fully-donated
  piece's id vanishes from `PieceMap` → reassembly skips it and its `Solved` entry is dropped.

### 5a. Global invalidation (rot all)

Some changes invalidate **every** piece, so they rot the whole `Solved` collection (or bump a generation
that makes all entries stale):

- a **Solve-param** change — Accuracy/target-strain, **Subdivision level**, IsometricLM weights, seam-pin;
- the **Authoring mesh** changing — load / revert / authoring-subdivide / `RebindPattern` (new mesh → fresh-
  born-stale `Solved`, exactly as the wholesale Transients are reborn today).

## 6. Scope — Free-float only; Coupled stays whole-mesh

Per-piece caching is correct **iff** a piece's developed result is independent of its neighbours:

- **Free-float** freezes each piece's own boundary and develops it alone → independent → cacheable. ✅
- **Coupled** is a single **welded global** solve (crease verts excluded from smoothing, but one connected
  system) → a change anywhere couples through the whole mesh → **not separable**. Coupled keeps the
  whole-mesh re-solve (SOLVEDPIECE §2–4 unchanged). ✅

This matches Free-float being the chosen develop direction.

## 7. Solve / TAB

TAB-to-Developed is the Solve trigger (SOLVEDPIECE §5). Incremental just makes the `Ensure` cheap:

```
Solve (Free-float):
  foreach id in current PieceMap ids:
    sp = Solved.GetOrCreate(id)                       // born stale if new
    if sp.IsFresh:                                    // (1) not rotted at all
        reuse sp.Peek()
    else:                                             // stale: rotted by a delta, global rot, or born-stale
        h = inputHash(id)
        if sp.HasBake && h == sp.BakedHash:           // (2) EXACT-GATE hit: input unchanged
            revalidate(sp)                            //     cheap — clear stale, keep cached mesh; no bake
        else:                                         // (3) input changed (or never baked)
            develop piece id (BFF + IsometricLM, ×levels)
            sp.Supply(mesh @ slot); sp.BakedHash = h  //     re-bake; store mesh + the hash that made it
  reassemble (cached + revalidated + freshly-developed panels)  →  Supply _developed
  Console: "developed {baked} / {n} pieces ({revalidated} revalidated, {fresh} cached)"
```

Three tiers: **(1)** never rotted → reuse; **(2)** rotted but input hash matches the last bake → free
revalidate (the escape hatch); **(3)** input actually changed → re-bake. Only tier (3) pays the LM cost.

- Honors **DOC-SPEC §9's generation guard** (the async bake): a Pattern edit *during* a bake cancels in-
  flight piece solves and re-rots, so a late panel can't land stale. (TAB-as-Solve forces the gen-guard
  regardless — SOLVEDPIECE §5.)
- **Layout stability:** a reused `SolvedPiece` keeps its prior **layout slot**; only re-developed / new
  panels are (re)placed. So untouched panels don't jump while you iterate on one seam.

## 8. Edge cases & tradeoffs

- **Undo/redo is usually free.** Inverting a delta rots the touched ids, but the input-hash gate (§7) then
  revalidates any whose input matches their last bake — no re-develop. The one case that *does* re-bake:
  undo to a state **older** than a piece's last bake (e.g. `A→B→undo`, where the piece's last bake was `B`,
  so `hash(A) ≠ BakedHash`). It re-bakes once, and only that piece. A per-piece hash→mesh **history** would
  remove even that (§10, optional) — the single-last-bake baseline already makes the common loop (edit one
  seam, undo, redo) cost at most one piece's bake.
- **No warm-start.** A re-developed piece bakes from **Authoring** (re-BFF the piece, then LM), never from
  its old developed mesh — the seams/decomposition changed and the LM is non-convex (SOLVEDPIECE §4). Incremental
  changes *which* pieces bake, not *how* a piece bakes.
- **Subdivision level** is global (§5a): changing it rots all. A `SolvedPiece` caches the panel at the active
  level only (per-level retention is the SOLVEDPIECE §10 memory tradeoff — out of scope).

## 9. Build order (when scheduled)

Builds on SOLVEDPIECE's P1 (developed-as-Transient) + P2 (generation guard):

1. **`SolvedPiece` Supplied Transient + `Pattern.Solved` keyed collection** — born-stale, lazily created;
   carries `{mesh, BakedHash, slot}`; `Rot()` keeps the mesh.
2. **Delta-driven per-piece rot** in `Pattern.Apply`/`Invert` (collect touched ids → `Solved[id].Rot()`);
   global rot on param/level/mesh change (§5a).
3. **`inputHash(id)`** — sorted face-set + global salt (§3); cheap, recomputed at the re-check.
4. **Free-float bake = the three-tier loop** (§7): reuse-fresh / revalidate-on-hash-match / re-bake-else;
   `RunBakeFreeFloat` develops a panel only on tier (3); reassemble; Console feedback.
5. **Layout slots** held per id for panel stability.
6. *(optional)* per-piece hash→mesh **history (LRU)** to make *deep* undo free too (§10).

## 10. Out of scope / open

- **Coupled partial** — infeasible (global welded solve); Coupled stays whole-mesh.
- **Soft / coupled seams** (the named seam-relaxation follow-up): if seams stop being frozen-per-piece, a
  piece's result depends on its neighbours → per-piece independence breaks → invalidation must widen to the
  **ring of pieces sharing a moved seam**, not just the touched piece. Re-spec when seam relaxation is built.
- **Per-piece hash→mesh history (LRU)** — the baseline keeps only the *last* bake's `(hash, mesh)`, so undo
  to an *older* state re-bakes once (§8). A small per-piece LRU of past bakes would make deep undo/redo free
  too, at a memory cost. Pure optimization on top of the baseline hash gate — deferred.
- **Cross-session persistence** of `SolvedPiece`s — ids reassign on load (identity is session-only today);
  the cache is rebuilt by the first Solve after load.
- **Per-level retention** (instant TAB between subdivision levels) — SOLVEDPIECE §10, deferred.
