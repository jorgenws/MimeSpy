namespace MimeSpy;

/// <summary>
/// Which end of the file a <see cref="PronomByteSequence"/>'s offsets are measured from.
/// </summary>
internal enum PronomAnchor
{
    BeginningOfFile,
    EndOfFile,
}

/// <summary>
/// A single byte-matching rule within a <see cref="PronomBytePattern"/>: PRONOM's
/// extended-hex wildcard forms, alongside a plain fixed byte. Each token always
/// consumes a fixed number of bytes (<see cref="Length"/>), even when its value
/// isn't fixed - a signature never has to guess how many bytes a wildcard "ate".
/// </summary>
internal abstract record PronomByteToken(int Length)
{
    public abstract bool Matches(ReadOnlySpan<byte> data, int offset);
}

internal sealed record PronomLiteralToken(byte Value) : PronomByteToken(1)
{
    public override bool Matches(ReadOnlySpan<byte> data, int offset) => data[offset] == Value;
}

/// <summary>[XX:YY] - an inclusive, big-endian numeric range over Low.Length bytes.</summary>
internal sealed record PronomRangeToken(byte[] Low, byte[] High) : PronomByteToken(Low.Length)
{
    public override bool Matches(ReadOnlySpan<byte> data, int offset)
    {
        var candidate = data.Slice(offset, Length);
        return CompareBytes(candidate, Low) >= 0 && CompareBytes(candidate, High) <= 0;
    }

    private static int CompareBytes(ReadOnlySpan<byte> a, byte[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] < b[i] ? -1 : 1;
            }
        }

        return 0;
    }
}

/// <summary>[!hex] - matches anything except this exact byte value.</summary>
internal sealed record PronomNegatedValueToken(byte[] Value) : PronomByteToken(Value.Length)
{
    public override bool Matches(ReadOnlySpan<byte> data, int offset) => !data.Slice(offset, Length).SequenceEqual(Value);
}

/// <summary>[&amp;XX] / [!&amp;XX] - a single byte tested against a bitmask.</summary>
internal sealed record PronomBitmaskToken(byte Mask, bool Negated) : PronomByteToken(1)
{
    public override bool Matches(ReadOnlySpan<byte> data, int offset)
    {
        var anyBitSet = (data[offset] & Mask) != 0;
        return Negated ? !anyBitSet : anyBitSet;
    }
}

/// <summary>(HH|HH|...) - matches any one of a short list of fixed byte values, all the same length.</summary>
internal sealed record PronomByteSetToken(IReadOnlyList<byte[]> Alternatives) : PronomByteToken(Alternatives[0].Length)
{
    public override bool Matches(ReadOnlySpan<byte> data, int offset)
    {
        // Indexed rather than foreach - see PronomSignatureMatcher's loop comment.
        var candidate = data.Slice(offset, Length);
        for (var i = 0; i < Alternatives.Count; i++)
        {
            if (candidate.SequenceEqual(Alternatives[i]))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A fixed-length byte pattern - a run of <see cref="PronomByteToken"/>s, most often
/// all <see cref="PronomLiteralToken"/> but occasionally mixing in a wildcard token.
/// </summary>
internal sealed record PronomBytePattern(IReadOnlyList<PronomByteToken> Tokens)
{
    public int Length { get; } = Tokens.Sum(t => t.Length);

    /// <summary>
    /// The flat literal bytes this pattern reduces to, if every token is a plain
    /// <see cref="PronomLiteralToken"/> - lets the matcher use
    /// <see cref="ReadOnlySpan{T}"/>'s vectorized IndexOf/LastIndexOf instead of a
    /// manual per-position token scan, for the common case where a pattern has no
    /// wildcards at all.
    /// </summary>
    public byte[]? LiteralBytes { get; } = Tokens.All(t => t is PronomLiteralToken)
        ? [.. Tokens.Cast<PronomLiteralToken>().Select(t => t.Value)]
        : null;

    public bool MatchesAt(ReadOnlySpan<byte> data, int start)
    {
        if (start < 0 || start + Length > data.Length)
        {
            return false;
        }

        if (LiteralBytes is { } literal)
        {
            return data.Slice(start, Length).SequenceEqual(literal);
        }

        // Indexed rather than foreach - see PronomSignatureMatcher's loop comment.
        var offset = start;
        for (var i = 0; i < Tokens.Count; i++)
        {
            if (!Tokens[i].Matches(data, offset))
            {
                return false;
            }

            offset += Tokens[i].Length;
        }

        return true;
    }
}

/// <summary>
/// One step of a <see cref="PronomSubSequence"/>'s left- or right-fragment chain:
/// a gap window (<see cref="MinOffset"/>/<see cref="MaxOffset"/>) from the previous
/// step, followed by any one of <see cref="Alternatives"/> - multiple fragments at
/// the same PRONOM "Position" are alternatives (OR), not a further chain (AND).
/// </summary>
internal sealed record PronomFragmentGroup(int MinOffset, int MaxOffset, IReadOnlyList<PronomBytePattern> Alternatives);

/// <summary>
/// One fixed anchor byte pattern within a <see cref="PronomByteSequence"/>, optionally
/// flanked by chains of alternative fragments extending backward
/// (<see cref="LeftFragmentGroups"/>) and/or forward (<see cref="RightFragmentGroups"/>)
/// from the anchor - PRONOM's way of expressing "one of several possible words" either
/// side of a short, cheap-to-search fixed byte run. <see cref="Position"/> gives this
/// whole anchor+fragments unit's matching order among its <see cref="PronomByteSequence"/>
/// siblings, and <see cref="MinOffset"/>/<see cref="MaxOffset"/> bound the gap between it
/// and the previous one (or the file boundary, for the first).
/// </summary>
/// <param name="Position">Matching order among this unit's <see cref="PronomByteSequence"/> siblings.</param>
/// <param name="MinOffset">Minimum gap, in bytes, before this unit.</param>
/// <param name="MaxOffset">Maximum gap, in bytes, before this unit. Null means the gap is unbounded.</param>
/// <param name="Anchor">The byte pattern searched for directly - usually literal, occasionally wildcarded.</param>
/// <param name="LeftFragmentGroups">Alternative fragment chains extending backward from the anchor, nearest first.</param>
/// <param name="RightFragmentGroups">Alternative fragment chains extending forward from the anchor, nearest first.</param>
internal sealed record PronomSubSequence(
    int Position,
    int MinOffset,
    int? MaxOffset,
    PronomBytePattern Anchor,
    IReadOnlyList<PronomFragmentGroup> LeftFragmentGroups,
    IReadOnlyList<PronomFragmentGroup> RightFragmentGroups);

/// <summary>
/// An ordered chain of <see cref="PronomSubSequence"/> anchors that must all be found,
/// in order and within their gap bounds, measured from <see cref="Anchor"/>.
/// </summary>
internal sealed record PronomByteSequence(PronomAnchor Anchor, IReadOnlyList<PronomSubSequence> SubSequences);

/// <summary>
/// A named byte signature from PRONOM's DROID signature file. A format matches if
/// every one of its <see cref="ByteSequences"/> matches (BOF and EOF sequences on
/// the same signature are an AND, not an OR).
/// </summary>
internal sealed record PronomInternalSignature(int Id, IReadOnlyList<PronomByteSequence> ByteSequences);
