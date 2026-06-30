# File > Save / File > Open — neutral grouped-OBJ document

> **Status: SPEC — not yet implemented.** No File > Open / Save / Save As menu items and no document
> round-trip exist yet (only Import + Export do). This is the plan.
>
> **Supersedes** this doc's earlier design (a self-contained `.crease` **JSON snapshot**). The decision was
> re-opened in a 2026-06-30 brainstorm and redirected to a **neutral, interop-first** format: the document is
> a **plain grouped, welded `.obj`** carrying mesh + partition and *nothing app-specific*. (History of the
> JSON plan is in git.) Vocabulary/decisions settled live (propose→accept; the user's call).

Sequel to [`DOC-TX-REFACTOR.md`](../DOC-TX-REFACTOR.md) (Real/Transient/Ephemeral). Two parts: **Design
Note** + **Implementation Plan**.

---

# Part 1 — Design Note

## The principle: neutral for as long as we can

The guiding constraint (the brainstorm's first decision) is **interop-first**: prefer an open interchange
format other tools (Rhino, Blender, MeshLab) read, and keep a proprietary surface at **zero** for as long as
the data can be expressed neutrally. A CreaseStudio document is therefore a **plain `.obj`** — it opens
anywhere as an ordinary connected grouped mesh; only CreaseStudio also reads the partition out of it.

This rides the Real/Transient/Ephemeral line (the third thing it governs, after undo + regen), trimmed by the
interop constraint and the "params aren't document state" decision:

- **Saved (Real, minus params):** the **welded authoring mesh** (geometry + connected topology) and the
  **`Pattern.PieceMap`** (the partition) as OBJ **face groups**. **That pair is sufficient — creases/seams are
  reconstructed from it (interior edges between differing groups), never stored** (see below).
- **NOT saved — params:** Accuracy, Subdiv level, Crease ∠, iso/bend/anchor/scale weights, seam-pin. These
  are **app/session preferences, not document state** (Decision 3). Open uses whatever the UI currently holds.
  *(Consequence, accepted: reopening + Solving may develop differently than at save time.)*
- **NOT saved — Transient:** `CreaseMap` (regen from `PieceMap`), the developed PieceMesh + per-piece
  `SolvedPiece` cache, the flat panels, overlays. Re-Solve regenerates; the cache rebuilds.
- **Discarded — Ephemeral:** selection, camera, active phase. Open starts fresh.

## The format: a plain grouped, WELDED `.obj`

```obj
# a connected WELDED mesh: a seam is a SHARED edge between two groups -> adjacency is in the indices
v 0.0 0.0 0.0
v 1.0 0.0 0.0
...
g piece_0
f 1 2 3
g piece_5            # the stable MintId id is the group token: piece_<id>
f 3 4 5              # shares edge (3,?) with a piece_0 face -> that shared edge IS the seam between them
```

- **Pieces = OBJ face groups**, one `g piece_<id>` per piece (the stable `MintId` id), emitted before that
  piece's faces. Group token is **`g`** (the standard face-group idiom), over `o` / `usemtl` / a `# piece`
  comment.
- **Welded → seam correspondence is implicit-but-complete.** The mesh is connected: a seam between two pieces
  is a *single SHARED edge* flanked by a face in each group. So **the welded mesh + the Pattern ids are enough
  to reconstruct every crease** — an interior edge whose two faces' groups differ *is* a seam (exactly the
  app's `CreaseMap` derivation, run on load). No explicit seam table is needed; "edge A ↔ edge Q" is read
  straight off the shared vertex indices.
- **FBX solids weld on save.** A mesh imported unwelded (per-brep-face components — e.g. a cube as 6
  components) is **welded by position** into one connected mesh + groups for the document; the **unweld is
  regenerated at develop** (`UnweldByRegion` from the Pattern). *(Caveat: a non-conforming seam — mismatched
  tessellation / T-junction — can't vertex-weld, so correspondence stays geometric at those spots; conforming
  seams weld losslessly within a tolerance.)*
- **Extension: plain `.obj`.** Opening it in Rhino/Blender/MeshLab yields a connected grouped mesh; those
  tools ignore nothing they can't use (it's all valid OBJ). CreaseStudio recognizes its own files by the
  `piece_` groups — no custom extension, no magic header.
- **No params, no app metadata in the file** → a *pure* OBJ. Zero proprietary surface.

## Save captures the authoring document; Export captures the developed result

Two distinct verbs, two purposes (don't conflate):

- **Save** = the **welded authoring mesh + partition** — the reopenable document (so you keep editing). The
  developed result is never in a Save (it's Transient; re-Solve).
- **Export** (already built) = the **developed / flat** geometry as OBJ/STL — the fabrication deliverable.

Both happen to write OBJ; they differ in *what* (welded authoring+groups vs developed) and *why* (reopen vs
fab).

## Open vs Import — kept separate (Decision 4)

Because the document is just an OBJ, Open and Import are mechanically close, but they stay **distinct verbs by
intent**:

- **Open** — load a CreaseStudio document: parse `g piece_<id>` → `PieceMap`, regen `CreaseMap` from the
  welded mesh + Pattern, land in the **Piecing phase** with the partition restored. A plain OBJ with no
  `piece_` groups opens as a single unpieced mesh. Sets the current document path.
- **Import** — bring in **raw geometry** (STL / FBX / plain OBJ) to start a **new** piecing from scratch:
  geometry only, any groups ignored, → Propose. Does **not** set the document path (untitled).

## The neutrality boundary (named, deferred)

The file stays pure-OBJ until a **Real appears that the welded-mesh-plus-Pattern can't express** — chiefly
**seam B-splines** (a fitted curve per seam) and **crease *types*** (a seam labelled join-vs-separate). Plain
crease *geometry* needs nothing extra (it's derived). At that boundary we revisit, preferring to keep the
neutral OBJ *unpolluted* (a sidecar, or a proprietary container that *embeds* the OBJ) over stuffing
non-neutral data into comments — consistent with the "don't save params in the file" stance. Until then there
is no proprietary surface at all.

The one-way **`.journal`** op-log Save stays exactly as-is — the separate **replay / scripting / CLI-parity**
artifact (and live undo stream), not the document format.

## Decisions

1. **Interop-first:** the document is a neutral file for as long as the data can be expressed neutrally; zero
   proprietary surface until forced.
2. **Format = plain grouped, WELDED `.obj`** — a connected mesh + partition as `g piece_<id>` face groups.
   Welded so seams are shared edges → **welded mesh + Pattern reconstructs every crease** (no seam table). FBX
   solids weld on save; the unweld regenerates at develop. (Non-conforming seams stay geometric.)
3. **Params are NOT saved** — app/session preferences, not document state. Open uses current UI values.
4. **Open vs Import kept separate** — Open restores a grouped-OBJ document (partition); Import loads raw
   geometry (STL/FBX/OBJ) to start a new piecing.
5. **Save = welded authoring + partition; Export = developed/flat** (existing). Two verbs, two purposes.
6. **Group token `g piece_<id>`** (stable MintId id), over `o` / `usemtl` / comment encodings.
7. **Menu/shortcuts:** `File > Open…` (**Ctrl+O**), `Save` (**Ctrl+S**) / `Save As…`, `Import…` (move **off**
   Ctrl+O), `Revert`, `Export…` (existing). The Console's `.journal` Save/Load stays where it is.

## Non-goals (deferred)

Seam-B-spline / crease-type serialization (the neutrality boundary) · embedding the developed PieceMesh /
`SolvedPiece` cache · binary/compressed format · unifying the document with the `.journal` · params-in-file
(explicitly rejected) · stable cross-session ids (session-only stance).

---

# Part 2 — Implementation Plan

Build-green per step; one commit each.

### Phase 1 — engine: welded OBJ-groups round-trip (shared `src/MeshIO.cs`)
- **Weld helper:** ensure a `MeshOps`-level weld-by-position (tolerance) exists — reuse the STL-load weld
  logic — so an unwelded authoring mesh can be normalized to a connected mesh before write. (No-op input that's
  already welded passes through.)
- **Write:** add an optional `int[] pieceMap` to `WriteObj` (overload) — weld if needed, then emit
  `g piece_<id>` before each piece's face run (faces grouped by id, ascending). With no `pieceMap`, behaviour
  is unchanged (today's plain WriteObj).
- **Read:** `LoadObj` parses `g <name>`; a `piece_<int>` name assigns that id to subsequent faces → an
  `out int[] pieceMap` (faces before any group, or a non-`piece_` group → default id 0). Plain OBJs (no
  groups) → single-piece / "no partition."
- **Round-trip test** (headless / CLI hook): a welded mesh + a 2-piece `pieceMap` → Write → Load → assert
  verts/faces/`pieceMap` identical **and the derived crease set (interior edges between differing groups) is
  identical**. This is the gate.

### Phase 2 — app: File > Open / Save / Save As (PieceSolver)
- **Save / Save As:** `SaveFileDialog` (`*.obj`) → `MeshIO.WriteObj(path, authoringMesh, pieceMap)` (welds FBX
  solids internally). Track the current document path so `Ctrl+S` re-saves silently; `Save As` always prompts
  + retargets the path.
- **Open:** `OpenFileDialog` (`*.obj`) → `MeshIO.LoadObj(path, out mesh, out pieceMap)` → `new FlowSession` →
  `RebindPattern()` → set `Pattern.PieceMap` → regen `CreaseMap` → activate the Piecer with the restored
  partition → upload + frame. Sets the document path. (Selection/camera/develop start fresh.)
- **Import:** keep the existing mesh import (STL/FBX/OBJ → geometry, → Propose); **move it off Ctrl+O**; it
  leaves the document untitled.

### Phase 3 — polish
- Title-bar **dirty marker** (`*`) + **"save changes?"** prompt on Open / Import / Revert / quit.
- Error handling: unreadable/short OBJ, group/face mismatch → reject cleanly with a Console message.

### Deferred (named)
- Seam-B-spline / crease-type serialization at the neutrality boundary (sidecar or embedding container).
- Optional develop-cache embed · binary format if files get big · journal *replay*.
- *(Follow-on, not Save/Open):* welding FBX solids at **import** too would unify the FBX-solid develop path
  and the painted-partition path onto one "welded mesh + Pattern" representation — a simplification worth
  considering once Save/Open lands.

## Verification & risks
- Each step builds 0/0 + launches; Phase 1's round-trip test is the gate.
- **Topology fidelity** is the main risk: the writer must emit, and the reader reproduce, *exact* vertex/face
  indexing so `PieceMap` stays index-coupled and the welded seams (shared edges between groups) survive →
  creases reconstruct identically (the round-trip test asserts the derived crease set matches).
- **Welding FBX solids** is the secondary risk: a tolerance choice, and non-conforming seams that can't
  vertex-weld (correspondence stays geometric there — acceptable, named).
- No engine/solver math changes → bench checksums unaffected. Document Save/Open is PieceSolver-only (net8)
  plus the shared `MeshIO` OBJ-group support (also usable from the CLI).
