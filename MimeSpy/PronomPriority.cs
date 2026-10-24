namespace MimeSpy;

// DROID's own tie-break: when two matched formats are related by PRONOM's
// HasPriorityOverFileFormatID (e.g. PDF/A over the plain PDF it also matches), the
// outranked one is dropped. Same single pass as DROID's removeLowerPriorityHits: collect
// every ID any hit outranks, then drop those hits. PRONOM's priority graph is acyclic,
// so a non-empty list never comes back empty. See docs/adr/0023.
internal static class PronomPriority
{
    // Only a full match may outrank: a partial one matched without the part of its
    // signature that tells it apart from what it would outrank (DNG vs TIFF under Spy).
    public static IReadOnlyList<PronomMatch> RemoveOutranked(IReadOnlyList<PronomMatch> hits)
    {
        if (hits.Count < 2)
        {
            return hits;
        }

        HashSet<int>? outranked = null;
        for (var i = 0; i < hits.Count; i++)
        {
            if (hits[i].Match != Match.Full)
            {
                continue;
            }

            var hasPriorityOverIds = hits[i].Format.HasPriorityOverIds;
            for (var j = 0; j < hasPriorityOverIds.Count; j++)
            {
                outranked ??= [];
                outranked.Add(hasPriorityOverIds[j]);
            }
        }

        if (outranked is null)
        {
            return hits;
        }

        List<PronomMatch> kept = [];
        for (var i = 0; i < hits.Count; i++)
        {
            if (!outranked.Contains(hits[i].Format.Id))
            {
                kept.Add(hits[i]);
            }
        }

        return kept.Count == hits.Count ? hits : kept;
    }

    // Full matches before partial ones; within each group, a format before any format in
    // the same group it outranks (DNG before the TIFF it refines), otherwise in the order
    // found. A stable topological sort - lists are a handful long, so the quadratic scan
    // is cheaper than building a graph.
    public static IReadOnlyList<PronomMatch> Order(IReadOnlyList<PronomMatch> hits)
    {
        if (hits.Count < 2)
        {
            return hits;
        }

        List<PronomMatch> ordered = new(hits.Count);
        AppendInPriorityOrder(hits, Match.Full, ordered);
        AppendInPriorityOrder(hits, Match.Partial, ordered);
        return ordered;
    }

    private static void AppendInPriorityOrder(IReadOnlyList<PronomMatch> hits, Match match, List<PronomMatch> ordered)
    {
        List<PronomMatch> remaining = [];
        for (var i = 0; i < hits.Count; i++)
        {
            if (hits[i].Match == match)
            {
                remaining.Add(hits[i]);
            }
        }

        while (remaining.Count > 0)
        {
            // The priority graph is acyclic, so some remaining format is always
            // unoutranked; falling back to the first just guards against bad data.
            var next = 0;
            for (var i = 0; i < remaining.Count; i++)
            {
                if (!IsOutrankedByAny(remaining[i], remaining))
                {
                    next = i;
                    break;
                }
            }

            ordered.Add(remaining[next]);
            remaining.RemoveAt(next);
        }
    }

    private static bool IsOutrankedByAny(PronomMatch hit, List<PronomMatch> others)
    {
        for (var i = 0; i < others.Count; i++)
        {
            var hasPriorityOverIds = others[i].Format.HasPriorityOverIds;
            for (var j = 0; j < hasPriorityOverIds.Count; j++)
            {
                if (hasPriorityOverIds[j] == hit.Format.Id)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
