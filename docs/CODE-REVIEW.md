# Code Review — Architecture Audit (status tracker)

**Audit:** 2026-06-23 — six read-only investigators, one lens each: double-work,
optimizations, leaky abstractions, poorly-defined bounds, unclear contracts,
inconsistent language. Read-only; nothing was changed during the audit itself.
**Status as of:** 2026-06-24. Legend: `[x]` done · `[ ]` not yet · `[~]` partial.

**Verdict (unchanged):** the core architecture is genuinely sound — all agents
confirmed the five-role law holds, self-reject is consistent, the glossary is
"locked" *in code*. The rot is concentrated in three places: a few small real
hazards (Tier 1), the `studio/` duplication (Tier 2 #6), and docs that describe
the *target* as if *built* (Tier 4).

## Snapshot

- **Tier 3 essentially cleared.** Engine dedup (#9) done 2026-06-24
  (`f147074`→`8547b8c`, value-identical — bench `sumE` bit-identical, FD
  classifications identical pre/post). The three dead-code removals (#10) done
  earlier this session.
- **Tier 1 — 2 of 5 done** (#1 shadow `_pattern`, #2 `_session` mid-bake guard); 3 hazards still open.
- **Tier 2 — `studio/` ✓, `Picker` ✓, `DisplaySource` ✓ (View standup), + the whole View drain
  (camera/picking/IEditorHost → `View`) landed 2026-06-24**; `CreaseEngine` library + `BakeRunner`
  + reset-state dedup still open.
- **Tier 4 — partly closed**: "dirty bit" killed + the Real/Transient/Ephemeral vocabulary now
  aligned across AGENTS.md / DOC-TX-REFACTOR / DoD / DOC-SPEC; "journaling out-of-scope", the
  DOC-TX self-contradiction, and the naming items still open.

## Tier 1 — real hazards (small fixes, worth doing soon)

- [x] **1. Shadow `_pattern` field** — `MainWindow._pattern` parallels
  `Doc.Pattern`, kept equal only by convention. → delete it, route
  `IEditorHost.Pattern` through `_doc.Pattern`. *(Agent A#1)*
  — **done 2026-06-24:** field deleted; all reads + `IEditorHost.Pattern` route
  through `_doc.Pattern`; `RebindPattern` goes through `_doc.Rebind`. `_doc.Pattern`
  is now the single authority (0 `_pattern` refs).
- [x] **2. Unguarded `_session` swap during bake** — `OnSolveAsync` swaps
  `_session` to a clone and restores in `finally`, but the mesh-index slider /
  `ApplyLoad` aren't gated on `_baking`; a mid-bake load overwrites `_session`,
  then `finally` restores the *stale* authoring mesh. *(Agent B#3 — latent bug)*
  — **done 2026-06-24:** one guard at the `Execute` chokepoint rejects Load/Subdivide/Revert
  while `_baking` (replay-safe). All mesh-changers route through there.
- [ ] **3. `Doc.Run` duplicates the commit path** — it reimplements
  apply+push+RecordOps instead of being `OpenTx→Apply→Commit` sugar (which the
  doc *claims* it is). → make Run actual sugar. *(Agent A#2)*
  — *verified: `Run` (Doc.cs:120) and `CloseTx` (Doc.cs:134) are still two
  separate commit implementations.*
- [ ] **4. Flow params via mutable statics** — `CrazeBand`/`AdaptiveDetMix` are
  process-global statics on `DevelopabilityEnergy`; the worker thread *and* a GH
  display tick both touch them → thread-safety hazard. → pass as params/config.
  *(Agent C#4)* — *verified still `public static` (DevelopabilityEnergy.cs:47,70).*
- [ ] **5. BFF shared temp files + hardcoded exe path** — fixed
  `%TEMP%\patchsolver_bff_*.obj` + an absolute `C:\Repo\GeometryCollective\…`
  exe path. Safe only because flatten is serial today; the obvious
  "parallelize per-panel" optimization silently corrupts. → per-call temp names
  + configurable exe path. *(Agent D#1)*

## Tier 2 — structural (the big ones)

- [~] **6. `studio/` deletable + no engine library.** *(Agents F#1-3,#6, E#5)*
  - [x] delete `studio/` — done `b1b37f5` (its unique work had already leaked
    into PieceSolver; remainder in git history).
  - [ ] extract a **`CreaseEngine`** library (multi-target `net48;net8.0`) so
    `src/` stops being source-copied into 4 `.csproj`s with hand-maintained
    `<Compile>` lists. GH-host code (`src/CreaseMachine.cs`) stays out.
- [x] **7. "Which mesh is displayed" is implicit** → a single `DisplaySource` enum. *(Agents B#1, D#8)*
  — **done 2026-06-24 (View standup, `b977db1`):** `DisplaySource { Authoring, Pieces, Developed }` on
  the `View` replaced the scattered `_showPieces` / upload / `ShowProposedMesh`; the occlusion is now
  unrepresentable (Solve sets `Display=Developed`). `ShowProposedMesh` removed entirely.
- [~] **8. MainWindow god-file seams.** *(Agent B#5,#7,#2,#11,#12)*
  - [x] ray-pick math → `Picker.cs`.
  - [x] **the View drain (2026-06-24):** display state, orbit `Camera`, picking, brush-footprint,
    and the whole `IEditorHost` role moved onto `View`/`Camera`; MainWindow is now the chrome shell
    + render loop. (See `project_view_abstraction` memory.) **Next: render-loop → View.**
  - [ ] the ~640-line bake engine (`OnSolveAsync`/`RunBake*`/`DevelopPiece`/…) → a `BakeRunner`.
  - [ ] dedupe the 3 copies of "reset develop-state" + the 2 modal-bake setups.

## Tier 3 — engine dedup + dead code

- [x] **9. Copy-paste in the engine** — done 2026-06-24.
  *(Agents C#1,#2,#5,#11, A#7, F#9)*
  - [x] `UniformSubdivide` GH↔MeshOps → one (`MeshOps.UniformSubdivide`).
  - [x] union-find + halfedge-walk boilerplate → `UnionFind` +
    `ForEachInteriorEdge` (each caller keeps its own predicate).
  - [x] `RepresentativeEdge` ×3 → one (`MeshOps.RepresentativeEdge`).
  - [x] acos-clamp idiom ×3 → one (`Vec3.SafeAcos`).
  - [x] `DeCrazeMax=0.04` ×N → one engine constant (`DevelopabilityEnergy.DeCrazeMax`).
- [~] **10. Dead/legacy code.** *(Agents D#2, A#8, B, C#8, F#4,#12)*
  - [x] GL_LINES ruling overlay in `MeshView` (unreachable) — removed.
  - [x] `Pattern.Paint` — removed.
  - [x] legacy `Run`/`ApplyRun`/`PatchStep` replay path — removed.
  - [ ] bench-only energy machinery shipped in every front-end → fence into the
    bench build. *Keep, don't delete — it is the FD correctness oracle; the goal
    is `#if BENCH`/partial so it isn't compiled into the shipping front-ends.*
  - [ ] `gui/` (legacy, subprocess GUI) → `attic/`. *(verified still present.)*
  - [ ] `repro/` (frozen racking experiment) → `attic/`.

## Tier 4 — docs & naming coherence (the meta-finding)

Dominant pattern: specs were written forward-looking and shipped *partially*,
but never downgraded to as-built.

- [ ] **`DOC-TX-REFACTOR.md` contradicts itself** — the auto-merged "Revision
  supersedes Parts 1–2" claim fights its own Shortcomings list (the op-log did
  *not* collapse the undo stacks; two journals remain). → reframe Revision as
  *target*, Parts 1–2 + Shortcomings as *as-built*. *(Agent E#1 — top doc fix)*
  — *sidework revised Part 3 + Shortcomings (`8627c65`); re-check whether the contradiction is fully resolved.*
- [x] **"dirty bit" in AGENTS.md** — the banned word, in the canonical glossary.
  *(Agent E#2)* — **done 2026-06-24:** the AGENTS.md Transient definition now uses the
  Fresh/Stale + Grow/Supply model (no "dirty bit" / "derives-from dependency"); cross-refs
  `docs/specs/DOC-SPEC.md`. `DOC-TX-REFACTOR.md` + `DEFINITION-OF-DONE.md` aligned too.
- [ ] **"journaling of piecing" listed out-of-scope though it shipped.**
  *(Agent E)* — *verified AGENTS.md:294; the journal landed via the concurrent
  agent (`49ecdd0`…`5836ee2`).*
- [ ] **`Commands.cs` described as holding Delete/Carve/Grow/Mint** — it holds
  `Merge` and `DelPiece` (delpiece merged); Carve/Grow/Mint stay `ComputeDelta`-in-Piecer. *(Agent E#4)*
  — *AGENTS.md now reads correctly (Pattern owns those engines); re-check
  `DOC-TX-REFACTOR.md`.*
- [ ] **stale `Transient<T>` merge-debt note.** *(Agent E#6)*
- [~] **Real/Transient/Ephemeral redefined in 4 docs** with drift. *(Agent E#3,#12)*
  — *AGENTS.md / DOC-TX-REFACTOR / DoD / DOC-SPEC aligned to one vocabulary 2026-06-24; re-check HANDOFF.md.*
- [ ] **Naming:** `region` vs `piece` (in `PieceMap`/`NewRegionId` vs the locked
  "piece"); `StudioCommand`/`CmdKind` in an app named PieceSolver; `Rot()` vs
  stale/regen vocabulary; the Reset/Revert split undocumented; three senses of
  "studio" (partly eased by the `studio/` removal). *(Agents A#8,#12, F#7, E#8,#11)*

## Low-confidence (noted, not urgent)

- [ ] latent multi-part-`Changed` asymmetry.
- [ ] a `continue` that gates regularizers.

---

*Filename proposed — rename if you'd prefer `AUDIT.md` / `CLEANUP.md`.*
