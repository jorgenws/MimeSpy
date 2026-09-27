using System.Globalization;
using System.Xml;

namespace MimeSpy;

/// <summary>
/// Parses the embedded PRONOM DROID signature file once into
/// <see cref="PronomInternalSignature"/> and <see cref="PronomFileFormat"/> tables,
/// keyed so a match can walk from an internal signature ID back to the formats that
/// claim it. The XML also carries per-fragment Boyer-Moore-Horspool shift tables
/// (&lt;Shift&gt;/&lt;DefaultShift&gt;) that speed up DROID's own search - this parser
/// never reads them, since <see cref="PronomSignatureMatcher"/> gets the same result
/// from <see cref="ReadOnlySpan{T}"/>'s built-in (also optimized) search instead.
/// </summary>
internal static class PronomSignatureIndex
{
    private const string ResourceName = "pronom-droid-signatures.xml";

    private static readonly Dictionary<int, PronomInternalSignature> SignaturesById;
    private static readonly Dictionary<int, List<PronomFileFormat>> FormatsBySignatureId;

    public static IReadOnlyList<PronomFileFormat> Formats { get; }

    static PronomSignatureIndex()
    {
        using var stream = typeof(PronomSignatureIndex).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });

        var signaturesById = new Dictionary<int, PronomInternalSignature>();
        var formats = new List<PronomFileFormat>();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "InternalSignature":
                    var signature = ParseInternalSignature(reader.ReadSubtree());
                    signaturesById[signature.Id] = signature;
                    break;
                case "FileFormat":
                    formats.Add(ParseFileFormat(reader.ReadSubtree()));
                    break;
            }
        }

        SignaturesById = signaturesById;
        Formats = formats;

        var formatsBySignatureId = new Dictionary<int, List<PronomFileFormat>>();
        foreach (var format in formats)
        {
            foreach (var signatureId in format.InternalSignatureIds)
            {
                if (!formatsBySignatureId.TryGetValue(signatureId, out var bucket))
                {
                    bucket = [];
                    formatsBySignatureId[signatureId] = bucket;
                }

                bucket.Add(format);
            }
        }

        FormatsBySignatureId = formatsBySignatureId;
    }

    /// <summary>
    /// Every format that claims <paramref name="signatureId"/> as one of its
    /// alternative <see cref="PronomFileFormat.InternalSignatureIds"/>.
    /// </summary>
    public static IReadOnlyList<PronomFileFormat> FindFormatsBySignatureId(int signatureId) =>
        FormatsBySignatureId.TryGetValue(signatureId, out var bucket) ? bucket : [];

    public static PronomInternalSignature GetSignature(int id) => SignaturesById[id];

    private static PronomInternalSignature ParseInternalSignature(XmlReader reader)
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
        var position = ParseIntAttribute(reader, "Position") ?? throw new InvalidOperationException("SubSequence is missing its Position attribute.");
        var minOffset = ParseIntAttribute(reader, "SubSeqMinOffset") ?? 0;
        var maxOffset = ParseIntAttribute(reader, "SubSeqMaxOffset");

        byte[]? anchor = null;
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
                        anchor = ParseHex(reader.ReadElementContentAsString());
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
        var pattern = ParseBytePattern(reader.ReadElementContentAsString());

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

    // PRONOM's extended-hex syntax: plain hex byte pairs, plus bracketed wildcards -
    // [XX:YY] an inclusive range, [!hex] a negated exact value, and [&XX]/[!&XX] a
    // bitmask test - mixed freely within one fragment (e.g. "TGR[30:39][30:39]...").
    private static PronomBytePattern ParseBytePattern(string raw)
    {
        var tokens = new List<PronomByteToken>();
        var i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '[')
            {
                var close = raw.IndexOf(']', i);
                tokens.Add(ParseBracketToken(raw.Substring(i + 1, close - i - 1)));
                i = close + 1;
            }
            else
            {
                tokens.Add(new PronomLiteralToken(byte.Parse(raw.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                i += 2;
            }
        }

        return new PronomBytePattern(tokens);
    }

    private static PronomByteToken ParseBracketToken(string inner)
    {
        if (inner.StartsWith("!&", StringComparison.Ordinal))
        {
            return new PronomBitmaskToken(byte.Parse(inner.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), Negated: true);
        }

        if (inner.StartsWith("&", StringComparison.Ordinal))
        {
            return new PronomBitmaskToken(byte.Parse(inner.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture), Negated: false);
        }

        if (inner.StartsWith("!", StringComparison.Ordinal))
        {
            return new PronomNegatedValueToken(ParseHex(inner.Substring(1)));
        }

        var colon = inner.IndexOf(':');
        return new PronomRangeToken(ParseHex(inner.Substring(0, colon)), ParseHex(inner.Substring(colon + 1)));
    }

    private static PronomFileFormat ParseFileFormat(XmlReader reader)
    {
        reader.MoveToContent();
        var id = ParseIntAttribute(reader, "ID") ?? throw new InvalidOperationException("FileFormat is missing its ID attribute.");
        var puid = reader.GetAttribute("PUID") ?? throw new InvalidOperationException($"FileFormat {id} is missing its PUID attribute.");
        var name = reader.GetAttribute("Name") ?? throw new InvalidOperationException($"FileFormat {id} is missing its Name attribute.");
        var version = reader.GetAttribute("Version");
        var mimeType = reader.GetAttribute("MIMEType");

        var extensions = new List<string>();
        var internalSignatureIds = new List<int>();

        // See the matching comment in ParseSubSequence: ReadElementContentAsString()
        // already advances past the sibling it reads, so this can't be a plain
        // while (reader.Read()) loop without silently skipping every other Extension
        // or InternalSignatureID - and some FileFormat elements (no signature, no
        // extension) are entirely empty, so IsEmptyElement has to be checked first.
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
                    case "Extension":
                        extensions.Add(reader.ReadElementContentAsString().ToLowerInvariant());
                        break;
                    case "InternalSignatureID":
                        internalSignatureIds.Add(int.Parse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture));
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }

        return new PronomFileFormat(id, puid, name, version, mimeType, extensions, internalSignatureIds);
    }

    private static int? ParseIntAttribute(XmlReader reader, string name)
    {
        var raw = reader.GetAttribute(name);
        return raw is null ? null : int.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static byte[] ParseHex(string raw)
    {
        var bytes = new byte[raw.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(raw.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }
}
