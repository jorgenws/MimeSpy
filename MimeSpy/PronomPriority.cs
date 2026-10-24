namespace MimeSpy;

// DROID's own tie-break: when two matched formats are related by PRONOM's
// HasPriorityOverFileFormatID (e.g. PDF/A over the plain PDF it also matches), the
// outranked one is dropped. Same single pass as DROID's removeLowerPriorityHits: collect
// every ID any hit outranks, then drop those hits. PRONOM's priority graph is acyclic,
// so a non-empty list never comes back empty. See docs/adr/0023.
internal static class PronomPriority
{
    // canOutrank, when given, limits which hits may drop others - FindHeaderMatches only
    // lets a hit outrank if one of its matching signatures was checked in full, not one
    // that only matched because its EOF sequences were skipped.
    public static IReadOnlyList<PronomFileFormat> RemoveOutranked(IReadOnlyList<PronomFileFormat> hits, HashSet<int>? canOutrank = null)
    {
        if (hits.Count < 2)
        {
            return hits;
        }

        HashSet<int>? outranked = null;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (canOutrank is not null && !canOutrank.Contains(hit.Id))
            {
                continue;
            }

            for (var j = 0; j < hit.HasPriorityOverIds.Count; j++)
            {
                outranked ??= [];
                outranked.Add(hit.HasPriorityOverIds[j]);
            }
        }

        if (outranked is null)
        {
            return hits;
        }

        List<PronomFileFormat> kept = [];
        for (var i = 0; i < hits.Count; i++)
        {
            if (!outranked.Contains(hits[i].Id))
            {
                kept.Add(hits[i]);
            }
        }

        return kept.Count == hits.Count ? hits : kept;
    }
}
