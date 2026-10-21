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

    // The Spy/Investigate contract itself (docs/adr/0017, 0019): Spy evaluates a
    // signature's BOF portion but skips (rather than requires) its EOF check, while
    // Investigate requires it against the buffer's real end - so a merely-partial
    // buffer that happens to satisfy a format's BOF portion is caught by Spy but
    // not by Investigate.

    [Fact]
    public void Spy_PdfHeaderWithoutTrailer_MatchesPdf_BySkippingTheEofCheck()
    {
        var result = _sut.Spy(PdfHeaderOnly);

        Assert.Contains(result, f => f.Extensions.Contains("pdf"));
    }

    [Fact]
    public void Investigate_PdfHeaderWithoutTrailer_ReturnsNoMatch_BecauseTheTruncatedBufferHasNoRealTrailer()
    {
        // Safe, not wrong: the missing "%%EOF" trailer just isn't found against
        // this buffer's own (truncated) end, so Investigate correctly reports no
        // match rather than a false positive - see docs/adr/0019.
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
    public void Stream_SeekableStream_RestoresOriginalPosition(StreamMethod method)
    {
        using var stream = new MemoryStream(PdfHeaderOnly);
        stream.Position = 3;

        method.Invoke(_sut, stream);

        Assert.Equal(3, stream.Position);
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

    // Spy(Stream)/SpyAsync's length-adaptive promotion (docs/adr/0019): a small
    // enough seekable stream gets read in full and handed to Investigate for its
    // fully-confirmed answer, instead of the bounded, EOF-skipping peek.

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
