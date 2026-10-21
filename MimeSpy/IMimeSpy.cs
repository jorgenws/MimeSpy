namespace MimeSpy;

/// <summary>
/// Detects file format(s) from a file's bytes, using PRONOM's own signature data.
/// Implemented by <see cref="MimeSpy"/>, which has no constructor arguments and no
/// mutable state - register it as a singleton against this interface for callers that
/// want a mockable seam (e.g. flow code that calls these methods inline rather than
/// taking a pre-computed <see cref="Dossier"/> list as input).
/// </summary>
public interface IMimeSpy
{
    /// <summary>
    /// Identifies the PRONOM format(s) whose header matches the leading bytes of a
    /// file, including formats PRONOM only fully confirms by checking a trailer near
    /// the end of the file (PNG, JPEG, GIF, and a plain PDF/ZIP among them - see
    /// docs/adr/0017), since that check can't be done from a leading-bytes buffer at
    /// all. This is the practical default for a quick "what kind of file is this"
    /// guess from a partial read; a match here is not guaranteed to also be reported
    /// by <see cref="Investigate(ReadOnlySpan{byte})"/> against the same file's
    /// complete bytes - a truncated or corrupted file could pass the header check
    /// here and still fail there. Where a format's header happens to be identical to
    /// another cataloged format's (TIFF vs. its Digital Negative variant, GIF vs. an
    /// unrelated format that happens to share its magic bytes, ...), both are
    /// reported as a tie here rather than one being silently preferred - see
    /// docs/adr/0001 for why ties are returned rather than resolved.
    /// </summary>
    /// <param name="bytes">
    /// The start of the file. Supplying fewer bytes never produces a wrong answer,
    /// only a less specific one - PRONOM signatures probe anywhere from the first
    /// byte to (rarely) tens of thousands of bytes in, so there's no small fixed
    /// count that reaches all of them. Unlike <see cref="Spy(Stream)"/>, this overload
    /// always treats <paramref name="bytes"/> as a possibly-partial buffer (skipping
    /// EOF/container confirmation) - a caller who knows they hold the complete file
    /// already knows that, and should call <see cref="Investigate(ReadOnlySpan{byte})"/>
    /// directly rather than have this method try to infer it.
    /// </param>
    IReadOnlyList<Dossier> Spy(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Identifies the PRONOM format(s) whose header matches the leading bytes of
    /// <paramref name="stream"/> - or, when <paramref name="stream"/> reports (via
    /// <see cref="Stream.Length"/>) that it's small enough to be cheap to read in
    /// full, this reads the whole thing and returns <see cref="Investigate(Stream)"/>'s
    /// fully-confirmed answer instead. That's gated on the stream's own reported
    /// length - a fact known before reading a single byte - rather than trying
    /// <see cref="Investigate(Stream)"/> and detecting whether it "worked": a truncated
    /// file doesn't reliably fail in a way that's safe to detect after the fact (see
    /// docs/adr/0019). A non-seekable stream, or one larger than that threshold,
    /// falls back to a bounded peek exactly like <see cref="Spy(ReadOnlySpan{byte})"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read from - the whole thing if cheap to, otherwise only a
    /// bounded prefix (see this method's own summary). If the stream is seekable,
    /// its position is restored afterwards, so this is a peek rather than a
    /// consume; on a non-seekable stream the read bytes are gone from it.
    /// </param>
    IReadOnlyList<Dossier> Spy(Stream stream);

    /// <summary>
    /// Asynchronously identifies the PRONOM format(s) whose header matches the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Read the same way, and for the same reason, as <see cref="Spy(Stream)"/> - see that overload's own doc.</param>
    /// <param name="cancellationToken">Cancels the pending read from <paramref name="stream"/>.</param>
    Task<IReadOnlyList<Dossier>> SpyAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies the PRONOM format(s) matching a complete file, including - unlike
    /// <see cref="Spy(ReadOnlySpan{byte})"/> - signatures anchored to the end of
    /// the file and PRONOM's container-signature disambiguation of ZIP/OLE2
    /// sub-formats (docx/xlsx/doc/xls/jar/odt and the like resolve to their specific
    /// PUID here, not just the generic ZIP/OLE2 one). This trusts that
    /// <paramref name="bytes"/> is genuinely the complete file - it does not, and
    /// cannot, verify that. A truncated buffer is treated as if its end were the
    /// file's real end: an EOF-anchored signature check against it usually just
    /// fails to find what it's looking for (a safe, if less specific, answer), but
    /// isn't guaranteed to - see docs/adr/0019. Pass a merely-partial buffer to
    /// <see cref="Spy(ReadOnlySpan{byte})"/> instead.
    /// </summary>
    /// <param name="bytes">
    /// The complete file content, not just its leading bytes. This method needs no
    /// more memory than <paramref name="bytes"/> itself already occupies - the same
    /// array is read from directly throughout, including by the ZIP/OLE2 readers
    /// behind container-signature disambiguation, never copied again internally (see
    /// docs/adr/0018). Only <see cref="Investigate(ReadOnlySpan{byte})"/> itself has to
    /// copy once, up front, into an array it can keep: a <see cref="ReadOnlySpan{T}"/>
    /// can't be captured past the call that provides it, so there's nothing to reuse.
    /// A caller choosing between this and <see cref="Spy(ReadOnlySpan{byte})"/> based
    /// on a file's size - a reasonable policy this library deliberately leaves to the
    /// caller, since the right threshold depends on constraints only the caller knows
    /// (available memory, concurrent load, latency budget) - can treat "peak memory
    /// here" as approximately equal to the file's own size.
    /// </param>
    IReadOnlyList<Dossier> Investigate(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Identifies the PRONOM format(s) matching the complete content of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read to its end (see <see cref="Investigate(ReadOnlySpan{byte})"/> for
    /// why the whole thing, unlike <see cref="Spy(Stream)"/> - and for this
    /// overload's own memory cost, which is the same: reading a stream instead of an
    /// already-in-memory span doesn't add a second copy on top of it). If the stream
    /// is seekable, its position is restored afterwards, so this is a peek rather
    /// than a consume; on a non-seekable stream the read bytes are gone from it.
    /// </param>
    IReadOnlyList<Dossier> Investigate(Stream stream);

    /// <summary>
    /// Asynchronously identifies the PRONOM format(s) matching the complete content of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Read the same way, and for the same reason, as <see cref="Investigate(Stream)"/> - see that overload's own doc.</param>
    /// <param name="cancellationToken">Cancels the pending read from <paramref name="stream"/>.</param>
    Task<IReadOnlyList<Dossier>> InvestigateAsync(Stream stream, CancellationToken cancellationToken = default);
}
