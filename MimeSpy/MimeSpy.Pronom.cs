namespace MimeSpy;

/// <inheritdoc/>
public sealed partial class MimeSpy
{
    // Unlike SignatureIndex.MaxHeaderReach (derived from the old CSV table so it
    // can't drift), there's no small fixed byte count that reaches every PRONOM
    // signature: most anchors sit in the first few hundred bytes, but a handful
    // probe up to several thousand, and a few (BibTeX-style text formats) up to
    // 15000 - and container-signature disambiguation needs the complete file
    // regardless (see docs/adr/0016), so it isn't in Sniff's reach at any size. This
    // is a chosen, revisable default balancing thoroughness against not reading a
    // possibly-large file in full just to sniff its header.
    private const int SniffReadSize = 65536;

    /// <inheritdoc/>
    public IReadOnlyList<PronomFormat> Sniff(ReadOnlySpan<byte> bytes) => ToPublic(PronomSignatureMatcher.FindMatchesForward(bytes));

    /// <inheritdoc/>
    public IReadOnlyList<PronomFormat> Sniff(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var startPosition = stream.CanSeek ? stream.Position : -1;

        // Always a heap array, unlike Spy(Stream)'s stackalloc - SniffReadSize is
        // large enough that stack-allocating it would risk a stack overflow.
        var array = new byte[SniffReadSize];
#if NETSTANDARD2_0
        var totalRead = ReadAtLeast(stream, array);
#else
        var totalRead = stream.ReadAtLeast(array.AsSpan(), array.Length, throwOnEndOfStream: false);
#endif

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

        return Sniff(array.AsSpan(0, totalRead));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PronomFormat>> SniffAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        return SniffAsyncCore(stream, cancellationToken);
    }

    private async Task<IReadOnlyList<PronomFormat>> SniffAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        var startPosition = stream.CanSeek ? stream.Position : -1;
        var buffer = new byte[SniffReadSize];
        var totalRead = await ReadAtLeastAsync(stream, buffer, cancellationToken).ConfigureAwait(false);

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

        return Sniff(buffer.AsSpan(0, totalRead));
    }

    /// <inheritdoc/>
    public IReadOnlyList<PronomFormat> Guess(ReadOnlySpan<byte> bytes) => ToPublic(PronomSignatureMatcher.FindMatchesGuess(bytes));

    /// <inheritdoc/>
    public IReadOnlyList<PronomFormat> Guess(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var startPosition = stream.CanSeek ? stream.Position : -1;
        var array = new byte[SniffReadSize];
#if NETSTANDARD2_0
        var totalRead = ReadAtLeast(stream, array);
#else
        var totalRead = stream.ReadAtLeast(array.AsSpan(), array.Length, throwOnEndOfStream: false);
#endif

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

        return Guess(array.AsSpan(0, totalRead));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PronomFormat>> GuessAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        return GuessAsyncCore(stream, cancellationToken);
    }

    private async Task<IReadOnlyList<PronomFormat>> GuessAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        var startPosition = stream.CanSeek ? stream.Position : -1;
        var buffer = new byte[SniffReadSize];
        var totalRead = await ReadAtLeastAsync(stream, buffer, cancellationToken).ConfigureAwait(false);

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

        return Guess(buffer.AsSpan(0, totalRead));
    }

    /// <inheritdoc/>
    public IReadOnlyList<PronomFormat> Analyze(ReadOnlySpan<byte> bytes)
    {
        // Unlike the Stream overloads (which already hold an array and can hand it
        // to AnalyzeCore untouched - see their own comments), a caller-supplied span
        // isn't necessarily backed by an array this method can keep past the call,
        // so this copy is the one truly unavoidable one.
        var array = bytes.ToArray();
        return AnalyzeCore(array, array.Length);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PronomFormat> Analyze(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var startPosition = stream.CanSeek ? stream.Position : -1;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

        // GetBuffer(), not ToArray(): the MemoryStream's own backing array is reused
        // directly - it's usually larger than buffer.Length (it grows by doubling),
        // so every reader downstream is bounds-checked against the length passed
        // alongside it, never against the array's own (possibly larger) Length. This
        // is what makes a large file's peak memory here ~1x its size instead of the
        // 2-3x an extra ToArray()/per-reader copy would cost.
        return AnalyzeCore(buffer.GetBuffer(), (int)buffer.Length);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PronomFormat>> AnalyzeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        return AnalyzeAsyncCore(stream, cancellationToken);
    }

    private async Task<IReadOnlyList<PronomFormat>> AnalyzeAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        var startPosition = stream.CanSeek ? stream.Position : -1;
        using var buffer = new MemoryStream();

        // netstandard2.0's Stream.CopyToAsync has no (Stream, CancellationToken)
        // overload without also naming a buffer size - 81920 matches the default the
        // newer overload uses internally.
        await stream.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

        return AnalyzeCore(buffer.GetBuffer(), (int)buffer.Length);
    }

    // bytes may be larger than length (see Analyze(Stream)'s own comment) - every
    // reader this reaches is written to respect length rather than bytes.Length.
    private static IReadOnlyList<PronomFormat> AnalyzeCore(byte[] bytes, int length) =>
        ToPublic(PronomContainerMatcher.Refine(bytes, length, PronomSignatureMatcher.FindMatchesFull(bytes.AsSpan(0, length))));

    private static IReadOnlyList<PronomFormat> ToPublic(IReadOnlyList<PronomFileFormat> formats) =>
        formats.Count == 0 ? [] : [.. formats.Select(f => new PronomFormat(f.Puid, f.Name, f.Version, f.MimeType, f.Extensions))];
}

/// <summary>
/// A PRONOM file format that matched, from <see cref="MimeSpy.Sniff(ReadOnlySpan{byte})"/>,
/// <see cref="MimeSpy.Guess(ReadOnlySpan{byte})"/>, or <see cref="MimeSpy.Analyze(ReadOnlySpan{byte})"/>
/// (and their overloads).
/// </summary>
/// <param name="Puid">The PRONOM Unique Identifier (e.g. <c>"fmt/412"</c>, <c>"x-fmt/263"</c>) - PRONOM's own stable format key.</param>
/// <param name="Name">The format's name, as PRONOM records it (e.g. <c>"Microsoft Word for Windows"</c>).</param>
/// <param name="Version">The format's version, if PRONOM records one for this PUID specifically (distinct PUIDs usually exist per version instead).</param>
/// <param name="MimeType">The MIME type PRONOM associates with this format, if any - not every PRONOM format has one.</param>
/// <param name="Extensions">File extensions associated with this format.</param>
public sealed record PronomFormat(string Puid, string Name, string? Version, string? MimeType, IReadOnlyList<string> Extensions);
