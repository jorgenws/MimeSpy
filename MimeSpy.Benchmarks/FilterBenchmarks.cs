using BenchmarkDotNet.Attributes;

namespace MimeSpy.Benchmarks;

/// <summary>
/// Compares an unfiltered <see cref="MimeSpy"/> against one scoped to
/// <c>MimeTypes = ["image/"]</c> via <see cref="MimeSpyOptions"/>, on both image
/// fixtures (where the filtered instance should still match) and non-image ones
/// (where it should narrow the catalog it even tries). Answers whether
/// <see cref="MimeSpyOptions"/> actually earns its complexity performance-wise.
/// </summary>
[MemoryDiagnoser]
public class FilterBenchmarks
{
    private static readonly MimeSpy Unfiltered = new();
    private static readonly MimeSpy ImagesOnly = new(new MimeSpyOptions { MimeTypes = ["image/"] });

    [ParamsSource(nameof(FixtureNames))]
    public string FixtureName { get; set; } = string.Empty;

    private byte[] _bytes = [];

    public static IEnumerable<string> FixtureNames =>
    [
        "sample.png",
        "sample.jpg",
        "sample.gif",
        "sample.bmp",
        "sample.ico",
        "sample.webp",
        "sample.pdf",   // non-image hit for MimeSpy, miss for the images-only filter
        "sample.docx",  // non-image, ZIP container
    ];

    [GlobalSetup]
    public void Setup() => _bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName));

    [Benchmark(Baseline = true)]
    public object Unfiltered_Investigate() => Unfiltered.Investigate(_bytes);

    [Benchmark]
    public object ImagesOnly_Investigate() => ImagesOnly.Investigate(_bytes);
}
