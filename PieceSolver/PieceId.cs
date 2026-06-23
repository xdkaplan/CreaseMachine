using System;

namespace PieceSolver
{
    // A zero-cost typed handle over the int region id stored densely in Pattern.PieceMap.
    // The id, distinct from the future first-class Piece entity. (`Id`, not `ID` — .NET treats
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
    }
}
