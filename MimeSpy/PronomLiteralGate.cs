namespace MimeSpy;

// One byte string a literal gate looks for, and the window its start has to fall in.
internal sealed record PronomGateLiteral(byte[] Bytes, int MinStart, int MaxStart)
{
    // An empty window (only possible from offsets that contradict each other) is left
    // unusable rather than trusted to rule the signature out.
    public bool IsUsable => MaxStart >= MinStart && Bytes.Length >= PronomLiteralGate.MinLiteralLength;

    // How far into the head this literal can end, or PronomLiteralGate.Unbounded.
    public int Reach { get; } = PronomLiteralGate.Add(MaxStart, Bytes.Length);

    // How selective it is - see PronomLiteralGate. A run of one repeated byte (00 00 00
    // 00, FF FF FF FF) counts as a single byte: real files are full of such runs, and
    // inside one it matches at every position.
    public double Bits { get; } = (8 * (Bytes.All(b => b == Bytes[0]) ? 1 : Bytes.Length)) - Math.Log(Math.Min((double)MaxStart - MinStart + 1, PronomCandidateIndex.ScanLimit), 2);
}

// Picks a signature's literal gate: byte strings it can't match without, any one of
// which opens it (several only for a fragment group's alternatives), each with the
// window its start must fall in. Used by PronomCandidateIndex for signatures without
// a fixed first byte.
internal static class PronomLiteralGate
{
    public const int Unbounded = int.MaxValue;

    // A shorter literal occurs all over ordinary content, and every occurrence is a hit
    // to check.
    public const int MinLiteralLength = 3;

    // Longer literals are cut to their most varied 8 bytes: that's selective enough, and
    // keeps the trie small.
    private const int MaxLiteralLength = 8;

    // How selective a gate is, in bits: 8 per literal byte, minus log2 of how many
    // positions its start can take (an unbounded window counts as ScanLimit's), minus
    // log2 of its number of alternatives. A gate within ScanLimit with NearGateBits or
    // more beats a more selective one further in, which a longer head always passes.
    private const int MinGateBits = 4;
    private const int NearGateBits = 8;

    // A bound plus an offset, staying Unbounded (and treating anything absurdly far as
    // Unbounded, rather than overflowing).
    public static int Add(int bound, long offset) =>
        bound == Unbounded || bound + offset > int.MaxValue / 2 ? Unbounded : (int)(bound + offset);

    // Every BOF sequence has to match, as does every sub-sequence's anchor and one
    // alternative of every fragment group, so any of them can be the gate - this picks
    // the most selective, preferring one within ScanLimit when it's selective enough.
    // EOF sequences are never used: Spy skips them (docs/adr/0017).
    public static PronomGateLiteral[]? Choose(PronomInternalSignature signature)
    {
        PronomGateLiteral[]? best = null;
        (bool Near, double Bits) bestRank = default;
        foreach (var gate in CandidateGates(signature))
        {
            var bits = gate.Min(l => l.Bits) - Math.Log(gate.Length, 2);
            if (bits < MinGateBits)
            {
                continue;
            }

            (bool Near, double Bits) rank = (bits >= NearGateBits && gate.All(l => l.Reach <= PronomCandidateIndex.ScanLimit), bits);
            if (best is null || rank.CompareTo(bestRank) > 0)
            {
                best = gate;
                bestRank = rank;
            }
        }

        return best;
    }

    // The windows mirror PronomSignatureMatcher.MatchesForward: a unit's anchor starts at
    // least MinOffset past the previous unit's reach, which is at least 0. Only the first
    // unit is measured from the start of the file, so only it has an upper bound - its
    // checked window bounds the left-fragment chain's reach, which the anchor can sit up
    // to MaxLeftReach past. Fragment windows then follow from the anchor's.
    private static IEnumerable<PronomGateLiteral[]> CandidateGates(PronomInternalSignature signature)
    {
        foreach (var sequence in signature.ByteSequences)
        {
            if (sequence.Anchor != PronomAnchor.BeginningOfFile)
            {
                continue;
            }

            for (var i = 0; i < sequence.SubSequences.Count; i++)
            {
                var sub = sequence.SubSequences[i];
                var anchorMin = sub.MinOffset;
                var anchorMax = i == 0 && sub.MaxOffset is int max ? Add(max, sub.MaxLeftReach) : Unbounded;
                if (LiteralGate(sub.Anchor, anchorMin, anchorMax) is { } anchorGate)
                {
                    yield return [anchorGate];
                }

                // Right fragments run forward from the anchor's end, each group from the
                // previous one's.
                var cursorMin = anchorMin + sub.Anchor.Length;
                var cursorMax = Add(anchorMax, sub.Anchor.Length);
                foreach (var group in sub.RightFragmentGroups)
                {
                    var startMin = cursorMin + group.MinOffset;
                    var startMax = Add(cursorMax, group.MaxOffset);
                    if (FragmentGroupGate(group, _ => startMin, _ => startMax) is { } gate)
                    {
                        yield return gate;
                    }

                    cursorMin = startMin + group.Alternatives.Min(a => a.Length);
                    cursorMax = Add(startMax, group.Alternatives.Max(a => a.Length));
                }

                // Left fragments run backward from the anchor's start, each group from
                // the previous one's start.
                cursorMin = anchorMin;
                cursorMax = anchorMax;
                foreach (var group in sub.LeftFragmentGroups)
                {
                    var (fromMin, fromMax) = (cursorMin, cursorMax);
                    int StartMin(PronomBytePattern alternative) => Math.Max(0, fromMin - group.MaxOffset - alternative.Length);
                    int StartMax(PronomBytePattern alternative) => Add(fromMax, -group.MinOffset - alternative.Length);
                    if (FragmentGroupGate(group, StartMin, StartMax) is { } gate)
                    {
                        yield return gate;
                    }

                    cursorMin = group.Alternatives.Min(StartMin);
                    cursorMax = group.Alternatives.Max(StartMax);
                }
            }
        }
    }

    // Any one alternative opens the gate, so each needs a usable literal of its own.
    private static PronomGateLiteral[]? FragmentGroupGate(PronomFragmentGroup group, Func<PronomBytePattern, int> startMin, Func<PronomBytePattern, int> startMax)
    {
        var gate = new PronomGateLiteral[group.Alternatives.Count];
        for (var i = 0; i < gate.Length; i++)
        {
            var alternative = group.Alternatives[i];
            if (LiteralGate(alternative, startMin(alternative), startMax(alternative)) is not { } literal)
            {
                return null;
            }

            gate[i] = literal;
        }

        return gate;
    }

    // The most selective usable literal run in pattern, given that the pattern starts
    // within [startMin, startMax].
    private static PronomGateLiteral? LiteralGate(PronomBytePattern pattern, int startMin, int startMax)
    {
        PronomGateLiteral? best = null;
        foreach (var (bytes, offset) in LiteralRuns(pattern))
        {
            var literal = new PronomGateLiteral(bytes, startMin + offset, Add(startMax, offset));
            if (literal.IsUsable && (best is null || literal.Bits > best.Bits))
            {
                best = literal;
            }
        }

        return best;
    }

    // Each maximal run of plain literal bytes in pattern, cut to MaxLiteralLength, with
    // its byte offset from the pattern's start.
    private static IEnumerable<(byte[] Bytes, int Offset)> LiteralRuns(PronomBytePattern pattern)
    {
        var run = new List<byte>();
        var offset = 0;
        var runStart = 0;
        foreach (var token in pattern.Tokens)
        {
            if (token is PronomLiteralToken literal)
            {
                run.Add(literal.Value);
            }
            else
            {
                if (run.Count > 0)
                {
                    yield return Truncate(run, runStart);
                    run.Clear();
                }

                runStart = offset + token.Length;
            }

            offset += token.Length;
        }

        if (run.Count > 0)
        {
            yield return Truncate(run, runStart);
        }
    }

    // The window of run with the most distinct byte values (the last, on a tie - long
    // literals tend to share their opening, like "<?xml version=" or "http://www.").
    private static (byte[] Bytes, int Offset) Truncate(List<byte> run, int runStart)
    {
        if (run.Count <= MaxLiteralLength)
        {
            return ([.. run], runStart);
        }

        // Slides the window a byte at a time, keeping a count of each value in it.
        var counts = new int[256];
        var distinct = 0;
        var bestOffset = 0;
        var bestDistinct = 0;
        for (var end = 0; end < run.Count; end++)
        {
            if (counts[run[end]]++ == 0)
            {
                distinct++;
            }

            var offset = end - MaxLiteralLength + 1;
            if (offset < 0)
            {
                continue;
            }

            if (distinct >= bestDistinct)
            {
                bestOffset = offset;
                bestDistinct = distinct;
            }

            if (--counts[run[offset]] == 0)
            {
                distinct--;
            }
        }

        return ([.. run.GetRange(bestOffset, MaxLiteralLength)], runStart + bestOffset);
    }
}
