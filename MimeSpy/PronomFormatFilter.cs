namespace MimeSpy;

// The formats MimeSpyOptions allows, and a PronomCandidateIndex scope over the
// signatures they reference plus the container triggers' (docs/adr/0027). Built once
// per MimeSpy instance.
internal sealed class PronomFormatFilter
{
    public PronomCandidateIndex.Scope Candidates { get; }

    private readonly HashSet<int> _allowedFormatIds;
    private readonly HashSet<int> _containerTriggerFormatIds;

    private PronomFormatFilter(PronomCandidateIndex.Scope candidates, HashSet<int> allowedFormatIds, HashSet<int> containerTriggerFormatIds)
    {
        Candidates = candidates;
        _allowedFormatIds = allowedFormatIds;
        _containerTriggerFormatIds = containerTriggerFormatIds;
    }

    public bool Allows(PronomFileFormat format) => _allowedFormatIds.Contains(format.Id);

    public bool IsContainerTrigger(PronomFileFormat format) => _containerTriggerFormatIds.Contains(format.Id);

    public static PronomFormatFilter Build(MimeSpyOptions options)
    {
        var includedSignatureIds = new HashSet<int>();
        var allowedFormatIds = new HashSet<int>();
        foreach (var format in PronomSignatureIndex.AllFormats)
        {
            var included = options.MimeTypes.Count == 0
                || format.MimeTypes.Any(mimeType => MatchesAnyPattern(mimeType, options.MimeTypes));
            var excluded = format.MimeTypes.Any(mimeType => MatchesAnyPattern(mimeType, options.ExcludedMimeTypes));
            if (included && !excluded)
            {
                allowedFormatIds.Add(format.Id);
                includedSignatureIds.UnionWith(format.InternalSignatureIds);
            }
        }

        var containerTriggerFormatIds = new HashSet<int>();
        foreach (var puid in PronomContainerSignatureIndex.TriggerPuids)
        {
            if (PronomSignatureIndex.FindFormatByPuid(puid) is { } trigger)
            {
                containerTriggerFormatIds.Add(trigger.Id);
                includedSignatureIds.UnionWith(trigger.InternalSignatureIds);
            }
        }

        return new PronomFormatFilter(PronomSignatureIndex.Candidates.CreateScope(includedSignatureIds), allowedFormatIds, containerTriggerFormatIds);
    }

    private static bool MatchesAnyPattern(string mimeType, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (mimeType.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
