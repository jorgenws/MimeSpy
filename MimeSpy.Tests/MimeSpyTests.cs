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
    public void Spy_JpegHeader_PrimaryMimeTypeIsMostRepresentedAmongAliasExtensions()
    {
        // The FF D8 FF signature lists extensions jfif|jpe|jpeg|jpg. Three of those
        // (jpe, jpeg, jpg) resolve to image/jpeg and only one (jfif) resolves to
        // image/pjpeg, so image/jpeg should win as the primary mime type.
        var result = _sut.Spy(new byte[] { 0xFF, 0xD8, 0xFF, 0x00 });

        Assert.Contains(result, r => r.PrimaryMimeType == "image/jpeg");
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

    [Fact]
    public void Spy_ZipStoredMimetypeEntryIsOpenDocument_NarrowsToOdf()
    {
        var bytes = BuildZipWithFirstEntry("mimetype", "application/vnd.oasis.opendocument.text"u8.ToArray(), stored: true);

        var result = _sut.Spy(bytes);

        Assert.Contains(result, r => r.Extensions.Contains("odt"));
        Assert.DoesNotContain(result, r => r.Extensions.Contains("zip"));
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
}
