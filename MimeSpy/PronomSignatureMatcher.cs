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
    // How a signature's end-of-file-anchored ByteSequences are treated when
    // bytes might only be a leading prefix of the real file:
    //   RejectIfPresent (Sniff) - merely *having* an EOF sequence disqualifies the
    //     whole signature, since it can never be verified this way; guarantees every
    //     match here would also be reported by Analyze.
    //   Ignore (Guess) - EOF sequences are skipped rather than rejected, so a
    //     signature matches on its BOF portion alone. For a format whose BOF happens
    //     to be shared with another cataloged format (e.g. TIFF/DNG, GIF/Persuasion
    //     Player - see docs/adr/0017), skipping EOF for both sides means both match,
    //     surfacing the real ambiguity as a tie instead of silently guessing one.
    //   Require (Analyze) - EOF sequences are evaluated against bytes' true end, so
    //     bytes must be the complete file.
    private enum EofPolicy
    {
        RejectIfPresent,
        Ignore,
        Require,
    }

    /// <summary>
    /// Matches signatures whose byte sequences are all anchored to the start of the
    /// file, against a buffer that may only be a leading prefix of the real file.
    /// Signatures with an end-of-file-anchored sequence can never be evaluated safely
    /// this way - <paramref name="bytes"/> might not reach the real end of file - so
    /// they're skipped rather than matched against a false EOF. See
    /// <see cref="FindMatchesGuess"/> for a more permissive alternative.
    /// </summary>
    public static IReadOnlyList<PronomFileFormat> FindMatchesForward(ReadOnlySpan<byte> bytes) => FindMatches(bytes, EofPolicy.RejectIfPresent);

    /// <summary>
    /// Matches signatures the same way as <see cref="FindMatchesForward"/>, except a
    /// signature's end-of-file-anchored sequences are skipped rather than
    /// disqualifying it - so this can report a format PRONOM itself only considers
    /// confirmed once its trailer is checked (docs/adr/0017). Unlike
    /// <see cref="FindMatchesForward"/>, a match here is not guaranteed to also be
    /// reported by <see cref="FindMatchesFull"/> against the same file's complete bytes.
    /// </summary>
    public static IReadOnlyList<PronomFileFormat> FindMatchesGuess(ReadOnlySpan<byte> bytes) => FindMatches(bytes, EofPolicy.Ignore);

    /// <summary>
    /// Matches signatures against <paramref name="bytes"/> as the complete file
    /// content, so end-of-file-anchored sequences can be evaluated against its true end.
    /// </summary>
    public static IReadOnlyList<PronomFileFormat> FindMatchesFull(ReadOnlySpan<byte> bytes) => FindMatches(bytes, EofPolicy.Require);

    /// <summary>
    /// Matches a single signature against <paramref name="bytes"/> as complete
    /// content (a container signature's extracted entry, e.g.) - used by
    /// <see cref="PronomContainerMatcher"/> rather than the DROID table walk above.
    /// </summary>
    public static bool MatchesSignature(PronomInternalSignature signature, ReadOnlySpan<byte> bytes) => Matches(signature, bytes, EofPolicy.Require);

    private static IReadOnlyList<PronomFileFormat> FindMatches(ReadOnlySpan<byte> bytes, EofPolicy eofPolicy)
    {
        List<PronomFileFormat>? matches = null;

        foreach (var format in PronomSignatureIndex.Formats)
        {
            foreach (var signatureId in format.InternalSignatureIds)
            {
                if (!Matches(PronomSignatureIndex.GetSignature(signatureId), bytes, eofPolicy))
                {
                    continue;
                }

                matches ??= [];
                matches.Add(format);
                break;
            }
        }

        return matches ?? (IReadOnlyList<PronomFileFormat>)[];
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
    private static bool Matches(PronomInternalSignature signature, ReadOnlySpan<byte> bytes, EofPolicy eofPolicy)
    {
        // Under Ignore, a signature made up entirely of EOF-anchored sequences (a
        // few formats - e.g. TGA Bitmap, Apple Disk Image - are identified only by
        // their trailer, no BOF at all) would otherwise have nothing left to check
        // once every one of them is skipped, and match vacuously. There has to be at
        // least one BOF sequence actually evaluated for a "guess" to mean anything.
        if (eofPolicy == EofPolicy.Ignore && !signature.ByteSequences.Any(bs => bs.Anchor == PronomAnchor.BeginningOfFile))
        {
            return false;
        }

        var budget = InitialProbeBudget;
        foreach (var byteSequence in signature.ByteSequences)
        {
            if (byteSequence.Anchor == PronomAnchor.EndOfFile)
            {
                switch (eofPolicy)
                {
                    case EofPolicy.RejectIfPresent:
                        return false;
                    case EofPolicy.Ignore:
                        continue;
                    case EofPolicy.Require:
                        if (!MatchesBackward(byteSequence.SubSequences, bytes, ref budget))
                        {
                            return false;
                        }

                        break;
                }
            }
            else if (!MatchesForward(byteSequence.SubSequences, bytes, ref budget))
            {
                return false;
            }
        }

        return true;
    }

    // subSequences is ordered by Position ascending (1 = closest to the start of
    // file). Each unit's forward reach (its right-fragment chain's end, or its
    // anchor's own end with no right fragments) becomes the reference point for the
    // next unit's gap window.
    private static bool MatchesForward(IReadOnlyList<PronomSubSequence> subSequences, ReadOnlySpan<byte> data, ref int budget)
    {
        var cursor = 0;
        foreach (var sub in subSequences)
        {
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
        foreach (var sub in subSequences)
        {
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
        foreach (var alternative in group.Alternatives)
        {
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
        foreach (var alternative in group.Alternatives)
        {
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
