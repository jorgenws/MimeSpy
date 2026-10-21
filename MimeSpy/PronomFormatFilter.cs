namespace MimeSpy;

/// <summary>
/// A per-<see cref="MimeSpy"/>-instance narrowing of PRONOM's full catalog to just
/// the signatures referenced by formats matching a <see cref="MimeSpyOptions.MimeTypes"/>
/// pattern list, built once when that instance is constructed (see <see cref="Build"/>).
/// Mirrors <see cref="PronomSignatureIndex"/>'s own first-byte/fallback split, but
/// over that narrower set, so a filtered instance's calls skip every signature that
/// could only ever resolve to an excluded format.
///
/// This is the only place the <see cref="MimeSpyOptions.MimeTypes"/> decision is
/// made - everything downstream of it (<see cref="PronomSignatureMatcher"/>'s actual
/// matching, and resolving a matched signature back to its claiming format(s)) stays
/// exactly as it is for an unfiltered <see cref="MimeSpy"/>. One consequence: if a
/// signature is shared between an included and an excluded format (a version/alias
/// tie), both are still returned once that signature matches - this only decides
/// which signatures are worth trying at all, not which formats a matching signature
/// is allowed to resolve to.
/// </summary>
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
            if (format.MimeType is { } mimeType && MatchesAnyPattern(mimeType, options.MimeTypes))
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
