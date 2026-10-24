namespace MimeSpy;

// Which end of the file a PronomByteSequence's offsets are measured from.
internal enum PronomAnchor
{
    BeginningOfFile,
    EndOfFile,
}

// A single byte-matching rule within a PronomBytePattern: PRONOM's extended-hex
// wildcard forms, alongside a plain fixed byte. Each token always consumes a fixed
// number of bytes (Length), even when its value isn't fixed - a signature never has to
// guess how many bytes a wildcard "ate".
internal abstract record PronomByteToken(int Length)
{
    public abstract bool Matches(ReadOnlySpan<byte> data, int offset);
}

internal sealed record PronomLiteralToken(byte Value) : PronomByteToken(1)
{
    public override bool Matches(ReadOnlySpan<byte> data, int offset) => data[offset] == Value;
}

// [XX:YY] - an inclusive, big-endian numeric range over Low.Length bytes.
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

// [!hex] - matches anything except this exact byte value.
internal sealed record PronomNegatedValueToken(byte[] Value) : PronomByteToken(Value.Length)
{
    public override bool Matches(ReadOnlySpan<byte> data, int offset) => !data.Slice(offset, Length).SequenceEqual(Value);
}

// [&XX] / [!&XX] - a single byte tested against a bitmask.
internal sealed record PronomBitmaskToken(byte Mask, bool Negated) : PronomByteToken(1)
{
    public override bool Matches(ReadOnlySpan<byte> data, int offset)
    {
        var anyBitSet = (data[offset] & Mask) != 0;
        return Negated ? !anyBitSet : anyBitSet;
    }
}

// (HH|HH|...) - matches any one of a short list of fixed byte values, all the same
// length.
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

// A fixed-length byte pattern - a run of PronomByteTokens, most often all
// PronomLiteralToken but occasionally mixing in a wildcard token.
internal sealed record PronomBytePattern(IReadOnlyList<PronomByteToken> Tokens)
{
    public int Length { get; } = Tokens.Sum(t => t.Length);

    // The flat literal bytes this pattern reduces to, if every token is a plain
    // PronomLiteralToken - lets the matcher use ReadOnlySpan<T>'s vectorized
    // IndexOf/LastIndexOf instead of a manual per-position token scan, for the common
    // case where a pattern has no wildcards at all.
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

// One step of a PronomSubSequence's left- or right-fragment chain: a gap window
// (MinOffset/MaxOffset) from the previous step, followed by any one of Alternatives -
// multiple fragments at the same PRONOM "Position" are alternatives (OR), not a further
// chain (AND).
internal sealed record PronomFragmentGroup(int MinOffset, int MaxOffset, IReadOnlyList<PronomBytePattern> Alternatives);

// One unit of a PronomByteSequence: an Anchor pattern searched for directly, optionally
// flanked by chains of alternative fragments, nearest first, backward
// (LeftFragmentGroups) and forward (RightFragmentGroups) - PRONOM's way of saying "one
// of several words" either side of a short, cheap-to-search byte run. Position orders
// units within the sequence; MinOffset/MaxOffset bound the gap from the previous unit
// (or the file boundary, for the first), and a null MaxOffset is unbounded.
internal sealed record PronomSubSequence(
    int Position,
    int MinOffset,
    int? MaxOffset,
    PronomBytePattern Anchor,
    IReadOnlyList<PronomFragmentGroup> LeftFragmentGroups,
    IReadOnlyList<PronomFragmentGroup> RightFragmentGroups);

// An ordered chain of PronomSubSequence anchors that must all be found, in order and
// within their gap bounds, measured from Anchor.
internal sealed record PronomByteSequence(PronomAnchor Anchor, IReadOnlyList<PronomSubSequence> SubSequences);

// A named byte signature from PRONOM's DROID signature file. A format matches if every
// one of its ByteSequences matches (BOF and EOF sequences on the same signature are an
// AND, not an OR).
internal sealed record PronomInternalSignature(int Id, IReadOnlyList<PronomByteSequence> ByteSequences);
