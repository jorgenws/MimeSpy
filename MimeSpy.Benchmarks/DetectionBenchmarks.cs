using BenchmarkDotNet.Attributes;
using MimeDetective;
using MimeDetective.Definitions;
using MimeDetective.Definitions.Licensing;

namespace MimeSpy.Benchmarks;

/// <summary>
/// Compares both of MimeSpy's full-buffer detection methods against Mime-Detective's
/// equivalent <c>ContentInspector.Inspect(byte[])</c>, using both of Mime-Detective's
/// publicly available definition packs (its bundled "Default" set and the larger
/// "Condensed" add-on package). Both libraries build their one-time lookup structures
/// ahead of the timed region, per each library's own guidance.
///
/// <see cref="MimeSpy.Investigate(ReadOnlySpan{byte})"/> and Mime-Detective's
/// <c>Inspect</c> aren't quite doing the same amount of work: <c>Investigate</c> also
/// runs PRONOM's container-signature engine (opening the ZIP/OLE2 archive to
/// disambiguate docx/xlsx/ppt/etc. from a plain zip/OLE2 match) and evaluates
/// end-of-file-anchored signatures for real - neither of which Mime-Detective's flat
/// prefix/substring matching does at all (see its own <c>Storage.Signature</c> type -
/// no EOF-anchored concept exists). <see cref="MimeSpy.Spy(ReadOnlySpan{byte})"/> skips
/// both of those (docs/adr/0016, 0017), so it's the closer apples-to-apples comparison
/// of "guess from raw bytes, no real parsing" - <c>Investigate</c> is kept alongside it
/// to show what the extra correctness actually costs.
/// </summary>
[MemoryDiagnoser]
public class DetectionBenchmarks
{
    private static readonly MimeSpy Spy = new();

    private static readonly IContentInspector DefaultInspector = new ContentInspectorBuilder
    {
        Definitions = DefaultDefinitions.All(),
    }.Build();

    private static readonly IContentInspector CondensedInspector = new ContentInspectorBuilder
    {
        Definitions = new CondensedBuilder { UsageType = UsageType.PersonalNonCommercial }.Build(),
    }.Build();

    [ParamsSource(nameof(FixtureNames))]
    public string FixtureName { get; set; } = string.Empty;

    private byte[] _bytes = [];

    public static IEnumerable<string> FixtureNames =>
    [
        "sample.zip",    // 340 B - tiny container format
        "sample.jpg",    // 572 B - tiny, end-anchored signature
        "sample.mp3",    // 1.7 KB
        "sample.docx",   // 13 KB - ZIP container disambiguation
        "sample.pdf",    // 34 KB - end-anchored signature
        "sample.ppt",    // 608 KB - OLE2 container disambiguation
        "sample.heic",   // 1.6 MB - largest fixture
    ];

    [GlobalSetup]
    public void Setup() => _bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName));

    [Benchmark(Baseline = true)]
    public object MimeSpy_Investigate() => Spy.Investigate(_bytes);

    [Benchmark]
    public object MimeSpy_Spy() => Spy.Spy(_bytes);

    [Benchmark]
    public object MimeDetective_Default() => DefaultInspector.Inspect(_bytes);

    [Benchmark]
    public object MimeDetective_Condensed() => CondensedInspector.Inspect(_bytes);
}
