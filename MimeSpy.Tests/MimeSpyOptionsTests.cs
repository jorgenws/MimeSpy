namespace MimeSpy.Tests;

public class MimeSpyOptionsTests
{
    private static readonly byte[] PngBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.png"));
    private static readonly byte[] PdfBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.pdf"));

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
    public void NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MimeSpy(null!));
    }
}
