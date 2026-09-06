namespace MimeSpy;

/// <summary>
/// Shared by every <see cref="IContainerSniffer"/> that disambiguates within a
/// single tied match's own <see cref="Result.MimeTypes"/> list (e.g. Ogg's
/// audio/video/application split or ASF's WMA/WMV split, docs/adr/0007 and
/// docs/adr/0008) rather than across multiple tied rows. Just attaches the raw
/// sniffed value to every tied result as evidence (docs/adr/0013) - it's
/// <see cref="Result.PrimaryMimeType"/> that decides whether a given result's own
/// <see cref="Result.MimeTypes"/> actually backs it, so a sniffer can never affect
/// a match it has nothing to do with.
/// </summary>
internal static class PrimaryMimeTypeRefiner
{
    public static IReadOnlyList<Result> Apply(IReadOnlyList<Result> results, string? sniffedMimeType)
    {
        if (sniffedMimeType is null)
        {
            return results;
        }

        var refined = new List<Result>(results.Count);
        foreach (var result in results)
        {
            refined.Add(result with { SniffedMimeType = sniffedMimeType });
        }

        return refined;
    }
}
