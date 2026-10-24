namespace MimeSpy;

/// <summary>
/// Detects file format(s) from a file's bytes, using PRONOM's own signature data.
/// Implemented by <see cref="MimeSpy"/>, which has no mutable state - register it as a singleton against this interface for callers that
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
    /// bounded prefix (see this method's own summary). The stream is the whole
    /// file, so a seekable stream must be at position 0. A non-seekable stream is
    /// read from wherever it is. Either way the stream is left open, at wherever
    /// reading stopped - set <see cref="Stream.Position"/> back to 0 before reading
    /// it again.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is seekable but isn't at position 0.</exception>
    IReadOnlyList<Dossier> Spy(Stream stream);

    /// <summary>
    /// Asynchronously identifies the PRONOM format(s) whose header matches the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Read the same way, and for the same reason, as <see cref="Spy(Stream)"/> - see that overload's own doc.</param>
    /// <param name="cancellationToken">Cancels the pending read from <paramref name="stream"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is seekable but isn't at position 0.</exception>
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
    /// The complete file content, not just its leading bytes. This overload copies
    /// <paramref name="bytes"/> once, up front, into an array it can keep (a
    /// <see cref="ReadOnlySpan{T}"/> can't be captured past the call that provides
    /// it), and reads only that copy from then on - so peak memory here is about
    /// twice the file's size. For a file that isn't already in memory, prefer
    /// <see cref="Investigate(Stream)"/>, whose memory use doesn't grow with the file.
    /// </param>
    IReadOnlyList<Dossier> Investigate(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Identifies the PRONOM format(s) matching the complete content of
    /// <paramref name="stream"/> - with the
    /// same EOF and container checks as <see cref="Investigate(ReadOnlySpan{byte})"/>,
    /// but without reading the whole stream into memory. A stream of 16 MiB or less
    /// is read whole. A larger one has only 8 MiB read from each end for signature
    /// matching, plus whatever parts a ZIP/OLE2 container check needs (its directory
    /// and the entries PRONOM names), read in place. Peak memory therefore stays
    /// around 16 MiB, plus the size of any container entries checked, however large
    /// the file is - a 50 GB file costs about the same as a 20 MB one. The one
    /// thing given up on a stream over 16 MiB: a handful of
    /// PRONOM signatures that search for a marker with no fixed distance from either
    /// end of the file only search the first 8 MiB, so a marker further in than that
    /// is missed and a less specific match is reported instead (see docs/adr/0022).
    /// </summary>
    /// <param name="stream">
    /// A seekable stream (<see cref="Stream.CanSeek"/>) at position 0 - the whole
    /// stream is the file. For a non-seekable source (a network or pipe stream),
    /// copy it into a <see cref="MemoryStream"/> or a temporary
    /// <see cref="FileStream"/> first - whichever suits its size - or pass its bytes
    /// to <see cref="Investigate(ReadOnlySpan{byte})"/>. The stream is left open, at
    /// wherever reading stopped - set <see cref="Stream.Position"/> back to 0 before
    /// reading it again.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> isn't seekable, or isn't at position 0.</exception>
    IReadOnlyList<Dossier> Investigate(Stream stream);

    /// <summary>
    /// Asynchronously identifies the PRONOM format(s) matching the complete content of <paramref name="stream"/>.
    /// </summary>
    /// <remarks>
    /// The signature-matching reads are asynchronous. On a stream over 16 MiB that
    /// turns out to be a ZIP/OLE2 container, the container check that follows reads
    /// <paramref name="stream"/> synchronously.
    /// </remarks>
    /// <param name="stream">Read the same way, and with the same seekable, position-0 requirement, as <see cref="Investigate(Stream)"/> - see that overload's own doc.</param>
    /// <param name="cancellationToken">Cancels the pending signature-matching reads from <paramref name="stream"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> isn't seekable, or isn't at position 0.</exception>
    Task<IReadOnlyList<Dossier>> InvestigateAsync(Stream stream, CancellationToken cancellationToken = default);
}
