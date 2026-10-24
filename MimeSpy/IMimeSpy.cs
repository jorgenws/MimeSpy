namespace MimeSpy;

/// <summary>
/// Detects file formats from a file's bytes, using PRONOM's signature data. Every method
/// returns every format that matched; an empty list means none did, and more than one
/// means the bytes can't tell those formats apart.
/// </summary>
/// <remarks>
/// <para>
/// Where PRONOM ranks one matching format over another (WAV over generic RIFF, say), only
/// the higher one is returned - unless the higher one is a <see cref="Match.Partial"/> match
/// whose skipped check could still rule it out, in which case both are returned. Results are ordered: <see cref="Match.Full"/>
/// matches before <see cref="Match.Partial"/> ones, and within each, more specific formats
/// before the ones they refine - so the first result is the best single answer.
/// </para>
/// <para>
/// <see cref="MimeSpy"/> is the implementation and has no mutable state, so it can be
/// registered as a singleton. This interface exists so callers can mock it.
/// </para>
/// </remarks>
public interface IMimeSpy
{
    /// <summary>
    /// A quick answer from the start of a file. Only the header part of each signature is
    /// checked; any trailer check near the end of the file is skipped.
    /// </summary>
    /// <remarks>
    /// This is what lets <c>Spy</c> report formats such as PNG, JPEG, GIF, PDF and ZIP from
    /// a partial read, since PRONOM only fully confirms those by their trailer; such a match
    /// has <see cref="Dossier.Match"/> <see cref="Match.Partial"/>. The cost is
    /// that a truncated or corrupt file can match here and then not match
    /// <see cref="Investigate(ReadOnlySpan{byte})"/>. ZIP and OLE2 files are not opened, so
    /// docx, xlsx, doc, xls and similar get a less specific answer than <c>Investigate</c> gives.
    /// </remarks>
    /// <param name="bytes">
    /// The start of the file. Fewer bytes give a less specific answer, never a wrong one.
    /// If you have the whole file, call <see cref="Investigate(ReadOnlySpan{byte})"/> instead.
    /// </param>
    IReadOnlyList<Dossier> Spy(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// A quick answer from the start of <paramref name="stream"/>, or the full
    /// <see cref="Investigate(Stream)"/> answer if the stream is seekable and no longer than 8 MiB.
    /// </summary>
    /// <remarks>
    /// A non-seekable or larger stream has its first 64 KiB read and checked as
    /// <see cref="Spy(ReadOnlySpan{byte})"/> does.
    /// </remarks>
    /// <param name="stream">
    /// The whole file. A seekable stream must be at position 0; a non-seekable one is read
    /// from where it is. The stream is left open, positioned wherever reading stopped.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is seekable but isn't at position 0.</exception>
    IReadOnlyList<Dossier> Spy(Stream stream);

    /// <summary>The asynchronous form of <see cref="Spy(Stream)"/>.</summary>
    /// <param name="stream">As for <see cref="Spy(Stream)"/>.</param>
    /// <param name="cancellationToken">Cancels the pending read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is seekable but isn't at position 0.</exception>
    Task<IReadOnlyList<Dossier>> SpyAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// A confirmed answer from a complete file. Header and trailer checks both run, and ZIP
    /// and OLE2 containers are opened to tell docx, xlsx, pptx, doc, xls, jar, odt and similar
    /// apart from each other and from a plain ZIP or OLE2 file.
    /// </summary>
    /// <remarks>
    /// The bytes are taken to be the complete file; this can't be verified. A truncated
    /// buffer usually just gives a less specific answer, but isn't guaranteed to. For a
    /// partial read, use <see cref="Spy(ReadOnlySpan{byte})"/>.
    /// </remarks>
    /// <param name="bytes">
    /// The complete file. It is copied once, so peak memory is about twice the file's size.
    /// For a file not already in memory, <see cref="Investigate(Stream)"/> uses far less.
    /// </param>
    IReadOnlyList<Dossier> Investigate(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// A confirmed answer from a complete file, as <see cref="Investigate(ReadOnlySpan{byte})"/>,
    /// without reading the whole stream into memory.
    /// </summary>
    /// <remarks>
    /// A stream up to 16 MiB is read whole. A larger one has 8 MiB read from each end, plus
    /// the parts of a ZIP/OLE2 container that the container check needs, read in place. Peak
    /// memory is therefore about 16 MiB plus the size of any container entries checked,
    /// however large the file. On a file over 16 MiB, the few PRONOM signatures that search
    /// for a marker at no fixed distance from either end only search the first 8 MiB, so a
    /// marker further in is missed and a less specific match is reported.
    /// </remarks>
    /// <param name="stream">
    /// The whole file: seekable and at position 0. Copy a non-seekable source into a
    /// <see cref="MemoryStream"/> or a temporary <see cref="FileStream"/> first. The stream
    /// is left open, positioned wherever reading stopped.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> isn't seekable, or isn't at position 0.</exception>
    IReadOnlyList<Dossier> Investigate(Stream stream);

    /// <summary>The asynchronous form of <see cref="Investigate(Stream)"/>.</summary>
    /// <remarks>
    /// On a stream over 16 MiB that turns out to be a ZIP/OLE2 container, the container
    /// check that follows the signature match reads synchronously.
    /// </remarks>
    /// <param name="stream">As for <see cref="Investigate(Stream)"/>.</param>
    /// <param name="cancellationToken">Cancels the pending signature-matching reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> isn't seekable, or isn't at position 0.</exception>
    Task<IReadOnlyList<Dossier>> InvestigateAsync(Stream stream, CancellationToken cancellationToken = default);
}
