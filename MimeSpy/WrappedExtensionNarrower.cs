namespace MimeSpy;

/// <summary>
/// Shared by every <see cref="IContainerSniffer"/> that disambiguates across
/// multiple tied signature rows (e.g. ZIP's docx/jar/apk/... or OLE2's doc/xls/ppt,
/// docs/adr/0002 and docs/adr/0011) by narrowing to whichever tied results carry a
/// sniffed wrapped extension. A no-op when there's nothing to narrow (a single
/// candidate, or nothing was sniffed) or when the sniffed extension doesn't actually
/// appear among the tied candidates' own extensions - narrowing to nothing would
/// throw away a real match, so this falls back to the unnarrowed list instead.
/// </summary>
internal static class WrappedExtensionNarrower
{
    public static IReadOnlyList<Result> Apply(IReadOnlyList<Result> results, string? wrappedExtension)
    {
        if (results.Count <= 1 || wrappedExtension is null)
        {
            return results;
        }

        var narrowed = results.Where(r => r.Extensions.Contains(wrappedExtension)).ToList();
        return narrowed.Count > 0 ? narrowed : results;
    }
}
