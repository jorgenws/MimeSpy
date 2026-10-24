namespace MimeSpy.Tests;

public class MimeSpyOptionsTests
{
    private static readonly byte[] PngBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.png"));
    private static readonly byte[] PdfBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.pdf"));
    private static readonly byte[] JpgBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.jpg"));

    // PRONOM gives RTF two MIME types, "application/rtf, text/rtf".
    private static readonly byte[] RtfBytes = @"{\rtf1\ansi\deff0 Hello}"u8.ToArray();

    [Fact]
    public void ImagesOnlyFilter_StillMatchesAnImage()
    {
        var sut = new MimeSpy(new MimeSpyOptions { MimeTypes = ["image/"] });

        var result = sut.Investigate(PngBytes);

        Assert.Contains(result, f => f.MimeType == "image/png");
    }

    [Fact]
    public void ImagesOnlyFilter_ExcludesANonImageFormat()
    {
        var sut = new MimeSpy(new MimeSpyOptions { MimeTypes = ["image/"] });

        var result = sut.Investigate(PdfBytes);

        Assert.Empty(result);
    }

    [Fact]
    public void EmptyMimeTypes_BehavesLikeUnfiltered()
    {
        var filtered = new MimeSpy(new MimeSpyOptions());
        var unfiltered = new MimeSpy();

        var result = filtered.Investigate(PdfBytes);

        Assert.Equal(unfiltered.Investigate(PdfBytes), result);
    }

    [Fact]
    public void ExactMimeTypePattern_MatchesOnlyThatType()
    {
        var sut = new MimeSpy(new MimeSpyOptions { MimeTypes = ["application/pdf"] });

        Assert.Contains(sut.Investigate(PdfBytes), f => f.MimeType == "application/pdf");
        Assert.Empty(sut.Investigate(PngBytes));
    }

    [Fact]
    public void Filter_MatchesAFormatByAnyOfItsMimeTypes_NotJustTheFirst()
    {
        var sut = new MimeSpy(new MimeSpyOptions { MimeTypes = ["text/"] });

        var result = sut.Spy(RtfBytes);

        Assert.Contains(result, f => f.MimeTypes.Contains("text/rtf"));
    }

    [Fact]
    public void ExcludedMimeTypes_LeavesOutAMatchingFormat_AndKeepsTheRest()
    {
        var sut = new MimeSpy(new MimeSpyOptions { ExcludedMimeTypes = ["image/png"] });

        Assert.Empty(sut.Investigate(PngBytes));
        Assert.Equal(new MimeSpy().Investigate(PdfBytes), sut.Investigate(PdfBytes));
    }

    [Fact]
    public void ExcludedMimeTypes_WinsOverMimeTypes()
    {
        var sut = new MimeSpy(new MimeSpyOptions { MimeTypes = ["image/"], ExcludedMimeTypes = ["image/png"] });

        Assert.Empty(sut.Investigate(PngBytes));
        Assert.Contains(sut.Investigate(JpgBytes), f => f.MimeType == "image/jpeg");
    }

    [Fact]
    public void ExcludedMimeTypes_LeavesOutAFormatIfAnyOfItsMimeTypesMatches()
    {
        // RTF lists "application/rtf, text/rtf"; excluding only the second still drops it.
        var sut = new MimeSpy(new MimeSpyOptions { ExcludedMimeTypes = ["text/rtf"] });

        Assert.Empty(sut.Spy(RtfBytes));
    }

    [Fact]
    public void NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MimeSpy(null!));
    }
}
