# Overnight Architecture Review — Runbook

A reusable procedure for the nightly "overnight debrief." Written so an agent with **less
context than the author** can run it end-to-end and produce a report of the same quality as
`2026-06-26-overnight-architecture-review.md` (the worked example — read it first; it is the
template for shape, depth, and tone).

The job is **not** "list everything wrong." It is: *measure how faithfully the code matches
its own stated architecture, find the places it cheats, and rank them by how much they
actually bite* — with every serious claim verified by hand, not taken on faith.

---

## 0. Operating rules (do not skip)

- **Read-only on the app.** You may create the report + runbook. You may **not** change app
  code, and you may **never** touch the frozen Doc orchestration (`Run`/`OpenTx`/`CloseTx`/
  `Undo`/`Redo`/`Apply`/`Invert`/delta shapes) — see AGENTS.md. The output is *findings*,
  not fixes.
- **Work in a worktree off master HEAD.** `git worktree add .claude/worktrees/overnight-review
  master` (or reuse it). Commit the report on that branch; the main agent merges. Never commit
  to master.
- **Where this runbook lives, and read-only runs.** This runbook + the dated reports are
  committed under `docs/reviews/` on the **`overnight-review`** branch — they may not be on
  `master` yet. If you're invoked against a branch that lacks them, look in the
  `overnight-review` worktree (glob `**/OVERNIGHT-REVIEW-RUNBOOK.md`). A **read-only validation
  run** — an agent dispatched only to re-run the procedure and report what it finds — skips the
  worktree-create and commit steps entirely and returns findings in its final message instead of
  writing them to disk. Run the *procedure* (§1–§5); skip the *delivery* (§6).
- **No ultracode / no Workflow** unless the user explicitly opted in. Use the **Agent** tool
  for parallel auditors (this review used six). They are read-only general-purpose agents.
- **Verify before you assert.** Every HIGH finding must be re-checked by hand against the
  actual control flow before it goes in the report. Subagents are fast and usually right, but
  they *will* occasionally overstate a claim (e.g. "happens after every Solve" when it's
  gated on a setting). The author's job is to catch that. A finding you couldn't verify is
  reported as "unverified — needs confirmation," never as fact.
- **Extend, don't restate.** `docs/CODE-REVIEW.md` is the standing tracker. Reconcile its
  open items' *current* status; don't re-report them as new. New findings get new IDs.

---

## 1. Load the model (you cannot audit against a spec you haven't read)

Read these **first**, fully, in this order. They are the yardstick — every finding is
"deviates from X in this doc."

1. `docs/specs/DOC-SPEC.md` — the canonical node model. The whole review is measured against
   it. Internalize: **Real** (authored, ownership tree, never stale, tx-only mutation, saved),
   **Transient** (derived, dependency DAG, Fresh/Stale, Grown=`.Value`/Supplied=`Peek`, never
   a live alias of Real), **Ephemeral** (selection/camera/gesture preview — discarded with
   scope, not saved/undoable/refreshed). Two channels: **rot cascade** (invalidation only,
   never writes Real, not undoable) vs **ops/tx** (authored, undoable). **Single-writer:**
   graph state on one thread; off-thread work re-enters via `Supply`.
2. `docs/CODE-REVIEW.md` — the prior audit + open-item tracker. Your status reconciliation
   anchors here.
3. `docs/specs/NODE-MODEL-CONVERGENCE.md` — what's built vs planned (the live Status block at
   the top supersedes the per-phase checkboxes below it — a known trap; the checkboxes lie).
4. The `## In-process app — PieceSolver/` section of `AGENTS.md` — the as-built architecture
   prose and the locked vocabulary.

Then read the **node-model core by hand** (small, load-bearing, where cheats hide):
`Transient.cs`, `Real.cs`, `Pattern.cs`, `Doc.cs`. Don't delegate these — you need them in
your own head to verify the agents' findings.

---

## 2. The cheat taxonomy (what you are hunting)

Frame every finding as one of these. This is the lens that makes the review *architectural*
rather than a generic lint pass.

**CHEAT** — a violation of the model:
- Real state hiding in an Ephemeral (the classic trap).
- A Transient that's a live *alias* of Real instead of computed-*from* it.
- A rot that *writes* Real state (the cascade must only invalidate).
- A Real→Real update outside a transaction.
- Tree/DAG conflation (Parent/Child used for derives-from, or Upstream/Downstream for
  ownership).
- **Graph state mutated off the UI thread** (single-writer breach) — *the highest-value
  thing to check; it's the one that bit this codebase.*
- A stale-Supplied `.Value` returning data instead of throwing; naked Supply setters used as
  Real.

**STALE** — dead/vestigial code: leftovers from a migration (node-model build, View drain,
Element→Real retirement), shadow fields kept equal by convention, commented-out blocks,
flags no longer read, `RenderKind`s defined but never produced.

**LEAK** — a consumer reaching past its interface: an editor past `IEditorHost`, the shell
into View/editor internals, a Store mutated not-via-tx, the Doc orchestration touched outside
its gate.

**WRONG** — logic that doesn't do what its name/comment says: off-by-one, swapped condition,
a comment asserting an invariant the code violates, a resource leak, a threading hazard.

**Also answer the three standing questions** every run (the user asks them): *Real tree vs
Transient DAG status? Do we have ephemerals (and are they clean)? How many times are we
cheating the model (the ledger)?*

---

## 3. Fan out (six auditors, one lens each)

Dispatch read-only **Agent** tool calls **in parallel, in one message**. Cluster by
subsystem so each agent holds its files in context at once. The clustering that worked:

| Agent | Files | Focus |
|---|---|---|
| A · Doc+Tx | `Doc.cs`, `Tx.cs` | the two commit paths, channel discipline, single-writer, Selection-as-Ephemeral, the gate |
| B · View+render | `View.cs`, `MeshView.cs`, `RenderData.cs`, `Camera.cs` | RenderData as a clean Transient, DisplaySource, render-loop ownership, camera Ephemeral, leaks |
| C · interaction | `Piecer.cs`, `Editor.cs`, `Commands.cs`, `PieceId.cs`, `Picker.cs` | Ephemeral trap, Commands purity, the `IEditorHost` wall, stale selection ids |
| D · god-file | `MainWindow.xaml.cs` | off-thread graph mutation, the bake, mutable-static touches, reset-state dup, Doc-orchestration reach-ins; verify CODE-REVIEW T1/T2 |
| E · doc staleness | all of `docs/` + cross-check vs code | which docs say built-not-built or vice versa; re-verify every CODE-REVIEW open item's current status |
| F · engine + host glue | `DevelopabilityEnergy.cs`, `Bff.cs`, `IsometricLM.cs`, smoothers, **`src/CreaseMachine.cs`, `src/Session.cs`** | the static-race (#4), BFF temp/exe (#5), bench-fence (#10), NaN/leak/threading hazards. Include the host glue: the mutable-static *writes* and the GH worker↔UI-tick race only become visible by reading `CreaseMachine.cs` / `Session.cs`, not the energy file alone. |

**Give every agent the same preamble:** a compressed statement of the model (§1), the cheat
taxonomy (§2), the instruction to read `DOC-SPEC.md` + skim `CODE-REVIEW.md` and *not*
re-report known items, and a fixed output contract:

> Return a concise bulleted list grouped by category (CHEAT / STALE / LEAK / WRONG / clean).
> Each finding: `file.cs:NN — [HIGH|MED|LOW] one-line what · why it violates the model`.
> Cite real line numbers. Conclusions, not file dumps. If a cluster is clean, say so in one
> line.

Scale the fleet to the codebase. Six fit this ~30-file app. A larger codebase wants more
agents with tighter clusters; a tiny change wants two. Don't fan out wider than you can
synthesize.

---

## 4. Verify the HIGH findings by hand

This is the step that separates a trustworthy report from an agent-output dump. For **each
HIGH-severity claim**, open the cited lines yourself and trace the actual control flow:

- Does the dangerous call really run where the agent says (e.g. *off* the UI thread)? Follow
  the call chain — `Task.Run` → method → method — don't trust the summary.
- Is it **constant or gated**? "Happens after every Solve" vs "happens when SubdivLevel > 0"
  changes the severity. The 2026-06-26 run caught exactly this: the bake's off-thread `Rebind`
  is gated on subdivision level, not universal. Report the gate.
- **If it's gated on a setting, look up that setting's *default*.** This is the step the first
  pass missed and two independent re-runs caught. A hazard behind an off-by-default flag is
  MED; the *same* hazard on the default path is HIGH. The bake's off-thread `Rebind` is gated
  on `SubdivLevel` — whose default is `2` (`SimSettings.cs`), so it fires on the **default**
  Solve, not an edge case. Grep the view-model / settings for the field's initializer before
  you rank. A gate is only mitigating if the default disarms it.
- Trace the **downstream consequence**, not just the breach. "Mutates graph off-thread" is the
  mechanism; the *harm* is what a later read does with the torn state (e.g. a null `PieceMap`
  re-`Seed`s and silently destroys the authored partition). The consequence is what sets
  severity and what the reader needs — chase it to where it detonates.
- Does the cited invariant actually exist? The strongest findings cite a **comment that
  asserts the very invariant the code breaks** (F-2 in the worked example) — find those.

If you can't verify a HIGH claim, downgrade it to "unverified" and say so. Never launder an
agent's confidence into the report's voice.

---

## 5. Synthesize

Structure the report like the worked example:

1. **Verdict** — one honest paragraph. Is the architecture sound? Where is the rot
   concentrated? Resist both "everything's fine" and "everything's broken."
2. **The user's questions, answered directly** — Real tree vs DAG; ephemerals; the cheat
   count. These are why the review exists; lead with them.
3. **The cheat ledger** — a numbered table (ID · cheat · severity · new-or-tracked). This is
   the "how many times are we cheating" answer made concrete.
4. **Findings (detail)** — one block per finding: the model rule, the code, *why it matters*,
   reachability, and a fix direction (without doing the fix). HIGH first.
5. **What's clean** — name the parts that hold, so the next reviewer doesn't re-litigate
   them. This is not filler; it's how the review compounds across nights.
6. **CODE-REVIEW reconciliation** — a status table for every prior open item.
7. **Doc staleness** — where docs and code disagree (this codebase's docs run a cycle behind;
   yours may differ).
8. **Recommended actions** — priority-ordered, flagging anything that touches the frozen
   layer as "needs sign-off."

Severity discipline: **HIGH** = breaks a core invariant with a real (even if narrow) path to
corruption. **MED** = local cheat, latent or low-blast. **LOW** = cosmetic / vestigial.
Inflating severity is as damaging as missing a finding — it trains the reader to ignore you.

---

## 6. Deliver

- Write the report to `docs/reviews/YYYY-MM-DD-overnight-architecture-review.md`.
- Update this runbook **only** if you found a better procedure (note what changed and why).
- Commit both on the review worktree branch (traditional cadence, one commit). Do **not**
  merge to master — surface the branch to the user / main agent.
- In your closing message to the user: the verdict in 2-3 sentences, the HIGH findings by ID,
  and the single highest-leverage next action. Link the report. Don't paste the whole thing —
  they'll read the file.

---

## 7. Failure modes to avoid (learned this run)

- **Trusting a subagent's severity.** They found the bake's off-thread mutation correctly but
  one called it universal; hand-verification showed it's gated on a setting. Always check.
- **Re-reporting tracked items as new.** Reconcile against `CODE-REVIEW.md` first; a finding
  already in the tracker is a *status update*, not a discovery.
- **Generic lint.** "This method is long" / "consider extracting" is noise unless it maps to a
  model violation or a tracked structural item. Stay on the architecture lens.
- **Severity inflation — and its mirror, count-anchoring.** If everything is HIGH, nothing is.
  But don't force-fit a number either: describe the *bar* for HIGH (a broken core invariant
  with a real, reachable path to corruption) and let the count fall out of the evidence. Zero
  HIGH findings is a valid result; so is five. Past runs of this codebase have landed on a
  small handful — treat that as context, not a quota to hit.
- **Touching the frozen layer.** Findings about `Doc`/`Tx` are fine; *edits* are not. Flag and
  stop.
- **Believing the docs.** Several specs say "not built" for shipped work. Verify against code,
  not prose.
