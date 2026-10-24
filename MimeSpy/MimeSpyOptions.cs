namespace MimeSpy;

/// <summary>
/// Restricts a <see cref="MimeSpy"/> instance to part of PRONOM's catalog. The other formats
/// are skipped entirely, which makes each call faster.
/// </summary>
public sealed class MimeSpyOptions
{
    /// <summary>
    /// MIME type prefixes to consider, matched case-insensitively: <c>"image/"</c> covers
    /// every image type, <c>"application/pdf"</c> just PDF. A format is considered if its
    /// MIME type starts with any of them. Formats with no MIME type are excluded whenever
    /// this list is non-empty. Empty (the default) considers every format.
    /// </summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];
}
