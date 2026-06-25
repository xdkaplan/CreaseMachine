# Node-Model Convergence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Finish migrating PieceSolver's un-modeled MainWindow state onto the Real/Transient node model so the
codebase runs on **one** invalidation system (the rot cascade), then fold the cheap D-renames in along the way.

**Architecture:** The node model (Real ownership tree + Transient dependency DAG + `rotDownstream` cascade) is
built through I3 (`Node`/`Transient`/`Real`/`Pattern`). This plan converges the *transitional* and
*un-migrated* state onto it — in dependency order, each phase a working `build 0/0` — and ends at a single
PUSH system with `MainWindow` reduced to chrome + a thin GL stage.

**Tech Stack:** net8 WPF, OpenTK + GLWpfControl, in-process Rhino-free engine, Plankton (netstandard2.0).

**Verification model:** every task ends with `dotnet build PieceSolver/PieceSolver.csproj -c Release --nologo`
→ **0/0**, plus the **named smoke check** in that task, plus a **commit**. No task touches the frozen tx
primitives (`Run`/`OpenTx`/`CloseTx`/`Undo`/`Redo`/`Apply`/`Invert`/the delta shapes). Adding *new* downstream
Transients to `Pattern` is additive and within the freeze (same as `CreaseMap`/`Geometry` already are).

---

## Status — 2026-06-25 (THIS is the live TODO; the per-phase checkboxes below are the original plan)

**Done & on `master`** (each build-0/0 + smoke-tested):
- ✓ **D-1 / Task 5** — `Real.Invalidate` hoist; deleted `Pattern.RegenCrease` (`92c6735`)
- ✓ **Phase 1 · Task 1** — cascade unit test (`fc70356`)
- ✓ **Phase 1 · Task 2+3** — crease overlay → `Pattern.CreaseLines` (Grown; multi-level DAG) (`c90d285`)
- ✓ **Phase 2 · Task 4** — `_developed` governed by the cascade **+ TAB toggle** (`b6c8509`). *Root-caused
  differently than the plan assumed: the fix is `UploadForDisplay` + `_developed.Rot()` on mesh change; the
  planned ":654 read guard" was moot (nothing read `_developed` at display time).*
- ✓ **Phase 3 · Task 6** — editor ownership → View (view-drain 1a) (`ed28c7b`)
- ✓ **Phase 3 · Task 7** — pointer pipeline → View (view-drain 1b) (`bfec806`)
- ✓ **D-2 / Phase 5** — `region → piece` rename sweep (`c311569`)
- (+ `50c9346` — the two-channel invariant pinned in DOC-SPEC)

**Remaining:**
- [ ] **Phase 3 · Task 9** — the render-loop drain (`OnRender` + the `_meshDirty`/`_pieceDirty`/`_creaseDirty`
  flags → View; let a Real's geometry rot drive staging). The big, riskiest remaining piece.
  *Task 8 (preview dot → View) is **descoped**: `_previewDot` is a WPF `Ellipse` = chrome; it stays in
  MainWindow via the `Show/HideBrushPreview` hooks. Those hooks + `RefreshPieces`/`RefreshCreaseOverlay`
  collapse together with Task 9.*
- [ ] **Phase 4 · Task 10/11** — M′ flat-map → `Transient` + collapse the 3 copy-paste reset blocks.

**Follow-on (separate track):** `BakeRunner` extraction · `CreaseEngine` multi-target library · `Doc.Run`
sugar (D-3, deferred) · **I4** new Real types (per-`Piece` identity, `Crease`-with-identity, `Spline`).

---

## Phase 0 — Decisions (gate; nothing builds until these are answered)

These are the user's calls (naming + frozen sign-off). The plan assumes the **Recommended** answer; if the
user picks otherwise, swap the name/flag in the affected tasks.

- [x] **D-1 · cascade origin is architectural (RESOLVED).** Don't rename `RegenCrease` — *remove* it. The
  Real-mutation→cascade affordance belongs on the base, not coined per-Store. Add **`Real.Invalidate()
  => RotDownstream()`** (public), delete `Pattern.RegenCrease`, repoint its sites to the inherited
  `Invalidate()`. Symmetric three origins: `Transient.Rot()`, `Transient.Supply()`, `Real.Invalidate()`.
  Name `Invalidate` pending final accept (alt: expose `RotDownstream()`). *Foundational — Phase 1, runs early.*
- [ ] **D-2 · `region → piece` sign-off + targets.** Direction is fixed (locked noun = "piece"). Targets:
  `NewRegionId→NewPieceId`, `FaceFill(int face, int region)→int piece`, `ActiveRegionColor→ActivePieceColor`,
  locals `region`/`regionBlobs`/`sreg`→`piece`/`pieceBlobs`/`spiece`. **Recommended:** yes, as Phase 5.
- [ ] **D-3 · `Doc.Run` sugar.** Frozen tx core, no user-visible benefit. **Recommended: defer** (not in this
  plan). Listed under Follow-on.

---

## File map (what this plan touches)

- `PieceSolver/Pattern.cs` — gains a **`CreaseLines`** Grown Transient (Phase 1); `RegenCrease→Invalidate` (Phase 2); `region→piece` (Phase 5).
- `PieceSolver/Real.cs` — **delete** `CreaseOverlay` (Phase 1).
- `PieceSolver/View.cs` — drop `CreaseOverlay` (Phase 1); absorb editor + pointer + preview + render staging (Phase 3).
- `PieceSolver/MainWindow.xaml.cs` — drop `_creasePts`/`RebuildCreaseOverlay`/`SetCreasePts` (Phase 1); `_developed` governed (Phase 2); pointer/editor/render-loop → View (Phase 3); M′ → Transient (Phase 4).
- `PieceSolver/Editor.cs` — `RefreshCreaseOverlay` hook removed (Phase 1/3).
- **New** `test/NodeModelTests` (or a `[Conditional]` self-check) — cascade unit test (Phase 1, Task 1).

---

## Phase 1 — Fold `CreaseOverlay` into `Pattern` (+ protect the cascade with a test)

**Why first:** it's the clearest transitional artifact, it converts the flat graph into a real **multi-level**
DAG (`Pattern → CreaseMap → CreaseLines`) that exercises the cascade, and it removes a whole redundant field.

### Task 1 — A unit test for the rot cascade (the DoD's "test-protected dependency tree")

**Files:**
- Create: `test/NodeModelTests.csproj` (net8; `<Compile>` of `PieceSolver/Transient.cs`, `Real.cs`, `RenderData.cs` — no WPF/GL deps), `test/NodeModel/CascadeTests.cs`

- [ ] **Step 1 — Write the cascade tests** (pure logic; no GL):

```csharp
// CascadeTests.cs — exercises Node/Transient with a tiny FakeReal : Real.
sealed class FakeReal : Real { public override string Name => "Fake"; }

static void GrowGrowsOnRead() {
    int grows = 0; var t = new Transient<int>(() => { grows++; return 7; });
    Assert(t.Value == 7 && grows == 1);     // grows on first read
    Assert(t.Value == 7 && grows == 1);     // fresh: no regrow
    t.Rot(); Assert(t.Value == 7 && grows == 2);   // stale: regrows
}
static void SupplyCascadesDownstream() {
    var up = new FakeReal();
    var down = new Transient<int>(() => 0);
    up.AddDownstream(down); _ = down.Value;  // down fresh
    var src = new Transient<int>();          // Supplied
    // wire src as an upstream of down via a second FakeReal-style holder:
    var holder = new FakeReal(); holder.AddDownstream(down); _ = down.Value;
    holder.RotPublicForTest();               // (see note) rots down
    Assert(down.IsStale);
}
```

  *(Note: `Node.RotDownstream` is `protected`. To test the cascade, add an `internal` test seam — e.g.
  `internal void RotForTest() => RotDownstream();` on `Node` under `#if DEBUG` or `[InternalsVisibleTo]` — OR
  test via `Transient.Rot()`/`Supply()` which already call it publicly. Prefer the latter: assert that
  `parent.Supply(x)` flips a registered downstream to `IsStale`, and that an already-stale node short-circuits.)*

- [ ] **Step 2 — Build + run:** `dotnet run --project test/NodeModelTests.csproj -c Release`. Expected: all asserts pass.
- [ ] **Step 3 — Commit:** `test(node): cascade unit tests (Rot/Supply/Grow + idempotent short-circuit)`

*If standing up a test project proves heavier than ~30 min (Plankton/transitive refs leak in), STOP and report —
fall back to a `GradCheck`-style console check or defer the test, but say so explicitly.*

### Task 2 — `Pattern.CreaseLines`: a Grown Transient deriving the wire segments from `CreaseMap`

**Files:** Modify `PieceSolver/Pattern.cs`

- [ ] **Step 1 — Add the Transient + its grow recipe.** Lift the segment-building loop out of MainWindow's
  `RebuildCreaseOverlay` (~`MainWindow.xaml.cs:883`) into a private `DeriveCreaseLines()` on `Pattern` (it has
  `_mesh` + `CreaseMap` — pure inputs). Add:

```csharp
// DERIVED wire overlay: the crease edges as GL line-segment vertices (Kind=Lines). GROWN from CreaseMap (+ the
// held mesh positions) — pure, so it self-grows on read. A downstream of CreaseMap: a PieceMap change rots
// CreaseMap, which rots this (the first multi-level edge: Pattern -> CreaseMap -> CreaseLines).
public readonly Transient<RenderData> CreaseLines;
```
  In the ctor, after `CreaseMap = …`: `CreaseLines = new Transient<RenderData>(DeriveCreaseLines); CreaseMap.AddDownstream(CreaseLines);`
  (downstream of `CreaseMap`, **not** Pattern — it derives from the crease set). `DeriveCreaseLines()` returns
  `new RenderData { Kind = RenderKind.Lines, Segments = <segments built from CreaseMap.Value + _mesh> }`.

- [ ] **Step 2 — Build 0/0.** (Nothing consumes `CreaseLines` yet — pure addition.)
- [ ] **Step 3 — Commit:** `feat(pattern): CreaseLines Grown Transient (Pattern -> CreaseMap -> CreaseLines)`

### Task 3 — Point `OnRender` at `Pattern.CreaseLines`; delete `CreaseOverlay` + `_creasePts`

**Files:** Modify `MainWindow.xaml.cs`, `View.cs`, `Real.cs`, `Editor.cs`

- [ ] **Step 1 — OnRender pulls the new Transient.** Replace the crease stage (~`:1817`) to pull
  `_doc.Pattern?.CreaseLines` instead of `_view.CreaseOverlay.Geometry`:

```csharp
// pull the crease wires from Pattern's CreaseLines Transient (Grown) + stage
if (_creaseDirty && !_baking && _renderer != null && _doc.Pattern != null
    && _doc.Pattern.CreaseLines.Peek(out var creaseRd))   // Peek: don't force a grow on the GL thread
{ _renderer.SetCreases(creaseRd?.Segments ?? System.Array.Empty<float>()); _creaseDirty = false; }
```
  *(Keep `_creaseDirty` as the render-stage flag for now — Phase 3 unifies it. `RebuildCreaseOverlay` becomes
  just `{ _creaseDirty = true; _gl?.InvalidateVisual(); }` — it no longer builds segments; the Transient grows
  them on read. Its callers (`Doc.Changed`, `:862`, the clear at `:651/:982`) stay, now only setting dirty.)*
  Since `CreaseLines` is **Grown**, `Peek` may return stale early; if so, call `.Value` here (safe — pure,
  fast) instead of `Peek`. Decide at execution; prefer `.Value` if the overlay ever renders blank.

- [ ] **Step 2 — Delete the redundancies.** Remove: `CreaseOverlay` class (`Real.cs:21`); `View.CreaseOverlay`
  (`View.cs:33`); `_creasePts` field (`:95`) + `SetCreasePts` (`:920`); rewrite the segment-building body of
  `RebuildCreaseOverlay` away (it's now `DeriveCreaseLines` on Pattern). Update the `:977` guard that reads
  `_creasePts` to read `_doc.Pattern?.CreaseLines.Peek(...)` or drop that clause.

- [ ] **Step 3 — Build 0/0.**
- [ ] **Step 4 — SMOKE:** Propose → crease wires draw; brush/merge/delete → wires update; Solve → wires clear;
  Revert → wires gone. (Identical to today; only the source changed.)
- [ ] **Step 5 — Commit:** `refactor(piecesolver): crease overlay is now Pattern.CreaseLines; drop CreaseOverlay + _creasePts`

**Phase 1 done:** one Real type retired, one redundant field gone, the cascade is multi-level + test-protected.

---

## Phase 2 — `_developed` governed by the cascade (fixes the stale-mesh bug) + `RegenCrease` rename

### Task 4 — Make `_developed` rot when the authoring mesh changes

**Files:** Modify `MainWindow.xaml.cs`

- [ ] **Step 1 — Rot/clear `_developed` on every mesh change.** `_developed` (`:103`) is read at `:654` and
  never invalidated. Add `_developed.Rot();` (or `.Clear()`) at the three authoring-mesh-change sites:
  `ApplyLoad` (~`:450`), `Revert` (~`:1342`), `ApplySubdivide`. Guard the read at `:654`/the display path:
  show the developed mesh only when `_developed.Peek(out var d)` is fresh; otherwise fall back to authoring.

- [ ] **Step 2 — Build 0/0.**
- [ ] **Step 3 — SMOKE (the deferred repro, deliberately):** Propose → Solve → Revert → Propose, repeated.
  Confirm the mesh is never stale; confirm Solve still shows the developed sheet; Revert returns to authoring.
- [ ] **Step 4 — Commit:** `fix(piecesolver): _developed rots on mesh change — closes the Revert->Propose stale mesh`

### Task 5 — Hoist the cascade origin to `Real.Invalidate()`; delete `Pattern.RegenCrease` (D-1) — *foundational, run early*

The Real-mutation→cascade affordance is architectural; it belongs on the base, not hand-rolled per-Store.
Symmetric result: `Transient.Rot()` / `Transient.Supply()` / `Real.Invalidate()`.

**Files:** Modify `PieceSolver/Real.cs` (add `Invalidate`), `Pattern.cs` (delete `RegenCrease`, repoint ~7 internal calls), `MainWindow.xaml.cs` (~2 calls), `AGENTS.md` (the `:262` reference).

- [ ] **Step 1 — Add to `Real.cs`:** `public void Invalidate() => RotDownstream();` with a comment (the rule-1
  cascade origin; provided once so no Store coins its own hook).
- [ ] **Step 2 — Delete `Pattern.RegenCrease`** (`Pattern.cs:326`); repoint every call (grep first —
  Pattern-internal + the `MainWindow:862` external caller) to `Invalidate()`. Leave `Apply`/`Invert` logic
  otherwise untouched (they now call `Invalidate()`).
- [ ] **Step 3 — Build 0/0; grep `RegenCrease` → 0 hits.**
- [ ] **Step 4 — SMOKE:** any piece edit still updates creases + pieces (cascade unchanged).
- [ ] **Step 5 — Commit:** `refactor(node): hoist cascade origin to Real.Invalidate; delete Pattern.RegenCrease`

---

## Phase 3 — Finish the View drain (unifies the dirty-flag PUSH with the cascade; unblocks the Creaser)

**Decompose; each sub-task builds 0/0 + smoke + commit. This is the largest phase — re-detail at execution if
the code has shifted.**

- [ ] **Task 6 — Move `_activeEditor` + the `Editor` lifecycle onto `View`** (`View` already hosts the editor
  via `IEditorHost`). Smoke: piecing still activates after Propose→Accept; ESC cancels.
- [ ] **Task 7 — Move the pointer pipeline (`OnMouseDown/Up/Move`, `~:1616-1703`) onto `View`** (it owns
  Camera + picking already). MainWindow forwards GL events to `View`. Smoke: orbit/pan/zoom + brush gestures.
- [ ] **Task 8 — Move the brush-preview dot (`_previewDot`/`_lastHover`) onto `View`; delete the 4 `Action`
  hooks** in the `View` ctor (`_showPreview`/`_hidePreview`/`_refreshPieces`/`_refreshCreaseOverlay`). Smoke:
  hover shows the dot; gestures preview correctly.
- [ ] **Task 9 — Move the render staging into `View`** (or a `View.StageFrame(renderer)` the GL tick calls):
  fold `_meshDirty`/`_pieceDirty`/`_creaseDirty` so a Real's `Geometry`/`CreaseLines` rot drives staging
  (extend the `Peek`-in-OnRender shape). Smoke: every view (authoring/pieces/developed) + overlays render.

**Phase 3 done:** MainWindow is the WPF chrome shell + a thin GL tick; one invalidation system (the cascade).

---

## Phase 4 — M′ flat-map → Transient + collapse the 3 reset blocks

- [ ] **Task 10 — Promote M′ to `Transient<PlanktonMesh>` downstream of the authoring mesh** (Supplied by the
  bake / Grown via BFF). Fold `_flat`/`_M0`/`_hasFlat`/`_bffNeeded`/`_refMeanLen2`/`_isoResFactor`/`_lmLambda`
  (`:18-33`) into it + its companion scalars. Smoke: Flatten + Solve + the 2D pane still work.
- [ ] **Task 11 — Replace the 3 copy-paste "reset develop-state" blocks** (`:450`, `:620`, `:1342`) with one
  `ResetDevelopState()` that just `.Rot()`s the developed + M′ Transients. Smoke: Load/Revert/Subdivide reset.
- [ ] Commit each task.

---

## Phase 5 — `region → piece` sweep (D-2; cosmetic, mechanical, gated on sign-off)

- [ ] **Task 12 — Stage the rename:** (a) `NewRegionId→NewPieceId` (4 sites) → build 0/0 → commit; (b)
  `Editor.FaceFill(int region)→int piece` + `Piecer.ActiveRegionColor→ActivePieceColor` + `Selected(int)`
  param (~6 sites) → build 0/0 → commit; (c) file-local locals `region`/`regionBlobs`/`sreg` → build 0/0 →
  commit. grep `\bregion` after → only legitimate flood-fill-internal uses remain (or none).
- [ ] **Task 13 — Sync the docs** (AGENTS/DOC-TX/DOC-SPEC) for the renamed surface. Commit.

---

## Follow-on (not in this plan)

- **`BakeRunner` extraction** (~640 lines) + **`CreaseEngine` multi-target library** (audit B#5/#6) — god-file
  drain + build hygiene; adjacent to the node model, own track.
- **`Doc.Run` → `OpenTx→Apply→Commit` sugar** (D-3) — frozen tx core; behavior-identical pass + sign-off.
- **I4 new Real types** — per-`Piece` identity, `Crease`-with-identity, `Spline`/`Control-point`. The reason
  Phases 1–4 come first: they make the model trustworthy *before* the vocabulary scales to new node types.

---

## Self-review

- **Spec coverage** (vs `docs/HOMOGENIZATION.md` Priority B + the transitional bucket): `_developed` ✓ (T4),
  M′ ✓ (T10/11), render dirty-flags ✓ (T9), View drain ✓ (T6-9), CreaseOverlay fold ✓ (T2/3), `_fullyMarked`
  already done. D: `RegenCrease` ✓ (T5), `region→piece` ✓ (T12/13), `Doc.Run` deferred (declared). BakeRunner /
  CreaseEngine / I4 declared as Follow-on. No Priority-B item is silently dropped.
- **Type consistency:** `CreaseLines` (T2) is the name used in T3; `Invalidate` (T5/D-1) is the rename used
  nowhere earlier; `Pattern.CreaseMap.AddDownstream(CreaseLines)` matches the existing `AddDownstream` API.
- **Frozen-layer check:** no task edits `Run`/`OpenTx`/`Apply`/`Invert`/deltas. Additive Transients on
  `Pattern` only. `Doc.Run` explicitly deferred.
- **Granularity caveat:** Phases 1–2 are detailed to the step; Phases 3–5 are decomposed to tasks with files +
  transformation + smoke, to be re-detailed at execution (they depend on earlier phases landing + the exact
  current code). Flagged, not hidden.

*Filename/location provisional — sibling to `NODE-MODEL-IMPL.md`; rename or fold in as you prefer.*
