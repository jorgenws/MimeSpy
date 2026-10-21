namespace MimeSpy;

/// <summary>
/// Configures a <see cref="MimeSpy"/> instance to only ever consider a known subset
/// of PRONOM's catalog, so every <c>Sniff</c>/<c>Spy</c>/<c>Investigate</c> call on it
/// skips the rest of the catalog entirely rather than matching against it and
/// discarding the result - useful when a caller already knows it only ever ingests,
/// say, images. The default (no options at all, or an empty <see cref="MimeTypes"/>)
/// considers every cataloged format, identical to a <see cref="MimeSpy"/> built with
/// no options.
/// </summary>
public sealed class MimeSpyOptions
{
    /// <summary>
    /// MIME type patterns a format's own MIME type must start with (case-insensitive)
    /// at least one of, to be considered. There's no separate wildcard syntax - a
    /// whole top-level type is just the pattern it's a prefix of, e.g.
    /// <c>"image/"</c> matches any MIME type starting with that (every image
    /// subtype), the same way <c>"application/pdf"</c> matches that one exactly (no
    /// other cataloged MIME type happens to start with it). Patterns are additive -
    /// <c>["image/", "audio/"]</c> includes a format matching either. A format with
    /// no MIME type at all (<see cref="PronomFormat.MimeType"/> can be null - not
    /// every PRONOM format has one) never matches any pattern here, so is excluded
    /// whenever this list is non-empty.
    /// </summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];
}
