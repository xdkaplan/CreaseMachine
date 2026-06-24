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
    }
}
