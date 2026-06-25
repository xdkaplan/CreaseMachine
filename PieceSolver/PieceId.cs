using System;

namespace PieceSolver
{
    // A zero-cost typed handle over the int piece id stored densely in Pattern.PieceMap.
    // The id, distinct from the future first-class Piece Real. (`Id`, not `ID` — .NET treats
    // "Id" as a word, cf. Process.Id.) The int still lives in the hot-path array; this struct
    // only appears at the API / selection boundary, where it makes "a selection is a Piece" free.
    public readonly struct PieceId : IEquatable<PieceId>
    {
        public readonly int Value;
        public PieceId(int value) { Value = value; }

        public bool Equals(PieceId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PieceId other && Equals(other);
        public override int GetHashCode() => Value;
        public static bool operator ==(PieceId a, PieceId b) => a.Value == b.Value;
        public static bool operator !=(PieceId a, PieceId b) => a.Value != b.Value;
        public override string ToString() => Value.ToString();

        // Pluralized display name for a COUNT of pieces — "1 Piece" / "5 Pieces". A low-churn home for the
        // piece-naming helper until the first-class Piece Real type lands; then it migrates to
        // that Real (with a heterogeneous Name(list) overload for "15 Pieces, 10 Tabs" / "25 Reals").
        public static string Name(int count) => $"{count} {(count == 1 ? "Piece" : "Pieces")}";
    }
}
