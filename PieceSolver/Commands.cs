using System.Collections.Generic;

namespace PieceSolver
{
    // Commands: pure functions that read Selection + Real state and COMPUTE an IDelta (they never mutate —
    // Doc.Run applies the result). The user calls these "Tools"; we call them Commands. See DOC-TX-REFACTOR.md.
    static class Commands
    {
        // Merge every selected piece into the survivor (the min id). The caller must ensure the selection is a
        // single connected group (Pattern.RegionsConnected) so the result is one connected piece — no auto-split.
        public static PieceDelta Merge(Pattern p, HashSet<int> selection, int keep)
        {
            var ops = new List<Op>();
            var map = p?.PieceMap;
            if (map == null || selection == null || selection.Count < 2) return new PieceDelta(ops);
            for (int f = 0; f < map.Length; f++)
                if (selection.Contains(map[f]) && map[f] != keep) ops.Add(new Op(f, map[f], keep));
            return new PieceDelta(ops);
        }
    }
}
