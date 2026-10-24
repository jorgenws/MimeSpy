namespace MimeSpy;

// The formats MimeSpyOptions allows - matching MimeTypes (if any) and none of
// ExcludedMimeTypes - and the signatures they reference, bucketed by required first byte
// the same way PronomSignatureIndex is. Built once per MimeSpy instance. Narrowing the
// signatures skips most of the work; Allows then drops any disallowed format a shared
// signature or a container check still produced, before priority is applied, so an
// excluded format can neither appear nor outrank one that's allowed.
//
// The container-trigger formats (generic ZIP, Office Open XML, OLE2) are the one
// exception: Investigate still matches them when they're disallowed, because opening the
// container is the only way to reach docx/xlsx/doc and the like. They're kept only for
// that check - they can't outrank anything, and Refine removes them before returning.
internal sealed class PronomFormatFilter
{
    public IReadOnlyList<int> SignatureIdsWithoutFixedFirstByte { get; }

    private readonly Dictionary<byte, List<int>> _signatureIdsByFirstByte;
    private readonly HashSet<int> _allowedFormatIds;
    private readonly HashSet<int> _containerTriggerFormatIds;

    private PronomFormatFilter(Dictionary<byte, List<int>> signatureIdsByFirstByte, List<int> signatureIdsWithoutFixedFirstByte, HashSet<int> allowedFormatIds, HashSet<int> containerTriggerFormatIds)
    {
        _signatureIdsByFirstByte = signatureIdsByFirstByte;
        SignatureIdsWithoutFixedFirstByte = signatureIdsWithoutFixedFirstByte;
        _allowedFormatIds = allowedFormatIds;
        _containerTriggerFormatIds = containerTriggerFormatIds;
    }

    public bool Allows(PronomFileFormat format) => _allowedFormatIds.Contains(format.Id);

    public bool IsContainerTrigger(PronomFileFormat format) => _containerTriggerFormatIds.Contains(format.Id);

    public IReadOnlyList<int> FindSignatureIdsByFirstByte(byte firstByte) =>
        _signatureIdsByFirstByte.TryGetValue(firstByte, out var bucket) ? bucket : [];

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

        var signatureIdsByFirstByte = new Dictionary<byte, List<int>>();
        var signatureIdsWithoutFixedFirstByte = new List<int>();
        foreach (var signatureId in includedSignatureIds)
        {
            if (PronomSignatureIndex.GetRequiredFirstByte(signatureId) is byte firstByte)
            {
                if (!signatureIdsByFirstByte.TryGetValue(firstByte, out var bucket))
                {
                    bucket = [];
                    signatureIdsByFirstByte[firstByte] = bucket;
                }

                bucket.Add(signatureId);
            }
            else
            {
                signatureIdsWithoutFixedFirstByte.Add(signatureId);
            }
        }

        return new PronomFormatFilter(signatureIdsByFirstByte, signatureIdsWithoutFixedFirstByte, allowedFormatIds, containerTriggerFormatIds);
    }

    // A case-insensitive starting-with match, nothing more - see MimeSpyOptions.MimeTypes'
    // doc for why there's no separate wildcard syntax.
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
