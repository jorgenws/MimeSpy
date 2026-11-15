using System.Xml;

namespace MimeSpy.Tests;

// A gate may let a signature through that then fails to match, but must never rule out
// one that would have matched - results have to be exactly what trying every signature
// gives.
public class PronomCandidateIndexTests
{
    [Theory]
    [InlineData(0, 0, "41424344", nameof(PronomGateKind.FirstByte))]
    [InlineData(0, 4, "41424344", nameof(PronomGateKind.NearLiteral))]
    [InlineData(0, null, "41424344", nameof(PronomGateKind.FarLiteral))]
    [InlineData(0, 100, "01", nameof(PronomGateKind.Always))]
    public void GetGateKind_EachShapeOfSignature_GetsTheCheapestGateThatApplies(int minOffset, int? maxOffset, string sequence, string expected)
    {
        var index = new PronomCandidateIndex([Parse(SubSequence(minOffset, maxOffset, sequence))]);

        Assert.Equal(expected, index.GetGateKind(1).ToString());
    }

    [Theory]
    [InlineData("41", true)]
    [InlineData("4100", true)]
    [InlineData("0041", false)]
    [InlineData("", false)]
    public void FindCandidates_FirstByte_PassesOnlyThatFirstByte(string hex, bool expected)
    {
        var index = new PronomCandidateIndex([Parse(SubSequence(0, 0, "41"))]);

        Assert.Equal(expected, IsCandidate(index, Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("41424344", true)]
    [InlineData("0000000041424344", true)]
    [InlineData("000000000041424344", false)]
    [InlineData("414243", false)]
    public void FindCandidates_NearLiteral_PassesOnlyWithinItsWindow(string hex, bool expected)
    {
        var index = new PronomCandidateIndex([Parse(SubSequence(0, 4, "41424344"))]);

        Assert.Equal(expected, IsCandidate(index, Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("01414243", true)]
    [InlineData("010000444546", true)]
    [InlineData("0100000000", false)]
    [InlineData("01", false)]
    public void FindCandidates_FragmentAlternatives_EitherPasses(string hex, bool expected)
    {
        // The one-byte anchor is too short to scan for, so the fragment group's
        // alternatives are the gate.
        var index = new PronomCandidateIndex([Parse(SubSequence(0, 100, "01",
            """
            <RightFragment MinOffset="0" MaxOffset="2" Position="1">414243</RightFragment>
            <RightFragment MinOffset="0" MaxOffset="2" Position="1">444546</RightFragment>
            """))]);

        Assert.Equal(expected, IsCandidate(index, Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(PronomCandidateIndex.ScanLimit, false)]
    [InlineData(PronomCandidateIndex.ScanLimit + 1, true)]
    public void FindCandidates_FarLiteral_PassesOnAnyHeadLongerThanTheScan(int length, bool expected)
    {
        var index = new PronomCandidateIndex([Parse(SubSequence(0, null, "41424344"))]);
        var scope = new PronomCandidateIndex.Scope(null, PronomCandidateIndex.ScanLimit, ScansLiterals: true);

        Assert.Equal(expected, IsCandidate(index, new byte[length], scope));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    public void FindCandidates_Always_Passes(string hex)
    {
        var index = new PronomCandidateIndex([Parse(SubSequence(0, 100, "01"))]);

        Assert.True(IsCandidate(index, Convert.FromHexString(hex)));
    }

    [Fact]
    public void FindCandidates_ScopeWithTooFewLiteralGatesToScanFor_PassesThemAll()
    {
        var index = new PronomCandidateIndex([Parse(SubSequence(0, 4, "41424344"))]);

        Assert.True(IsCandidate(index, [0x00], index.CreateScope([1])));
    }

    [Fact]
    public void FindCandidates_EverySignatureMatchingAFixture_IsACandidate()
    {
        var index = PronomSignatureIndex.Candidates;
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures")))
        {
            var whole = File.ReadAllBytes(path);
            foreach (var length in new[] { 16, 512, PronomCandidateIndex.ScanLimit, whole.Length })
            {
                var bytes = whole.AsSpan(0, Math.Min(length, whole.Length)).ToArray();
                for (var i = 0; i < index.SignatureCount; i++)
                {
                    var signature = PronomSignatureIndex.GetSignature(index.GetSignatureId(i));
                    if (PronomSignatureMatcher.MatchesSignature(signature, bytes))
                    {
                        Assert.True(IsCandidate(index, bytes, index.AllSignatures, i), $"signature {signature.Id} on {Path.GetFileName(path)}[..{bytes.Length}]");
                    }
                }
            }
        }
    }

    private static bool IsCandidate(PronomCandidateIndex index, byte[] head, PronomCandidateIndex.Scope? scope = null, int signatureIndex = 0)
    {
        var candidates = new ulong[index.MaskLength];
        index.FindCandidates(head, scope ?? index.AllSignatures, candidates);
        return PronomCandidateIndex.NextSetBit(candidates, signatureIndex) == signatureIndex;
    }

    private static string SubSequence(int minOffset, int? maxOffset, string sequence, string fragments = "") => $"""
        <SubSequence Position="1" SubSeqMinOffset="{minOffset}"{(maxOffset is int max ? $" SubSeqMaxOffset=\"{max}\"" : "")}>
          <Sequence>{sequence}</Sequence>
          {fragments}
        </SubSequence>
        """;

    private static PronomInternalSignature Parse(string subSequence)
    {
        var xml = $"""
            <InternalSignature ID="1">
              <ByteSequence Reference="BOFoffset">
                {subSequence}
              </ByteSequence>
            </InternalSignature>
            """;
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { IgnoreWhitespace = true });
        return PronomInternalSignatureParser.ParseInternalSignature(reader);
    }
}
