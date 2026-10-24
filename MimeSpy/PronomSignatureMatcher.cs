namespace MimeSpy;

// Matches bytes against PRONOM's DROID signature table. A signature's byte sequences
// chain together left-to-right (BOF-anchored) or right-to-left (EOF-anchored). Each
// PronomSubSequence's outer gap window bounds not the anchor's own position, but the
// reach of the whole unit on the side facing the previous chain step - the leftmost
// point of its left-fragment chain for a forward (BOF) walk, the rightmost point of its
// right-fragment chain for a backward (EOF) walk - falling back to the anchor's own
// start/end when that side has no fragments. If a fragment chain fails to hold for a
// given anchor occurrence, the search backtracks to the next occurrence in range rather
// than giving up outright - a short, common anchor (e.g. a single "@" or "\") can occur
// incidentally before the one that's actually surrounded by valid fragments.
internal static class PronomSignatureMatcher
{
    // Every loop over an IReadOnlyList<T> here indexes it rather than using foreach:
    // foreach through the interface boxes a new enumerator per loop, and the fragment
    // search below runs its loops once per candidate position - on a large buffer
    // that was tens of MB of garbage per call, and a measurable share of the time.

    // For Spy: bytes may be only a leading prefix, so EOF-anchored sequences are skipped
    // (docs/adr/0017). A match here may not survive FindFullMatches on the whole file.
    // Two formats sharing a BOF (e.g. TIFF/DNG) both match - a tie, not a guess.
    public static IReadOnlyList<PronomFileFormat> FindHeaderMatches(ReadOnlySpan<byte> bytes, PronomFormatFilter? filter = null) => FindMatches(bytes, bytes, checkEof: false, filter);

    // For Investigate: BOF sequences are matched against head (starting at the file's
    // first byte), EOF sequences against tail (ending at its last byte). Pass the same
    // span twice for a whole file in memory, or a window onto each end of a large one;
    // each side's offsets are relative to its own end, so only an unbounded gap that
    // runs past its window is affected.
    public static IReadOnlyList<PronomFileFormat> FindFullMatches(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, PronomFormatFilter? filter = null) => FindMatches(head, tail, checkEof: true, filter);

    // One signature against complete content - a container entry, for PronomContainerMatcher.
    public static bool MatchesSignature(PronomInternalSignature signature, ReadOnlySpan<byte> bytes) => Matches(signature, bytes, bytes, checkEof: true);

    // Only signatures that could match head[0] are tried: those indexed under that byte,
    // plus the small set with no fixed first byte, which are always tried. A filter swaps
    // in its own narrower pair of sets; nothing after candidate selection knows about it.
    private static IReadOnlyList<PronomFileFormat> FindMatches(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, bool checkEof, PronomFormatFilter? filter)
    {
        List<PronomFileFormat>? matches = null;
        HashSet<int>? matchedFormatIds = null;
        HashSet<int>? fullyCheckedFormatIds = null;

        if (head.Length > 0)
        {
            var candidates = filter is null
                ? PronomSignatureIndex.FindSignatureIdsByFirstByte(head[0])
                : filter.FindSignatureIdsByFirstByte(head[0]);
            TryMatchCandidates(candidates, head, tail, checkEof, ref matches, ref matchedFormatIds, ref fullyCheckedFormatIds);
        }

        var fallback = filter?.SignatureIdsWithoutFixedFirstByte ?? PronomSignatureIndex.SignatureIdsWithoutFixedFirstByte;
        TryMatchCandidates(fallback, head, tail, checkEof, ref matches, ref matchedFormatIds, ref fullyCheckedFormatIds);

        if (matches is null)
        {
            return [];
        }

        // With checkEof every match was checked in full, so any of them may outrank
        // another. Without it, only those in fullyCheckedFormatIds may - see PronomPriority.
        return checkEof ? PronomPriority.RemoveOutranked(matches)
            : fullyCheckedFormatIds is null ? matches
            : PronomPriority.RemoveOutranked(matches, fullyCheckedFormatIds);
    }

    // matchedFormatIds dedupes across the two candidate sets above, and across a
    // single format's own alternative signatures (PronomFileFormat.InternalSignatureIds
    // is an OR - a format with two alternative signatures that both match must still
    // only appear once). fullyCheckedFormatIds is only tracked without checkEof: the
    // formats with at least one matching signature that had no EOF sequence to skip.
    private static void TryMatchCandidates(IReadOnlyList<int> signatureIds, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, bool checkEof, ref List<PronomFileFormat>? matches, ref HashSet<int>? matchedFormatIds, ref HashSet<int>? fullyCheckedFormatIds)
    {
        for (var i = 0; i < signatureIds.Count; i++)
        {
            var signatureId = signatureIds[i];
            var signature = PronomSignatureIndex.GetSignature(signatureId);
            if (!Matches(signature, head, tail, checkEof))
            {
                continue;
            }

            var formats = PronomSignatureIndex.FindFormatsBySignatureId(signatureId);
            for (var j = 0; j < formats.Count; j++)
            {
                var format = formats[j];
                if (!checkEof && signature.IsBofOnly)
                {
                    fullyCheckedFormatIds ??= [];
                    fullyCheckedFormatIds.Add(format.Id);
                }

                matchedFormatIds ??= [];
                if (!matchedFormatIds.Add(format.Id))
                {
                    continue;
                }

                matches ??= [];
                matches.Add(format);
            }
        }
    }

    // A handful of signatures combine wide-open fragment gaps (PRONOM's 999999
    // "effectively unbounded" sentinel) with several chained fragment groups, which
    // backtracking could otherwise multiply into millions of candidate positions on
    // a large file. This budget is a shared probe count across one whole signature
    // match attempt (every anchor retry and fragment candidate checked, at any
    // nesting depth, spends one unit of it) - once exhausted, remaining candidates
    // are treated as non-matching rather than explored, trading a vanishingly rare
    // missed match for a hard bound on how much work one signature check can do.
    private const int InitialProbeBudget = 50_000;

    // A signature's byte sequences are an AND: every one of them - BOF and EOF alike -
    // has to match for the signature as a whole to match (EOF ones only when checkEof).
    // BOF sequences only ever look at head, EOF sequences only ever at tail.
    private static bool Matches(PronomInternalSignature signature, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, bool checkEof)
    {
        var budget = InitialProbeBudget;
        var matchedBof = false;
        for (var i = 0; i < signature.ByteSequences.Count; i++)
        {
            var byteSequence = signature.ByteSequences[i];
            if (byteSequence.Anchor == PronomAnchor.EndOfFile)
            {
                if (!checkEof)
                {
                    continue;
                }

                if (!MatchesBackward(byteSequence.SubSequences, tail, ref budget))
                {
                    return false;
                }
            }
            else if (MatchesForward(byteSequence.SubSequences, head, ref budget))
            {
                matchedBof = true;
            }
            else
            {
                return false;
            }
        }

        // Without checkEof, a signature made up entirely of EOF-anchored sequences (a
        // few formats - e.g. TGA Bitmap, Apple Disk Image - are identified only by
        // their trailer, no BOF at all) has had every one of them skipped, and would
        // match vacuously. There has to be at least one BOF sequence actually
        // evaluated for a "guess" to mean anything.
        return checkEof || matchedBof;
    }

    // subSequences is ordered by Position ascending (1 = closest to the start of
    // file). Each unit's forward reach (its right-fragment chain's end, or its
    // anchor's own end with no right fragments) becomes the reference point for the
    // next unit's gap window.
    private static bool MatchesForward(IReadOnlyList<PronomSubSequence> subSequences, ReadOnlySpan<byte> data, ref int budget)
    {
        var cursor = 0;
        for (var i = 0; i < subSequences.Count; i++)
        {
            var sub = subSequences[i];
            var checkedLo = cursor + sub.MinOffset;
            var checkedHi = sub.MaxOffset is int max ? cursor + max : data.Length;

            // The checked window bounds this unit's leftmost reach, not the anchor's
            // own start - when left fragments exist, the anchor can sit arbitrarily
            // further right than that, so its own search can't be tightly bounded above.
            var searchHi = sub.LeftFragmentGroups.Count == 0 ? checkedHi : data.Length;
            if (searchHi > data.Length - sub.Anchor.Length)
            {
                searchHi = data.Length - sub.Anchor.Length;
            }

            if (searchHi < checkedLo || !TryMatchAnchorForward(sub, data, checkedLo, searchHi, checkedLo, checkedHi, ref budget, out cursor))
            {
                return false;
            }
        }

        return true;
    }

    // subSequences is ordered by Position ascending (1 = closest to the end of
    // file). Each unit's backward reach (its left-fragment chain's start, or its
    // anchor's own start with no left fragments) becomes the reference point for the
    // next (earlier) unit's gap window, mirroring MatchesForward from the other end.
    private static bool MatchesBackward(IReadOnlyList<PronomSubSequence> subSequences, ReadOnlySpan<byte> data, ref int budget)
    {
        var cursor = data.Length;
        for (var i = 0; i < subSequences.Count; i++)
        {
            var sub = subSequences[i];
            var checkedHi = cursor - sub.MinOffset;
            var checkedLo = sub.MaxOffset is int max ? cursor - max : 0;

            var searchHi = checkedHi - sub.Anchor.Length;

            // The checked window bounds this unit's rightmost reach, not the anchor's
            // own end - when right fragments exist, the anchor can sit arbitrarily
            // further left than that, so its own search can't be tightly bounded below.
            var searchLo = sub.RightFragmentGroups.Count == 0 ? checkedLo - sub.Anchor.Length : 0;
            if (searchLo < 0)
            {
                searchLo = 0;
            }

            if (searchHi < searchLo || !TryMatchAnchorBackward(sub, data, searchLo, searchHi, checkedLo, checkedHi, ref budget, out cursor))
            {
                return false;
            }
        }

        return true;
    }

    // searchLo/searchHi bound the anchor's candidate start index. checkedLo/checkedHi
    // bound whichever reach applies (see MatchesForward's comment). Tries anchor
    // occurrences left-to-right, verifying both fragment chains and the checked reach
    // for each, backtracking to the next occurrence on failure.
    private static bool TryMatchAnchorForward(PronomSubSequence sub, ReadOnlySpan<byte> data, int searchLo, int searchHi, int checkedLo, int checkedHi, ref int budget, out int nextCursor)
    {
        var pos = searchLo;
        while (pos <= searchHi)
        {
            if (budget-- <= 0)
            {
                break;
            }

            var idx = FindForward(sub.Anchor, data, pos, searchHi, ref budget);
            if (idx < 0)
            {
                break;
            }

            var anchorStart = idx;
            var leftOk = sub.LeftFragmentGroups.Count == 0
                ? IsWithin(anchorStart, checkedLo, checkedHi)
                : MatchesFragmentGroupsBackward(sub.LeftFragmentGroups, 0, data, anchorStart, ref budget, out var leftReach) && IsWithin(leftReach, checkedLo, checkedHi);

            if (leftOk && TryMatchFragmentGroupsForward(sub.RightFragmentGroups, 0, data, anchorStart + sub.Anchor.Length, ref budget, out nextCursor))
            {
                return true;
            }

            pos = anchorStart + 1;
        }

        nextCursor = 0;
        return false;
    }

    private static bool TryMatchAnchorBackward(PronomSubSequence sub, ReadOnlySpan<byte> data, int searchLo, int searchHi, int checkedLo, int checkedHi, ref int budget, out int nextCursor)
    {
        var pos = searchHi;
        while (pos >= searchLo)
        {
            if (budget-- <= 0)
            {
                break;
            }

            var idx = FindBackward(sub.Anchor, data, searchLo, pos, ref budget);
            if (idx < 0)
            {
                break;
            }

            var anchorStart = idx;
            var anchorEnd = anchorStart + sub.Anchor.Length;

            var rightOk = sub.RightFragmentGroups.Count == 0
                ? IsWithin(anchorEnd, checkedLo, checkedHi)
                : TryMatchFragmentGroupsForward(sub.RightFragmentGroups, 0, data, anchorEnd, ref budget, out var rightReach) && IsWithin(rightReach, checkedLo, checkedHi);

            if (rightOk && MatchesFragmentGroupsBackward(sub.LeftFragmentGroups, 0, data, anchorStart, ref budget, out nextCursor))
            {
                return true;
            }

            pos = anchorStart - 1;
        }

        nextCursor = 0;
        return false;
    }

    private static bool IsWithin(int value, int lo, int hi) => value >= lo && value <= hi;

    // Leftmost occurrence of pattern with a start index in [lo, hi]. Most anchors
    // are plain literal bytes (see PronomBytePattern.LiteralBytes), so this almost
    // always takes ReadOnlySpan<byte>'s vectorized IndexOf; a wildcarded anchor (rare
    // - only the container-signature file's Sequences ever have one) falls back to a
    // manual per-position scan, budgeted the same as fragment candidates.
    private static int FindForward(PronomBytePattern pattern, ReadOnlySpan<byte> data, int lo, int hi, ref int budget)
    {
        if (pattern.LiteralBytes is { } literal)
        {
            var idx = data.Slice(lo, hi - lo + pattern.Length).IndexOf(literal);
            return idx < 0 ? -1 : lo + idx;
        }

        for (var i = lo; i <= hi; i++)
        {
            if (budget-- <= 0)
            {
                return -1;
            }

            if (pattern.MatchesAt(data, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindBackward(PronomBytePattern pattern, ReadOnlySpan<byte> data, int lo, int hi, ref int budget)
    {
        if (pattern.LiteralBytes is { } literal)
        {
            var idx = data.Slice(lo, hi - lo + pattern.Length).LastIndexOf(literal);
            return idx < 0 ? -1 : lo + idx;
        }

        for (var i = hi; i >= lo; i--)
        {
            if (budget-- <= 0)
            {
                return -1;
            }

            if (pattern.MatchesAt(data, i))
            {
                return i;
            }
        }

        return -1;
    }

    // groups is ordered by Position ascending (1 = closest to the anchor, extending
    // toward the start of file). Alternatives within a group, and candidate positions
    // within an alternative's window, are tried with backtracking: which choice
    // satisfies a later group can depend on which one an earlier group picked (e.g.
    // RTF's "f"/"f1" - "f" fits the gap on its own, but only "f1" leaves the byte
    // that the next group needs). Returns the leftmost boundary reached, or
    // anchorStart unchanged if there are no groups.
    private static bool MatchesFragmentGroupsBackward(IReadOnlyList<PronomFragmentGroup> groups, int index, ReadOnlySpan<byte> data, int cursor, ref int budget, out int matchStart)
    {
        if (index == groups.Count)
        {
            matchStart = cursor;
            return true;
        }

        var group = groups[index];
        for (var i = 0; i < group.Alternatives.Count; i++)
        {
            var alternative = group.Alternatives[i];
            var hiEnd = cursor - group.MinOffset;
            var loEnd = cursor - group.MaxOffset;

            var hi = hiEnd - alternative.Length;
            var lo = loEnd - alternative.Length;
            if (lo < 0)
            {
                lo = 0;
            }

            for (var candidate = hi; candidate >= lo; candidate--)
            {
                if (budget-- <= 0)
                {
                    matchStart = 0;
                    return false;
                }

                if (alternative.MatchesAt(data, candidate) && MatchesFragmentGroupsBackward(groups, index + 1, data, candidate, ref budget, out matchStart))
                {
                    return true;
                }
            }
        }

        matchStart = 0;
        return false;
    }

    // groups is ordered by Position ascending (1 = closest to the anchor, extending
    // toward the end of file). Backtracks the same way as MatchesFragmentGroupsBackward,
    // mirrored. Returns the rightmost boundary reached, or anchorEnd unchanged if
    // there are no groups.
    private static bool TryMatchFragmentGroupsForward(IReadOnlyList<PronomFragmentGroup> groups, int index, ReadOnlySpan<byte> data, int cursor, ref int budget, out int matchEnd)
    {
        if (index == groups.Count)
        {
            matchEnd = cursor;
            return true;
        }

        var group = groups[index];
        for (var i = 0; i < group.Alternatives.Count; i++)
        {
            var alternative = group.Alternatives[i];
            var lo = cursor + group.MinOffset;
            var hi = cursor + group.MaxOffset;
            if (hi > data.Length - alternative.Length)
            {
                hi = data.Length - alternative.Length;
            }

            for (var candidate = lo; candidate <= hi; candidate++)
            {
                if (budget-- <= 0)
                {
                    matchEnd = 0;
                    return false;
                }

                if (alternative.MatchesAt(data, candidate) && TryMatchFragmentGroupsForward(groups, index + 1, data, candidate + alternative.Length, ref budget, out matchEnd))
                {
                    return true;
                }
            }
        }

        matchEnd = 0;
        return false;
    }
}
