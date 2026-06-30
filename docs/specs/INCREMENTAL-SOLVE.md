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
| **SolvedPiece** | the developed geometry of ONE piece, at the active subdivision level. A **Supplied Transient** (DOC-SPEC §6) keyed by the piece's stable id. The per-`Piece` form of `Dev{level}` from [SOLVEDPIECE.md](SOLVEDPIECE.md). |
| **rot / stale** | a `SolvedPiece` marked invalid (`Transient.Rot()`), so the next Solve re-develops it. "Never developed" and "developed-then-rotted" are the same observable state (`IsStale`) — a born-stale Transient. |
| **touched ids** | the set of piece ids a delta affects = the union over its `Op`s of `{From, To}`. The delta-driven rot target. |
| **layout slot** | the placement of a piece's flat panel beside the model. Held per id so an unchanged panel doesn't jump when a *different* piece re-develops. |

## 4. The model — per-piece `SolvedPiece` Transients on `Pattern`

`Pattern` gains a keyed collection of Supplied Transients:

```
Pattern (Real; PieceMap)
   ├─ CreaseMap / Geometry / CreaseLines     (wholesale Transients — rot on ANY edit, as today)
   └─ Solved : Dictionary<int, SolvedPiece>  (one Supplied Transient per piece id; rot PER-id)
```

- `Solved` is **lazily populated**: the first time an id is seen (Solve/reassembly or a mint `Op`'s `To`),
  a **born-stale** `SolvedPiece` is created for it.
- A `SolvedPiece` is **Supplied**, never Grown — developing is the expensive async LM bake; readers `Peek`,
  the bake `Supply`s (exactly DOC-SPEC §6). It carries the developed mesh for the active level + its layout
  slot.

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
    sp = Solved.GetOrCreate(id)          // born stale if new
    if sp.IsStale:  develop piece id (BFF + IsometricLM, ×levels);  Supply sp (mesh @ slot)   // re-bake
    else:           reuse sp.Peek()                                                            // cache hit
  reassemble cached + freshly-developed panels  →  Supply _developed
  Console: "developed {k} / {n} pieces ({n-k} cached)"
```

- Honors **DOC-SPEC §9's generation guard** (the async bake): a Pattern edit *during* a bake cancels in-
  flight piece solves and re-rots, so a late panel can't land stale. (TAB-as-Solve forces the gen-guard
  regardless — SOLVEDPIECE §5.)
- **Layout stability:** a reused `SolvedPiece` keeps its prior **layout slot**; only re-developed / new
  panels are (re)placed. So untouched panels don't jump while you iterate on one seam.

## 8. Edge cases & tradeoffs

- **Undo/redo re-develops the touched pieces.** Inverting a delta rots the same touched ids → they re-bake
  on the next Solve, even though undo restored a state they were once developed in. This is the accepted cost
  of the in-graph delta-rot model (vs a content-fingerprint cache that would dedupe it). It only ever re-
  develops the *touched* pieces, never the whole model. *(Optional future: a content fingerprint stored
  under `SolvedPiece.Ensure()` could short-circuit a rot whose input matches the last bake — a pure
  optimization, not needed for correctness; see §10.)*
- **No warm-start.** A re-developed piece bakes from **Authoring** (re-BFF the piece, then LM), never from
  its old developed mesh — the seams/decomposition changed and the LM is non-convex (SOLVEDPIECE §4). Incremental
  changes *which* pieces bake, not *how* a piece bakes.
- **Subdivision level** is global (§5a): changing it rots all. A `SolvedPiece` caches the panel at the active
  level only (per-level retention is the SOLVEDPIECE §10 memory tradeoff — out of scope).

## 9. Build order (when scheduled)

Builds on SOLVEDPIECE's P1 (developed-as-Transient) + P2 (generation guard):

1. **`SolvedPiece` Supplied Transient + `Pattern.Solved` keyed collection** (born-stale, lazily created).
2. **Delta-driven per-piece rot** in `Pattern.Apply`/`Invert` (collect touched ids → `Solved[id].Rot()`);
   global rot on param/level/mesh change (§5a).
3. **Free-float bake reads/writes the cache** — `RunBakeFreeFloat` develops a panel only when its
   `SolvedPiece` is stale, else reuses; reassemble; Console feedback (§7).
4. **Layout slots** held per id for panel stability.
5. *(optional)* content-fingerprint short-circuit under `Ensure()` to dedupe undo/redo re-develops (§10).

## 10. Out of scope / open

- **Coupled partial** — infeasible (global welded solve); Coupled stays whole-mesh.
- **Soft / coupled seams** (the named seam-relaxation follow-up): if seams stop being frozen-per-piece, a
  piece's result depends on its neighbours → per-piece independence breaks → invalidation must widen to the
  **ring of pieces sharing a moved seam**, not just the touched piece. Re-spec when seam relaxation is built.
- **Content-fingerprint dedupe** of undo/redo re-develops (§8) — pure optimization, deferred.
- **Cross-session persistence** of `SolvedPiece`s — ids reassign on load (identity is session-only today);
  the cache is rebuilt by the first Solve after load.
- **Per-level retention** (instant TAB between subdivision levels) — SOLVEDPIECE §10, deferred.
