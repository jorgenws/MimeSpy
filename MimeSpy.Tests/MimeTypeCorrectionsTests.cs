using System.Text;

namespace MimeSpy.Tests;

// mimespy-mime-types.xml rewrites PRONOM's MIME types by PUID after loading, leaving
// PRONOM's own file untouched (docs/adr/0029). Each entry states the value it was
// written against, so a PRONOM update that changes that value fails loading instead of
// being silently overwritten.
public class MimeTypeCorrectionsTests
{
    [Fact]
    public void Apply_PronomValueMatchesTheEntry_ReplacesIt()
    {
        List<PronomFileFormat> formats = [Format("fmt/199", "application/mp4", "video/mp4")];

        PronomMimeTypeCorrections.Apply(formats, Corrections(
            """<Format Puid="fmt/199" PronomMimeTypes="application/mp4, video/mp4" MimeTypes="video/mp4, application/mp4" Reason="video first" />"""));

        Assert.Equal(["video/mp4", "application/mp4"], formats[0].MimeTypes);
    }

    [Fact]
    public void Apply_FormatPronomGivesNoType_IsFilledWithoutNeedingAReason()
    {
        List<PronomFileFormat> formats = [Format("fmt/484")];

        PronomMimeTypeCorrections.Apply(formats, Corrections(
            """<Format Puid="fmt/484" PronomMimeTypes="" MimeTypes="application/x-7z-compressed" />"""));

        Assert.Equal(["application/x-7z-compressed"], formats[0].MimeTypes);
    }

    [Fact]
    public void Apply_CorrectionWithNoMimeTypes_RemovesPronomsTypes()
    {
        List<PronomFileFormat> formats = [Format("fmt/1", "wrong/type")];

        PronomMimeTypeCorrections.Apply(formats, Corrections(
            """<Format Puid="fmt/1" PronomMimeTypes="wrong/type" MimeTypes="" Reason="no right type exists" />"""));

        Assert.Empty(formats[0].MimeTypes);
    }

    [Fact]
    public void Apply_LeavesFormatsWithoutAnEntryAlone()
    {
        List<PronomFileFormat> formats = [Format("fmt/199", "application/mp4", "video/mp4"), Format("fmt/2", "audio/x-wav")];

        PronomMimeTypeCorrections.Apply(formats, Corrections(
            """<Format Puid="fmt/199" PronomMimeTypes="application/mp4, video/mp4" MimeTypes="video/mp4, application/mp4" Reason="video first" />"""));

        Assert.Equal(["audio/x-wav"], formats[1].MimeTypes);
    }

    [Theory]
    [InlineData("application/mp4, video/mp4", "")]
    [InlineData("application/mp4", "application/mp4, video/mp4")]
    [InlineData("video/mp4, application/mp4", "application/mp4, video/mp4")]
    public void Apply_PronomValueNoLongerMatchesTheEntry_Throws(string pronomNow, string entryWrittenAgainst)
    {
        List<PronomFileFormat> formats = [Format("fmt/199", pronomNow.Split(", ", StringSplitOptions.RemoveEmptyEntries))];

        var error = Assert.Throws<InvalidOperationException>(() => PronomMimeTypeCorrections.Apply(formats, Corrections(
            $"""<Format Puid="fmt/199" PronomMimeTypes="{entryWrittenAgainst}" MimeTypes="video/mp4" Reason="video first" />""")));

        Assert.Contains("fmt/199", error.Message);
    }

    [Theory]
    [InlineData("""<Format Puid="fmt/404" PronomMimeTypes="" MimeTypes="a/b" />""", "fmt/404")]
    [InlineData("""<Format Puid="fmt/1" PronomMimeTypes="" MimeTypes="a/b" /><Format Puid="fmt/1" PronomMimeTypes="" MimeTypes="a/b" />""", "fmt/1")]
    [InlineData("""<Format Puid="fmt/1" MimeTypes="a/b" />""", "PronomMimeTypes")]
    [InlineData("""<Format Puid="fmt/1" PronomMimeTypes="" />""", "MimeTypes")]
    [InlineData("""<Format PronomMimeTypes="" MimeTypes="a/b" />""", "Puid")]
    [InlineData("""<Format Puid="fmt/2" PronomMimeTypes="audio/x-wav" MimeTypes="audio/wav" />""", "Reason")]
    public void Apply_MalformedEntry_ThrowsNamingTheProblem(string entries, string expectedInMessage)
    {
        List<PronomFileFormat> formats = [Format("fmt/1"), Format("fmt/2", "audio/x-wav")];

        var error = Assert.Throws<InvalidOperationException>(() => PronomMimeTypeCorrections.Apply(formats, Corrections(entries)));

        Assert.Contains(expectedInMessage, error.Message);
    }

    [Theory]
    [InlineData("fmt/199", "video/mp4")]
    [InlineData("fmt/357", "video/3gpp")]
    public void ShippedCorrections_PutTheConventionalTypeFirst(string puid, string expectedFirst)
    {
        var format = PronomSignatureIndex.FindFormatByPuid(puid);

        Assert.NotNull(format);
        Assert.Equal(expectedFirst, format.MimeTypes[0]);
    }

    private static PronomFileFormat Format(string puid, params string[] mimeTypes) =>
        new(1, puid, "Name", null, mimeTypes, [], [], []);

    private static MemoryStream Corrections(string entries) =>
        new(Encoding.UTF8.GetBytes($"<MimeTypeCorrections>{entries}</MimeTypeCorrections>"));
}
