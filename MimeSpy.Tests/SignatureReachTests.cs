namespace MimeSpy.Tests;

// Investigate(Stream) matches a large file against only InvestigateWindowSize bytes from
// each end (docs/adr/0031), so every byte sequence has to fit in that from the end it's
// anchored to. A PRONOM update that reaches further means revisiting that decision, not
// just raising the constant.
public class SignatureReachTests
{
    [Fact]
    public void EveryByteSequence_ReachesNoFurtherThanTheInvestigateWindow()
    {
        var index = PronomSignatureIndex.Candidates;
        var longest = Enumerable.Range(0, index.SignatureCount)
            .Select(i => PronomSignatureIndex.GetSignature(index.GetSignatureId(i)))
            .SelectMany(signature => signature.ByteSequences.Select(sequence => (signature.Id, Reach: Reach(sequence))))
            .MaxBy(r => r.Reach);

        Assert.True(longest.Reach <= MimeSpy.InvestigateWindowSize, $"signature {longest.Id} reaches {longest.Reach} bytes from its end");
    }

    // The furthest from its end a sequence can reach: each unit's checked window (an
    // unbounded one ends at MaxBytesToScan), then its left fragments, anchor and right
    // fragments - PronomSignatureMatcher.MatchesForward's bounds, mirrored for EOF.
    private static long Reach(PronomByteSequence sequence)
    {
        long reach = 0;
        foreach (var sub in sequence.SubSequences)
        {
            var windowEnd = sub.MaxOffset is int max ? reach + max : Math.Max(reach, PronomSignatureMatcher.MaxBytesToScan);
            reach = windowEnd + sub.MaxLeftReach + sub.Anchor.Length + sub.MaxRightReach;
        }

        return reach;
    }
}
