namespace MimeSpy.Tests;

// Guards the assumption docs/adr/0028 rests on, against updates to
// pronom-container-signatures.xml.
public class ContainerSignatureDataTests
{
    private static readonly IReadOnlyList<PronomContainerSignature> ZipSignatures =
        PronomContainerSignatureIndex.FindSignaturesByContainerType("ZIP");

    [Fact]
    public void ZipContainerSignatures_NeverCheckAnEntryFromItsEnd()
    {
        var fromTheEnd = SequencesOf(ZipSignatures)
            .Where(s => s.Sequence.Anchor == PronomAnchor.EndOfFile)
            .Select(s => s.Describe());

        Assert.Empty(fromTheEnd);
    }

    [Fact]
    public void ZipContainerSignatures_WithABoundedReach_FitInTheEntryPrefixRead()
    {
        var tooFar = SequencesOf(ZipSignatures)
            .Where(s => Reach(s.Sequence) > PronomZipContainerReader.EntryPrefixLength)
            .Select(s => $"{s.Describe()} reaches {Reach(s.Sequence)} bytes");

        Assert.Empty(tooFar);
    }

    // How far into the entry a sequence can look: every subsequence's maximum gap, its
    // anchor, and each fragment group's maximum gap plus its longest alternative. Null
    // when a gap is unbounded - such a search simply covers whatever prefix was read.
    private static long? Reach(PronomByteSequence sequence)
    {
        long reach = 0;
        foreach (var sub in sequence.SubSequences)
        {
            if (sub.MaxOffset is not int maxOffset)
            {
                return null;
            }

            reach += maxOffset + sub.Anchor.Length;
            foreach (var group in sub.LeftFragmentGroups.Concat(sub.RightFragmentGroups))
            {
                reach += group.MaxOffset + group.Alternatives.Max(a => a.Length);
            }
        }

        return reach;
    }

    private static IEnumerable<ContainerSequence> SequencesOf(IEnumerable<PronomContainerSignature> signatures) =>
        from signature in signatures
        from file in signature.Files
        where file.Signature is not null
        from sequence in file.Signature!.ByteSequences
        select new ContainerSequence(signature.Id, file.Path, sequence);

    private sealed record ContainerSequence(int SignatureId, string Path, PronomByteSequence Sequence)
    {
        public string Describe() => $"container signature {SignatureId}, {Path}";
    }
}
