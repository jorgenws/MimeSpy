namespace MimeSpy;

/// <summary>
/// Matches bytes against PRONOM's DROID signature table. A signature's byte
/// sequences chain together left-to-right (BOF-anchored) or right-to-left
/// (EOF-anchored). Each <see cref="PronomSubSequence"/>'s outer gap window bounds
/// not the anchor's own position, but the reach of the whole unit on the side facing
/// the previous chain step - the leftmost point of its left-fragment chain for a
/// forward (BOF) walk, the rightmost point of its right-fragment chain for a
/// backward (EOF) walk - falling back to the anchor's own start/end when that side
/// has no fragments. If a fragment chain fails to hold for a given anchor
/// occurrence, the search backtracks to the next occurrence in range rather than
/// giving up outright - a short, common anchor (e.g. a single "@" or "\") can occur
/// incidentally before the one that's actually surrounded by valid fragments.
/// </summary>
internal static class PronomSignatureMatcher
{
    // Every loop over an IReadOnlyList<T> here indexes it rather than using foreach:
    // foreach through the interface boxes a new enumerator per loop, and the fragment
    // search below runs its loops once per candidate position - on a large buffer
    // that was tens of MB of garbage per call, and a measurable share of the time.
    // How a signature's end-of-file-anchored ByteSequences are treated when
    // bytes might only be a leading prefix of the real file:
    //   Ignore (Spy) - EOF sequences are skipped rather than evaluated, so a
    //     signature matches on its BOF portion alone. For a format whose BOF happens
    //     to be shared with another cataloged format (e.g. TIFF/DNG, GIF/Persuasion
    //     Player - see docs/adr/0017), skipping EOF for both sides means both match,
    //     surfacing the real ambiguity as a tie instead of silently guessing one.
    //   Require (Investigate) - EOF sequences are evaluated against the true end of
    //     the file, so the EOF-side buffer must end where the file does.
    private enum EofPolicy
    {
        Ignore,
        Require,
    }

    /// <summary>
    /// Matches signatures against a buffer that may only be a leading prefix of the
    /// real file, skipping a signature's end-of-file-anchored sequences rather than
    /// evaluating them - so this can report a format PRONOM itself only considers
    /// confirmed once its trailer is checked (docs/adr/0017). A match here is not
    /// guaranteed to also be reported by <see cref="FindMatchesFull"/> against the
    /// same file's complete bytes.
    /// </summary>
    public static IReadOnlyList<PronomFileFormat> FindMatchesGuess(ReadOnlySpan<byte> bytes, PronomFormatFilter? filter = null) => FindMatches(bytes, bytes, EofPolicy.Ignore, filter);

    /// <summary>
    /// Matches signatures against a complete file, so end-of-file-anchored sequences
    /// can be evaluated against its true end. BOF-anchored sequences are evaluated
    /// against <paramref name="head"/> (which starts at the file's first byte),
    /// EOF-anchored ones against <paramref name="tail"/> (which ends at the file's
    /// last byte) - pass the same span as both when the whole file is in hand. A file
    /// too large to hold whole can pass a window onto each end instead: every offset
    /// either side works with is relative to its own end of the file, so neither needs
    /// to know how far apart the two windows really are - but a sequence whose gap
    /// reaches past the end of its window (only possible for PRONOM's unbounded gaps)
    /// is searched only as far as the window goes.
    /// </summary>
    public static IReadOnlyList<PronomFileFormat> FindMatchesFull(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, PronomFormatFilter? filter = null) => FindMatches(head, tail, EofPolicy.Require, filter);

    /// <summary>
    /// Matches a single signature against <paramref name="bytes"/> as complete
    /// content (a container signature's extracted entry, e.g.) - used by
    /// <see cref="PronomContainerMatcher"/> rather than the DROID table walk above.
    /// </summary>
    public static bool MatchesSignature(PronomInternalSignature signature, ReadOnlySpan<byte> bytes) => Matches(signature, bytes, bytes, EofPolicy.Require);

    // Rather than walking every cataloged format's signature(s) against every file,
    // only signatures that could possibly match head[0] are tried: those indexed
    // under that exact byte, plus the (much smaller) set PronomSignatureIndex
    // couldn't pin to one required first byte at all (see its own
    // TryGetRequiredFirstByte doc) - those are always tried, so this never excludes
    // a signature that could legitimately match, only ones whose own BOF anchor
    // proves it can't. filter, when given, swaps both candidate sets for a narrower
    // pair built from only the signatures its included formats reference (see
    // PronomFormatFilter) - null (the default, unfiltered MimeSpy()) takes the exact
    // same path as before filtering existed. Everything past this candidate
    // selection - Matches() itself, and resolving a matched signature back to its
    // claiming format(s) - is unaware filtering exists at all.
    //
    // head and tail are the same span whenever the whole file is in hand; see
    // FindMatchesFull for when they aren't.
    private static IReadOnlyList<PronomFileFormat> FindMatches(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, EofPolicy eofPolicy, PronomFormatFilter? filter)
    {
        List<PronomFileFormat>? matches = null;
        HashSet<int>? matchedFormatIds = null;

        if (head.Length > 0)
        {
            var candidates = filter is null
                ? PronomSignatureIndex.FindSignatureIdsByFirstByte(head[0])
                : filter.FindSignatureIdsByFirstByte(head[0]);
            TryMatchCandidates(candidates, head, tail, eofPolicy, ref matches, ref matchedFormatIds);
        }

        var fallback = filter?.SignatureIdsWithoutFixedFirstByte ?? PronomSignatureIndex.SignatureIdsWithoutFixedFirstByte;
        TryMatchCandidates(fallback, head, tail, eofPolicy, ref matches, ref matchedFormatIds);

        return matches ?? (IReadOnlyList<PronomFileFormat>)[];
    }

    // matchedFormatIds dedupes across the two candidate sets above, and across a
    // single format's own alternative signatures (PronomFileFormat.InternalSignatureIds
    // is an OR - a format with two alternative signatures that both match must still
    // only appear once).
    private static void TryMatchCandidates(IReadOnlyList<int> signatureIds, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, EofPolicy eofPolicy, ref List<PronomFileFormat>? matches, ref HashSet<int>? matchedFormatIds)
    {
        for (var i = 0; i < signatureIds.Count; i++)
        {
            var signatureId = signatureIds[i];
            if (!Matches(PronomSignatureIndex.GetSignature(signatureId), head, tail, eofPolicy))
            {
                continue;
            }

            var formats = PronomSignatureIndex.FindFormatsBySignatureId(signatureId);
            for (var j = 0; j < formats.Count; j++)
            {
                var format = formats[j];
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
    // has to match for the signature as a whole to match (subject to eofPolicy).
    // BOF sequences only ever look at head, EOF sequences only ever at tail.
    private static bool Matches(PronomInternalSignature signature, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, EofPolicy eofPolicy)
    {
        var budget = InitialProbeBudget;
        var matchedBof = false;
        for (var i = 0; i < signature.ByteSequences.Count; i++)
        {
            var byteSequence = signature.ByteSequences[i];
            if (byteSequence.Anchor == PronomAnchor.EndOfFile)
            {
                if (eofPolicy == EofPolicy.Ignore)
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

        // Under Ignore, a signature made up entirely of EOF-anchored sequences (a
        // few formats - e.g. TGA Bitmap, Apple Disk Image - are identified only by
        // their trailer, no BOF at all) has had every one of them skipped, and would
        // match vacuously. There has to be at least one BOF sequence actually
        // evaluated for a "guess" to mean anything.
        return eofPolicy == EofPolicy.Require || matchedBof;
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
