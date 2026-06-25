using System.Collections.Generic;

namespace PieceSolver
{
    // Commands: pure functions that read Selection + Real state and COMPUTE an IDelta (they never mutate —
    // Doc.Run applies the result). The user calls these "Tools"; we call them Commands. See DOC-TX-REFACTOR.md.
    static class Commands
    {
        // Merge each selected piece into its connected-component survivor (Pattern.MergeGroups): every adjacent
        // cluster fuses independently; an isolated selected piece maps to itself (no-op). Empty if nothing moves.
        public static PieceDelta Merge(Pattern p, Dictionary<int, int> groups)
        {
            var ops = new List<Op>();
            var map = p?.PieceMap;
            if (map == null || groups == null) return new PieceDelta(ops);
            for (int f = 0; f < map.Length; f++)
                if (groups.TryGetValue(map[f], out int surv) && map[f] != surv) ops.Add(new Op(f, map[f], surv));
            return new PieceDelta(ops);
        }

        // DelPiece: delete the selected pieces, healing each into its dominant surviving neighbour — the
        // "kill & donate" op, i.e. the same Pattern.Delete a Ctrl-drag runs with nothing selected, but driven
        // from the selection instead of a brush footprint. touched = every face of the selected pieces, so
        // Delete's MostlyMarked returns exactly the selection; ComputeDelta captures the heal as one rolled-back
        // delta for Doc.Run. Empty when a selected blob has no surviving neighbour (e.g. the whole mesh
        // selected) — there's nothing to donate to, so nothing moves.
        public static PieceDelta DelPiece(Pattern p, HashSet<int> selection)
        {
            var map = p?.PieceMap;
            if (map == null || selection == null || selection.Count == 0) return new PieceDelta(new List<Op>());
            var touched = new HashSet<int>();
            for (int f = 0; f < map.Length; f++) if (selection.Contains(map[f])) touched.Add(f);
            return p.ComputeDelta(() => p.Delete(touched));
        }
    }
}
