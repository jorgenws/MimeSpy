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

    public static TheoryData<string, string> Fixtures => new()
    {
        // Images
        { "sample.jpg", "jpg" },
        { "sample.png", "png" },
        { "sample.gif", "gif" },
        { "sample.bmp", "bmp" },
        { "sample.ico", "ico" },
        { "sample.tiff", "tiff" },
        { "sample.webp", "webp" },
        { "sample.heic", "heic" },

        // Audio
        { "sample.wav", "wav" },
        { "sample.mp3", "mp3" },
        { "sample.ogg", "ogg" },
        { "sample.flac", "flac" },
        { "sample.m4a", "m4a" },

        // Video
        { "sample.mp4", "mp4" },
        { "sample.webm", "webm" },
        { "sample.avi", "avi" },
        { "sample.mkv", "mkv" },
        { "sample.mov", "mov" },
        { "sample.ogv", "ogv" },

        // Fonts - sample.otf is deliberately not here, see
        // Investigate_RealOtfFile_IsNotIdentified_KnownGap below.
        { "sample.ttf", "ttf" },
        { "sample.woff2", "woff2" },

        // Archives
        { "sample.zip", "zip" },
        { "sample.gz", "gz" },
        { "sample.bz2", "bz2" },
        { "sample.xz", "xz" },
        { "sample.tar", "tar" },
        { "sample.7z", "7z" },

        // Documents and zip/OLE2-based containers
        { "sample.pdf", "pdf" },
        { "sample.rtf", "rtf" },
        { "sample.docx", "docx" },
        { "sample.xlsx", "xlsx" },
        { "sample.pptx", "pptx" },
        { "sample.odt", "odt" },
        { "sample.ods", "ods" },
        { "sample.odp", "odp" },
        { "sample.jar", "jar" },
        { "sample.doc", "doc" },
        { "sample.xls", "xls" },
        { "sample.ppt", "ppt" },
        { "sample.epub", "epub" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Investigate_RealSampleFile_MatchesExpectedExtension(string fileName, string expectedExtension)
    {
        var bytes = ReadFixture(fileName);

        var result = _sut.Investigate(bytes);

        Assert.Contains(result, f => f.Extensions.Contains(expectedExtension));
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
        // The scenario docs/adr/0017 describes by name: TIFF and its Digital
        // Negative (DNG) variant share the same BOF bytes, and Spy skips the
        // EOF/structure check that would tell them apart - so a real TIFF
        // file matches every DNG/TIFF-family PUID that shares its header, not
        // just the one it actually is. Several of those outrank plain TIFF, but
        // only on evidence Spy skipped, so priority doesn't drop it (docs/adr/0023).
        var bytes = ReadFixture("sample.tiff");

        var result = _sut.Spy(bytes);

        Assert.True(result.Count > 1, "expected the wide TIFF/DNG-family tie");
        Assert.Contains(result, f => f.Name.Contains("Digital Negative", StringComparison.Ordinal));
        Assert.Contains(result, f => f.Extensions.Contains("tiff"));
    }

    [Fact]
    public void Investigate_RealTiffFile_NarrowsToPlainTiffAlone()
    {
        // Unlike Spy above, Investigate runs the real structure check and
        // correctly narrows this file down to plain TIFF, dropping every DNG
        // alternative Spy couldn't rule out from the header alone.
        var bytes = ReadFixture("sample.tiff");

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal("fmt/353", single.Puid);
    }

    [Fact]
    public void Spy_RealDocxFile_OnlyMatchesGenericZip_ContainerEngineNeedsInvestigate()
    {
        // Spy never runs PRONOM's container-signature engine (docs/adr/0016) -
        // that needs the complete file - so a real docx only ever matches the
        // generic ZIP PUID under Spy, not its own docx-specific one.
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
        // The container engine matches both ODF 1.1 and 1.2 PUIDs for each family;
        // PRONOM gives 1.2 priority (docs/adr/0023). A document still can't be told
        // from its own template - each PUID carries both extensions.
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
        // Unlike the old Gary-Kessler-table engine (which had a documented
        // known gap here - see git history), PRONOM's container engine
        // correctly resolves this real epub to its own PUID.
        var bytes = ReadFixture("sample.epub");

        var result = _sut.Investigate(bytes);

        var single = Assert.Single(result);
        Assert.Equal("fmt/483", single.Puid);
        Assert.Contains("epub", single.Extensions);
    }

    private static byte[] ReadFixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
