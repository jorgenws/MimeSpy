namespace MimeSpy.IntegrationTests;

/// <summary>
/// Runs <see cref="MimeSpy.Spy(ReadOnlySpan{byte})"/> against real files - not bytes
/// hand-built to match the signature table - across as many mainstream formats as this
/// repo can reasonably produce or source: either genuine encoder/tool output (ffmpeg,
/// ImageMagick, gzip/bzip2/xz/tar) or real-world files pulled from Apache Tika's test
/// suite. See README.md for exactly where each one came from and how to regenerate it.
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

        // Fonts
        { "sample.ttf", "ttf" },
        { "sample.otf", "otf" },
        { "sample.woff2", "woff2" },

        // Archives
        { "sample.zip", "zip" },
        { "sample.gz", "gz" },
        { "sample.bz2", "bz2" },
        { "sample.xz", "xz" },
        { "sample.tar", "tar" },
        { "sample.7z", "7z" },

        // Documents and zip-based containers
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

        // sample.epub is deliberately not here - see
        // Spy_RealEpubFile_IsNotIdentifiedAsEpub_KnownGap below.
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Spy_RealSampleFile_MatchesExpectedExtension(string fileName, string expectedExtension)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains(expectedExtension));
    }

    [Fact]
    public void Spy_RealPptxFile_NarrowsToOfficeOpenXmlTiedWithDocxAndXlsx()
    {
        // sample.pptx really does have "[Content_Types].xml" as its first zip
        // entry (verified when the fixture was sourced), so ZipContainerSniffer
        // narrows it down to the single "MS Office Open XML Format Document" row
        // - which, from the first entry name alone, can't be told apart from an
        // actual .docx or .xlsx (see docs/adr/0002). One result, three tied
        // extensions is the documented, correct outcome here, not a gap.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.pptx"));

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Contains("docx", single.Extensions);
        Assert.Contains("xlsx", single.Extensions);
        Assert.Contains("pptx", single.Extensions);
    }

    [Theory]
    [InlineData("sample.odt", "odt")]
    [InlineData("sample.ods", "ods")]
    [InlineData("sample.odp", "odp")]
    public void Spy_RealOdfFile_NarrowsToExactlyItsOwnExtension(string fileName, string expectedExtension)
    {
        // Before docs/adr/0010, odt/odp were tied together in one upstream row
        // and ods had no row at all - a real .ods or .odp file's stored
        // mimetype entry would have narrowed wrong or not at all. Now each of
        // these three real, independently-produced ODF files narrows to a
        // single result containing only its own extension.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Equal([expectedExtension], single.Extensions);
    }

    [Fact]
    public void Spy_RealJarFileWhoseFirstEntryIsAMetaInfDirectory_StillNarrowsToJar()
    {
        // This real jar's actual first zip entry is the "META-INF/" directory
        // itself (a very common thing for a real jar tool to write before the
        // manifest file it contains), not "META-INF/MANIFEST.MF" - but it's a
        // zero-length directory placeholder, so ZipContainerSniffer skips past
        // it and narrows on the entry right after (see docs/adr/0009). A
        // hand-built fixture with the manifest as the literal first entry would
        // never have exercised that skip.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.jar"));

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Contains("jar", single.Extensions);
    }

    [Fact]
    public void Spy_RealDocxFileWithMultipleEntriesBeforeContentTypesXml_FallsBackToFullZipFamilyTie()
    {
        // Unlike sample.jar above, skipping this real .docx's one leading
        // "_rels/" directory placeholder isn't enough: the entry right after it
        // is "_rels/.rels" - a real file, not a directory, and not
        // "[Content_Types].xml" either (that entry exists in this archive, just
        // further in, produced by an older Word build - see its own metadata).
        // ZipContainerSniffer (docs/adr/0009) only walks past *leading*
        // directory placeholders, it doesn't scan the whole archive looking for
        // a name it recognizes, so it stops at "_rels/.rels" and Spy() falls
        // back to the full tied set of every "just a zip" format, which still
        // contains "docx" - just alongside many others, unnarrowed. A
        // hand-built fixture that always puts [Content_Types].xml right after
        // any leading directory entry would never have surfaced this case.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.docx"));

        var result = _sut.Spy(bytes);

        Assert.True(result.Count > 1, "expected the full, unnarrowed zip-family tie");
        Assert.Contains(result, r => r.Extensions.Contains("docx"));
    }

    [Fact]
    public void Spy_RealEpubFile_IsNotIdentifiedAsEpub_KnownGap()
    {
        // A known gap, not an oversight: this real epub misses both of the
        // paths that could identify it as "epub". Its own header bytes are
        // "50 4B 03 04 0A 00 00 00" - general-purpose flag 0x0000, not the
        // 0x0002 the EPUB-specific 8-byte signature requires - so it doesn't
        // win outright the way docs/adr/0002 describes. And its first
        // non-directory zip entry (after skipping the leading "META-INF/"
        // placeholder, see docs/adr/0009) is "META-INF/container.xml", not
        // "mimetype", so the ODF-style content-sniffing narrowing (which does
        // work for a real epub whose first real entry actually is "mimetype")
        // never gets a chance to run either. Extending ZipContainerSniffer to
        // also skip past "META-INF/container.xml" looking for "mimetype"
        // would fix this specific file, but that starts hardcoding knowledge
        // of one format's internal layout into what's meant to stay a check
        // of only the first entry - not done here, see AGENTS.md's "one layer,
        // don't make it complicated". Documented as current behavior instead.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.epub"));

        var result = _sut.Spy(bytes);

        Assert.DoesNotContain(result, r => r.Extensions.Contains("epub"));
        Assert.Contains(result, r => r.Extensions.Contains("zip"));
    }

    [Theory]
    [InlineData("sample.doc", "doc", "dot")]
    [InlineData("sample.xls", "xls", "xla")]
    [InlineData("sample.ppt", "ppt", "pps")]
    public void Spy_RealLegacyOfficeFile_NarrowsToItsOwnWordExcelOrPowerPointFamily(string fileName, string primaryExtension, string templateExtension)
    {
        // doc/xls/ppt (LibreOffice's real Word 97/Excel 97/PowerPoint 97 export
        // filters) all share the exact same 8-byte OLE2/CFBF header
        // (D0 CF 11 E0 A1 B1 1A E1) as dozens of unrelated formats in
        // file_signatures.csv (Access, Visio, MSI, Publisher, ...).
        // Ole2ContainerSniffer resolves this the way Tika/POI do - not from any
        // fixed-offset byte pattern (the table's offset-512 "subheader" rows
        // don't match what LibreOffice's writer actually produces here), but by
        // parsing the CFB directory sector and matching its distinctive stream
        // name ("WordDocument"/"Workbook"/"PowerPoint Document") - see
        // docs/adr/0011. That narrows all 17 originally-tied results down to a
        // single one - still tied with its own template/add-in/slideshow
        // sibling (dot/xla/pps), since that pair is structurally identical
        // from the CFB side alone, the same way docx/pptx/xlsx stay tied to
        // each other above.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Equal(2, single.Extensions.Count);
        Assert.Contains(primaryExtension, single.Extensions);
        Assert.Contains(templateExtension, single.Extensions);
        Assert.Equal(primaryExtension, single.PrimaryExtension());
    }
}
