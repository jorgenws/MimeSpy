namespace MimeSpy;

// The cheapest check that can rule a signature out without trying it. Each signature
// gets exactly one, chosen once at load time by PronomCandidateIndex.Place.
internal enum PronomGateKind
{
    // Its first BOF sub-sequence is a literal fixed at offset 0, so it's ruled out by a
    // buffer whose first byte is anything else.
    FirstByte,

    // A literal gate (PronomLiteralGate) whose window ends within ScanLimit: ruled out
    // when the trie scan doesn't find it there.
    NearLiteral,

    // A literal gate whose window reaches past ScanLimit: ruled out when the trie scan
    // covers the whole head and doesn't find it, and otherwise always tried.
    FarLiteral,

    // Nothing usable to check: always tried.
    Always,
}

// Decides which signatures PronomSignatureMatcher has to try on a buffer. Place gives
// every signature one gate kind; FindCandidates runs each kind's check and returns the
// signatures none of them ruled out. A gate only ever rules out a signature that
// couldn't have matched, so results are exactly what trying every signature would give
// (docs/adr/0030).
internal sealed class PronomCandidateIndex
{
    // The trie scan covers at most this much of the head. Further in, scanning a byte
    // at a time costs more than the far-reaching signatures' own vectorized IndexOf
    // searches, so their gates aren't checked past it.
    public const int ScanLimit = 4 * 1024;

    // A Scope allowing fewer literal-gated signatures than this tries them all rather
    // than scanning for them: the scan would cost more than it saves.
    private const int MinScannedSignatures = 100;

    private readonly int[] _signatureIds;
    private readonly Dictionary<int, int> _indexBySignatureId;
    private readonly PronomGateKind[] _kinds;

    // FirstByte: signature indexes by their required first byte.
    private readonly int[][] _byFirstByte;

    // NearLiteral and FarLiteral: one automaton over every literal-gated signature's
    // gate literals, and per pattern, whose it is and the window its start must fall in.
    private readonly PronomAhoCorasick _automaton;
    private readonly int[] _patternSignatureIndex;
    private readonly int[] _patternLength;
    private readonly int[] _patternMinStart;
    private readonly int[] _patternMaxStart;
    private readonly int[] _gateReach;
    private readonly ulong[] _literalGated;
    private readonly ulong[] _farLiteral;

    private readonly ulong[] _always;

    // Every signature, scanning as far as any near gate needs.
    public Scope AllSignatures { get; }

    // ulongs in a bitset with one bit per signature.
    public int MaskLength => _always.Length;

    public int SignatureCount => _signatureIds.Length;

    public PronomCandidateIndex(IReadOnlyList<PronomInternalSignature> signatures)
    {
        _signatureIds = new int[signatures.Count];
        _indexBySignatureId = new Dictionary<int, int>(signatures.Count);
        _kinds = new PronomGateKind[signatures.Count];
        _gateReach = new int[signatures.Count];
        _always = new ulong[(signatures.Count + 63) / 64];
        _literalGated = new ulong[_always.Length];
        _farLiteral = new ulong[_always.Length];

        var byFirstByte = new List<int>[256];
        var literals = new List<PronomGateLiteral>();
        var patternSignatureIndex = new List<int>();
        for (var index = 0; index < signatures.Count; index++)
        {
            _signatureIds[index] = signatures[index].Id;
            _indexBySignatureId[signatures[index].Id] = index;

            var placement = Place(signatures[index]);
            _kinds[index] = placement.Kind;
            switch (placement.Kind)
            {
                case PronomGateKind.FirstByte:
                    (byFirstByte[placement.FirstByte] ??= []).Add(index);
                    break;
                case PronomGateKind.NearLiteral or PronomGateKind.FarLiteral:
                    foreach (var literal in placement.Literals!)
                    {
                        literals.Add(literal);
                        patternSignatureIndex.Add(index);
                        _gateReach[index] = Math.Max(_gateReach[index], literal.Reach);
                    }

                    Set(_literalGated, index);
                    if (placement.Kind == PronomGateKind.FarLiteral)
                    {
                        Set(_farLiteral, index);
                    }

                    break;
                case PronomGateKind.Always:
                    Set(_always, index);
                    break;
            }
        }

        _byFirstByte = [.. byFirstByte.Select(bucket => bucket?.ToArray() ?? [])];
        _automaton = new PronomAhoCorasick([.. literals.Select(l => l.Bytes)]);
        _patternSignatureIndex = [.. patternSignatureIndex];
        _patternLength = [.. literals.Select(l => l.Bytes.Length)];
        _patternMinStart = [.. literals.Select(l => l.MinStart)];
        _patternMaxStart = [.. literals.Select(l => l.MaxStart)];
        AllSignatures = new Scope(null, GetScanEnd(null), ScansLiterals: true);
    }

    // Which signatures FindCandidates considers (Mask, null for all of them), how far
    // it scans for their near gates, and whether it scans at all.
    public sealed record Scope(ulong[]? Mask, int ScanEnd, bool ScansLiterals);

    public int GetSignatureId(int index) => _signatureIds[index];

    public PronomGateKind GetGateKind(int signatureId) => _kinds[_indexBySignatureId[signatureId]];

    // A Scope over just signatureIds, for a PronomFormatFilter. It scans only as far as
    // their near gates need, or not at all when there are too few for it to pay.
    public Scope CreateScope(IEnumerable<int> signatureIds)
    {
        var mask = new ulong[MaskLength];
        foreach (var signatureId in signatureIds)
        {
            if (_indexBySignatureId.TryGetValue(signatureId, out var index))
            {
                Set(mask, index);
            }
        }

        var literalGated = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            literalGated += PopCount(mask[i] & _literalGated[i]);
        }

        return literalGated < MinScannedSignatures
            ? new Scope(mask, 0, ScansLiterals: false)
            : new Scope(mask, GetScanEnd(mask), ScansLiterals: true);
    }

    // Sets the bit of every signature in scope that head might match.
    public void FindCandidates(ReadOnlySpan<byte> head, Scope scope, Span<ulong> candidates)
    {
        _always.CopyTo(candidates);

        if (head.Length > 0)
        {
            foreach (var index in _byFirstByte[head[0]])
            {
                Set(candidates, index);
            }
        }

        if (!scope.ScansLiterals)
        {
            Or(candidates, _literalGated);
        }
        else
        {
            var scanned = head[..Math.Min(head.Length, scope.ScanEnd)];
            ScanForLiterals(scanned, candidates);
            if (scanned.Length < head.Length)
            {
                Or(candidates, _farLiteral);
            }
        }

        if (scope.Mask is { } mask)
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                candidates[i] &= mask[i];
            }
        }
    }

    // The index of the next set bit at or after from, or -1.
    public static int NextSetBit(ReadOnlySpan<ulong> bits, int from)
    {
        var word = from >> 6;
        if (word >= bits.Length)
        {
            return -1;
        }

        var remaining = bits[word] & (ulong.MaxValue << (from & 63));
        while (true)
        {
            if (remaining != 0)
            {
                return (word << 6) + TrailingZeroCount(remaining);
            }

            if (++word == bits.Length)
            {
                return -1;
            }

            remaining = bits[word];
        }
    }

    // FirstByte wins when it applies: a lookup on the first byte costs nothing. Otherwise
    // the signature's literal gate, if it has one, and how far its window reaches.
    private static Placement Place(PronomInternalSignature signature)
    {
        if (TryGetRequiredFirstByte(signature) is byte firstByte)
        {
            return new Placement(PronomGateKind.FirstByte, firstByte, null);
        }

        var literals = PronomLiteralGate.Choose(signature);
        var kind = literals switch
        {
            null => PronomGateKind.Always,
            _ when literals.All(l => l.Reach <= ScanLimit) => PronomGateKind.NearLiteral,
            _ => PronomGateKind.FarLiteral,
        };
        return new Placement(kind, 0, literals);
    }

    // A signature needs a specific byte at offset 0 only when its first BOF sequence
    // starts with a sub-sequence fixed at exactly offset 0, with no left fragments, whose
    // anchor starts with a plain literal byte.
    private static byte? TryGetRequiredFirstByte(PronomInternalSignature signature)
    {
        foreach (var byteSequence in signature.ByteSequences)
        {
            if (byteSequence.Anchor != PronomAnchor.BeginningOfFile || byteSequence.SubSequences.Count == 0)
            {
                continue;
            }

            var first = byteSequence.SubSequences[0];
            if (first.MinOffset == 0 && first.MaxOffset == 0 && first.LeftFragmentGroups.Count == 0 &&
                first.Anchor.Tokens is [PronomLiteralToken literal, ..])
            {
                return literal.Value;
            }
        }

        return null;
    }

    // Sets the bit of every literal-gated signature whose gate literal occurs in data,
    // starting within its window.
    private void ScanForLiterals(ReadOnlySpan<byte> data, Span<ulong> candidates)
    {
        var state = 0;
        for (var i = 0; i < data.Length; i++)
        {
            state = _automaton.Step(state, data[i]);
            if (!_automaton.HasMatches(state))
            {
                continue;
            }

            var matches = _automaton.MatchesAt(state);
            for (var j = 0; j < matches.Length; j++)
            {
                var pattern = matches[j];
                var start = i + 1 - _patternLength[pattern];
                if (start >= _patternMinStart[pattern] && start <= _patternMaxStart[pattern])
                {
                    Set(candidates, _patternSignatureIndex[pattern]);
                }
            }
        }
    }

    // How far the scan has to go to decide the near gates of the signatures in mask
    // (all of them when null).
    private int GetScanEnd(ulong[]? mask)
    {
        var end = 0;
        for (var index = 0; index < _gateReach.Length; index++)
        {
            if (_kinds[index] == PronomGateKind.NearLiteral && (mask is null || IsSet(mask, index)))
            {
                end = Math.Max(end, _gateReach[index]);
            }
        }

        return end;
    }

    private static void Set(Span<ulong> bits, int index) => bits[index >> 6] |= 1UL << (index & 63);

    private static bool IsSet(ulong[] bits, int index) => (bits[index >> 6] & (1UL << (index & 63))) != 0;

    private static void Or(Span<ulong> bits, ulong[] other)
    {
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] |= other[i];
        }
    }

    // BitOperations' TrailingZeroCount and PopCount, which netstandard2.0 lacks.
    private static int TrailingZeroCount(ulong value)
    {
#if NET
        return System.Numerics.BitOperations.TrailingZeroCount(value);
#else
        var count = 0;
        while ((value & 1) == 0)
        {
            value >>= 1;
            count++;
        }

        return count;
#endif
    }

    private static int PopCount(ulong value)
    {
#if NET
        return System.Numerics.BitOperations.PopCount(value);
#else
        var count = 0;
        for (; value != 0; value &= value - 1)
        {
            count++;
        }

        return count;
#endif
    }

    private readonly record struct Placement(PronomGateKind Kind, byte FirstByte, PronomGateLiteral[]? Literals);
}
