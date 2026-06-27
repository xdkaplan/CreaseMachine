# Overnight Architecture Review — 2026-06-26

**Lens:** code self-alignment with the node model (`docs/specs/DOC-SPEC.md`) — Real
ownership tree vs Transient dependency DAG, Ephemeral discipline, and every place the
code *cheats* that model. Plus the four standing rot-classes: stale code, architectural
cheats, leaky abstractions, embarrassingly-wrong implementations.

**Method:** one lead pass over the node-model core (`Transient.cs` / `Real.cs` /
`Pattern.cs` / `Doc.cs`) read by hand, plus six parallel read-only auditors over the
rest of the surface (Doc+Tx · View+render · interaction/Ephemeral · MainWindow god-file
· spec-vs-code staleness · engine). Every HIGH finding below was re-verified by hand
against the actual control flow before being written down — none is taken on an agent's
word. Read-only: nothing in the app was changed; this document and the runbook beside it
are the only output. The methodology is captured in `OVERNIGHT-REVIEW-RUNBOOK.md` so a
later, less-context-rich agent can reproduce it.

**Baseline:** master `cfdb1d0`. This review *extends* `docs/CODE-REVIEW.md` (the
2026-06-23 tracker) — it does not restate its known items except to reconcile their
current status (see §6).

---

> **Update 2026-06-26 (post-review):** the two HIGH findings **F-1 and F-2 are now FIXED** on this
> branch (commit `1bd8c92`) — `SubdivideCompute` no longer rebinds the live Doc Pattern while
> baking (`if (!_baking) RebindPattern()`), which closes both the off-thread graph mutation and the
> destroyed-partition consequence with one guard. The findings are kept below as written (severity,
> mechanism, reachability) for the record; treat them as resolved. Everything else stands.

## 1. Verdict

**The architecture holds.** The two-graph model is not aspirational hand-waving — the
Transient DAG is built, multi-level, and test-protected, and the Ephemeral/Command/tx
discipline the spec demands is intact across the interaction layer. The core is sound.

The rot is concentrated in **one systemic place and a handful of local ones**:

- **Systemic (the one real architectural breach):** the **Solve bake mutates Doc graph
  state off the UI thread** and leaves the Pattern↔mesh coupling inconsistent afterward.
  This is the single HIGH-severity violation of the model's single-writer rule, and it
  is *new* relative to the last audit (which generously logged the bake as "approximates
  single-writer"). Two concrete defects fall out of it (§4, F-1 / F-2).
- **Local:** the render path still runs on hand-set dirty-bits rather than the cascade
  (the "frame is a Transient" comment is ahead of the code); `Doc.Run` is still a second
  commit path; the selection isn't pruned after destructive deltas; two small tx-gate
  ordering gaps. All MED-or-below, all localized.
- **Pervasive but harmless:** **the docs are a full cycle behind the code.** Almost every
  spec/tracker says "designed, not built" for machinery that *shipped* (the cascade, the
  Grown/Supplied flavors, the `region→piece` sweep, piece-op journaling). This is the
  Tier-4 failure mode from last audit running *in reverse* — planned-as-pending for work
  that's done. Zero correctness impact, real onboarding cost.

---

## 2. The user's four questions, answered

### "How are we coming on the Real tree vs the Transient DAG?"

**Asymmetrically — and that's correct for where we are.**

- **Transient DAG: built and healthy.** `Node`/`Transient`/`Transient<T>` (Transient.cs)
  implement freshness (`IsFresh`/`IsStale`), the `RotDownstream` cascade (idempotent,
  diamond-safe — Transient.cs:31), and **both** refresh flavors: Grown (self-rebuilds on
  `.Value`, Transient.cs:53) and Supplied (producer-fed via `Supply`, read via `Peek`,
  :60/:64). The graph is genuinely **multi-level**: `Pattern → CreaseMap → CreaseLines`
  (Pattern.cs:43-47) is a two-hop chain — a `PieceMap` edit rots `CreaseMap`, which rots
  `CreaseLines` in turn — plus `Pattern → Geometry`. It is **test-protected**
  (`test/NodeModel` cascade tests, the DoD's "test-protected dependency tree"). This half
  of the model is real.

- **Real tree: a single node.** There is exactly **one** Real in the codebase today —
  `Pattern` (Pattern.cs:13). `Real` (Real.cs) carries the hooks for a tree (`Geometry`,
  `Invalidate`) but **not** `Parent`/`Children` — the ownership-tree axis is
  *designed-not-built* because there is nothing yet to compose. It materializes at **I4**
  (per-`Piece` identity, `Crease`-with-identity, `Spline`), which is correctly deferred.
  So "the Real tree" is today a stub: one authored node, no edges. That is the *right*
  sequencing — the DAG had to be trustworthy before the vocabulary scales to new node
  types — but it means the "tree vs DAG" symmetry the spec describes is, in code, "one
  node vs a working DAG."

**One-liner:** the DAG is the built half; the tree is a single node waiting for I4.

### "Do we have any ephemerals?"

**Yes — three, all clean.** None holds Real state (the trap the model warns about):

| Ephemeral | Where | Verified clean |
|---|---|---|
| `Selection<PieceId>` | `Doc.Pieces` (Doc.cs:69) | Not on undo/redo, not serialized, dropped by `Rebind` (Doc.cs:92). ✓ |
| Orbit `Camera` | `Camera.cs` | Pure orbit params + derived matrices/pick-rays; no mesh/Doc/Transient cached inside. ✓ |
| Gesture accumulators | `Piecer._touched`/`_selTouched`/`_growTouched` + the `_tx` lease | Preview-only; discarded on commit/cancel; the only persistent writes route through `_tx.Apply`. ✓ |

The Ephemeral layer is the **best-behaved part of the codebase** — the interaction
auditor found no Real state leaking into it, no mutation bypassing the tx, and no reach
past `IEditorHost`.

### "How many times are we cheating this document model?"

**Nine distinct deviations** (the "cheat ledger" — §3). But the headline number flatters
the danger and the danger flatters the number: **eight of nine are local and low-blast**,
and **one is the systemic breach** (off-thread graph mutation in the bake) that is worth
more than the other eight combined. The model's *core* — rot cascade, tx layer, Ephemeral
discipline — is not cheated anywhere. The cheats live at the **edges**: the bake
(threading), the render loop (still dirty-bits), and one by-design gap the spec itself
flagged (stale-Supplied `.Value`).

---

## 3. The cheat ledger

Every place the code deviates from `DOC-SPEC.md`, ranked. IDs are referenced by §4.

| # | Cheat | Severity | New? |
|---|---|---|---|
| F-1 | Bake worker mutates Doc graph state off the UI thread (single-writer breach) | **HIGH** | new · **FIXED `1bd8c92`** |
| F-2 | Pattern left coupled to the clone (not `_session`) after a subdivided Solve | **HIGH** | new · **FIXED `1bd8c92`** |
| F-3 | Stale-Supplied `.Value` returns stale data instead of throwing | MED | by-design gap (spec-acknowledged) |
| F-4 | "Frame is a Transient" — render runs on hand-set dirty-bits, not the cascade | MED | tracked (#8 render-loop) |
| F-5 | `Pattern.Geometry` is *produced* by MainWindow reaching across into the Real | MED | tracked (#8) |
| F-6 | `Doc.Run` is a second commit path, not `OpenTx→Apply→Commit` sugar | MED | tracked (#3) |
| F-7 | Selection not pruned after a carve consumes a whole selected piece → stale `PieceId` lingers | MED | new · **FIXED** |
| F-8 | `Doc.OpenTx` stale-tx auto-cancel runs *before* the Busy gate → can mutate Real mid-bake | MED | new |
| F-9 | `Doc.Rebind` drops `_open` without `Cancel()` → orphans an open tx | LOW | new |

---

## 4. Findings (detail)

### F-1 — [HIGH, NEW · FIXED `1bd8c92`] The bake worker mutates Doc graph state off the UI thread

**The model:** DOC-SPEC §8 — graph state (freshness, caches, the Store, undo/redo, edges)
mutates on **one** thread (UI/Doc); heavy work runs off-thread on an immutable snapshot
and re-enters only through `Supply`.

**The code:** `OnSolveAsync` runs the bake under `await Task.Run(RunBake)`
(MainWindow.xaml.cs:662). When **Subdivision level > 0**, `RunBakeSingle` (1147) and
`RunBakeMulti` (1181) call `SubdivideCompute()` → `RebindPattern()` (1290) →
`_doc.Rebind(new Pattern(_session.Mesh))`. `Doc.Rebind` (Doc.cs:92) **swaps the Pattern
Store, clears both undo and redo stacks, nulls `_open`, and `Pieces.ClearSilent()`** —
all on the **worker thread**.

`EnterBusy(Calculating)` (651) only makes `Run`/`OpenTx`/`Undo`/`Redo` *self-reject*; it
does **not** stop the bake itself from writing the graph. A `Doc.Changed` / `Pieces.Changed`
subscriber (`RebuildPieces`, wired at MainWindow:134-135) can therefore fire against a
half-swapped Pattern. Verified by hand: the `Task.Run` at 662, the `SubdivideCompute`
call sites at 1147/1181, and the `RebindPattern`→`_doc.Rebind` chain at 921/1290/92 all
line up. Gated on `SubdivLevel > 0` (a level-0 solve never reaches `SubdivideCompute`) — but
**`SubdivLevel` defaults to `2`** (SimSettings.cs:106), so it fires on the **default** Solve,
not an edge case. (Two independent re-runs of this review caught the default; the first pass
under-rated it as merely "reachable.")

**Why it matters:** this is the one place the codebase genuinely breaks the single-writer
invariant the whole node model rests on. The blast radius is a torn-Pattern read during a
display refresh — at worst an out-of-range index or a one-frame wrong piece view; at best
nothing, because timing usually hides it. That "usually hides it" is exactly why it
survived the last audit.

**Fix (applied `1bd8c92`):** simpler than marshaling — `SubdivideCompute` is shared between the
interactive subdivide (UI thread, where rewrapping the live Pattern is correct) and the bake
worker (where `_session` is a throwaway clone the Pattern must not follow). Guarding the rewrap
with `if (!_baking) RebindPattern()` removes the off-thread `Rebind` entirely and leaves the
authoring Pattern coupled to the authoring mesh across the bake — closing F-1 and F-2 together.
No frozen primitive touched (`Rebind` is a call site, not `Run`/`OpenTx`/`Apply`/`Invert`).

### F-2 — [HIGH, NEW · FIXED `1bd8c92`] Pattern is left coupled to the clone after a subdivided Solve

**The invariant** (stated in the code itself, MainWindow.xaml.cs:680): *"restore the
authoring session (Pattern still coupled to it)."*

**The code that breaks it:** the bake swaps `_session` to the derived develop mesh at 646
(`_session = new FlowSession(developMesh)`), then — when `SubdivLevel > 0` —
`SubdivideCompute` calls `RebindPattern()` (1290), re-pointing `_doc.Pattern` at a Pattern
that wraps the **subdivided clone**. At bake end, line 680 restores `_session = authoring`
**but never calls `RebindPattern` again**. So after a subdivided Solve, `_doc.Pattern._mesh`
is the *clone* (more faces, renumbered), while `IEditorHost.Mesh` resolves to
`_session.Mesh` = the *authoring* mesh. The comment at 680 now asserts an invariant the
code has falsified.

**Why it matters — and it's worse than "wrong indices":** the re-pointed Pattern over the
clone has a **null `PieceMap`** (only `Seed`/`Propose` populate one). The next display
refresh runs `RebuildPieces` → `EnsurePieceMap` (MainWindow.xaml.cs:1022), which sees
`PieceMap == null` (or a length mismatch) and calls `Seed()` — a whole-partition flood-fill
**Chapter reset**. So returning to the Pieces view after a subdivided Solve doesn't just
mis-index — it **silently destroys the user's painted partition** and re-seeds from scratch,
the exact outcome the "Solve develops a *derived* mesh, never the authoring mesh" design
exists to prevent. The comment at line 680 ("Pattern still coupled to it") asserts the very
invariant the code has falsified.

**Reachability:** same gate as F-1 (`SubdivLevel > 0`, **default 2**), so a default subdivided
Solve of a *pieced* mesh trips it; a Revert (which calls `RebindPattern` at 1333) heals it
afterward, but only if the user reverts before touching the Pieces view. Default-path, not
narrow. Same root cause as F-1: the bake's relationship to the graph.

### F-3 — [MED, by-design gap] Stale-Supplied `.Value` returns stale data instead of throwing

`Transient<T>.Value` (Transient.cs:51-54): a Grown transient regrows on read; a **Supplied**
transient with no grow func returns `_value` as-is even when `IsStale`. DOC-SPEC designs a
stale-Supplied `.Value` read to **throw** (a Supplied node can't make its own value, so an
assertive read of a stale one is a bug) — and the code comment at Transient.cs:49 admits it:
*"(Designed: a stale SUPPLIED read should throw.)"* Today it silently hands back the last
supplied value (or `default`/null if never supplied). Every current reader of a Supplied
transient correctly uses `Peek` (View.cs reads `Pattern.Geometry` via `Peek`), so this is
latent, not live — but it's a real safety rail the model specifies and the code omits.

### F-4 — [MED, tracked #8] "Frame is a Transient" is aspirational; render runs on dirty-bits

View.cs:72-75 comments that the rendered frame is a Transient rotted by `View.Rot()`. In
fact `View.Rot()` only calls `_gl.InvalidateVisual()` — it pokes the paint tick but
invalidates **nothing** in the graph. Refresh is actually driven by hand-set
`_meshDirty`/`_pieceDirty`/`_creaseDirty`/`_rulingsDirty` bools set at ~15 sites in
MainWindow. This is the unbuilt **Phase 3 · Task 9** (render-loop drain) from
`NODE-MODEL-CONVERGENCE.md` — known and tracked — but the comment oversells it as done.
The cascade does not yet reach the frame.

### F-5 — [MED, tracked #8] The Real's geometry is produced by the shell

`Pattern.Geometry` is correctly a **Supplied** Transient (Pattern.cs:23, read via `Peek` —
the right flavor and the right read). But its *producer* is `RebuildPieces` in MainWindow
(~1012), which builds the `RenderData` and calls `Pattern.Geometry.Supply(...)` — the shell
reaching across into the Real to feed its geometry. `RenderData` itself is clean (an
immutable float-array snapshot, not an alias of Real — so no silent staleness). The leak is
only *where the producer lives*: it belongs on/near the Real once the render loop drains
onto View (same Task 9).

### F-6 — [MED, tracked #3] `Doc.Run` is a second commit implementation

`Doc.Run` (Doc.cs:120-125) inlines `ApplyInternal + _undo.Push + _redo.Clear + RecordOps +
Changed`; `CloseTx` (134-151) is a *separate* commit path with its own composite-bundling.
The class comment (118) calls Run "open + apply + commit, atomically," but it never opens a
`Tx`. Two latent divergences: (a) Run pushes a raw `CompositeDelta` un-unwrapped where
CloseTx unwraps single-part lists — different undo granularity for the same logical op; (b)
Run fires `Changed` once, the tx path per `ApplyLive`. Not a correctness bug today (both
reach Pattern via `ApplyInternal`), but it is exactly the duplication the doc claims it
isn't. **Frozen layer — needs sign-off (D-3, already deferred in the convergence plan).**

### F-7 — [MED, NEW · FIXED] Selection not pruned after a carve consumes a whole selected piece

After a Ctrl-carve that **fully consumes a selected piece** (all its faces donated to neighbours
/ new islands), the selected `PieceId` in `Doc.Pieces` references an id no longer present in
`PieceMap`. Effect is **silent, not a crash**: `FaceFill`'s `Selected(piece)` just never matches
the phantom, but `Sel.Count` and the context-menu "N Pieces" header over-count, and a later
`Merge`/`DelPiece` carries a dead id (harmless — both filter by `PieceMap` membership).

**Scope (verified):** carve is the *only* leaking gesture. `Merge` already resets the selection to
the survivors (MainWindow.xaml.cs:942) and `DelPiece` clears it (MainWindow.xaml.cs:961); grow
preserves its selected ids and mint replaces them; `SplitDisconnected` keeps the selected id on the
*largest* island, so it never orphans. `Doc.Rebind` prunes on *re-mesh* (Doc.cs:92) but an
*in-place* delta had no equivalent.

**Fix (applied):** a `PruneSelection()` helper in the Piecer (`Sel ∩ PieceMap`) called at the end of
`CommitRemove`; it only re-`Set`s the selection (firing one rebuild) when an id was actually dropped.
Self-contained — no Doc-orchestration change (the `Selection<T>` is Ephemeral, not on the undo stack).

### F-8 — [MED, NEW] `OpenTx` stale-tx auto-cancel runs before the Busy gate

`Doc.OpenTx` (Doc.cs:110-116) cancels a leaked open tx (line 112, which calls
`InvertInternal` → mutates the live Pattern) **before** it checks `State != Busy.None`
(line 113). So if a tx is somehow open when a bake is running, the rollback mutation slips
through during `Busy.Calculating`. In practice `_open` should be null at bake start, but the
gate ordering doesn't guarantee it. Swap the two checks (gate first, then cancel). **Frozen
layer — sign-off required.**

### F-9 — [LOW, NEW] `Rebind` orphans an open tx

`Doc.Rebind` (Doc.cs:92) nulls `_open` directly without `Cancel()`, so a re-mesh mid-gesture
orphans an open `Tx` — harmless (the new Store invalidates its deltas) but it bypasses the
"every tx is opened and closed" invariant the class comment asserts. **Frozen layer.**

---

## 5. What's clean (so the next reviewer doesn't re-litigate it)

- **Channel discipline:** `Apply`/`Invert` mutate Real then `Invalidate()` (tx-then-rot, the
  correct order) — Pattern.cs:378/384. No rot writes Real; no Real→Real outside a tx.
- **Self-reject:** `Run`/`Undo`/`Redo` all gate on `Ready`; `OpenTx` returns a dead Tx when
  busy. No mutation entry point bypasses the gate (modulo F-8's ordering nit).
- **Commands are pure:** `Merge` and `DelPiece` compute an `IDelta` and mutate nothing;
  `Pattern.ComputeDelta` mutate-then-rolls-back cleanly (`PieceMap = before; Invalidate()`),
  so the in-place engines are reused as delta producers without leaking state.
- **The editor wall holds:** the Piecer reaches the host only through `IEditorHost` — no
  reach-around into MainWindow/View internals.
- **MainWindow respects the Doc surface:** it *calls* `_doc.Run`/`_doc.Undo` (the sanctioned
  API), never reimplements the commit path; the `_pattern` shadow field is gone (0 refs);
  the mid-bake `Execute` guard (396-400) holds.
- **The develop kernel is correct:** `IsometricLM` / `IsometricSmoothers` — threading is
  sound (race-free gather-by-vertex apply), divide-by-zero guarded throughout, no
  allocation in the CG inner loop, comments match code. No findings.
- **`RenderData` and `Camera`** match the model exactly (immutable snapshot; pure Ephemeral).

---

## 6. CODE-REVIEW.md reconciliation (status as of 2026-06-26)

The 2026-06-23 tracker, re-verified against current code:

| Item | 2026-06-24 status | **Now** |
|---|---|---|
| T1 #1 shadow `_pattern` | done | ✅ confirmed (0 refs) |
| T1 #2 `_session` mid-bake guard | done | ✅ confirmed (Execute 396-400) |
| T1 #3 `Doc.Run` two paths | open | ⛔ still open (F-6) |
| T1 #4 flow-param mutable statics | open | ⛔ still open — `CrazeBand` (DevelopabilityEnergy.cs:70) written by the **Propose** worker (Session.cs/CreaseMachine.cs:388) and read by a UI tick; **MainWindow is not the racer**, the GH component is. Real race, low blast radius. |
| T1 #5 BFF temp files + hardcoded exe | open | ⛔ still open (`Bff.cs:17` abs exe path; `:31-32` fixed `%TEMP%` names) |
| T2 #6 delete `studio/` | done | ✅ (stale `studio/bin`+`obj` artifacts still on disk — trivial sweep) |
| T2 #6 extract `CreaseEngine` lib | open | ⛔ still open (0 code hits; `src/` still source-copied into N csprojs) |
| T2 #7 `DisplaySource` | done | ✅ confirmed (occlusion unrepresentable) |
| T2 #8 View drain | partial | ◑ camera/picking/IEditorHost done; **render-loop → View still open** (F-4/F-5) |
| T2 #8 `BakeRunner` + reset-dedup | open | ⛔ still open — bake inline (~640 lines); "reset develop-state" block ×3 (449/647/1336); 2 modal setups (651/710) |
| T3 #9 engine dedup | done | ✅ |
| T3 #10 bench energy fenced | open | ⛔ still open (no `#if BENCH`; FD oracle ships in every front-end) |
| T3 #10 `gui/`+`repro/` → `attic/` | open | ⛔ still open |
| T4 DOC-TX self-contradiction | partial | ✅ now resolved (banner reframes Revision=target) — **can be closed** |
| T4 journaling out-of-scope | open | ✅ fixed in AGENTS.md ("shipped … replaying is the gap"); tracker line is stale |
| T4 `Commands.cs` desc | partial | ✅ AGENTS already fixed; `DOC-TX-REFACTOR.md:58` corrected 2026-06-26 (Merge+DelPiece; Carve/Grow/Mint are Pattern engines) |
| T4 stale `Transient<T>` note | open | ✅ resolved (`CreaseMap` is `Transient<HashSet<long>>`) |
| T4 naming (`region→piece`, `RegenCrease`) | partial | ✅ swept in code (0 hits for `NewRegionId`/`FullyMarked`/`RegenCrease`); `StudioCommand`/`CmdKind` intentionally deferred to the CreaseStudio promotion |

**Net:** the open *code* hazards are exactly the five the tracker already names (F-6/#3,
statics/#4, BFF/#5, CreaseEngine+BakeRunner/#6+#8, bench-fence+attic/#10) **plus the two
new bake/single-writer findings (F-1/F-2)**. Everything in the node-model / vocabulary /
journaling column **shipped**.

---

## 7. Docs are a cycle behind the code (the meta-finding, reversed)

Last audit's Tier-4 pattern was "specs written forward-looking, shipped partially, never
downgraded to as-built." It is now running **in reverse**: the work shipped, but the
planning docs still say "designed, not built." Status of the doc fixes (swept 2026-06-26):

- **`DOC-SPEC.md`** — ✅ §10 rows corrected: Grown/Supplied → "◑ behavioural (not formalized as
  types)", `.Value`-throws clarified, single-writer row updated to note the bake conforms after
  the F-1/F-2 fix. ⛔ *still pending:* the front-matter status (`:7`) ("the one thing built today
  is `Transient<T>` plus a single hand-wired edge") understates the shipped cascade + multi-level
  DAG — a prose rewrite left for a focused front-matter pass.
- **`CODE-REVIEW.md` / `HOMOGENIZATION.md`** — ✅ `CODE-REVIEW.md` carries the 2026-06-26
  re-verification pointer; `HOMOGENIZATION.md` got a "stale snapshot" banner pointing here.
- **`DOC-TX-REFACTOR.md`** — ✅ `:58` corrected (`Merge`+`DelPiece`; Carve/Grow/Mint are Pattern
  engines) and `:65` notes `RegenCrease→Invalidate`. The `RegenCrease` mentions in the Part-1
  code samples (`:124/:271/:302`) are left as period-accurate design history.
- **`HANDOFF.md`** — ⛔ *still pending:* `:59` says MainWindow is the `IEditorHost` (moved to
  View); `:105` cites `Pattern.RegionsConnected` (renamed `MergeGroups`). Left for the HANDOFF
  trim the prior audits already flagged.

These are documentation-only; none changes behavior. They are called out so the *next*
agent doesn't trust a stale "not built" and redo finished work.

---

## 8. Recommended next actions (priority order)

1. ~~**F-1 / F-2 — the bake's relationship to the graph (HIGH).**~~ ✅ **DONE `1bd8c92`** —
   `if (!_baking) RebindPattern()` in `SubdivideCompute`. Manual smoke still recommended: Solve a
   pieced mesh at `SubdivLevel ≥ 1`, return to the Pieces view, confirm the partition survives.
2. **Re-baseline the docs (§7).** ◑ partially done 2026-06-26 (§10, the trackers, DOC-TX `:58`);
   *remaining:* the `DOC-SPEC.md` front-matter prose and the `HANDOFF.md` trim.
3. ~~**F-7 — prune the selection after destructive deltas (MED, NEW).**~~ ✅ **DONE** —
   `PruneSelection()` in the Piecer, called from `CommitRemove` (carve is the only leaking gesture).
4. **The standing tracked items** — render-loop → View (F-4/F-5, the unblock for the
   Creaser), `Doc.Run` sugar (F-6, sign-off), the engine hazards (#4/#5), and the
   structural extractions (`BakeRunner`/`CreaseEngine`, reset-dedup) — unchanged from the
   tracker; sequence per `NODE-MODEL-CONVERGENCE.md` Phase 3/4.
5. **F-8 / F-9 (frozen layer)** — fold into the next sanctioned Doc-orchestration pass; not
   worth a standalone change.

---

*Companion: `OVERNIGHT-REVIEW-RUNBOOK.md` — how this review was run, so it can be repeated.*
