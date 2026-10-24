using System.IO.Compression;

namespace MimeSpy.IntegrationTests;

/// <summary>
/// Runs <see cref="MimeSpy.Investigate(Stream)"/> against real container files grown
/// past the size where it stops reading the stream whole (docs/adr/0022), so the
/// ZIP/OLE2 readers have to work against the live stream instead of an in-memory
/// copy - and checks the answer is the same one <see cref="MimeSpy.Investigate(ReadOnlySpan{byte})"/>
/// gives for the same bytes, which never windows anything.
/// </summary>
public class LargeStreamTests
{
    // Past the 2 x 8 MiB point where Investigate(Stream) switches to windowed reads.
    private const int PaddingSize = 17 * 1024 * 1024;

    private readonly MimeSpy _sut = new();

    [Fact]
    public void Investigate_LargeDocxStream_NarrowsToWordSpecificPuid()
    {
        // A ZIP can't just be padded at either end (the central directory is at the
        // end, and its offsets are absolute), so the padding goes in as an extra
        // stored entry instead - PRONOM's docx signature only requires the entries
        // it names to be present, not that nothing else is.
        var bytes = WithStoredPaddingEntry(ReadFixture("sample.docx"));
        using var stream = new MemoryStream(bytes);

        var result = _sut.Investigate(stream);

        var single = Assert.Single(result);
        Assert.Equal("fmt/412", single.Puid);
        Assert.Equal(Puids(_sut.Investigate(bytes)), Puids(result));
    }

    [Fact]
    public void Investigate_LargeDocStream_NarrowsToWordSpecificPuids()
    {
        // OLE2 sectors are addressed from the header, so trailing bytes past the
        // last sector are never looked at - padding at the end leaves the file valid.
        byte[] bytes = [.. ReadFixture("sample.doc"), .. new byte[PaddingSize]];
        using var stream = new MemoryStream(bytes);

        var result = _sut.Investigate(stream);

        Assert.Equal(2, result.Count);
        Assert.All(result, f => Assert.Contains("doc", f.Extensions));
        Assert.Equal(Puids(_sut.Investigate(bytes)), Puids(result));
    }

    private static byte[] WithStoredPaddingEntry(byte[] zip)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var from = entry.Open();
                using var to = target.CreateEntry(entry.FullName).Open();
                from.CopyTo(to);
            }

            using var padding = target.CreateEntry("padding.bin", CompressionLevel.NoCompression).Open();
            padding.Write(new byte[PaddingSize]);
        }

        return output.ToArray();
    }

    private static IEnumerable<string> Puids(IReadOnlyList<Dossier> result) => result.Select(f => f.Puid).OrderBy(p => p);

    private static byte[] ReadFixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
