# Dev2PQ → primary studio (PieceSolver) integration spec

How a main agent wires `dev2pq.exe` into the studio Solve pipeline as a subprocess step.
Same pattern as `PieceSolver/Bff.cs` (drives `bff-command-line.exe`); the worktree's
`relax/Relax.cs` `RunDirectional` is a working reference. Build: `relax/directional/BUILD.md`.

## What it is / where it slots
`dev2pq.exe` = ruling-aligned PQ-strip remeshing of a **developable** surface (Verhoeven/Vaxman 2021)
via Vaxman's Directional library. It runs **downstream of develop** (`IsometricLM`): develop a panel →
`dev2pq` for its ruling/strip structure → the oversize-and-trim crease kernel. Per-panel.

## Subprocess contract
- Invoke: `dev2pq.exe in.off out.off [flags]`
- Input: triangle mesh as **OFF**. Output: **polygon OFF** (degree-prefixed faces, mixed n-gons).
- Flags (all optional): `rulefeed` (feed raw ruling r instead of r⊥ — diagnostic), `iso`
  (`branched_isolines`: rulings only, no DCEL — robust where the mesher crashes), `lr=<v>`
  (strip density / lengthRatio, default 0.02; raise to coarsen), `rs` (round seams), `dumpu` (diag).
- Exit: `0` = success (out.off written). **Non-zero = failure** — includes the mesher's DCEL
  assert (`abort`, exit 3) on the heavily-branched figures. `abort` is **dialog-suppressed in
  `main()`** (`_set_abort_behavior`), so a crash exits cleanly instead of popping a WER box.
- Three failure modes, all to be handled as a clean `false`: **crash** (exit≠0), **hang** (a few
  branched figures spin in the mesher — needs the timeout), **empty** (out.off has 0 faces).

## Driver (timeout is a caller-visible parameter — required)
Model on `Bff.cs`. **The timeout is passed in by the call site**, not hardcoded inside:

```csharp
// returns false on crash / hang(>timeout) / empty output; never throws to the caller.
public static bool TryDev2PQ(PlanktonMesh mesh, int timeoutMs,
                             out PlanktonMesh result, out string log);
// call site, timeout visible:
//   bool ok = Dev2PQ.TryDev2PQ(developedPanel, timeoutMs: 30_000, out var pq, out var log);
```

Body (the only deltas from `Bff.TryFlatten`):
1. `MeshIO`-write `mesh` to a temp `.off` (triangles, degree-3 faces — a few lines; see
   `RunDirectional` for the inline writer).
2. `ProcessStartInfo { FileName = Dev2pqExePath, Arguments = "\"in\" \"out\"", RedirectStd*,
   UseShellExecute=false, CreateNoWindow=true }`. (No `WorkingDirectory` needed — the gmp DLLs sit
   next to the exe.)
3. **`if (!proc.WaitForExit(timeoutMs)) { proc.Kill(); log="dev2pq timed out >"+timeoutMs+"ms"; return false; }`**
   — this is the hang guard; `Bff.cs` omits it (no timeout), so do NOT copy that line verbatim.
4. `if (proc.ExitCode != 0) return false;` (crash) · `if (!File.Exists(out)) return false;` (no output).
5. Read the polygon OFF → `PlanktonMesh` (header `OFF`, then `nv nf 0`, `nv` vertex lines, `nf`
   `deg i0 i1 …` face lines → `Mp.Faces.AddFace(idx[])`; n-gons are fine). Empty (`nf==0`) → `false`.

## Deploy
Copy `dev2pq.exe` + `gmp-10.dll` + `gmpxx-4.dll` to a known folder; hardcode `Dev2pqExePath`
like `Bff.ExePath`. Rebuild per `BUILD.md` (needs `/O2` + GMP; **never** `/DNDEBUG`).

## Robustness contract
- **Crashes are contained** — proven all session: `relax.exe` (a WPF app, same pattern) survives
  every dev2pq abort. A failed panel is a clean `false`, never a host crash.
- The **well-behaved majority** returns a clean PQ mesh. The **branched minority**
  (fig15/16/18/19/20/21) fails — contained, per panel. For those, the caller may either skip the
  panel or re-run with the `iso` flag to recover the rulings (no PQ polygons, just the level-set
  lines). This is a known-open correctness gap, independent of integration readiness.
