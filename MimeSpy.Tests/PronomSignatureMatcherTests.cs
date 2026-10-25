using System.Xml;

namespace MimeSpy.Tests;

// The matcher only searches for an anchor as far from its sub-sequence's checked window
// as the anchor's fragment chain can reach, so an anchor at exactly that reach must
// still be found, and one a byte beyond it must not.
public class PronomSignatureMatcherTests
{
    [Theory]
    [InlineData("BBAA", true)]
    [InlineData("BB000000AA", true)]
    [InlineData("BB00000000AA", false)]
    [InlineData("00BB000000AA", false)]
    public void MatchesSignature_BofAnchorWithLeftFragment_FindsTheAnchorUpToTheFragmentsFullReach(string hex, bool expected)
    {
        var signature = Parse("BOFoffset", "<LeftFragment MinOffset=\"0\" MaxOffset=\"3\" Position=\"1\">BB</LeftFragment>");

        Assert.Equal(expected, PronomSignatureMatcher.MatchesSignature(signature, Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("AABB", true)]
    [InlineData("AA000000BB", true)]
    [InlineData("AA00000000BB", false)]
    [InlineData("AA000000BB00", false)]
    public void MatchesSignature_EofAnchorWithRightFragment_FindsTheAnchorUpToTheFragmentsFullReach(string hex, bool expected)
    {
        var signature = Parse("EOFoffset", "<RightFragment MinOffset=\"0\" MaxOffset=\"3\" Position=\"1\">BB</RightFragment>");

        Assert.Equal(expected, PronomSignatureMatcher.MatchesSignature(signature, Convert.FromHexString(hex)));
    }

    private static PronomInternalSignature Parse(string reference, string fragment)
    {
        var xml = $"""
            <InternalSignature ID="1">
              <ByteSequence Reference="{reference}">
                <SubSequence Position="1" SubSeqMinOffset="0" SubSeqMaxOffset="0">
                  <Sequence>AA</Sequence>
                  {fragment}
                </SubSequence>
              </ByteSequence>
            </InternalSignature>
            """;
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { IgnoreWhitespace = true });
        return PronomInternalSignatureParser.ParseInternalSignature(reader);
    }
}
