using MimeDetective;
using MimeDetective.Definitions;
using MimeDetective.Definitions.Licensing;
using MimeDetective.Engine;

namespace MimeSpy.Benchmarks;

/// <summary>
/// Not a BenchmarkDotNet benchmark - a one-shot console report comparing what each
/// library actually identifies the committed <c>MimeSpy.IntegrationTests/Fixtures</c>
/// samples as, since raw throughput only matters if both sides are answering the
/// same question correctly. Run with the `accuracy` command-line argument.
/// </summary>
internal static class AccuracyReport
{
    public static void Run()
    {
        var spy = new MimeSpy();

        var defaultInspector = new ContentInspectorBuilder
        {
            Definitions = DefaultDefinitions.All(),
        }.Build();

        var condensedInspector = new ContentInspectorBuilder
        {
            Definitions = new CondensedBuilder { UsageType = UsageType.PersonalNonCommercial }.Build(),
        }.Build();

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var files = Directory.GetFiles(fixturesDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"{"File",-14} {"MimeSpy (Investigate)",-35} {"MimeSpy (Spy)",-35} {"Mime-Detective Default",-30} {"Mime-Detective Condensed",-30}");
        Console.WriteLine(new string('-', 150));

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var name = Path.GetFileName(file);

            var investigateResult = DescribeMimeSpy(spy.Investigate(bytes));
            var spyResult = DescribeMimeSpy(spy.Spy(bytes));
            var defaultResult = Describe(defaultInspector.Inspect(bytes));
            var condensedResult = Describe(condensedInspector.Inspect(bytes));

            Console.WriteLine($"{name,-14} {Truncate(investigateResult, 35),-35} {Truncate(spyResult, 35),-35} {Truncate(defaultResult, 30),-30} {Truncate(condensedResult, 30),-30}");
        }
    }

    private static string DescribeMimeSpy(IReadOnlyCollection<PronomFormat> matches) =>
        matches.Count == 0
            ? "(no match)"
            : string.Join(", ", matches.Select(f => f.MimeType ?? $"({f.Puid}, no mime type)"));

    private static string Describe(IReadOnlyCollection<DefinitionMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "(no match)";
        }

        var best = matches.OrderByDescending(m => m.Points).First();
        return best.Definition.File.MimeType ?? "(no mime type)";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
