# Homogenization Audit — docs + architecture vs specs (2026-06-25)

**Purpose.** Before building bigger, messier tools, make the approach *homogeneous* — one model,
one vocabulary, docs that match the code. **Method.** Four read-only investigators, one lens each:
(1) as-built doc accuracy, (2) vocabulary/naming, (3) architectural pattern coherence, (4) spec
reconciliation. Read-only; nothing was changed during the audit. This report is the synthesis and
**feeds [`CODE-REVIEW.md`](CODE-REVIEW.md)** — many items corroborate its open Tier-1/2/4 entries.

## The throughline

The **node model** (Real/Transient/Ephemeral + rot cascade), the **Doc/tx undo layer**, and the
**View/IEditorHost** abstraction are coherent, small, well-documented, and ready to build on.
**`MainWindow.xaml.cs` (~1942 lines) is where the un-homogenized state hides** — the develop
pipeline, the BFF flat-map subsystem, and a parallel render dirty-flag PUSH system all sit *outside*
the node model as raw fields. The docs are mostly accurate but carry the classic "target described
as built" drift in a handful of named spots. **None of the high-value architecture fixes touch the
frozen Doc/tx primitives.**

Two places where homogenization has **already fully succeeded** — use them as the template for the
rest: the **Upstream/Downstream (DAG) vs Parent/Child (tree)** axis, and the **Transient
`Supply`/`Peek`/`Grow`** API.

---

## Priority A — As-built doc drift (cheap, no code risk)

| Doc:loc | Claim | Reality | Sev |
|---|---|---|---|
| `AGENTS.md:268`, `HANDOFF.md:105` | `Pattern.RegionsConnected` is a current query | **Symbol does not exist** (grep: 0 hits). The real query is `Pattern.MergeGroups` (`Pattern.cs:386`) | High |
| `AGENTS.md:267`, `DOC-TX-REFACTOR.md:301` | `Pattern.FullyMarked` | Renamed → `MostlyMarked` (`Pattern.cs:426`); 0 hits for old name in code | High |
| `AGENTS.md:301` | "journaling of piecing" is *out of scope* | It **shipped** (`Doc.cs` op-log; DOC-TX Part 3 Slice A = done). Already tracked `CODE-REVIEW.md:111` | High |
| `DOC-SPEC.md:190, :207` | Transient method is `Set` | It's `Supply` (`Transient.cs:64`). DOC-SPEC §12 itself says the `Set→Supply` rename is done — **self-contradiction** | Med |
| `HANDOFF.md:108`, `CODE-REVIEW.md:114` | `Commands.cs` holds only `Merge` | Now `Merge` **and** `DelPiece` (`Commands.cs:27`) — delpiece merged | Med |
| `AGENTS.md`, `README.md:72` | `NOTICE.md` at repo root | File is `docs/NOTICE.md` | Med |
| `README.md` | `lib/Plankton.dll` is "stock, unmodified upstream" | It's a **netstandard2.0 rebuild** (zero code changes) per AGENTS/NOTICE | Low |
| `DOC-TX-REFACTOR.md` | Revision "supersedes Parts 1–2" | Fights its own Shortcomings list (op-log did *not* collapse the undo stacks). Reframe: Parts 1–2 + Shortcomings = as-built, Revision = target. **Top doc fix** (`CODE-REVIEW.md:104`) | Med |
| `HANDOFF.md` §1 | Re-narrates PieceSolver/Doc/tx/Solver-phase | Duplicates AGENTS (a source of the "Real/Transient defined in 4 docs" drift). Trim §1 to pointers; keep §§4–9 (its unique flow-engine lessons) | Med |
| `DOC-SPEC.md:213` | "Element … No Element class, facet, **or term**" | Term survives in code comments + the `"No Elements Selected"` UI string — see Priority C | Low |
| code comments | `Pattern.cs:31-32` "PUSHes via `.Set`" | `.Set` gone; the call uses `.Supply` (`MainWindow.xaml.cs:877`) | Low |

---

## Priority B — Architecture homogenization (do before bigger tools)

Ranked by leverage. **Items 1–4 drain `MainWindow` into the existing patterns; none touch the frozen layer.**

1. **`_developed` is a write-once Transient that's never rotted → the stale-mesh bug's root cause.**
   `_developed` (`MainWindow.xaml.cs:103`) is read once (`:654`) and never invalidated when the
   authoring mesh changes (Load/Revert/Subdivide clear it *nowhere*). The displayed developed sheet
   can outlive the mesh it derived from. **Fix:** make it a downstream of the authoring mesh/Pattern
   (or minimally `.Clear()` it in the 3 reset paths) so the cascade governs it like `CreaseMap`.
   *Smallest fix, highest correctness payoff — and it closes the deferred "Propose→Solve→Revert→Propose"
   staleness item properly (the cascade did not actually fix it; it just stopped reproducing).*

2. **The BFF flat-map subsystem is 8 raw fields hand-invalidated in 3 copy-paste blocks.**
   `_flat`/`_M0`/`_hasFlat`/`_bffNeeded`/`_flatRenderer`/`_refMeanLen2`/`_isoResFactor`/`_lmLambda`
   (`MainWindow.xaml.cs:18-33`), nulled in concert at `:450-452, :620-621, :1342-1344`. M′ is the
   textbook "derived from M" Transient. **Fix:** promote M′ to a `Transient<PlanktonMesh>` downstream
   of the authoring mesh; fold the scalars in; the 2D pane becomes a second View later.

3. **Render dirty-flags are a parallel PUSH system duplicating the rot cascade.**
   `_meshDirty`/`_pieceDirty`/`_creaseDirty`/`_rulingsDirty`/`_seamsDirty` + 3 matcap flags set
   imperatively, drained in `OnRender`. Two invalidation systems for one job. **Fix:** the
   explicitly-"next" **render-loop → View** drain; let `Real.Geometry` rot drive staging (the crease
   path already half-does this via `Peek` in `OnRender`). Unifies the bool-dirty PUSH with the cascade.

4. **The View drain is incomplete — `_activeEditor` + pointer handlers + brush-preview dot still in
   MainWindow.** View "is the editor's host" but doesn't own the editor or the pointer pipeline
   (`MainWindow.xaml.cs:1616-1703`; brush-preview wired back via `Action` hooks `View.cs:95-98`).
   **Fix:** move `_activeEditor` + `OnPointer*` + `_previewDot`/`_lastHover` onto View. **Unblocks a
   second editor (the deferred Creaser) without touching the god-file.**

5. **Extract `BakeRunner`** (~640 lines: `OnSolveAsync`/`RunBake*`/`DevelopPiece`/`SolveToAccuracy`/
   `FlattenPure`, `MainWindow.xaml.cs:593-1268`). Removes a third of MainWindow, dedupes the 2
   modal-bake setups, and gives the **two-kernel boundary** (`FlowSession.NesterovStep` covariance vs
   `IsometricLM.Solve`) a documented home — today the split is invisible at the call site.

6. **Extract a `CreaseEngine` multi-target (`net48;net8.0`) library.** `src/*.cs` is hand-`<Compile>`'d
   into 5 projects whose lists **already disagree** (bench=3, CLI/PieceSolver=7, the .gha globs all 10
   incl. the GH-host `CreaseMachine.cs`). Subagent confirms extraction is clean (zero `#if`, single
   Plankton dep). Keep `CreaseMachine.cs` out; disable the .gha default-globbing. (`CODE-REVIEW.md` Tier-2 #6.)

7. **Collapse the 3 copies of "reset develop-state"** (`:450, :620, :1342`, identical 7-field blocks)
   into one `ResetDevelopState()` — which evaporates once #2 makes M′ a Transient with a `.Clear()`.

8. **Move `gui/` + `repro/` → `attic/`** (legacy subprocess GUI + frozen experiment) to settle the
   "which front-end is active" story. (`CODE-REVIEW.md` Tier-2 #10.) Also `rm` the stale `studio/bin`+`obj`.

9. **Move flow-param statics → `FlowParams`.** `CrazeBand`/`AdaptiveDetMix` are process-global mutable
   statics on `DevelopabilityEnergy` (`src/DevelopabilityEnergy.cs:47,70`); the bake worker + a UI tick
   both touch them → thread-safety race as background compute grows. The `FlowParams` struct already
   exists (`Session.cs`). (`CODE-REVIEW.md` Tier-1 #4.)

---

## Priority C — Vocabulary (propose → accept; naming is the user's call)

Renames are **proposed, not prescribed** — each needs explicit acceptance. Ranked by blast radius.

1. **`region` → `piece`** (~69 refs / 8 files). The locked noun is "piece", but the public surface
   uses "region": `Pattern.NewRegionId()` (`:416`), `Editor.FaceFill(int face, int region)`
   (`Editor.cs:55`), `Piecer.ActiveRegionColor`/`Selected(int region)`, plus local
   `region`/`regionBlobs`/`sreg`. Largest same-concept→two-names split; already on radar
   (`CODE-REVIEW.md:121`). Stage: `NewRegionId→NewPieceId` (4 sites) → `FaceFill` param + Piecer
   colours (~6) → locals (file-local).
2. **`RegenCrease` → a rot-named verb** (`Rot`/`RotDownstream`/`Invalidate`; 11 sites + 2 docs). It now
   rots **all** downstreams — the code **self-flags the misnomer** (`Pattern.cs:324`). `RotDownstream`
   (already the body) matches the locked **Rot** umbrella.
3. **"Element"/"entity" → "Real"/"Piece"** (~10 code refs incl. **2 user-facing strings**
   `"No Elements Selected"` `MainWindow.xaml.cs:1649,1660`; also `Doc.cs:7`, `Editor.cs:33`,
   `PieceId.cs:6,22,23`, `View.cs:19`). The spec declares Element retired; the sweep never reached code
   or UI (`DOC-SPEC.md:230` marks it "pending sign-off"). Leave `IsometricLM.cs:47` (different FE-mesh sense).
4. **Doc-only batch (no code risk):** `FullyMarked→MostlyMarked` (AGENTS:267, DOC-TX:301); `.Set→.Supply`
   comment (Pattern.cs:31); "regen"→"Refresh" in AGENTS glossary (:299,310); document the Reset/Revert
   split + fix the `Revert()` comment that still says "Reset" (`MainWindow.xaml.cs:1332`).
5. **"studio" comments (5)** call *this* app "the studio" — misleading today; → "the app/PieceSolver's".
   **Defer** `StudioCommand`/`CmdKind` (44 refs) to the planned CreaseStudio promotion, not piecemeal.
6. **Minor:** field `Piecer._fullyMarked` → `_mostlyMarked` (3 file-local refs) to match its source.

---

## Priority D — Frozen layer (sign-off required, do not touch unprompted)

- **`Doc.Run` should be `OpenTx→Apply→Commit` sugar** — it re-implements the commit path
  (`Doc.cs:120` vs `CloseTx:134`), so the two drift. (`CODE-REVIEW.md` Tier-1 #3.)
- The **`region→piece` / `RegenCrease`** renames above touch `Pattern` (the Store) — naming sign-off.

---

## Doc canonicalization

Charters are clean — **DOC-SPEC** = the node/freshness *model* (SSOT); **NODE-MODEL-IMPL** = its build
plan; **DOC-TX-REFACTOR** = the tx/undo layer; **DEFINITION-OF-DONE** = the bar; **JACOBI-PCG-PINNED**
= the develop kernel; **HANDOFF §§4–9** = the covariance-flow lessons; **PERF-CHA** = perf; **AGENTS**
= architecture + glossary SSOT + a *summary* of the model docs. **Nothing needs archiving** (the three
`docs/archive/` docs are correctly placed). The work is drift-correction + two seams:

- **Trim `HANDOFF.md` §1** to pointers (it re-defines what AGENTS/DOC-SPEC own).
- **Promote the still-live bits out of archived `APP-CONSOLIDATION.md`** — the CreaseStudio rename and
  the `CreaseEngine` extraction are actionable roadmap buried in an archived doc; surface into AGENTS /
  CODE-REVIEW. Likewise the 6-stage pipeline that the active `BRUSH-SCOPE.md` depends on.

---

## Suggested sequence before bigger tools

1. **Priority-A doc fixes** + the **Priority-C doc-only batch** — one cheap, no-risk doc-sync commit.
2. **`_developed` → governed by the cascade** (B#1) — closes the staleness bug at its root.
3. **Render-loop + editor + pointer + preview → View** (B#3, B#4) — unifies the two PUSH systems and
   unblocks the second editor; the natural prerequisite for any new tool.
4. **M′ flat-map → Transient** + collapse the 3 reset blocks (B#2, B#7).
5. **`BakeRunner` + `CreaseEngine`** extractions (B#5, B#6); `attic/` the legacy front-ends (B#8).
6. **Get sign-off** on the `region→piece` / `RegenCrease` renames + `Doc.Run` sugar (C#1–2, D).

*Filename provisional — rename if you'd prefer `ARCH-AUDIT.md`, or fold into `CODE-REVIEW.md`.*
