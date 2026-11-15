namespace MimeSpy.Tests;

// The automaton has to report exactly what a naive search for every pattern at every
// position would - including patterns that end inside a longer one.
public class PronomAhoCorasickTests
{
    [Theory]
    [InlineData("ushers", "he@3 she@3 hers@5")]
    [InlineData("hishers", "his@2 he@4 she@4 hers@6")]
    [InlineData("hhhe", "he@3")]
    [InlineData("xyz", "")]
    public void Scan_ClassicPatternSet_ReportsEveryOccurrence(string input, string expected)
    {
        string[] patterns = ["he", "she", "his", "hers"];
        var automaton = new PronomAhoCorasick([.. patterns.Select(p => p.Select(c => (byte)c).ToArray())]);

        var hits = Scan(automaton, [.. input.Select(c => (byte)c)]).Select(h => $"{patterns[h.Pattern]}@{h.End}");

        Assert.Equal(expected, string.Join(" ", hits));
    }

    [Fact]
    public void Scan_RandomPatternsOverSmallAlphabet_MatchesNaiveSearch()
    {
        // A three-byte alphabet makes patterns overlap and share prefixes and suffixes
        // constantly; every other round uses eight values, for states with more edges.
        var random = new Random(1234);
        for (var round = 0; round < 200; round++)
        {
            var patterns = Enumerable.Range(0, random.Next(1, 30))
                .Select(_ => Enumerable.Range(0, random.Next(1, 6)).Select(_ => (byte)random.Next(round % 2 == 0 ? 3 : 8)).ToArray())
                .ToArray();
            var input = Enumerable.Range(0, random.Next(0, 200)).Select(_ => (byte)random.Next(round % 2 == 0 ? 3 : 8)).ToArray();

            var automaton = new PronomAhoCorasick(patterns);

            Assert.Equal(NaiveScan(patterns, input), Scan(automaton, input).Order().ToList());
        }
    }

    private static List<(int Pattern, int End)> Scan(PronomAhoCorasick automaton, byte[] input)
    {
        var hits = new List<(int, int)>();
        var state = 0;
        for (var i = 0; i < input.Length; i++)
        {
            state = automaton.Step(state, input[i]);
            foreach (var pattern in automaton.MatchesAt(state).ToArray().Order())
            {
                hits.Add((pattern, i));
            }
        }

        return hits;
    }

    private static List<(int Pattern, int End)> NaiveScan(byte[][] patterns, byte[] input)
    {
        var hits = new List<(int, int)>();
        for (var p = 0; p < patterns.Length; p++)
        {
            for (var start = 0; start + patterns[p].Length <= input.Length; start++)
            {
                if (input.AsSpan(start, patterns[p].Length).SequenceEqual(patterns[p]))
                {
                    hits.Add((p, start + patterns[p].Length - 1));
                }
            }
        }

        return [.. hits.Order()];
    }
}
