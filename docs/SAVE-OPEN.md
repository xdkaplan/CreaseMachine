# File > Save / File > Open — document serialize / deserialize

A real round-trip: **Save** writes the document to disk, **Open** restores it. The just-merged op-log
"Save" is **one-way** (it serializes the in-effect piece ops, but Open/replay is unwired — and pure replay
is blocked: Propose isn't journaled, so the seeded partition can't rebuild, and op ids are session-local).
This spec makes the round-trip robust by saving **state, not a recipe**.

Sequel to [`DOC-TX-REFACTOR.md`](DOC-TX-REFACTOR.md) (Real/Transient/Ephemeral) and
[`SOLVER-PHASE.md`](SOLVER-PHASE.md). Two parts: **Design Note** + **Implementation Plan**.

---

# Part 1 — Design Note

## The principle: save Real, regen Transient, discard Ephemeral

This is the third thing the Real/Transient/Ephemeral line governs (the other two: undo, regen). The document
file is **exactly the Real state**:

- **Real → serialized:** the **mesh** (geometry + topology), the **`Pattern.PieceMap`** (the partition), and
  the **params** (Accuracy, Subdiv level, Crease ∠, the bake/iso settings). *(Later, when they become Real:
  crease types `separate`/`join`, seam B-splines.)*
- **Transient → NOT serialized, regenerated on Open:** `CreaseMap` (regen from `PieceMap`), the developed
  PieceMesh, the flat panels, overlays. (You re-Solve to get the develop back — or, later, the optional
  "save with caches" knob embeds it.)
- **Ephemeral → discarded:** selection, camera, active phase. Open starts fresh.

## Why a self-contained snapshot, not journal-replay

Two candidate formats:

| | **(A) Snapshot** (recommended) | **(B) Journal-replay** |
|---|---|---|
| Save | the Real state: embedded mesh + `PieceMap` + params | `load <path>` + the `setpiece` op-lines + params |
| Open | read state → rebuild Doc + regen Transients | re-`load` the source, **re-Propose**, re-apply ops |
| Self-contained? | **yes** (mesh embedded) | no — depends on the source file at `<path>` |
| Needs deterministic Propose? | **no** — saves the *result* (`PieceMap`) | **yes** — replays edits *onto* the seed (the blocker) |
| Needs stable GUIDs? | **no** — the file is internally index-consistent | yes — op ids are session-local today |
| Speed | fast | slow (re-runs the flow on Open) |
| Human-readable / scriptable | less (it's state) | **yes** (it's a script) |

**Snapshot wins for document persistence** precisely because it stores the *result*, sidestepping all three
known gaps (Propose-determinism, id-stability, source-file dependency). We save `PieceMap` directly, so a
re-Propose is never needed and the indices are self-consistent within the file.

**The `.journal` op-log keeps its own job** — it's the **replay / scripting / CLI-parity** artifact (and the
live undo stream), not the document format. So reframe: **File > Save/Open = the document snapshot**; the
op-log/journal becomes **Export/Replay Journal** (the Console's existing Save button / a separate menu).
Two artifacts, two purposes — don't conflate them. (If scripting later needs to rebuild pieces, *that's* when
Propose gets journaled + GUIDs land; the document round-trip doesn't wait on it.)

## The format

A single self-contained file (`*.crease` — own extension so it's distinct from `.journal` and from mesh
imports). **JSON** for v1 (System.Text.Json, PieceSolver is net8): human-inspectable, versioned, easy.

```jsonc
{
  "version": 1,
  "source": "Unwelded.fbx",          // provenance only (the mesh is embedded; not loaded from here)
  "vertices": [x,y,z, x,y,z, ...],   // flat double[] (nV*3)
  "faces":    [a,b,c, a,b,c, ...],   // flat int[] (nF*3); triangles (quads triangulate on import)
  "pieceMap": [p, p, ...],           // int[] (nF) — the partition (Real)
  "params":   { "accuracyPct": 1.0, "subdivLevel": 1, "creaseAngleDeg": 10, "iso": 1.0, "fair": ..., "anchor": ..., "bend": ..., "fixEdges": false, "seamRatio": ... }
}
```

The embedded mesh stores vertices + face-index triples explicitly, so it **preserves exact topology**,
including FBX-style **unwelded seams** (coincident-but-separate verts → one component per piece). An STL
re-weld would lose that — which is the other reason not to "just save an STL + a sidecar."

## Round-trip

- **Save:** `mesh = _session.Mesh`, `pieceMap = _pattern.PieceMap`, `params = _sim` → write JSON.
- **Open:** read JSON → build `PlanktonMesh` (Vertices.Add + Faces.AddFace) → `new FlowSession(mesh)` →
  `RebindPattern()` → `_pattern.PieceMap = saved` → `RegenCrease()` (Transients regen) → restore `_sim`
  params → upload + frame. Selection/camera/`_developed` start fresh (Ephemeral/Transient).

## Decisions

1. **File > Save/Open serialize the Real state as a self-contained snapshot** (embedded mesh + `PieceMap` +
   params). Robust round-trip; no re-Propose, no GUID dependency, no source-file dependency.
2. **`.crease` JSON** (v1), with a `version` field for forward migration.
3. **Transients regenerate on Open; Ephemeral starts fresh.** The developed mesh is *not* saved (re-Solve) —
   a "save with caches" knob is the deferred optional extra.
4. **The op-log/`.journal` stays the replay/scripting artifact**, reframed as Export/Replay Journal — *not*
   File > Save. (Closing the journal-replay gap = a separate effort, gated on Propose-journaling + GUIDs.)
5. **Menu/shortcuts:** `File > Open…` (**Ctrl+O**, the document), `Import…` (mesh — STL/FBX/OBJ, the one just
   added; **move it off Ctrl+O** since Open is the more primary verb), `Save` (**Ctrl+S**) / `Save As…`,
   `Revert`, then Export/Replay Journal under Window or File.

## Non-goals (deferred)

Unifying the document format with the journal · serializing crease types / seam B-splines (add to the schema
when they become Real) · embedding the developed PieceMesh as a cache · binary/compressed format · stable
GUIDs (the snapshot doesn't need them).

---

# Part 2 — Implementation Plan

Build-green per step; one commit each.

### Phase 1 — `DocumentIO` (pure serialize / deserialize)
- `PieceSolver/DocumentIO.cs`: `Save(string path, PlanktonMesh mesh, int[] pieceMap, DocParams p)` and
  `Load(string path, out PlanktonMesh mesh, out int[] pieceMap, out DocParams p)` over the JSON schema
  above (System.Text.Json). `DocParams` = a small POCO mirroring the saved `_sim` fields.
- A version check on Load (reject/upgrade unknown versions with a clear message).
- **Headless-ish sanity** (a tiny round-trip test, or a `crease`-CLI hook): Save a mesh + a 2-piece
  `pieceMap` → Load → assert verts/faces/pieceMap identical and topology (component count) preserved.

### Phase 2 — wire File > Open / Save / Save As
- **Save / Save As:** `SaveFileDialog` (`*.crease`) → `DocumentIO.Save(path, _session.Mesh, _pattern.PieceMap,
  DocParams.From(_sim))`. Track the current doc path so `Ctrl+S` re-saves silently, `Save As` always prompts.
- **Open:** `OpenFileDialog` (`*.crease`) → `DocumentIO.Load` → set `_session`/`_meshPath`-equivalent →
  `RebindPattern` → `_pattern.PieceMap = loaded` → `RegenCrease` → apply params to `_sim` → upload + `_reframe`
  → activate the Piecer (post-load you're in the Piecing phase with the restored partition).
- Reshuffle shortcuts per Decision 5 (Open = Ctrl+O; Import drops Ctrl+O).

### Phase 3 — polish
- Title-bar dirty marker + "save before Open/Revert/quit?" prompt (since the document now has unsaved state).
- Error handling: corrupt/old file, face/vertex-count mismatch (reject cleanly).

### Deferred (named)
- Reframe the op-log Save → "Export Journal" + wire journal **replay** (needs Propose-journaling + GUIDs).
- Crease-types/seams in the schema (when Real). Optional develop-cache embed. Binary format if files get big.

## Verification & risks
- Each step builds 0/0 + launches; Phase 1's round-trip test is the gate (Save→Load→identical).
- **Topology fidelity** is the main risk: the embedded mesh must reproduce *exact* face/vertex indexing so
  `PieceMap` stays index-coupled (and unwelded seams survive). The round-trip test covers it.
- No engine/solver changes → bench checksums unaffected. The document Save/Open is PieceSolver-only (net8).
