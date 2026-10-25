namespace MimeSpy.Tests;

public class MimeSpyTests
{
    private readonly MimeSpy _sut = new();

    private static readonly byte[] PdfHeaderOnly = "%PDF-1.7 rest of file"u8.ToArray();
    private static readonly byte[] GenericZipHeaderOnly = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Gif89aHeaderOnly = "GIF89a"u8.ToArray();

    // A real, complete WAV file - unlike the EOF-anchored formats above (pdf, zip,
    // gif), WAV's PRONOM signature is pure BOF, so Spy/Investigate agree on it.
    // Used by tests that check stream-reading mechanics identically across both
    // methods, where the point is the plumbing, not either method's EOF-handling
    // contract.
    private static readonly byte[] WavBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.wav"));

    public static TheoryData<SpanMethod> SpanMethods => new()
    {
        new SpanMethod("Spy", (sut, bytes) => sut.Spy(bytes)),
        new SpanMethod("Investigate", (sut, bytes) => sut.Investigate(bytes)),
    };

    [Theory]
    [MemberData(nameof(SpanMethods))]
    public void EmptyInput_ReturnsNoMatches(SpanMethod method)
    {
        var result = method.Invoke(_sut, ReadOnlySpan<byte>.Empty);

        Assert.Empty(result);
    }

    [Theory]
    [MemberData(nameof(SpanMethods))]
    public void UnrecognizedBytes_ReturnsNoMatches(SpanMethod method)
    {
        // 0xF6 repeated isn't the start (or, for Investigate, the whole content)
        // of any signature PRONOM catalogs.
        var result = method.Invoke(_sut, new byte[] { 0xF6, 0xF6, 0xF6, 0xF6 });

        Assert.Empty(result);
    }

    [Theory]
    [MemberData(nameof(SpanMethods))]
    public void Wav_PriorityDropsTheGenericRiffAndWaveformMatches(SpanMethod method)
    {
        // A WAV file also matches PRONOM's generic RIFF (fmt/1886) and Waveform Audio
        // (fmt/6) signatures. PCMWAVEFORMAT (fmt/141) has priority over both, and its
        // signature is BOF-only, so even Spy has checked it in full.
        var result = method.Invoke(_sut, WavBytes);

        var single = Assert.Single(result);
        Assert.Equal("fmt/141", single.Puid);
        Assert.Equal(Match.Full, single.Match);
    }

    [Fact]
    public void Spy_FormatWithSeveralMimeTypes_ListsThemSeparately()
    {
        // PRONOM stores RTF's as one comma-joined string, "application/rtf, text/rtf".
        var result = _sut.Spy(@"{\rtf1\ansi\deff0 Hello}"u8);

        Assert.NotEmpty(result);
        Assert.All(result, f => Assert.Equal(["application/rtf", "text/rtf"], f.MimeTypes));
        Assert.All(result, f => Assert.Equal("application/rtf", f.MimeType));
    }

    [Fact]
    public void Spy_ViaIMimeSpyReference_MatchesSameAsConcreteType()
    {
        IMimeSpy sut = _sut;

        var result = sut.Spy(PdfHeaderOnly);

        Assert.Contains(result, f => f.Extensions.Contains("pdf"));
    }

    [Fact]
    public void Spy_IsRepeatableAcrossInstances()
    {
        var first = _sut.Spy(PdfHeaderOnly);
        var second = new MimeSpy().Spy(PdfHeaderOnly);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Spy_PdfHeaderWithoutTrailer_MatchesPdf_AsAPartialMatch()
    {
        var result = _sut.Spy(PdfHeaderOnly);

        var pdf = Assert.Single(result, f => f.Extensions.Contains("pdf"));
        Assert.Equal(Match.Partial, pdf.Match);
    }

    [Fact]
    public void Investigate_PdfHeaderWithoutTrailer_ReturnsNoMatch_BecauseTheTruncatedBufferHasNoRealTrailer()
    {
        var result = _sut.Investigate(PdfHeaderOnly);

        Assert.Empty(result);
    }

    [Fact]
    public void Spy_GenericZipHeaderOnly_MatchesZip()
    {
        var result = _sut.Spy(GenericZipHeaderOnly);

        Assert.Contains(result, f => f.Extensions.Contains("zip"));
    }

    [Fact]
    public void Spy_Gif89aHeaderOnly_MatchesGif()
    {
        var result = _sut.Spy(Gif89aHeaderOnly);

        Assert.Contains(result, f => f.Extensions.Contains("gif"));
    }

    public static TheoryData<StreamMethod> StreamMethods => new()
    {
        new StreamMethod("Spy", (sut, stream) => sut.Spy(stream)),
        new StreamMethod("Investigate", (sut, stream) => sut.Investigate(stream)),
    };

    [Theory]
    [MemberData(nameof(StreamMethods))]
    public void Stream_NullStream_Throws(StreamMethod method)
    {
        Assert.Throws<ArgumentNullException>(() => method.Invoke(_sut, null!));
    }

    [Theory]
    [MemberData(nameof(StreamMethods))]
    public void Stream_SeekableStreamNotAtStart_Throws(StreamMethod method)
    {
        using var stream = new MemoryStream(WavBytes);
        stream.Position = stream.Length;

        Assert.Throws<ArgumentException>(() => method.Invoke(_sut, stream));
    }

    [Theory]
    [MemberData(nameof(StreamMethods))]
    public void Stream_ThatOnlyReadsAFewBytesAtATime_StillAssemblesFullHeader(StreamMethod method)
    {
        // Some streams (e.g. network streams) can return fewer bytes than
        // requested even when more is available; the read loop must keep
        // pulling until it has enough or the stream is exhausted.
        using var stream = new TrickleStream(WavBytes, maxBytesPerRead: 2);

        var result = method.Invoke(_sut, stream);

        Assert.Contains(result, f => f.Extensions.Contains("wav"));
    }

    public static TheoryData<AsyncStreamMethod> AsyncStreamMethods => new()
    {
        new AsyncStreamMethod("SpyAsync", (sut, stream, ct) => sut.SpyAsync(stream, ct)),
        new AsyncStreamMethod("InvestigateAsync", (sut, stream, ct) => sut.InvestigateAsync(stream, ct)),
    };

    [Theory]
    [MemberData(nameof(AsyncStreamMethods))]
    public void StreamAsync_NullStream_Throws(AsyncStreamMethod method)
    {
        Assert.Throws<ArgumentNullException>(() => { _ = method.Invoke(_sut, null!, TestContext.Current.CancellationToken); });
    }

    [Theory]
    [MemberData(nameof(AsyncStreamMethods))]
    public void StreamAsync_SeekableStreamNotAtStart_Throws(AsyncStreamMethod method)
    {
        using var stream = new MemoryStream(WavBytes);
        stream.Position = stream.Length;

        Assert.Throws<ArgumentException>(() => { _ = method.Invoke(_sut, stream, TestContext.Current.CancellationToken); });
    }

    [Theory]
    [MemberData(nameof(AsyncStreamMethods))]
    public async Task StreamAsync_MatchesSameAsEquivalentSyncBytes(AsyncStreamMethod method)
    {
        using var stream = new MemoryStream(WavBytes);

        var result = await method.Invoke(_sut, stream, TestContext.Current.CancellationToken);

        Assert.Contains(result, f => f.Extensions.Contains("wav"));
    }

    [Theory]
    [MemberData(nameof(AsyncStreamMethods))]
    public async Task StreamAsync_CancelledToken_Throws(AsyncStreamMethod method)
    {
        using var stream = new MemoryStream(PdfHeaderOnly);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => method.Invoke(_sut, stream, cts.Token));
    }

    [Fact]
    public void Spy_SmallSeekableStream_PromotesToInvestigatesFullyConfirmedAnswer()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.zip"));
        using var stream = new MemoryStream(bytes);

        var viaSpyStream = _sut.Spy(stream);
        var viaInvestigateBytes = _sut.Investigate(bytes);

        Assert.Equal(
            viaInvestigateBytes.Select(f => f.Puid).OrderBy(p => p),
            viaSpyStream.Select(f => f.Puid).OrderBy(p => p));
    }

    [Fact]
    public void Spy_NonSeekableStream_FallsBackToTheBoundedPeekInsteadOfPromoting()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.zip"));
        using var nonSeekable = new NonSeekableStream(new MemoryStream(bytes));

        var viaSpyStream = _sut.Spy(nonSeekable);
        var viaSpyBytes = _sut.Spy(bytes);

        Assert.Equal(
            viaSpyBytes.Select(f => f.Puid).OrderBy(p => p),
            viaSpyStream.Select(f => f.Puid).OrderBy(p => p));
    }

    public static TheoryData<StreamMethod> InvestigateStreamMethods => new()
    {
        new StreamMethod("Investigate", (sut, stream) => sut.Investigate(stream)),
        new StreamMethod("InvestigateAsync", (sut, stream) => sut.InvestigateAsync(stream).GetAwaiter().GetResult()),
    };

    [Theory]
    [MemberData(nameof(InvestigateStreamMethods))]
    public void Investigate_NonSeekableStream_Throws(StreamMethod method)
    {
        using var nonSeekable = new NonSeekableStream(new MemoryStream(WavBytes));

        Assert.Throws<ArgumentException>(() => method.Invoke(_sut, nonSeekable));
    }

    public static TheoryData<string, string> EofAnchoredFixtures => new()
    {
        { "sample.pdf", "pdf" },
        { "sample.png", "png" },
        { "sample.gif", "gif" },
    };

    [Theory]
    [MemberData(nameof(EofAnchoredFixtures))]
    public void Investigate_StreamTooLargeToReadWhole_StillChecksTheRealTrailer(string fileName, string expectedExtension)
    {
        // Padding the middle pushes the file past where Investigate(Stream) stops
        // reading it whole, so the trailer is only seen through the tail window -
        // offsets in it have to line up with the file's real end for the match to hold.
        var original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
        var half = original.Length / 2;
        byte[] padded = [.. original.AsSpan(0, half), .. new byte[17 * 1024 * 1024], .. original.AsSpan(half)];
        using var stream = new MemoryStream(padded);

        var viaStream = _sut.Investigate(stream);

        Assert.Contains(viaStream, f => f.Extensions.Contains(expectedExtension));
        Assert.Equal(
            _sut.Investigate(padded).Select(f => f.Puid).OrderBy(p => p),
            viaStream.Select(f => f.Puid).OrderBy(p => p));
    }

    public sealed record SpanMethod(string Name, Func<MimeSpy, ReadOnlySpan<byte>, IReadOnlyList<Dossier>> Invoke)
    {
        public override string ToString() => Name;
    }

    public sealed record StreamMethod(string Name, Func<MimeSpy, Stream, IReadOnlyList<Dossier>> Invoke)
    {
        public override string ToString() => Name;
    }

    public sealed record AsyncStreamMethod(string Name, Func<MimeSpy, Stream, CancellationToken, Task<IReadOnlyList<Dossier>>> Invoke)
    {
        public override string ToString() => Name;
    }

    private sealed class TrickleStream(byte[] data, int maxBytesPerRead) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer)
        {
            var limited = buffer.Length > maxBytesPerRead ? buffer[..maxBytesPerRead] : buffer;
            return base.Read(limited);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var limited = Math.Min(count, maxBytesPerRead);
            return base.ReadAsync(buffer, offset, limited, cancellationToken);
        }
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
