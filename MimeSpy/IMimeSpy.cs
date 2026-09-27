namespace MimeSpy;

/// <summary>
/// Detects MIME type(s) from the leading bytes of a file. Implemented by <see cref="MimeSpy"/>,
/// which has no constructor arguments and no mutable state - register it as a singleton against
/// this interface for callers that want a mockable seam (e.g. flow code that calls <c>Spy</c>
/// inline rather than taking a pre-computed <see cref="Result"/> list as input).
/// </summary>
public interface IMimeSpy
{
    /// <summary>
    /// Identifies the file format(s) matching the leading bytes of a file.
    /// </summary>
    /// <param name="bytes">
    /// The start of the file. 532 bytes reaches every signature in the embedded
    /// table (the deepest is a subheader at offset 512); a shorter buffer only
    /// risks missing a match, never an incorrect one. Getting the most specific
    /// answer for every format this library can disambiguate needs more: up to
    /// <see cref="AsfContainerSniffer.SearchWindowSize"/> bytes for formats with a
    /// fixed-size disambiguation window, and, for a few container formats (ZIP-based
    /// and OLE2/CFBF), no fixed count at all - see docs/adr/0002 and docs/adr/0011.
    /// Supplying fewer bytes never produces a wrong answer, only a less specific one.
    /// A caller reading bytes itself (rather than going through
    /// <see cref="Spy(Stream)"/>/<see cref="SpyAsync"/>, which already read exactly
    /// this much) and wanting parity with those overloads without tracking either
    /// constant should read <see cref="AsfContainerSniffer.SearchWindowSize"/>
    /// (8192) bytes as a general-purpose default.
    /// </param>
    IReadOnlyList<Result> Spy(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Identifies the file format(s) matching the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read from. Enough leading bytes are read to give
    /// <see cref="Spy(ReadOnlySpan{byte})"/> every fixed-size disambiguation window
    /// this library uses - see its own <c>bytes</c> doc for what that does and
    /// doesn't guarantee - so a caller reading through this overload gets the most
    /// specific answer available without needing to know a byte count itself.
    /// If the stream is seekable, its position is restored afterwards, so this is a
    /// peek rather than a consume; on a non-seekable stream the read bytes are gone
    /// from it.
    /// </param>
    IReadOnlyList<Result> Spy(Stream stream);

    /// <summary>
    /// Asynchronously identifies the file format(s) matching the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Read the same way, and for the same reason, as <see cref="Spy(Stream)"/> - see that overload's own doc.</param>
    /// <param name="cancellationToken">Cancels the pending read from <paramref name="stream"/>.</param>
    Task<IReadOnlyList<Result>> SpyAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies the PRONOM format(s) matching the leading bytes of a file, using
    /// only signatures anchored to the start of the file - see docs/adr/0016 for why
    /// this can never apply PRONOM's container-signature disambiguation (ZIP/OLE2
    /// sub-formats like docx/xlsx/doc/xls), which needs the complete file; use
    /// <see cref="Analyze(ReadOnlySpan{byte})"/> for that. Every match here is also
    /// reported by <see cref="Analyze(ReadOnlySpan{byte})"/> against the same file's
    /// complete bytes - if that guarantee matters more to you than catching every
    /// format PRONOM can only confirm by its trailer, see
    /// <see cref="Guess(ReadOnlySpan{byte})"/> instead (docs/adr/0017).
    /// </summary>
    /// <param name="bytes">
    /// The start of the file. Supplying fewer bytes never produces a wrong answer,
    /// only a less specific one - PRONOM signatures probe anywhere from the first
    /// byte to (rarely) tens of thousands of bytes in, so unlike <see cref="Spy(ReadOnlySpan{byte})"/>
    /// there's no small fixed count that reaches all of them.
    /// </param>
    IReadOnlyList<PronomFormat> Sniff(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Identifies the PRONOM format(s) matching the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read from - only a bounded prefix, per <see cref="Sniff(ReadOnlySpan{byte})"/>.
    /// If the stream is seekable, its position is restored afterwards, so this is a
    /// peek rather than a consume; on a non-seekable stream the read bytes are gone
    /// from it.
    /// </param>
    IReadOnlyList<PronomFormat> Sniff(Stream stream);

    /// <summary>
    /// Asynchronously identifies the PRONOM format(s) matching the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Read the same way, and for the same reason, as <see cref="Sniff(Stream)"/> - see that overload's own doc.</param>
    /// <param name="cancellationToken">Cancels the pending read from <paramref name="stream"/>.</param>
    Task<IReadOnlyList<PronomFormat>> SniffAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies the PRONOM format(s) whose header matches the leading bytes of a
    /// file - like <see cref="Sniff(ReadOnlySpan{byte})"/>, but also reports formats
    /// PRONOM only fully confirms by checking a trailer near the end of the file
    /// (PNG, JPEG, GIF, and a plain PDF/ZIP among them - see docs/adr/0017), since
    /// that check can't be done from a leading-bytes buffer at all. This is the
    /// practical default for a quick "what kind of file is this" guess from a
    /// partial read; unlike <see cref="Sniff(ReadOnlySpan{byte})"/>, a match here is
    /// not guaranteed to also be reported by <see cref="Analyze(ReadOnlySpan{byte})"/>
    /// against the same file's complete bytes - a truncated or corrupted file could
    /// pass the header check here and still fail there. Where a format's header
    /// happens to be identical to another cataloged format's (TIFF vs. its Digital
    /// Negative variant, GIF vs. an unrelated format that happens to share its magic
    /// bytes, ...), both are reported as a tie here rather than one being silently
    /// preferred - see docs/adr/0001 for why ties are returned rather than resolved.
    /// </summary>
    /// <param name="bytes">
    /// The start of the file - see <see cref="Sniff(ReadOnlySpan{byte})"/> for how
    /// much reaches how much. Unlike <see cref="Guess(Stream)"/>, this overload
    /// always treats <paramref name="bytes"/> as a possibly-partial buffer (skipping
    /// EOF/container confirmation) - a caller who knows they hold the complete file
    /// already knows that, and should call <see cref="Analyze(ReadOnlySpan{byte})"/>
    /// directly rather than have this method try to infer it.
    /// </param>
    IReadOnlyList<PronomFormat> Guess(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Identifies the PRONOM format(s) whose header matches the leading bytes of
    /// <paramref name="stream"/> - or, when <paramref name="stream"/> reports (via
    /// <see cref="Stream.Length"/>) that it's small enough to be cheap to read in
    /// full, this reads the whole thing and returns <see cref="Analyze(Stream)"/>'s
    /// fully-confirmed answer instead. That's gated on the stream's own reported
    /// length - a fact known before reading a single byte - rather than trying
    /// <see cref="Analyze(Stream)"/> and detecting whether it "worked": a truncated
    /// file doesn't reliably fail in a way that's safe to detect after the fact (see
    /// docs/adr/0019). A non-seekable stream, or one larger than that threshold,
    /// falls back to a bounded peek exactly like <see cref="Guess(ReadOnlySpan{byte})"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read from - the whole thing if cheap to, otherwise only a
    /// bounded prefix (see this method's own summary). If the stream is seekable,
    /// its position is restored afterwards, so this is a peek rather than a
    /// consume; on a non-seekable stream the read bytes are gone from it.
    /// </param>
    IReadOnlyList<PronomFormat> Guess(Stream stream);

    /// <summary>
    /// Asynchronously identifies the PRONOM format(s) whose header matches the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Read the same way, and for the same reason, as <see cref="Guess(Stream)"/> - see that overload's own doc.</param>
    /// <param name="cancellationToken">Cancels the pending read from <paramref name="stream"/>.</param>
    Task<IReadOnlyList<PronomFormat>> GuessAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies the PRONOM format(s) matching a complete file, including - unlike
    /// <see cref="Sniff(ReadOnlySpan{byte})"/> - signatures anchored to the end of
    /// the file and PRONOM's container-signature disambiguation of ZIP/OLE2
    /// sub-formats (docx/xlsx/doc/xls/jar/odt and the like resolve to their specific
    /// PUID here, not just the generic ZIP/OLE2 one). This trusts that
    /// <paramref name="bytes"/> is genuinely the complete file - it does not, and
    /// cannot, verify that. A truncated buffer is treated as if its end were the
    /// file's real end: an EOF-anchored signature check against it usually just
    /// fails to find what it's looking for (a safe, if less specific, answer), but
    /// isn't guaranteed to - see docs/adr/0019. Pass a merely-partial buffer to
    /// <see cref="Sniff(ReadOnlySpan{byte})"/> or <see cref="Guess(ReadOnlySpan{byte})"/> instead.
    /// </summary>
    /// <param name="bytes">
    /// The complete file content, not just its leading bytes. This method needs no
    /// more memory than <paramref name="bytes"/> itself already occupies - the same
    /// array is read from directly throughout, including by the ZIP/OLE2 readers
    /// behind container-signature disambiguation, never copied again internally (see
    /// docs/adr/0018). Only <see cref="Analyze(ReadOnlySpan{byte})"/> itself has to
    /// copy once, up front, into an array it can keep: a <see cref="ReadOnlySpan{T}"/>
    /// can't be captured past the call that provides it, so there's nothing to reuse.
    /// A caller choosing between this and <see cref="Sniff(ReadOnlySpan{byte})"/>/
    /// <see cref="Guess(ReadOnlySpan{byte})"/> based on a file's size - a reasonable
    /// policy this library deliberately leaves to the caller, since the right
    /// threshold depends on constraints only the caller knows (available memory,
    /// concurrent load, latency budget) - can treat "peak memory here" as
    /// approximately equal to the file's own size.
    /// </param>
    IReadOnlyList<PronomFormat> Analyze(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Identifies the PRONOM format(s) matching the complete content of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read to its end (see <see cref="Analyze(ReadOnlySpan{byte})"/> for
    /// why the whole thing, unlike <see cref="Sniff(Stream)"/> - and for this
    /// overload's own memory cost, which is the same: reading a stream instead of an
    /// already-in-memory span doesn't add a second copy on top of it). If the stream
    /// is seekable, its position is restored afterwards, so this is a peek rather
    /// than a consume; on a non-seekable stream the read bytes are gone from it.
    /// </param>
    IReadOnlyList<PronomFormat> Analyze(Stream stream);

    /// <summary>
    /// Asynchronously identifies the PRONOM format(s) matching the complete content of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Read the same way, and for the same reason, as <see cref="Analyze(Stream)"/> - see that overload's own doc.</param>
    /// <param name="cancellationToken">Cancels the pending read from <paramref name="stream"/>.</param>
    Task<IReadOnlyList<PronomFormat>> AnalyzeAsync(Stream stream, CancellationToken cancellationToken = default);
}
