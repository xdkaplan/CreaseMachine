# Doc Spec — the node model: Reals (ownership tree) + Transients (dependency DAG)

**Status:** *design / target doc.* Most of this is not built yet — it's the agreed model
for how the `Doc` owns authored + derived state. Each section marks **✓ built** vs **○ designed**.
The one thing built today is `Transient<T>` (a freshness bool) plus a single hand-wired
edge (`Pattern → CreaseMap`). Everything about the *graphs* (edges, cascade, flavors,
threading rule) is designed-not-built. See [DOC-TX-REFACTOR.md](../DOC-TX-REFACTOR.md) for the
undo/transaction layer this sits beside, and [AGENTS.md](../../AGENTS.md) for the as-built
Real/Transient/Ephemeral glossary.

**Model update (2026-06-24):** there is **no `Element` class** — "Element" dissolves into
**`Real`**. A Real is the one authored-node type (the things you'd call "elements" are just
Reals with geometry); "viewable" is just "has a geometry Transient." See §3.

## 1. Purpose

Define how *derived* state stays correct as *authored* state changes: what's authoritative,
what's computed-from-it, how a change invalidates the things downstream, who recomputes
them, and on which thread. The goal is one mental model that governs **undo, regeneration,
save, and concurrency** at once.

## 2. The three kinds of state  ✓ built (as a distinction; see AGENTS.md)

- **Real** — authored source-of-truth (mesh, `Pattern`/`PieceMap`, params, future
  Pieces / Creases / Splines / Control-points). The **one authored-node class**: Reals form an
  **ownership tree** (§3), hold property values, and own **optional geometry-Transient children**.
  Undoable (mutated only via a tx, through their Store), saved to file, **never stale** —
  *"the Doc is never Dirty."* *There is no separate "Element" — the things you'd call elements
  are just Reals (with geometry); a Real with nothing to draw simply renders nothing.*
- **Transient** — *derived from* Real. Cached with a freshness flag; **refreshed** from
  Real; not saved, not undoable. (`CreaseMap`, `_developed`, the future `SolvedPiece`.)
  Computed-*from* Real, never a live alias of it.
- **Ephemeral** — computed once and discarded with its scope: a gesture's preview
  accumulators, an editor's selection, the camera. Not saved, not undoable, and **not even
  refreshed** — just thrown away.

**Durability scale** (safe → dragons): **Real** (on disk, undoable, never stale) → **Transient**
(recoverable — lose it, refresh it) → **Ephemeral** (raw memory bytes — not saved, not undoable,
not refreshed; lose it and it's gone). So Ephemeral is *only* for the genuinely disposable
(selection, camera, preview) — putting real state there is the trap.

## 3. The graphs — Real tree + Transient DAG  ○ designed

There are **two** node kinds and **two** relationships — the whole point is *not* to conflate them:

- **`Real` — an ownership TREE.** Reals are *authored*, so they never derive from each other;
  a Real's only edge to another Real is **composition / ownership** (a Control-point belongs to
  one Spline; a Crease/Piece belongs to the partition). Single-parent ⇒ strictly branching ⇒
  **tree**. The roots of everything; never stale. *Relationships* (a crease borders two pieces,
  adjacency) are **not** tree edges — keep them as references or derived, or the tree silently
  becomes a DAG.
- **`Transient` — a dependency DAG.** Transients *derive*, and one can derive from **several**
  parents (a panel-layout from many Pieces + params), acyclically ⇒ **directed acyclic graph**.
  Fresh/stale. The DAG **hangs off** the Real-tree: Reals are the sources, Transients are
  everything computed from them.
- **"Parent / Child" means different things per graph:** in the Real-tree it's *contains*; in
  the Transient-DAG it's *derives-from*. The freshness machinery (§5) needs the DAG's
  **down-edges** — a Transient knows its children so a rot can walk the subtree.

**Consumers project a Real.** A Real is never gated "viewable / not" — any consumer takes what
it can. The **3D View** pulls a Real's geometry Transient and draws it (type-dispatching on
mesh / lines / points), or no-ops when there's none. The **property / Settings panel** pulls the
*same* Real's values and shows them. Same Real, many projections; the viewport and the panels
are **peer consumers** of one Real-tree, not a privileged "viewable" hierarchy.

*Today:* exactly one edge exists, hand-wired — `Pattern.Apply/Invert` rots `CreaseMap`.

## 4. Freshness  ✓ built (the bool) / ○ the invariant

A transient is **Fresh** (cache valid — equals what a rebuild would produce) or **Stale**
(a parent changed; cache can't be trusted). A plain `bool` — nothing richer (see §9 for why
`Status` is deferred).

**Core invariant — freshness is downward-closed:** a Fresh node guarantees *all its
ancestors are Fresh too.* This is what makes lazy reads safe: `.Value` short-circuits on
Fresh without consulting parents, so a fresh child must never sit over a stale parent. §5
preserves this by making rot cascade down at invalidation time.

## 5. Transitions  ○ designed (rot/refresh model)

Two umbrella transitions, organic antonyms:

- **Rot** — Fresh → Stale. The invalidation. Cheap (a bool flip). **Cascades down.**
- **Refresh** — Stale → Fresh. Happens by one of two doors:
  - **Grow** — a **Grown** transient rebuilds *itself* by running its recipe (lazy, inline,
    on read). *(Was "regen".)*
  - **Supply** — a **Supplied** transient is *fed* a value by a producer. Freshens self,
    rots children. *(The `Supply` method — today a naked setter; named for the abstraction's
    role so it can later host invalidation / handlers.)*

  So **Refresh = Grow | Supply.** (Note: Rot is the *umbrella* invalidation — it hits every
  transient. Grow is only the Grown refresh door. Rot's true inverse is Refresh, not Grow —
  the Rot/Grow rhyme is a mnemonic, not an exact inverse.)

**The three rot-origin rules** — all the same primitive, `rotChildren`, fired at three sites:

```
Real.mutate()        → rotChildren(self)                       // a tx Apply/Invert
Transient.Rot()      → IsFresh=false; rotChildren(self)        // the cascade
Transient.Supply(v)  → _value=v; IsFresh=true; rotChildren(self)   // push: fresh self, stale kids

rotChildren(n): foreach child c: c.Rot()   // and c.Rot() recurses → whole subtree
```

Because `Rot` itself calls `rotChildren`, one `Rot()` at the top floods everything reachable
below — no separate "rot everything below" step. (In the DAG a node with several parents can be
reached by more than one path; `Rot` is an idempotent bool-flip, so re-visiting it is harmless —
it floods the reachable **sub-DAG**, not a strict subtree.) Only **Real.mutate** and **Supply** *originate* a rot
wave (the two genuine value-changes); rule 2 is just propagation. There is deliberately **no
"Grow rots children"** rule: by the time a Grown transient grows, its children were already
rotted when *it* was rotted.

**Value-blind / conservative:** if a Grow produces an identical value, the subtree was still
rotted and will re-refresh — correct, but wasted. Skipping that needs value-equality or
version memoization; not worth it until profiling says so.

## 6. Flavors — Grown vs Supplied  ○ designed

The axis is **availability to the reader**, not who-owns-the-recipe:

- **Grown** — produces its own value *on read*, here and now. **Total** — always has a value.
  Reads via `.Value` (grows if stale). *(e.g. `CreaseMap`.)*
- **Supplied** — must be *produced in advance* by a producer; the reader can only check
  whether it arrived. **Partial** — a `Maybe`, might not be ready. Reads via `Peek`.
  *(e.g. `_developed`, the future `SolvedPiece`.)*

A Supplied transient's producer may be an external feed **or** a self-owned background job
(so "self-derivable but expensive/async" — like a multi-pass solve — is **Supplied**, because
from the reader's seat it still had to be prepared ahead). Whether such a transient is
*self-kicking* (owns an `Ensure()` that launches its own job) or *externally-kicked* is a
sub-detail under Supplied, not a separate flavor.

## 7. Reads  ✓ `Peek` built / ○ `.Value`-throws designed

- **`.Value`** — the assertive read: *"give it to me, it should be ready."*
  - Grown: stale → Grow → value. Never throws (total).
  - Supplied: fresh → value; **stale → throw** (fail-fast — *currently returns stale/null;
    the throw is the proposed change, paired with moving stale-Supplied readers to `Peek`*).
- **`Peek(out v) → bool`** — the tentative read: *"not sure it's fresh; let me look."* Never
  grows, never throws; returns `(current cache, isFresh)`. For a maybe-not-ready Supplied, or
  a Grown where you deliberately *don't* want to trigger a Grow. *(Replaces the old "TryGet";
  the method is named `Peek` — "TryGet" is scrubbed from the code.)*

## 8. Concurrency — the single-writer rule  ○ designed

**Graph *state* mutates on exactly one thread** (the Doc's, which is the UI thread): the
freshness bits, caches, edges, and rot/refresh transitions. That thread is the
serialization point, so the graph needs **no locks**, rot-cascades are atomic, and it stays
coherent with undo and live gestures.

**Work is not owned by that thread.** A solve runs off-thread on an *immutable snapshot* of
its inputs, touching zero graph state while it runs; on completion it **marshals back** to
the Doc's thread and calls `Supply` there. So the Doc *owns the state, not the work* — the
heavy compute is off-thread; only the microsecond `Supply` (assign + flip + `rotChildren`)
re-enters. This is exactly the existing bake pattern, formalized.

## 9. Worked example — `SolvedPiece` (Supplied, progressive)  ○ designed, not built

`SolvedPiece`: a Piece iteratively solved low-res → high-res over several passes.

```
UI:     Piece changes → rot SolvedPiece (cancel in-flight job, gen→G) → Ensure() kicks job(G) on a worker
worker: pass1 (low-res) → marshal → if gen==G: Supply(p1)   // UI: fresh + rotChildren → render low-res
worker: pass2,3,4       → marshal → … Supply(p4)            // render progressively sharper; done
        (Piece changes mid-solve → rot → gen→G+1, cancel G → late G passes dropped at the gen check)
```

Progressive loading is just **several `Supply` calls** — each freshens self + rots children,
so a downstream flat-layout re-derives against each new resolution. **Async is contagious
downstream:** anything derived from a Supplied transient inherits "might-not-be-ready" (its
reads must `Peek`), because you can't refresh past a not-yet-produced ancestor.

**Deferred to when `SolvedPiece` is actually built** (don't invent ahead of the use case):
- **Status** — a richer state than the Fresh/Stale bool (e.g. `Solving(k/n)`, a %, an ETA).
  `SolvedPiece` will say what states it needs.
- **Generation guard** — the epoch/cancel token above that distinguishes "next pass of the
  current solve" (apply) from "a superseded stale solve" (drop).

## 10. As-built vs designed

| Piece | State |
|---|---|
| `Transient<T>` with `IsFresh`/`IsStale`, `Value`, `Peek`, `Set`, `Rot`, `Clear` | ✓ built |
| One hand-wired edge: `Pattern.Apply/Invert` rots `CreaseMap` | ✓ built |
| Real/Transient/Ephemeral distinction | ✓ built (AGENTS.md) |
| Parent/Child edges + `rotChildren` cascade | ○ designed |
| `Grown` / `Supplied` flavor (formalized) | ○ designed |
| `.Value` throws on stale-Supplied | ○ designed |
| `Supply` method (from `Set`) + Grown/Supplied/Grow vocab in `Transient.cs` | ✓ built |
| Single-writer rule (formalized) | ○ designed (the bake already approximates it) |
| `SolvedPiece`, Status, generation guard | ○ designed, deferred |

## 11. Glossary

**Real** · **Transient** · **Ephemeral** — the three state kinds (§2).
**Fresh / Stale** — a transient's freshness bool (§4).
**Rot** — Fresh→Stale; cascades down (§5).
**Refresh** — Stale→Fresh; umbrella for **Grow | Supply** (§5).
**Grow** — a Grown transient rebuilds itself on read (the self door).
**Supply** — a producer feeds a Supplied transient (the push door; method `Set` today).
**Real (tree)** — the one authored-node class; ownership tree; never stale; undo via its Store (§2, §3).
**Transient (DAG)** — derived node; dependency DAG hanging off the Reals; fresh/stale (§3).
**Parent / Child** — *contains* in the Real-tree, *derives-from* in the Transient-DAG; rot walks the DAG's down-edges (§3, §5).
**Consumer** — anything that projects a Real (the View → its geometry Transient; a panel → its values) (§3).
**Element** — *gone; it's just `Real`.* No Element class, facet, or term — what you'd have called an element is a `Real` (with geometry).
**Grown / Supplied** — the flavor axis: on-demand-self vs prepared-in-advance (§6).
**`.Value` / `Peek`** — assertive vs tentative read (§7).
**Single-writer rule** — graph state on one thread; work off-thread, re-enters via `Supply` (§8).

## 12. Implied renames / cleanup (when this lands)

- ✓ `Set` → **Supply** and `_regen` → **Grow** in `Transient.cs`; "TryGet" scrubbed from its comments.
- Adopt **Refresh** as the umbrella term elsewhere in comments/docs (ongoing).
- ✓ **Closed** the [CODE-REVIEW.md](../CODE-REVIEW.md) Tier-4 "dirty bit" / "derives-from dependency"
  drift: AGENTS.md's Transient definition (+ DOC-TX-REFACTOR + DoD) now use the Fresh/Stale + Grow/Supply
  model — no "dirty bit".
- **"Element" retired → "Real."** This spec drops the Element class (a Real is the node; geometry is a
  Transient child; "viewable" = "has a geometry Transient"). The `AGENTS.md` (~:297) and
  `DOC-TX-REFACTOR.md` (:65-66) glossary lines that still define *"Element (Piece, Crease — was entity)"*
  should be reframed to **"the viewable/selectable Reals → just Reals"** — *pending sign-off* (locked vocab).
