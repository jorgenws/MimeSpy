namespace MimeSpy;

// Refines a set of core DROID matches using PRONOM's container-signature table: when
// one of those matches is a generic ZIP/OLE2 PUID
// (PronomContainerSignatureIndex.FindTriggerContainerType says which ones are), opens
// the container and checks its named entries against every container signature of that
// type, resolving to whichever more specific PUIDs match instead.
internal static class PronomContainerMatcher
{
    // The container type ("ZIP" or "OLE2") of the first of coreMatches that's a generic
    // container PUID, or null if none is - in which case there's nothing to refine, and
    // coreMatches is already the answer.
    public static string? FindTriggerContainerType(IReadOnlyList<PronomMatch> coreMatches)
    {
        foreach (var match in coreMatches)
        {
            if (PronomContainerSignatureIndex.FindTriggerContainerType(match.Format.Puid) is { } containerType)
            {
                return containerType;
            }
        }

        return null;
    }

    // file is the complete file, seekable, at position 0 - the readers need random access
    // to it (a ZIP's central directory is at the end, OLE2 entries can be anywhere).
    // coreMatches is FindFullMatches' result for the same file, and containerType is
    // FindTriggerContainerType's (non-null) answer for them.
    public static IReadOnlyList<PronomMatch> Refine(Stream file, string containerType, IReadOnlyList<PronomMatch> coreMatches, PronomFormatFilter? filter)
    {
        var puids = containerType == "ZIP" ? MatchZip(file) : MatchOle2(file);
        if (puids.Count == 0)
        {
            return OnlyAllowed(coreMatches, filter);
        }

        List<PronomMatch> formats = [];
        foreach (var puid in puids)
        {
            if (PronomSignatureIndex.FindFormatByPuid(puid) is { } format && (filter is null || filter.Allows(format)))
            {
                formats.Add(new PronomMatch(format, Match.Full));
            }
        }

        // A container signature can resolve to a PUID that isn't itself in the main
        // DROID FileFormatCollection (shouldn't happen in practice, PRONOM's PUID
        // space is unified - but if it ever did, falling back to the generic
        // container match beats claiming a format this library can't describe).
        return formats.Count > 0 ? PronomPriority.Order(PronomPriority.RemoveOutranked(formats)) : OnlyAllowed(coreMatches, filter);
    }

    private static IReadOnlyList<PronomMatch> OnlyAllowed(IReadOnlyList<PronomMatch> matches, PronomFormatFilter? filter) =>
        filter is null ? matches : [.. matches.Where(m => filter.Allows(m.Format))];

    private static IReadOnlyList<string> MatchZip(Stream file)
    {
        using var reader = PronomZipContainerReader.TryOpen(file);
        return reader is null ? [] : MatchSignatures("ZIP", reader.TryReadEntry);
    }

    private static IReadOnlyList<string> MatchOle2(Stream file)
    {
        var reader = PronomOle2ContainerReader.TryOpen(file);
        return reader is null ? [] : MatchSignatures("OLE2", reader.TryReadStream);
    }

    private static IReadOnlyList<string> MatchSignatures(string containerType, Func<string, byte[]?> readEntry)
    {
        List<string>? puids = null;
        foreach (var signature in PronomContainerSignatureIndex.FindSignaturesByContainerType(containerType))
        {
            if (!MatchesContainerSignature(signature, readEntry) || PronomContainerSignatureIndex.FindPuid(signature.Id) is not { } puid)
            {
                continue;
            }

            puids ??= [];
            puids.Add(puid);
        }

        return puids ?? (IReadOnlyList<string>)[];
    }

    // Every one of a container signature's Files must be present (and, where it
    // carries its own binary signature, match) - an AND, like a DROID signature's
    // own ByteSequences.
    private static bool MatchesContainerSignature(PronomContainerSignature signature, Func<string, byte[]?> readEntry)
    {
        foreach (var file in signature.Files)
        {
            var content = readEntry(file.Path);
            if (content is null)
            {
                return false;
            }

            if (file.Signature is not null && !PronomSignatureMatcher.MatchesSignature(file.Signature, content))
            {
                return false;
            }
        }

        return true;
    }
}
