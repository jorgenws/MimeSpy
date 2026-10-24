using System.Xml;

namespace MimeSpy;

// Parses a PRONOM <InternalSignature> element - the
// ByteSequence/SubSequence/LeftFragment/RightFragment structure shared verbatim between
// the DROID-native signature file and the container-signature file's per-entry
// <BinarySignatures>. The two files differ only in how a Sequence/Fragment's text
// content is written (see PronomBytePatternParser), not in this surrounding element
// shape.
internal static class PronomInternalSignatureParser
{
    public static PronomInternalSignature ParseInternalSignature(XmlReader reader)
    {
        reader.MoveToContent();
        var id = ParseIntAttribute(reader, "ID") ?? throw new InvalidOperationException("InternalSignature is missing its ID attribute.");

        var byteSequences = new List<PronomByteSequence>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "ByteSequence")
            {
                byteSequences.Add(ParseByteSequence(reader.ReadSubtree()));
            }
        }

        return new PronomInternalSignature(id, byteSequences);
    }

    private static PronomByteSequence ParseByteSequence(XmlReader reader)
    {
        reader.MoveToContent();
        // "Variable" (used by exactly one signature, for a format with no fixed
        // anchor point at all) is treated the same as an unbounded forward search -
        // see PronomSubSequence's own offset handling for what makes that unbounded.
        var anchor = reader.GetAttribute("Reference") == "EOFoffset" ? PronomAnchor.EndOfFile : PronomAnchor.BeginningOfFile;

        var subSequences = new List<PronomSubSequence>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "SubSequence")
            {
                subSequences.Add(ParseSubSequence(reader.ReadSubtree()));
            }
        }

        subSequences.Sort((a, b) => a.Position.CompareTo(b.Position));
        return new PronomByteSequence(anchor, subSequences);
    }

    private static PronomSubSequence ParseSubSequence(XmlReader reader)
    {
        reader.MoveToContent();
        // The container-signature file omits Position on a SubSequence that's the
        // only one in its ByteSequence (no chaining, so nothing to order) - the
        // DROID-native file always writes it explicitly, even then.
        var position = ParseIntAttribute(reader, "Position") ?? 1;
        var minOffset = ParseIntAttribute(reader, "SubSeqMinOffset") ?? 0;
        var maxOffset = ParseIntAttribute(reader, "SubSeqMaxOffset");

        PronomBytePattern? anchor = null;
        var leftFragments = new List<(int Position, int MinOffset, int MaxOffset, PronomBytePattern Pattern)>();
        var rightFragments = new List<(int Position, int MinOffset, int MaxOffset, PronomBytePattern Pattern)>();

        // ReadElementContentAsString() (used here and in ParseFragment) already
        // advances the reader past the element it reads, to whatever sibling comes
        // next. A plain while (reader.Read()) loop would then advance a second time
        // before that sibling is ever inspected, silently skipping every other
        // Sequence/LeftFragment/RightFragment - so this loop only calls Read()
        // itself for nodes those cases don't already consume (Shift/DefaultShift,
        // via Skip(), and any non-element node). An empty element (no children at
        // all) needs its own check first: Read()-ing past it exhausts this subtree
        // reader entirely, so NodeType never becomes EndElement and a loop that
        // doesn't check for that spins forever re-reading past end of stream.
        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "Sequence":
                        anchor = PronomBytePatternParser.Parse(reader.ReadElementContentAsString());
                        break;
                    case "LeftFragment":
                        leftFragments.Add(ParseFragment(reader));
                        break;
                    case "RightFragment":
                        rightFragments.Add(ParseFragment(reader));
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }

        return new PronomSubSequence(
            position,
            minOffset,
            maxOffset,
            anchor ?? throw new InvalidOperationException($"SubSequence at position {position} has no <Sequence>."),
            GroupFragments(leftFragments),
            GroupFragments(rightFragments));
    }

    private static (int Position, int MinOffset, int MaxOffset, PronomBytePattern Pattern) ParseFragment(XmlReader reader)
    {
        var position = ParseIntAttribute(reader, "Position") ?? throw new InvalidOperationException("Fragment is missing its Position attribute.");
        var minOffset = ParseIntAttribute(reader, "MinOffset") ?? 0;
        var maxOffset = ParseIntAttribute(reader, "MaxOffset") ?? 0;
        var pattern = PronomBytePatternParser.Parse(reader.ReadElementContentAsString());

        return (position, minOffset, maxOffset, pattern);
    }

    // Fragments sharing the same Position are alternatives (any one satisfies that
    // step of the chain), ordered ascending - Position 1 is closest to the anchor.
    private static IReadOnlyList<PronomFragmentGroup> GroupFragments(List<(int Position, int MinOffset, int MaxOffset, PronomBytePattern Pattern)> fragments)
    {
        if (fragments.Count == 0)
        {
            return [];
        }

        return [.. fragments
            .GroupBy(f => f.Position)
            .OrderBy(g => g.Key)
            .Select(g => new PronomFragmentGroup(g.First().MinOffset, g.First().MaxOffset, [.. g.Select(f => f.Pattern)]))];
    }

    public static int? ParseIntAttribute(XmlReader reader, string name)
    {
        var raw = reader.GetAttribute(name);
        return raw is null ? null : int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
    }
}
