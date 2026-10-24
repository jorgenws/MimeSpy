using System.IO.Compression;
using BenchmarkDotNet.Attributes;

namespace MimeSpy.Benchmarks;

/// <summary>
/// Measures <see cref="MimeSpy.Investigate(Stream)"/> against a real
/// <see cref="FileStream"/>, next to the read-it-all-into-memory alternative a
/// caller would otherwise use (and that <c>Investigate(Stream)</c> itself did before
/// docs/adr/0022). The small files take the read-whole path; the 50 MB ones are past
/// its 16 MiB cut-off, so they take the windowed path - and, for docx/doc, refine the
/// ZIP/OLE2 container by reading the file in place. The interesting column for those
/// is Allocated: it should stay roughly flat for the stream, and track file size for
/// the baseline.
/// </summary>
[MemoryDiagnoser]
public class StreamBenchmarks
{
    private const int LargeFileSize = 50 * 1024 * 1024;

    private static readonly MimeSpy Spy = new();

    [ParamsSource(nameof(FileNames))]
    public string FileName { get; set; } = string.Empty;

    private string _path = string.Empty;

    public static IEnumerable<string> FileNames =>
    [
        "sample.pdf",   // 34 KB - read whole, end-anchored signature
        "sample.docx",  // 13 KB - read whole, ZIP container
        "sample.doc",   // read whole, OLE2 container
        "large.pdf",    // 50 MB - windowed, trailer seen through the tail window
        "large.docx",   // 50 MB - windowed, ZIP refined in place
        "large.doc",    // 50 MB - windowed, OLE2 refined in place
    ];

    [GlobalSetup]
    public void Setup()
    {
        if (!FileName.StartsWith("large.", StringComparison.Ordinal))
        {
            _path = FixturePath(FileName);
            return;
        }

        // The large files are generated from the real fixtures rather than committed:
        // padded in a way that keeps each one valid for its format (see each helper).
        var extension = Path.GetExtension(FileName);
        var original = File.ReadAllBytes(FixturePath("sample" + extension));
        var bytes = extension switch
        {
            ".pdf" => PaddedInTheMiddle(original),
            ".docx" => WithStoredPaddingEntry(original),
            ".doc" => PaddedAtTheEnd(original),
            _ => throw new InvalidOperationException($"No large variant for {FileName}"),
        };

        _path = Path.Combine(Path.GetTempPath(), $"MimeSpy.Benchmarks.{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(_path, bytes);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_path.StartsWith(Path.GetTempPath(), StringComparison.Ordinal))
        {
            File.Delete(_path);
        }
    }

    [Benchmark(Baseline = true)]
    public object ReadAllBytes_ThenInvestigate() => Spy.Investigate(File.ReadAllBytes(_path));

    [Benchmark]
    public object Investigate_FileStream()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Spy.Investigate(stream);
    }

    // PDF's BOF header and EOF trailer both stay where they were; only the bytes
    // between them grow.
    private static byte[] PaddedInTheMiddle(byte[] original)
    {
        var half = original.Length / 2;
        return [.. original.AsSpan(0, half), .. new byte[LargeFileSize], .. original.AsSpan(half)];
    }

    // OLE2 sectors are addressed from the header, so bytes past the last one are
    // never looked at.
    private static byte[] PaddedAtTheEnd(byte[] original) => [.. original, .. new byte[LargeFileSize]];

    // A ZIP can't be padded at either end (its central directory is at the end, with
    // absolute offsets), so the padding goes in as an extra stored entry instead.
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
            padding.Write(new byte[LargeFileSize]);
        }

        return output.ToArray();
    }

    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
