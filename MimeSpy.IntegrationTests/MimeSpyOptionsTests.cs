namespace MimeSpy.IntegrationTests;

/// <summary>
/// <see cref="MimeSpyOptions"/> against real files, where it meets PRONOM's priority data
/// (DNG over TIFF) and the container check (docx, xlsx and epub are only reached by
/// opening a ZIP).
/// </summary>
public class MimeSpyOptionsTests
{
    [Fact]
    public void ExcludingDng_LeavesTiffWithNoDngVariants()
    {
        var sut = new MimeSpy(new MimeSpyOptions { MimeTypes = ["image/"], ExcludedMimeTypes = ["image/dng"] });
        var bytes = ReadFixture("sample.tiff");

        var spied = sut.Spy(bytes);

        Assert.Equal("fmt/353", spied[0].Puid);
        Assert.DoesNotContain(spied, f => f.Name.Contains("Digital Negative", StringComparison.Ordinal));
        Assert.Equal("fmt/353", Assert.Single(sut.Investigate(bytes)).Puid);
    }

    [Theory]
    [InlineData(new[] { "application/vnd.openxmlformats" }, new string[0], "sample.docx", "fmt/412")]
    [InlineData(new[] { "application/vnd.openxmlformats" }, new string[0], "sample.xlsx", "fmt/214")]
    [InlineData(new string[0], new[] { "application/zip" }, "sample.docx", "fmt/412")]
    [InlineData(new string[0], new[] { "application/zip" }, "sample.epub", "fmt/483")]
    [InlineData(new[] { "application/zip" }, new string[0], "sample.xlsx", "x-fmt/263")]
    [InlineData(new string[0], new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml" }, "sample.docx", "x-fmt/263")]
    [InlineData(new[] { "application/vnd.ms-excel" }, new string[0], "sample.xls", "fmt/61")]
    public void Investigate_ContainerFormat_IsFoundOrFallsBackByTheFilter(string[] mimeTypes, string[] excludedMimeTypes, string fileName, string expectedPuid)
    {
        // Reaching docx/xlsx/epub/xls means matching the generic ZIP/OLE2 format and opening
        // the container, even when the filter leaves that generic format out - and when
        // the specific format is the one left out, the answer falls back to the container.
        var sut = new MimeSpy(new MimeSpyOptions { MimeTypes = mimeTypes, ExcludedMimeTypes = excludedMimeTypes });

        var result = sut.Investigate(ReadFixture(fileName));

        Assert.Equal(expectedPuid, Assert.Single(result).Puid);
    }

    [Fact]
    public void ExcludingZip_LeavesAPlainZipUnidentified()
    {
        var sut = new MimeSpy(new MimeSpyOptions { ExcludedMimeTypes = ["application/zip"] });

        Assert.Empty(sut.Investigate(ReadFixture("sample.zip")));
    }

    private static byte[] ReadFixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
