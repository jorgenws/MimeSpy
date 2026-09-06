namespace MimeSpy;

/// <summary>
/// Shared by every <see cref="IContainerSniffer"/> that disambiguates within a
/// single tied match's own <see cref="Result.MimeTypes"/> list (e.g. Ogg's
/// audio/video/application split or ASF's WMA/WMV split, docs/adr/0007 and
/// docs/adr/0008) rather than across multiple tied rows. Only overrides a result
/// that already lists the sniffed mime type as one of its candidates, so a sniffer
/// can never affect a match it has nothing to do with.
/// </summary>
internal static class PrimaryMimeTypeRefiner
{
    public static IReadOnlyList<Result> Apply(IReadOnlyList<Result> results, string? sniffedMimeType)
    {
        if (sniffedMimeType is null)
        {
            return results;
        }

        List<Result>? refined = null;
        for (var i = 0; i < results.Count; i++)
        {
            if (!results[i].MimeTypes.Contains(sniffedMimeType))
            {
                continue;
            }

            refined ??= [.. results];
            refined[i] = refined[i] with { PrimaryMimeType = sniffedMimeType };
        }

        return refined ?? results;
    }
}
