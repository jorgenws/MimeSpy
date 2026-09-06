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
}
