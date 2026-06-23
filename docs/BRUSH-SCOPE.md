# Brush scope — CreaseStudio (built on the PieceSolver base)

Status: scoping spec for the brush system in the consolidated app. The `studio/`
brush zoo was an exploratory study; the plan is to **port the brush *chassis*,
cull all 14 experimental brushes, and ship one good brush** (Freeze /
Consolidate). PieceSolver currently has **no** brush UI (0 brush refs) — this is
a port-the-scaffolding + build-one-brush task, not a merge.

The one-liner: **port the brush chassis (size / hotkeys / preview / stroke /
falloff) from `studio/`, throw away all 14 brush engines, and bolt on a single
Freeze brush that writes the `BrushWeights` field the flow already honours.**

This brush is **stage 3** of the consolidated pipeline (*manual discernment — a
brush isolates patches*); see [`APP-CONSOLIDATION.md`](APP-CONSOLIDATION.md) for
the full 6-stage workflow. The brush is one of three studio→PieceSolver ports
(brush chassis · the **crease proposer**, stage 2 · **Shine shading**), not the
only one. It also embodies the project's **fewer-options** principle: one brush
instead of fourteen, secondary controls behind *Advanced* rather than promoted.

> **Line refs below are approximate** — they predate the crease-proposer + Shine
> merge into `studio/`, which shifted `MainWindow.xaml.cs`. Current anchors:
> `enum BrushKind` :66 · `_previewDot = new` :113 · size hotkeys (`OemCloseBrackets`)
> :199 · `WireBrushTile` :260 · `ResizeBrush` :807. The brush code is all intact,
> just moved; treat the numbers as "find nearby," not exact.

---

## Layer 0 — Engine hook (already shared in `src/`; keep as-is, build nothing)

The paint mechanism already exists and both the GH plugin and `studio/` use it:

- `FlowSession.BrushWeights` (`src/Session.cs:41`) — per-vertex additive
  **deCraze boost**; `null` = none. The comment already names the trajectory:
  *"future: freeze field."*
- `DevelopabilityEnergy` consumes it in the L1 / deCraze block
  (`src/DevelopabilityEnergy.cs:1322`, `:1347`):
  `edgeCraze += 0.5 * (brushWeights[va] + brushWeights[vb])`. So **painting a
  vertex locally raises deCraze on its incident edges → consolidates sub-panels /
  locks creases exactly where you paint.** That *is* the freeze-creases north
  star (`AGENTS.md`), already wired into the flow.

**Implication:** the "one good brush" needs no new engine math — it writes
`BrushWeights`. PieceSolver already threads `BrushWeights` through its
`FlowSession`; it simply exposes no UI on it yet.

---

## Layer 1 — Infrastructure to PORT (the chassis to keep)

Lift these from `studio/` **decoupled from the 14-brush dispatch** — they are
brush-agnostic and reusable:

| Capability | Where in `studio/` | Notes |
|---|---|---|
| Brush params | `SimSettings.cs:34` — `BrushSize`, `BrushStrength`, `BrushSoftness`, `BrushFlow` | shared knobs, not per-brush |
| **Size hotkeys** | `MainWindow.xaml.cs:178-193` — `]` grow / `[` shrink, `Ctrl+Shift` = harden/soften | via `ResizeBrush(factor)` (`:637`), clamp 1–100 |
| Footprint preview | `_previewDot` Ellipse (`:70`, `:101-110`), `UpdatePreview(hover)`, `ScreenRadiusPx` (`:959-979`) | world-radius → screen projection |
| Surface pick / hover | `PickSurface(screen, out hit)` raycast, `_lastHover` | the screen→mesh ray |
| Stroke / dab engine | `_dabAccum` distance-carry (`:585-611`), dab spacing ~ ½ on-screen radius | zoom-consistent deposition along a drag |
| Radius + falloff | `R/R2` + gaussian `sigma = BrushSoftness*R` (`:871`) | per-vertex weight kernel |
| Drag-mode routing | `:49` — right=orbit, shift+right=pan, left=paint | |
| Journal integration | brush stroke as a `StudioCommand` (records / replays / undoes) | unify into PieceSolver's `Journal` |

Cleaner deposit-with-falloff reference: the **GH plugin** `HandleBrushPaint`
(`src/CreaseMachine.cs:475`) — 3D radius, cubic falloff, accumulates into
`brushWeights[]`. Consider porting *that* deposit core and wrapping `studio/`'s
screen-space stroke + preview around it, rather than `studio/`'s per-kind tangle.

---

## Layer 2 — Experiments to CULL (delete, do not port)

The entire 14-brush zoo and its plumbing:

- `enum BrushKind { None, Noise, Polish, Buff, Sharpen, Crease, Flatten, Smooth,
  Inflate, Scrape, Fill, Relax, Panel, Unfork, Fold }`
  (`studio/MainWindow.xaml.cs:54`) → collapse to one.
- All 14 `WireBrushTile(...)` calls (`:222-235`), the 14-way `SetBrush`
  `IsChecked` bookkeeping (`:251-264`), and **all per-kind apply math** (the
  `R = BrushSize…` blocks at `:711`, `:755`, `:871`, …).
- `studio/Perlin.cs` — only the Noise brush needs it; cull with it.
- The brush-tile strip in `studio/MainWindow.xaml`.

---

## Layer 3 — The one good brush: **Freeze / Consolidate**

The single survivor, built fresh against Layer 0:

- **What it does:** paint `BrushWeights` (local deCraze boost) with the Layer-1
  falloff kernel; the flow then consolidates sub-panels and locks creases under
  the stroke. The north-star brush.
- **Params (reuse, don't invent):** `BrushSize` = footprint radius,
  `BrushStrength` = deposited deCraze magnitude (mirror the existing
  `deCraze = BrushStrength * DeCrazeMax` mapping the old Buff brush and the GH
  plugin use), `BrushSoftness` = falloff, `BrushFlow` = accumulation per dab.
- **Feedback:** `BrushWeights` is already a viewable per-vertex field — colour
  the mesh by it, and the new **Ruling / Gradient LIC** will visibly respond as
  painted regions consolidate. Free, strong visual confirmation.
- **Forward path (v2, not now):** `BrushWeights` → a true **freeze field**
  (Dirichlet pin of painted vertices) per the `Session.cs:41` "future: freeze
  field" note. Keep v1's data model (a per-vertex scalar field) so v2 needs no
  schema change.

---

## Acceptance

1. One brush in the UI; size via `[` / `]`; footprint preview tracks hover;
   stroke deposits with falloff into `BrushWeights`.
2. Running the flow visibly consolidates / locks under the painted region;
   mesh-coloured-by-`BrushWeights` and the LIC both reflect it.
3. `Perlin.cs` and all `BrushKind != Freeze` code gone; brush stroke journals +
   replays.
4. **No engine (`src/`) changes required** — if a brush needs new `src/` math,
   it is out of scope for "one good brush."
