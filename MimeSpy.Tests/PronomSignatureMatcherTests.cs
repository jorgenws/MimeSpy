using System.Xml;

namespace MimeSpy.Tests;

// The matcher only searches for an anchor as far from its sub-sequence's checked window
// as the anchor's fragment chain can reach - and, with no SubSeqMaxOffset, only within
// MaxBytesToScan of its end of the file - so an anchor at exactly that reach must still
// be found, and one a byte beyond it must not.
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

    [Theory]
    [InlineData(PronomSignatureMatcher.MaxBytesToScan, true)]
    [InlineData(PronomSignatureMatcher.MaxBytesToScan + 1, false)]
    public void MatchesSignature_UnboundedBofAnchor_IsOnlySearchedForWithinMaxBytesToScanOfTheStart(int anchorStart, bool expected)
    {
        var bytes = new byte[anchorStart + 100];
        bytes[anchorStart] = 0xAA;

        Assert.Equal(expected, PronomSignatureMatcher.MatchesSignature(ParseUnbounded("BOFoffset"), bytes));
    }

    [Theory]
    [InlineData(PronomSignatureMatcher.MaxBytesToScan, true)]
    [InlineData(PronomSignatureMatcher.MaxBytesToScan + 1, false)]
    public void MatchesSignature_UnboundedEofAnchor_IsOnlySearchedForWithinMaxBytesToScanOfTheEnd(int anchorEndFromEnd, bool expected)
    {
        var bytes = new byte[anchorEndFromEnd + 100];
        bytes[^(anchorEndFromEnd + 1)] = 0xAA;

        Assert.Equal(expected, PronomSignatureMatcher.MatchesSignature(ParseUnbounded("EOFoffset"), bytes));
    }

    private static PronomInternalSignature ParseUnbounded(string reference) => Parse($"""
        <InternalSignature ID="1">
          <ByteSequence Reference="{reference}">
            <SubSequence Position="1" SubSeqMinOffset="0">
              <Sequence>AA</Sequence>
            </SubSequence>
          </ByteSequence>
        </InternalSignature>
        """);

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
        return Parse(xml);
    }

    private static PronomInternalSignature Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { IgnoreWhitespace = true });
        return PronomInternalSignatureParser.ParseInternalSignature(reader);
    }
}
