using System.Text;

namespace MimeSpy.Tests;

public class MimeSpyTests
{
    private readonly MimeSpy _sut = new();

    [Fact]
    public void Spy_EmptyInput_ReturnsNoMatches()
    {
        var result = _sut.Spy(ReadOnlySpan<byte>.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void Spy_UnrecognizedBytes_ReturnsNoMatches()
    {
        // 0xF6 is not claimed by any signature at offset 0 in the source table.
        var result = _sut.Spy(new byte[] { 0xF6, 0xF6, 0xF6, 0xF6 });

        Assert.Empty(result);
    }

    [Fact]
    public void Spy_PdfHeader_MatchesPdf()
    {
        var result = _sut.Spy("%PDF-1.7 rest of file"u8);

        Assert.Contains(result, r => r.Extensions.Contains("pdf"));
    }

    [Fact]
    public void Spy_LongerMoreSpecificHeader_WinsOverShorterPrefixMatch()
    {
        // "1F 8B 08" (GZ/VLT, 3 bytes) is a prefix of the Synology backup
        // signature "1F 8B 08 00" (4 bytes). With all 4 bytes present, only
        // the longer, more specific signature should be returned.
        var result = _sut.Spy(new byte[] { 0x1F, 0x8B, 0x08, 0x00 });

        Assert.DoesNotContain(result, r => r.Extensions.Contains("gz"));
        Assert.Contains(result, r => r.Extensions.Contains("dss"));
    }

    [Fact]
    public void Spy_AmbiguousHeaderShortOfDisambiguatingBytes_ReturnsAllTiedMatches()
    {
        // Only 3 bytes available: "1F 8B 08" matches both GZ and VLT (tied,
        // same length), and can't be distinguished from the longer Synology
        // signature because that one needs a 4th byte we don't have.
        var result = _sut.Spy(new byte[] { 0x1F, 0x8B, 0x08 });

        Assert.Contains(result, r => r.Extensions.Contains("gz"));
        Assert.Contains(result, r => r.Extensions.Contains("vlt"));
    }

    [Fact]
    public void Spy_IdenticalHeadersForDifferentFormats_ReturnsBothAsTies()
    {
        // bzip2 and a bzip2-compressed Mac disk image share the exact same
        // 3-byte header "42 5A 68" - genuinely ambiguous from header alone.
        var result = _sut.Spy(new byte[] { 0x42, 0x5A, 0x68 });

        Assert.Contains(result, r => r.Extensions.Contains("bz2"));
        Assert.Contains(result, r => r.Extensions.Contains("dmg"));
    }

    [Fact]
    public void Spy_HeaderOffsetHasTrailingJunkInSourceData_StillParsesAsOffsetZero()
    {
        // The "EA Interchange Format File (IFF)_3" row has "Header offset": "0(null)"
        // (a scraping artifact) - it must still be treated as offset 0, not dropped.
        var result = _sut.Spy(new byte[] { 0x43, 0x41, 0x54, 0x20 });

        Assert.Contains(result, r => r.Extensions.Contains("iff"));
    }

    [Fact]
    public void Spy_NonZeroOffsetSignature_MatchesAtCorrectPosition()
    {
        // RIFF <4-byte size> AVI LIST - the "AVI LIST" signature lives at
        // byte offset 8, not 0.
        var bytes = new byte[16];
        "RIFF"u8.CopyTo(bytes);
        "AVI LIST"u8.CopyTo(bytes.AsSpan(8));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("avi"));
    }

    [Fact]
    public void Spy_PdfHeader_ResolvesMimeTypeFromExtension()
    {
        var result = _sut.Spy("%PDF-1.7 rest of file"u8);

        Assert.Contains(result, r => r.MimeTypes.Contains("application/pdf"));
    }

    [Fact]
    public void Spy_PngHeader_PrimaryExtensionIsPng()
    {
        // A signature with only one extension trivially has that extension as
        // its PrimaryExtension - no tie to resolve.
        var result = _sut.Spy(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        Assert.Contains(result, r => r.PrimaryExtension == "png");
    }

    [Fact]
    public void Spy_TiedFormatsWithNoKnownPreference_PrimaryExtensionIsNull()
    {
        // docx/pptx/xlsx are tied together with no basis to prefer one over
        // the others (unlike doc/dot, xls/xla, ppt/pps - see docs/adr/0012),
        // so PrimaryExtension must be null rather than an arbitrary guess.
        var bytes = BuildZipWithFirstEntry("[Content_Types].xml", "<Types/>"u8.ToArray());

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Null(single.PrimaryExtension);
    }

    [Fact]
    public void Spy_JpegHeader_PrimaryMimeTypeIsMostRepresentedAmongAliasExtensions()
    {
        // The FF D8 FF signature lists extensions jfif|jpe|jpeg|jpg. Three of those
        // (jpe, jpeg, jpg) resolve to image/jpeg and only one (jfif) resolves to
        // image/pjpeg, so image/jpeg should win as the primary mime type.
        var result = _sut.Spy(new byte[] { 0xFF, 0xD8, 0xFF, 0x00 });

        Assert.Contains(result, r => r.PrimaryMimeType == "image/jpeg");
    }

    [Fact]
    public void Spy_JpegHeaderTiedAcrossFourSpellings_PrimaryExtensionResolvesViaMimeTypesDeclaredOrder()
    {
        // jfif/jpe/jpeg/jpg are pure spelling variants of one format, not
        // distinct formats - unlike docx/pptx/xlsx, three of these four
        // extensions agree on image/jpeg (only jfif resolves elsewhere, to
        // image/pjpeg), a real majority. mime.types lists this mime type as
        // "image/jpeg jpeg jpg jpe" - jpeg first - so that's the resolved
        // PrimaryExtension, derived from the same source data as
        // PrimaryMimeType rather than a hardcoded office-only table.
        var result = _sut.Spy(new byte[] { 0xFF, 0xD8, 0xFF, 0x00 });

        Assert.Contains(result, r => r.PrimaryExtension == "jpeg");
    }

    [Fact]
    public void Spy_IsRepeatableAcrossInstances()
    {
        var first = _sut.Spy("%PDF-1.7"u8);
        var second = new MimeSpy().Spy("%PDF-1.7"u8);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("sample.pdf", "pdf")]
    [InlineData("sample.png", "png")]
    [InlineData("sample.jpg", "jpg")]
    [InlineData("sample.gif", "gif")]
    [InlineData("sample.wav", "wav")]
    public void Spy_RealSampleFile_MatchesExpectedExtension(string fileName, string expectedExtension)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains(expectedExtension));
    }

    [Fact]
    public void Spy_RealZipFile_MatchesZipAmongTiedContainerFormats()
    {
        // A plain PK\x03\x04 local file header is shared verbatim by ZIP, APK,
        // JAR, KMZ, and the Office Open XML formats (docx/pptx/xlsx) - the four
        // magic bytes alone can't tell them apart. Disambiguating a real docx
        // from a real zip requires opening the archive and inspecting its
        // contents (e.g. looking for "[Content_Types].xml"), not just the header.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.zip"));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("zip"));
        Assert.Contains(result, r => r.Extensions.Contains("docx"));
    }

    [Fact]
    public void Spy_ZipFirstEntryIsContentTypesXml_NarrowsToOfficeOpenXml()
    {
        // A real docx/xlsx/pptx almost always has "[Content_Types].xml" as the
        // first entry in the archive - that's enough to tell it apart from a
        // plain zip, jar, apk, etc. without needing the rest of the file.
        var bytes = BuildZipWithFirstEntry("[Content_Types].xml", "<Types/>"u8.ToArray());

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("docx"));
        Assert.DoesNotContain(result, r => r.Extensions.Contains("jar"));
        Assert.DoesNotContain(result, r => r.Extensions.Contains("apk"));
    }

    [Fact]
    public void Spy_ZipFirstEntryIsManifest_NarrowsToJar()
    {
        var bytes = BuildZipWithFirstEntry("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n"u8.ToArray());

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("jar"));
        Assert.DoesNotContain(result, r => r.Extensions.Contains("zip"));
        Assert.DoesNotContain(result, r => r.Extensions.Contains("docx"));
    }

    [Fact]
    public void Spy_ZipFirstEntryIsAndroidManifest_NarrowsToApk()
    {
        var bytes = BuildZipWithFirstEntry("AndroidManifest.xml", [0x03, 0x00, 0x08, 0x00]);

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("apk"));
        Assert.DoesNotContain(result, r => r.Extensions.Contains("zip"));
    }

    [Theory]
    [InlineData("application/vnd.oasis.opendocument.text", "odt")]
    [InlineData("application/vnd.oasis.opendocument.text-template", "ott")]
    [InlineData("application/vnd.oasis.opendocument.spreadsheet", "ods")]
    [InlineData("application/vnd.oasis.opendocument.spreadsheet-template", "ots")]
    [InlineData("application/vnd.oasis.opendocument.presentation", "odp")]
    [InlineData("application/vnd.oasis.opendocument.presentation-template", "otp")]
    public void Spy_ZipStoredMimetypeEntryIsOpenDocumentSubtype_NarrowsToExactlyThatExtension(string mimeType, string expectedExtension)
    {
        // file_signatures.csv's own "OpenDocument template" row used to tie
        // odt/odp/ott together as one row's Extensions list, as if they were
        // interchangeable aliases the way jpg/jpeg genuinely are - and ods/
        // ots/otp had no row at all. The supplemental file's merge (docs/adr/
        // 0010) replaces that one bundled row with six independent,
        // single-extension ones, so each real subtype narrows to exactly
        // itself now - not a three-way (or, for ods/ots/otp, a zero-way) tie.
        var bytes = BuildZipWithFirstEntry("mimetype", Encoding.ASCII.GetBytes(mimeType), stored: true);

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Equal([expectedExtension], single.Extensions);
    }

    [Fact]
    public void Spy_ZipStoredMimetypeEntryIsUnrecognizedOpenDocumentSubtype_FallsBackToTiedMatches()
    {
        // "chart" isn't one of the ODF sub-formats this table knows how to map
        // to an extension - guessing "odt" just because the string starts with
        // "application/vnd.oasis.opendocument" (the old behavior) would be
        // wrong here, so this must fall back rather than guess.
        var bytes = BuildZipWithFirstEntry("mimetype", "application/vnd.oasis.opendocument.chart"u8.ToArray(), stored: true);

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("zip"));
        Assert.Contains(result, r => r.Extensions.Contains("odt"));
    }

    [Fact]
    public void Spy_ZipMimetypeEntryTruncatedBeforeItsContent_FallsBackToTiedMatches()
    {
        // Only the local file header and the entry name made it into the buffer -
        // the "mimetype" content itself (and therefore what it says) is missing.
        // Guessing here would be wrong more often than not, so this must fall back
        // to the same tied results a generic zip would produce, not throw or guess.
        var full = BuildZipWithFirstEntry("mimetype", "application/vnd.oasis.opendocument.text"u8.ToArray(), stored: true);
        var truncated = full[..38]; // header (30) + "mimetype".Length (8), no content bytes

        var result = _sut.Spy(truncated);

        Assert.Contains(result, r => r.Extensions.Contains("zip"));
        Assert.Contains(result, r => r.Extensions.Contains("odt"));
    }

    [Fact]
    public void Spy_ZipFirstEntryIsUnrecognizedName_ReturnsAllTiedMatchesUnnarrowed()
    {
        var bytes = BuildZipWithFirstEntry("readme.txt", "hi"u8.ToArray());

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("zip"));
        Assert.Contains(result, r => r.Extensions.Contains("docx"));
        Assert.Contains(result, r => r.Extensions.Contains("jar"));
    }

    [Fact]
    public void Spy_ZipFirstEntryIsEmptyDirectoryPlaceholder_SkipsItAndNarrowsOnTheNextEntry()
    {
        // Real OOXML writers commonly emit a "_rels/" directory entry before
        // "[Content_Types].xml" (see docs/adr/0009) - a zero-length "name/"
        // entry like this carries no information of its own, so it shouldn't
        // block narrowing on whatever comes right after it.
        var bytes = BuildZipWithEntries(
            ("_rels/", [], true),
            ("[Content_Types].xml", "<Types/>"u8.ToArray(), false));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("docx"));
        Assert.DoesNotContain(result, r => r.Extensions.Contains("jar"));
    }

    [Fact]
    public void Spy_ZipFirstEntryIsMetaInfDirectoryPlaceholder_SkipsItAndNarrowsToJar()
    {
        // The real jar tool writes "META-INF/" as its own directory entry
        // before "META-INF/MANIFEST.MF" - see docs/adr/0009.
        var bytes = BuildZipWithEntries(
            ("META-INF/", [], true),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n"u8.ToArray(), false));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("jar"));
        Assert.DoesNotContain(result, r => r.Extensions.Contains("zip"));
    }

    [Fact]
    public void Spy_ZipMultipleLeadingDirectoryPlaceholders_SkipsAllOfThemAndNarrows()
    {
        var bytes = BuildZipWithEntries(
            ("a/", [], true),
            ("a/b/", [], true),
            ("[Content_Types].xml", "<Types/>"u8.ToArray(), false));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("docx"));
    }

    [Fact]
    public void Spy_ZipDirectoryNamedEntryWithNonZeroSize_IsNotTreatedAsAPlaceholder()
    {
        // A "name/"-suffixed entry is only skipped as a directory placeholder
        // when it's actually empty - this isn't a real ZIP shape, but nothing
        // should skip past an entry that carries content just because its name
        // happens to end in "/".
        var bytes = BuildZipWithEntries(("weird/", "not actually empty"u8.ToArray(), false));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("zip"));
        Assert.Contains(result, r => r.Extensions.Contains("docx"));
    }

    [Fact]
    public void Spy_ZipEntryAfterLeadingDirectoryPlaceholderIsUnrecognized_FallsBackToTiedMatches()
    {
        // Mirrors a real docx from Word, whose actual first non-directory entry
        // is "_rels/.rels" - not one ZipContainerSniffer recognizes - so
        // narrowing correctly gives up rather than scanning deeper into the
        // archive for a name it does recognize (see docs/adr/0009).
        var bytes = BuildZipWithEntries(
            ("_rels/", [], true),
            ("readme.txt", "hi"u8.ToArray(), false));

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("zip"));
        Assert.Contains(result, r => r.Extensions.Contains("docx"));
        Assert.Contains(result, r => r.Extensions.Contains("jar"));
    }

    [Fact]
    public void Spy_ZipTruncatedRightAfterLeadingDirectoryPlaceholder_FallsBackToTiedMatches()
    {
        // Only the directory placeholder's own local file header and name made
        // it into the buffer; nothing at all is known about what comes next.
        // This must fall back gracefully, not throw or guess.
        var full = BuildZipWithEntries(
            ("_rels/", [], true),
            ("[Content_Types].xml", "<Types/>"u8.ToArray(), false));
        var truncated = full[..36]; // header (30) + "_rels/".Length (6), nothing more

        var result = _sut.Spy(truncated);

        Assert.Contains(result, r => r.Extensions.Contains("zip"));
        Assert.Contains(result, r => r.Extensions.Contains("docx"));
    }

    [Theory]
    [InlineData("WordDocument", "doc", "dot")]
    [InlineData("Workbook", "xls", "xla")]
    [InlineData("Book", "xls", "xla")]
    [InlineData("PowerPoint Document", "ppt", "pps")]
    public void Spy_Ole2FileWithRecognizedDirectoryEntryName_NarrowsToItsOwnFamily(string entryName, string primaryExtension, string siblingExtension)
    {
        var bytes = BuildOle2FileWithDirectoryEntryName(entryName);

        var result = _sut.Spy(bytes);

        var single = Assert.Single(result);
        Assert.Contains(primaryExtension, single.Extensions);
        Assert.Contains(siblingExtension, single.Extensions);
        Assert.Equal(primaryExtension, single.PrimaryExtension);
    }

    [Fact]
    public void Spy_Ole2FileWithUnrecognizedDirectoryEntryName_FallsBackToTiedMatches()
    {
        // A real CFB file (e.g. an MSI or a Visio drawing) whose directory
        // entry names don't include any of the ones Ole2ContainerSniffer
        // recognizes must fall back to the full tied set, not guess.
        var bytes = BuildOle2FileWithDirectoryEntryName("SomeUnrelatedStream");

        var result = _sut.Spy(bytes);

        Assert.True(result.Count > 1, "expected the full, unnarrowed OLE2-family tie");
        Assert.Contains(result, r => r.Extensions.Contains("doc"));
    }

    [Fact]
    public void Spy_Ole2HeaderWithoutDirectorySectorBytesSupplied_FallsBackToTiedMatches()
    {
        // Only the 512-byte header made it into the buffer - the directory
        // sector its own FAT chain points to is entirely out of reach. This
        // must fall back gracefully, not throw or guess.
        var full = BuildOle2FileWithDirectoryEntryName("WordDocument");
        var headerOnly = full[..512];

        var result = _sut.Spy(headerOnly);

        Assert.True(result.Count > 1, "expected the full, unnarrowed OLE2-family tie");
        Assert.Contains(result, r => r.Extensions.Contains("doc"));
    }

    [Fact]
    public void Spy_OggWithTheoraCodec_PrimaryMimeTypeIsVideoOgg()
    {
        // Without content sniffing this would default to audio/ogg, since "ogg"
        // and "oga" both resolve there while "ogv" (Theora's usual extension)
        // resolves to video/ogg alone - alias count alone picks the wrong one
        // for an actual video file.
        var bytes = BuildOggPage([0x80, 0x74, 0x68, 0x65, 0x6F, 0x72, 0x61]); // "\x80theora"

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "video/ogg");
    }

    [Fact]
    public void Spy_OggWithKateCodec_PrimaryMimeTypeIsApplicationOgg()
    {
        var bytes = BuildOggPage([0x80, 0x6B, 0x61, 0x74, 0x65, 0x00, 0x00, 0x00, 0x00]); // "\x80kate\0\0\0\0"

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "application/ogg");
    }

    [Fact]
    public void Spy_OggWithVorbisCodec_PrimaryMimeTypeIsAudioOgg()
    {
        var bytes = BuildOggPage([0x01, 0x76, 0x6F, 0x72, 0x62, 0x69, 0x73]); // "\x01vorbis"

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "audio/ogg");
    }

    [Fact]
    public void Spy_OggTruncatedBeforeCodecIdentifier_FallsBackToAliasCountDefault()
    {
        // Only the 8-byte "OggS" signature itself is available - not enough to
        // reach the codec identification packet at offset 28. This must fall
        // back to the existing alias-count answer rather than throw or guess.
        var bytes = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00 };

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "audio/ogg");
    }

    [Fact]
    public void Spy_OggWithUnrecognizedCodec_FallsBackToAliasCountDefault()
    {
        var bytes = BuildOggPage("unknown!"u8.ToArray());

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "audio/ogg");
    }

    [Theory]
    [InlineData("Windows Media Video")]
    [InlineData("VC-1 Advanced Profile")]
    [InlineData("wmv2")]
    public void Spy_AsfWithVideoCodecMarker_PrimaryMimeTypeIsWmv(string marker)
    {
        // Without content sniffing this would default to video/x-ms-asf for
        // every ASF/WMA/WMV file, audio-only WMA included - alias count can't
        // tell them apart since each extension resolves to exactly one mime
        // type of its own.
        var bytes = BuildAsfFile(marker);

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "video/x-ms-wmv");
    }

    [Fact]
    public void Spy_AsfWithAudioCodecMarker_PrimaryMimeTypeIsWma()
    {
        var bytes = BuildAsfFile("Windows Media Audio");

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "audio/x-ms-wma");
    }

    [Fact]
    public void Spy_AsfWithBothAudioAndVideoMarkers_PrimaryMimeTypeIsWmv()
    {
        // A real WMV typically carries both a video and an audio stream, so its
        // codec list contains both marker strings. Video wins, matching Tika's
        // WMV magic having a higher priority than its WMA magic.
        var bytes = BuildAsfFile("Windows Media Audio and Windows Media Video");

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "video/x-ms-wmv");
    }

    [Fact]
    public void Spy_AsfWithNoRecognizedCodecMarker_FallsBackToAliasCountDefault()
    {
        var bytes = BuildAsfFile("nothing recognizable here");

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "video/x-ms-asf");
    }

    [Fact]
    public void Spy_AsfCodecMarkerBeyondSearchWindow_FallsBackToAliasCountDefault()
    {
        // The marker is real, but it sits past AsfContainerSniffer's 8192-byte
        // search window, so it must not be found - this isn't allowed to scan
        // the whole file.
        var padding = new byte[8192];
        var bytes = BuildAsfFile("Windows Media Audio", padding);

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.PrimaryMimeType == "video/x-ms-asf");
    }

    [Fact]
    public void Spy_Stream_MatchesSameAsEquivalentBytes()
    {
        using var stream = new MemoryStream("%PDF-1.7 rest of file"u8.ToArray());

        var result = _sut.Spy(stream);

        Assert.Contains(result, r => r.Extensions.Contains("pdf"));
    }

    [Fact]
    public void Spy_SeekableStream_RestoresOriginalPosition()
    {
        using var stream = new MemoryStream("%PDF-1.7 rest of file"u8.ToArray());
        stream.Position = 3;

        _sut.Spy(stream);

        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public void Spy_StreamThatOnlyReadsAFewBytesAtATime_StillAssemblesFullHeader()
    {
        // Some streams (e.g. network streams) can return fewer bytes than
        // requested even when more is available; the read loop must keep
        // pulling until it has enough or the stream is exhausted.
        using var stream = new TrickleStream("%PDF-1.7 rest of file"u8.ToArray(), maxBytesPerRead: 2);

        var result = _sut.Spy(stream);

        Assert.Contains(result, r => r.Extensions.Contains("pdf"));
    }

    [Fact]
    public void Spy_ShortStream_ReturnsMatchesUsingWhateverWasAvailable()
    {
        using var stream = new MemoryStream("%PDF-1.7"u8.ToArray());

        var result = _sut.Spy(stream);

        Assert.Contains(result, r => r.Extensions.Contains("pdf"));
    }

    [Fact]
    public void Spy_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Spy((Stream)null!));
    }

    [Fact]
    public async Task SpyAsync_Stream_MatchesSameAsEquivalentBytes()
    {
        using var stream = new MemoryStream("%PDF-1.7 rest of file"u8.ToArray());

        var result = await _sut.SpyAsync(stream, TestContext.Current.CancellationToken);

        Assert.Contains(result, r => r.Extensions.Contains("pdf"));
    }

    [Fact]
    public async Task SpyAsync_SeekableStream_RestoresOriginalPosition()
    {
        using var stream = new MemoryStream("%PDF-1.7 rest of file"u8.ToArray());
        stream.Position = 3;

        await _sut.SpyAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public async Task SpyAsync_StreamThatOnlyReadsAFewBytesAtATime_StillAssemblesFullHeader()
    {
        using var stream = new TrickleStream("%PDF-1.7 rest of file"u8.ToArray(), maxBytesPerRead: 2);

        var result = await _sut.SpyAsync(stream, TestContext.Current.CancellationToken);

        Assert.Contains(result, r => r.Extensions.Contains("pdf"));
    }

    [Fact]
    public async Task SpyAsync_ShortStream_ReturnsMatchesUsingWhateverWasAvailable()
    {
        using var stream = new MemoryStream("%PDF-1.7"u8.ToArray());

        var result = await _sut.SpyAsync(stream, TestContext.Current.CancellationToken);

        Assert.Contains(result, r => r.Extensions.Contains("pdf"));
    }

    [Fact]
    public void SpyAsync_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = _sut.SpyAsync(null!, TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task SpyAsync_CancelledToken_Throws()
    {
        using var stream = new MemoryStream("%PDF-1.7 rest of file"u8.ToArray());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sut.SpyAsync(stream, cts.Token));
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

    private static byte[] BuildZipWithFirstEntry(string name, byte[] content, bool stored = false)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var header = new byte[30 + nameBytes.Length + content.Length];

        header[0] = 0x50;
        header[1] = 0x4B;
        header[2] = 0x03;
        header[3] = 0x04;
        // Compression method at offset 8-9: 0 = stored, 8 = deflated.
        header[8] = (byte)(stored ? 0 : 8);
        header[9] = 0;
        // Uncompressed size at offset 22-25 (little-endian).
        BitConverter.GetBytes((uint)content.Length).CopyTo(header, 22);
        // Filename length at offset 26-27 (little-endian); extra field length (28-29) left at 0.
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(header, 26);

        nameBytes.CopyTo(header, 30);
        content.CopyTo(header, 30 + nameBytes.Length);

        return header;
    }

    private static byte[] BuildZipWithEntries(params (string Name, byte[] Content, bool Stored)[] entries)
    {
        using var stream = new MemoryStream();

        foreach (var (name, content, stored) in entries)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name);
            var header = new byte[30 + nameBytes.Length + content.Length];

            header[0] = 0x50;
            header[1] = 0x4B;
            header[2] = 0x03;
            header[3] = 0x04;
            // Compression method at offset 8-9: 0 = stored, 8 = deflated.
            header[8] = (byte)(stored ? 0 : 8);
            header[9] = 0;
            // Compressed size at offset 18-21 and uncompressed size at 22-25
            // (little-endian) - both equal content.Length since these entries
            // aren't actually deflated, just copied in as-is.
            BitConverter.GetBytes((uint)content.Length).CopyTo(header, 18);
            BitConverter.GetBytes((uint)content.Length).CopyTo(header, 22);
            // Filename length at offset 26-27 (little-endian); extra field length (28-29) left at 0.
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(header, 26);

            nameBytes.CopyTo(header, 30);
            content.CopyTo(header, 30 + nameBytes.Length);

            stream.Write(header);
        }

        return stream.ToArray();
    }

    // A minimal but structurally valid CFB/OLE2 file: a 512-byte header (512-byte
    // sectors, one FAT sector at sector 0, directory stream starting at sector 1),
    // a FAT sector whose only entry that matters marks the directory stream as
    // one sector long, and a directory sector whose first 128-byte entry carries
    // the given name - enough for Ole2ContainerSniffer to reach it, nothing more.
    private static byte[] BuildOle2FileWithDirectoryEntryName(string entryName)
    {
        const int sectorSize = 512;
        var bytes = new byte[512 + (sectorSize * 2)]; // header + FAT sector + directory sector

        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)9).CopyTo(bytes, 30); // sector shift -> 512-byte sectors
        BitConverter.GetBytes((uint)1).CopyTo(bytes, 48); // first directory sector location
        BitConverter.GetBytes((uint)0).CopyTo(bytes, 76); // DIFAT[0] -> FAT sector is sector 0

        // FAT sector (file bytes 512-1023): FAT[1] = ENDOFCHAIN, so the directory
        // stream starting at sector 1 is exactly one sector long.
        BitConverter.GetBytes(0xFFFFFFFE).CopyTo(bytes, 512 + (1 * 4));

        // Directory sector (file bytes 1024-1535): one directory entry, name plus
        // its length field (which counts the trailing null character MS-CFB requires).
        var nameBytes = Encoding.Unicode.GetBytes(entryName);
        nameBytes.CopyTo(bytes, 1024);
        BitConverter.GetBytes((ushort)(nameBytes.Length + 2)).CopyTo(bytes, 1024 + 64);

        return bytes;
    }

    private static byte[] BuildOggPage(byte[] codecIdentifier)
    {
        var page = new byte[28 + codecIdentifier.Length];

        "OggS"u8.CopyTo(page);
        page[4] = 0x00; // stream structure version
        page[5] = 0x02; // header type flags: beginning of stream
        // Granule position (6-13), serial number (14-17), sequence number
        // (18-21), and checksum (22-25) are left at 0 - unused by the sniffer.
        page[26] = 0x01; // page_segments: one segment
        page[27] = (byte)codecIdentifier.Length; // that segment's length

        codecIdentifier.CopyTo(page, 28);

        return page;
    }

    private static byte[] BuildAsfFile(string codecNameMarker, byte[]? leadingPadding = null)
    {
        var header = new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11 };
        var padding = leadingPadding ?? [];
        var markerBytes = Encoding.Unicode.GetBytes(codecNameMarker);

        var bytes = new byte[header.Length + padding.Length + markerBytes.Length];
        header.CopyTo(bytes, 0);
        padding.CopyTo(bytes, header.Length);
        markerBytes.CopyTo(bytes, header.Length + padding.Length);

        return bytes;
    }
}
