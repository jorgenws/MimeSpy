namespace MimeSpy;

/// <inheritdoc/>
public sealed partial class MimeSpy : IMimeSpy
{
    private readonly PronomFormatFilter? _filter;

    /// <summary>Considers every format PRONOM catalogs.</summary>
    public MimeSpy()
    {
    }

    /// <summary>
    /// Considers only formats matching <paramref name="options"/> - see
    /// <see cref="MimeSpyOptions.MimeTypes"/>. Built once here, not per call.
    /// </summary>
    public MimeSpy(MimeSpyOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.MimeTypes.Count > 0)
        {
            _filter = PronomFormatFilter.Build(options);
        }
    }

    // How much of a Stream to read for Spy's bounded-peek fallback (non-seekable, or
    // seekable but larger than SpyFullReadThreshold below). There's no small fixed
    // byte count that reaches every PRONOM signature: most anchors sit in the first
    // few hundred bytes, but a handful probe up to several thousand, and a few
    // (BibTeX-style text formats) up to 15000 - so this is a chosen, revisable
    // default balancing thoroughness against not reading a possibly-large file in
    // full just to peek its header.
    private const int SpyPeekReadSize = 65536;

    // How much a Stream is worth fully buffering under Spy before falling back to
    // the bounded peek above - see docs/adr/0019. Chosen the same way: comfortably
    // past everyday documents/images/audio, well short of "unboundedly large" so a
    // caller who wanted a cheap peek never gets surprised by Spy quietly reading
    // gigabytes.
    private const long SpyFullReadThreshold = 8 * 1024 * 1024;

    /// <inheritdoc/>
    public IReadOnlyList<Dossier> Spy(ReadOnlySpan<byte> bytes) => ToPublic(PronomSignatureMatcher.FindMatchesGuess(bytes, _filter));

    /// <inheritdoc/>
    public IReadOnlyList<Dossier> Spy(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        // If the stream can tell us up front that it's small, read it all and get
        // Investigate's fully-confirmed answer (real EOF check, container engine)
        // instead of the peek-and-skip-EOF fallback below - see docs/adr/0019 for
        // why this is gated on a fact we already have (the stream's own reported
        // length), not on trying Investigate and detecting whether it "worked".
        if (TryGetRemainingLength(stream, out var remaining) && remaining <= SpyFullReadThreshold)
        {
            return Investigate(stream);
        }

        var startPosition = stream.CanSeek ? stream.Position : -1;
        var array = new byte[SpyPeekReadSize];
#if NETSTANDARD2_0
        var totalRead = ReadAtLeast(stream, array);
#else
        var totalRead = stream.ReadAtLeast(array.AsSpan(), array.Length, throwOnEndOfStream: false);
#endif

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

        return Spy(array.AsSpan(0, totalRead));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Dossier>> SpyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        return SpyAsyncCore(stream, cancellationToken);
    }

    private Task<IReadOnlyList<Dossier>> SpyAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        return TryGetRemainingLength(stream, out var remaining) && remaining <= SpyFullReadThreshold
            ? InvestigateAsyncCore(stream, cancellationToken)
            : SpyBoundedPeekAsync(stream, cancellationToken);
    }

    private async Task<IReadOnlyList<Dossier>> SpyBoundedPeekAsync(Stream stream, CancellationToken cancellationToken)
    {
        var startPosition = stream.CanSeek ? stream.Position : -1;
        var buffer = new byte[SpyPeekReadSize];
        var totalRead = await ReadAtLeastAsync(stream, buffer, cancellationToken).ConfigureAwait(false);

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

        return Spy(buffer.AsSpan(0, totalRead));
    }

    private static bool TryGetRemainingLength(Stream stream, out long remaining)
    {
        if (stream.CanSeek)
        {
            remaining = stream.Length - stream.Position;
            return true;
        }

        remaining = 0;
        return false;
    }

#if NETSTANDARD2_0
    private static int ReadAtLeast(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
#endif

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    /// <inheritdoc/>
    public IReadOnlyList<Dossier> Investigate(ReadOnlySpan<byte> bytes)
    {
        // Unlike the Stream overloads (which already hold an array and can hand it
        // to InvestigateCore untouched - see their own comments), a caller-supplied
        // span isn't necessarily backed by an array this method can keep past the
        // call, so this copy is the one truly unavoidable one.
        var array = bytes.ToArray();
        return InvestigateCore(array, array.Length);
    }

    /// <inheritdoc/>
    public IReadOnlyList<Dossier> Investigate(Stream stream)
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
        return InvestigateCore(buffer.GetBuffer(), (int)buffer.Length);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Dossier>> InvestigateAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        return InvestigateAsyncCore(stream, cancellationToken);
    }

    private async Task<IReadOnlyList<Dossier>> InvestigateAsyncCore(Stream stream, CancellationToken cancellationToken)
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

        return InvestigateCore(buffer.GetBuffer(), (int)buffer.Length);
    }

    // bytes may be larger than length (see Investigate(Stream)'s own comment) -
    // every reader this reaches is written to respect length rather than bytes.Length.
    private IReadOnlyList<Dossier> InvestigateCore(byte[] bytes, int length) =>
        ToPublic(PronomContainerMatcher.Refine(bytes, length, PronomSignatureMatcher.FindMatchesFull(bytes.AsSpan(0, length), _filter)));

    private static IReadOnlyList<Dossier> ToPublic(IReadOnlyList<PronomFileFormat> formats) =>
        formats.Count == 0 ? [] : [.. formats.Select(f => new Dossier(f.Puid, f.Name, f.Version, f.MimeType, f.Extensions))];
}

/// <summary>
/// Everything PRONOM knows about one format that matched, from
/// <see cref="MimeSpy.Spy(ReadOnlySpan{byte})"/> or <see cref="MimeSpy.Investigate(ReadOnlySpan{byte})"/>
/// (and their overloads).
/// </summary>
/// <param name="Puid">The PRONOM Unique Identifier (e.g. <c>"fmt/412"</c>, <c>"x-fmt/263"</c>) - PRONOM's own stable format key.</param>
/// <param name="Name">The format's name, as PRONOM records it (e.g. <c>"Microsoft Word for Windows"</c>).</param>
/// <param name="Version">The format's version, if PRONOM records one for this PUID specifically (distinct PUIDs usually exist per version instead).</param>
/// <param name="MimeType">The MIME type PRONOM associates with this format, if any - not every PRONOM format has one.</param>
/// <param name="Extensions">File extensions associated with this format.</param>
public sealed record Dossier(string Puid, string Name, string? Version, string? MimeType, IReadOnlyList<string> Extensions);
