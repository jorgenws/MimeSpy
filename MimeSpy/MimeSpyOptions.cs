namespace MimeSpy;

/// <summary>
/// Restricts a <see cref="MimeSpy"/> instance to part of PRONOM's catalog. The other formats
/// are skipped entirely - never returned, and never able to outrank one that is - which
/// also makes each call faster.
/// </summary>
/// <example>
/// Every image format except DNG, so a DNG file reports as the TIFF it's built on:
/// <code>new MimeSpyOptions { MimeTypes = ["image/"], ExcludedMimeTypes = ["image/dng"] }</code>
/// </example>
public sealed class MimeSpyOptions
{
    /// <summary>
    /// Strings a format's MIME type must start with to be considered - a plain,
    /// case-insensitive starts-with, so any prefix works: <c>"image/"</c> covers every image
    /// type, <c>"image/j"</c> every one starting that way, such as <c>image/jpeg</c> and <c>image/jxl</c>.
    /// A full type also covers longer types that start with it: <c>"font/woff"</c> includes
    /// <c>font/woff2</c>. A format is considered if any of its <see cref="Dossier.MimeTypes"/>
    /// starts with any of them. Formats with no MIME type are excluded whenever this list is
    /// non-empty. Empty (the default) considers every format.
    /// </summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];

    /// <summary>
    /// Strings to leave out, matched the same way as <see cref="MimeTypes"/>. A
    /// format is left out if any of its <see cref="Dossier.MimeTypes"/> starts with any of
    /// them, even if it also matches <see cref="MimeTypes"/>. DNG, for example, lists both
    /// <c>image/dng</c> and <c>image/tiff</c>, so <c>"image/dng"</c> excludes it while
    /// <c>"image/tiff"</c> excludes both DNG and TIFF.
    /// </summary>
    public IReadOnlyList<string> ExcludedMimeTypes { get; init; } = [];
}
