namespace MimeSpy;

// The formats MimeSpyOptions allows, and the signatures they reference plus the
// container triggers' (docs/adr/0027), bucketed by required first byte the same way
// PronomSignatureIndex is. Built once per MimeSpy instance.
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
