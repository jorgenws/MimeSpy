namespace MimeSpy;

/// <inheritdoc/>
public sealed class MimeSpy : IMimeSpy
{
    private readonly PronomFormatFilter? _filter;

    /// <summary>Considers every format PRONOM catalogs.</summary>
    public MimeSpy()
    {
    }

    /// <summary>Considers only the formats <paramref name="options"/> selects.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public MimeSpy(MimeSpyOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.MimeTypes.Count > 0 || options.ExcludedMimeTypes.Count > 0)
        {
            _filter = PronomFormatFilter.Build(options);
        }
    }

    // There's no small fixed byte count that reaches every PRONOM signature: most
    // anchors sit in the first few hundred bytes, but a handful probe up to several
    // thousand, and a few (BibTeX-style text formats) up to 15000 - so this is a
    // chosen, revisable default.
    private const int SpyPeekReadSize = 65536;

    // docs/adr/0019.
    private const long SpyFullReadThreshold = 8 * 1024 * 1024;

    // docs/adr/0022.
    private const int InvestigateWindowSize = 8 * 1024 * 1024;

    /// <inheritdoc/>
    public IReadOnlyList<Dossier> Spy(ReadOnlySpan<byte> bytes) => ToPublic(PronomSignatureMatcher.FindHeaderMatches(bytes, _filter));

    /// <inheritdoc/>
    public IReadOnlyList<Dossier> Spy(Stream stream)
    {
        ThrowIfNullOrNotAtStart(stream);

        if (CanPromoteToInvestigate(stream))
        {
            return Investigate(stream);
        }

        var array = new byte[SpyPeekReadSize];
        var totalRead = StreamReading.Fill(stream, array, 0, array.Length);

        return Spy(array.AsSpan(0, totalRead));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Dossier>> SpyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ThrowIfNullOrNotAtStart(stream);
        return SpyAsyncCore(stream, cancellationToken);
    }

    private Task<IReadOnlyList<Dossier>> SpyAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        return CanPromoteToInvestigate(stream)
            ? InvestigateAsyncCore(stream, cancellationToken)
            : SpyBoundedPeekAsync(stream, cancellationToken);
    }

    private async Task<IReadOnlyList<Dossier>> SpyBoundedPeekAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[SpyPeekReadSize];
        var totalRead = await StreamReading.FillAsync(stream, buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);

        return Spy(buffer.AsSpan(0, totalRead));
    }

    private static bool CanPromoteToInvestigate(Stream stream) =>
        stream.CanSeek && stream.Length <= SpyFullReadThreshold;

    /// <inheritdoc/>
    public IReadOnlyList<Dossier> Investigate(ReadOnlySpan<byte> bytes)
    {
        var array = bytes.ToArray();
        return InvestigateInMemory(array, array.Length);
    }

    /// <inheritdoc/>
    public IReadOnlyList<Dossier> Investigate(Stream stream)
    {
        ThrowIfNotInvestigable(stream);

        var length = stream.Length;
        if (length <= 2L * InvestigateWindowSize)
        {
            var whole = new byte[length];
            return InvestigateInMemory(whole, StreamReading.Fill(stream, whole, 0, whole.Length));
        }

        var head = new byte[InvestigateWindowSize];
        var headRead = StreamReading.Fill(stream, head, 0, head.Length);

        var tail = new byte[InvestigateWindowSize];
        stream.Position = length - tail.Length;
        var tailRead = StreamReading.Fill(stream, tail, 0, tail.Length);

        return InvestigateCore(head.AsSpan(0, headRead), tail.AsSpan(0, tailRead), stream);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Dossier>> InvestigateAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInvestigable(stream);
        return InvestigateAsyncCore(stream, cancellationToken);
    }

    private async Task<IReadOnlyList<Dossier>> InvestigateAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        var length = stream.Length;
        if (length <= 2L * InvestigateWindowSize)
        {
            var whole = new byte[length];
            var read = await StreamReading.FillAsync(stream, whole, 0, whole.Length, cancellationToken).ConfigureAwait(false);
            return InvestigateInMemory(whole, read);
        }

        var head = new byte[InvestigateWindowSize];
        var headRead = await StreamReading.FillAsync(stream, head, 0, head.Length, cancellationToken).ConfigureAwait(false);

        var tail = new byte[InvestigateWindowSize];
        stream.Position = length - tail.Length;
        var tailRead = await StreamReading.FillAsync(stream, tail, 0, tail.Length, cancellationToken).ConfigureAwait(false);

        return InvestigateCore(head.AsSpan(0, headRead), tail.AsSpan(0, tailRead), stream);
    }

    private static void ThrowIfNotInvestigable(Stream stream)
    {
        ThrowIfNullOrNotAtStart(stream);

        if (!stream.CanSeek)
        {
            throw new ArgumentException(
                "Investigate needs a seekable stream. Copy a non-seekable source into a MemoryStream or a temporary FileStream first, or pass its bytes to Investigate(ReadOnlySpan<byte>).",
                nameof(stream));
        }
    }

    private static void ThrowIfNullOrNotAtStart(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (stream.CanSeek && stream.Position != 0)
        {
            throw new ArgumentException(
                $"The stream is read as the whole file, so a seekable stream must be at position 0, but it was at {stream.Position}. Set stream.Position = 0 first.",
                nameof(stream));
        }
    }

    private IReadOnlyList<Dossier> InvestigateInMemory(byte[] bytes, int length)
    {
        using var file = new MemoryStream(bytes, 0, length, writable: false);
        var span = bytes.AsSpan(0, length);
        return InvestigateCore(span, span, file);
    }

    // head/tail are the file's two ends (the same span when it's all in memory);
    // file is the whole file, from position 0, for the container readers.
    private IReadOnlyList<Dossier> InvestigateCore(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, Stream file) =>
        ToPublic(PronomContainerMatcher.Refine(file, PronomSignatureMatcher.FindFullMatches(head, tail, _filter), _filter));

    private static IReadOnlyList<Dossier> ToPublic(IReadOnlyList<PronomMatch> matches) =>
        matches.Count == 0 ? [] : [.. matches.Select(m => new Dossier(m.Format.Puid, m.Format.Name, m.Format.Version, m.Format.MimeTypes, m.Format.Extensions, m.Match))];
}
