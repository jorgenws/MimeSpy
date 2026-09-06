namespace MimeSpy;

/// <summary>
/// Disambiguates one family of formats that share a header byte-for-byte (see
/// docs/adr/0001) using bytes past that header. Implementations only ever read
/// from the bytes passed to <see cref="Refine"/> - never a wider read - so a
/// truncated buffer just means falling back to the results unchanged rather
/// than guessing.
///
/// Adding support for a newly-ambiguous container format means implementing this
/// interface and adding an instance to the sniffer list in <see cref="MimeSpy.Spy(ReadOnlySpan{byte})"/>
/// - nothing else in this library needs to change.
/// </summary>
internal interface IContainerSniffer
{
    /// <summary>
    /// Returns <paramref name="results"/> as-is, or a narrowed/refined version of it,
    /// based on what <paramref name="bytes"/> reveals beyond the shared header. Must
    /// never affect a result this sniffer's format family has nothing to do with.
    /// </summary>
    IReadOnlyList<Result> Refine(ReadOnlySpan<byte> bytes, IReadOnlyList<Result> results);
}
