namespace MimeSpy.IntegrationTests;

/// <summary>
/// Runs <see cref="MimeSpy.Investigate(ReadOnlySpan{byte})"/> - the full-file,
/// container-aware tier, the direct analogue of what a complete real file needs -
/// against real files, not bytes hand-built to match a signature table, across as
/// many mainstream formats as this repo can reasonably produce or source: either
/// genuine encoder/tool output (ffmpeg, ImageMagick, gzip/bzip2/xz/tar) or real-world
/// files pulled from Apache Tika's test suite. See README.md for exactly where each
/// one came from and how to regenerate it. Expected results below were captured by
/// running the real PRONOM engine against each fixture, not assumed.
/// </summary>
public class RealSampleFileTests
{
    private readonly MimeSpy _sut = new();

    // File, an extension the top Investigate result must list, the PUID each of Spy
    // and Investigate must put first, and the top Investigate result's MimeType. Pinning the top result means a change that
    // demotes or loses a common format fails here rather than going unnoticed.
    public static TheoryData<string, string, string, string, string?> Fixtures => new()
    {
        // Images
        { "sample.jpg", "jpg", "fmt/43", "fmt/43", "image/jpeg" },
        { "sample.png", "png", "fmt/11", "fmt/11", "image/png" },
        { "sample.gif", "gif", "fmt/4", "fmt/4", "image/gif" },
        { "sample.bmp", "bmp", "fmt/119", "fmt/119", "image/bmp" },
        { "sample.ico", "ico", "x-fmt/418", "x-fmt/418", "image/vnd.microsoft.icon" },
        { "sample.tiff", "tiff", "fmt/353", "fmt/353", "image/tiff" },
        { "sample.webp", "webp", "fmt/566", "fmt/566", "image/webp" },
        { "sample.heic", "heic", "fmt/1101", "fmt/1101", "image/heif" },

        // Audio
        { "sample.wav", "wav", "fmt/141", "fmt/141", "audio/x-wav" },
        { "sample.mp3", "mp3", "fmt/134", "fmt/134", "audio/mpeg" },
        { "sample.ogg", "ogg", "fmt/203", "fmt/203", "audio/ogg" },
        { "sample.flac", "flac", "fmt/279", "fmt/279", "audio/flac" },
        { "sample.m4a", "m4a", "fmt/2094", "fmt/2094", "audio/mp4" },

        // Video
        { "sample.mp4", "mp4", "fmt/199", "fmt/199", "application/mp4" },
        { "sample.webm", "webm", "fmt/573", "fmt/573", "video/webm" },
        { "sample.avi", "avi", "fmt/5", "fmt/5", "video/x-msvideo" },
        { "sample.mkv", "mkv", "fmt/569", "fmt/569", "video/x-matroska" },
        { "sample.mov", "mov", "x-fmt/384", "x-fmt/384", "video/quicktime" },
        { "sample.ogv", "ogv", "fmt/945", "fmt/945", "video/ogg" },

        // Fonts - sample.otf is deliberately not here, see
        // Investigate_RealOtfFile_IsNotIdentified_KnownGap below.
        { "sample.ttf", "ttf", "x-fmt/453", "x-fmt/453", "font/ttf" },
        { "sample.woff2", "woff2", "fmt/1172", "fmt/1172", "font/woff2" },

        // Archives
        { "sample.zip", "zip", "x-fmt/263", "x-fmt/263", "application/zip" },
        { "sample.gz", "gz", "x-fmt/266", "x-fmt/266", "application/gzip" },
        { "sample.bz2", "bz2", "x-fmt/268", "x-fmt/268", "application/x-bzip2" },
        { "sample.xz", "xz", "fmt/1098", "fmt/1098", "application/x-xz" },
        { "sample.tar", "tar", "x-fmt/265", "x-fmt/265", "application/x-tar" },
        { "sample.7z", "7z", "fmt/484", "fmt/484", "application/x-7z-compressed" },

        // Documents and zip/OLE2-based containers
        { "sample.pdf", "pdf", "fmt/17", "fmt/17", "application/pdf" },
        { "sample.rtf", "rtf", "fmt/53", "fmt/53", "application/rtf" },
        { "sample.docx", "docx", "x-fmt/263", "fmt/412", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { "sample.xlsx", "xlsx", "fmt/189", "fmt/214", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { "sample.pptx", "pptx", "fmt/189", "fmt/215", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { "sample.odt", "odt", "fmt/291", "fmt/291", "application/vnd.oasis.opendocument.text" },
        { "sample.ods", "ods", "fmt/295", "fmt/295", "application/vnd.oasis.opendocument.spreadsheet" },
        { "sample.odp", "odp", "fmt/293", "fmt/293", "application/vnd.oasis.opendocument.presentation" },
        { "sample.jar", "jar", "x-fmt/412", "x-fmt/412", "application/java-archive" },
        { "sample.doc", "doc", "fmt/40", "fmt/40", "application/msword" },
        { "sample.xls", "xls", "fmt/111", "fmt/61", "application/vnd.ms-excel" },
        { "sample.ppt", "ppt", "fmt/126", "fmt/126", "application/vnd.ms-powerpoint" },
        { "sample.epub", "epub", "x-fmt/263", "fmt/483", "application/epub+zip" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void RealSampleFile_TopResultIsTheExpectedFormat(string fileName, string expectedExtension, string spyTopPuid, string investigateTopPuid, string? expectedMimeType)
    {
        var bytes = ReadFixture(fileName);

        var spied = _sut.Spy(bytes);
        var investigated = _sut.Investigate(bytes);

        Assert.Equal(spyTopPuid, spied[0].Puid);
        Assert.Equal(investigateTopPuid, investigated[0].Puid);
        Assert.Contains(expectedExtension, investigated[0].Extensions);
        Assert.Equal(expectedMimeType, investigated[0].MimeType);
        Assert.All(investigated, f => Assert.Equal(Match.Full, f.Match));
    }

    [Fact]
    public void Investigate_RealOtfFile_IsNotIdentified_KnownGap()
    {
        // A known gap, not an oversight: PRONOM's current DROID table (version
        // 125) has no signature that matches this real CFF-flavored ("OTTO")
        // OpenType font's actual bytes, even though "otf" is a cataloged
        // PRONOM extension in principle. Documented as current behavior.
        var bytes = ReadFixture("sample.otf");

        var result = _sut.Investigate(bytes);

        Assert.Empty(result);
    }

    [Fact]
    public void Investigate_RealJpegFile_NarrowsToJfifOverRawJpegStream()
    {
        // A real JPEG matches both JFIF (fmt/43) and the generic Raw JPEG Stream
        // (fmt/41); PRONOM gives JFIF priority (docs/adr/0023).
        var bytes = ReadFixture("sample.jpg");

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal("fmt/43", single.Puid);
    }

    [Fact]
    public void Spy_RealTiffFile_ReturnsWideAmbiguousTieIncludingDigitalNegativeVariants()
    {
        // docs/adr/0017, 0023.
        var bytes = ReadFixture("sample.tiff");

        var result = _sut.Spy(bytes);

        Assert.True(result.Count > 1, "expected the wide TIFF/DNG-family tie");
        Assert.Contains(result, f => f.Name.Contains("Digital Negative", StringComparison.Ordinal));
        Assert.Contains(result, f => f.Extensions.Contains("tiff"));
    }

    [Fact]
    public void Spy_RealTiffFile_ListsTheFullTiffMatchFirst_ThenPartialsMostSpecificFirst()
    {
        // docs/adr/0024.
        var bytes = ReadFixture("sample.tiff");

        var result = _sut.Spy(bytes);

        Assert.Equal("fmt/353", result[0].Puid);
        Assert.Equal(Match.Full, result[0].Match);
        Assert.All(result.Skip(1), f => Assert.Equal(Match.Partial, f.Match));
        var lastDng = result.ToList().FindLastIndex(f => f.Name.Contains("Digital Negative", StringComparison.Ordinal));
        var firstExif = result.ToList().FindIndex(f => f.Name.Contains("Exchangeable Image", StringComparison.Ordinal));
        Assert.True(lastDng < firstExif, "DNG outranks Exif, so every DNG match comes first");
    }

    [Fact]
    public void Spy_RealJpegFile_NarrowsToJfif_AsAPartialMatch()
    {
        // docs/adr/0023.
        var bytes = ReadFixture("sample.jpg");

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Equal("fmt/43", single.Puid);
        Assert.Equal(Match.Partial, single.Match);
    }

    [Fact]
    public void Spy_SmallSeekableStream_ReturnsInvestigatesFullMatch()
    {
        // docs/adr/0019.
        using var stream = new MemoryStream(ReadFixture("sample.jpg"));

        var result = _sut.Spy(stream);

        var single = Assert.Single(result);
        Assert.Equal("fmt/43", single.Puid);
        Assert.Equal(Match.Full, single.Match);
    }

    [Fact]
    public void Investigate_RealTiffFile_NarrowsToPlainTiffAlone()
    {
        var bytes = ReadFixture("sample.tiff");

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal("fmt/353", single.Puid);
    }

    [Fact]
    public void Spy_RealDocxFile_OnlyMatchesGenericZip_ContainerEngineNeedsInvestigate()
    {
        var bytes = ReadFixture("sample.docx");

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Equal("application/zip", single.MimeType);
    }

    [Fact]
    public void Investigate_RealDocxFile_NarrowsToWordSpecificPuid()
    {
        var bytes = ReadFixture("sample.docx");

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal("fmt/412", single.Puid);
        Assert.Contains("docx", single.Extensions);
    }

    [Theory]
    [InlineData("sample.odt", "fmt/291", "odt", "ott")]
    [InlineData("sample.ods", "fmt/295", "ods", "ots")]
    [InlineData("sample.odp", "fmt/293", "odp", "otp")]
    public void Investigate_RealOdfFile_NarrowsToOdf12_WhichStillCarriesItsTemplateExtension(string fileName, string expectedPuid, string expectedExtension, string templateExtension)
    {
        // ODF 1.2 outranks 1.1 (docs/adr/0023). Each PUID carries both the document and
        // its template's extension.
        var bytes = ReadFixture(fileName);

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal(expectedPuid, single.Puid);
        Assert.Equal([expectedExtension, templateExtension], single.Extensions);
    }

    [Fact]
    public void Investigate_RealJarFile_NarrowsToJarAlone()
    {
        var bytes = ReadFixture("sample.jar");

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal("x-fmt/412", single.Puid);
    }

    [Theory]
    [InlineData("sample.doc", "fmt/40", "doc")]
    [InlineData("sample.xls", "fmt/61", "xls")]
    [InlineData("sample.ppt", "fmt/126", "ppt")]
    public void Investigate_RealLegacyOfficeFile_NarrowsToASinglePuid(string fileName, string expectedPuid, string expectedExtension)
    {
        var bytes = ReadFixture(fileName);

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal(expectedPuid, single.Puid);
        Assert.Contains(expectedExtension, single.Extensions);
    }

    [Fact]
    public void Investigate_RealEpubFile_NarrowsToEpub()
    {
        var bytes = ReadFixture("sample.epub");

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal("fmt/483", single.Puid);
        Assert.Contains("epub", single.Extensions);
    }

    private static byte[] ReadFixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
