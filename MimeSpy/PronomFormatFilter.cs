namespace MimeSpy;

// The signatures referenced by formats matching MimeSpyOptions.MimeTypes, bucketed by
// required first byte the same way PronomSignatureIndex is. Built once per MimeSpy
// instance. It only narrows which signatures are tried: a matching signature shared
// with an excluded format still reports both.
internal sealed class PronomFormatFilter
{
    public IReadOnlyList<int> SignatureIdsWithoutFixedFirstByte { get; }

    private readonly Dictionary<byte, List<int>> _signatureIdsByFirstByte;

    private PronomFormatFilter(Dictionary<byte, List<int>> signatureIdsByFirstByte, List<int> signatureIdsWithoutFixedFirstByte)
    {
        _signatureIdsByFirstByte = signatureIdsByFirstByte;
        SignatureIdsWithoutFixedFirstByte = signatureIdsWithoutFixedFirstByte;
    }

    public IReadOnlyList<int> FindSignatureIdsByFirstByte(byte firstByte) =>
        _signatureIdsByFirstByte.TryGetValue(firstByte, out var bucket) ? bucket : [];

    public static PronomFormatFilter Build(MimeSpyOptions options)
    {
        var includedSignatureIds = new HashSet<int>();
        foreach (var format in PronomSignatureIndex.AllFormats)
        {
            if (format.MimeTypes.Any(mimeType => MatchesAnyPattern(mimeType, options.MimeTypes)))
            {
                includedSignatureIds.UnionWith(format.InternalSignatureIds);
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

        return new PronomFormatFilter(signatureIdsByFirstByte, signatureIdsWithoutFixedFirstByte);
    }

    // A case-insensitive starting-with match, nothing more - see
    // MimeSpyOptions.MimeTypes's own doc for why there's no separate wildcard syntax.
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
