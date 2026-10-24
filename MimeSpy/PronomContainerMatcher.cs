namespace MimeSpy;

// Refines a set of core DROID matches using PRONOM's container-signature table: when
// one of those matches is a generic ZIP/OLE2 PUID
// (PronomContainerSignatureIndex.FindTriggerContainerType says which ones are), opens
// the container and checks its named entries against every container signature of that
// type, resolving to whichever more specific PUIDs match instead.
internal static class PronomContainerMatcher
{
    // file is the complete file, seekable, at position 0. The readers need random access
    // to it (a ZIP's central directory is at the end, OLE2 entries can be anywhere) and
    // read only what they need, in place. Its position is left wherever they stopped.
    // coreMatches is FindFullMatches' result for the same file.
    public static IReadOnlyList<PronomFileFormat> Refine(Stream file, IReadOnlyList<PronomFileFormat> coreMatches)
    {
        string? containerType = null;
        foreach (var format in coreMatches)
        {
            containerType = PronomContainerSignatureIndex.FindTriggerContainerType(format.Puid);
            if (containerType is not null)
            {
                break;
            }
        }

        if (containerType is null)
        {
            return coreMatches;
        }

        var puids = containerType == "ZIP" ? MatchZip(file) : MatchOle2(file);
        if (puids.Count == 0)
        {
            return coreMatches;
        }

        List<PronomFileFormat> formats = [];
        foreach (var puid in puids)
        {
            if (PronomSignatureIndex.FindFormatByPuid(puid) is { } format)
            {
                formats.Add(format);
            }
        }

        // A container signature can resolve to a PUID that isn't itself in the main
        // DROID FileFormatCollection (shouldn't happen in practice, PRONOM's PUID
        // space is unified - but if it ever did, falling back to the generic
        // container match beats claiming a format this library can't describe).
        return formats.Count > 0 ? formats : coreMatches;
    }

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
