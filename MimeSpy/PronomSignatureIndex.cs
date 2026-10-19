using System.Globalization;
using System.Xml;

namespace MimeSpy;

/// <summary>
/// Parses the embedded PRONOM DROID signature file once into
/// <see cref="PronomInternalSignature"/> and <see cref="PronomFileFormat"/> tables,
/// plus two indexes <see cref="PronomSignatureMatcher"/> uses to avoid evaluating
/// every cataloged signature against every file: <see cref="FindSignatureIdsByFirstByte"/>
/// (signatures whose BOF anchor can only ever match at a known, fixed first byte) and
/// <see cref="FindFormatsBySignatureId"/> (walking a matched signature ID back to the
/// format(s) that claim it, the reverse of <see cref="PronomFileFormat.InternalSignatureIds"/>).
/// The XML also carries per-fragment Boyer-Moore-Horspool shift tables
/// (&lt;Shift&gt;/&lt;DefaultShift&gt;) that speed up DROID's own search - this parser
/// never reads them, since <see cref="PronomSignatureMatcher"/> gets the same result
/// from <see cref="ReadOnlySpan{T}"/>'s built-in (also optimized) search instead.
/// </summary>
internal static class PronomSignatureIndex
{
    private const string ResourceName = "pronom-droid-signatures.xml";

    private static readonly Dictionary<int, PronomInternalSignature> SignaturesById;
    private static readonly Dictionary<string, PronomFileFormat> FormatByPuid;
    private static readonly Dictionary<int, List<PronomFileFormat>> FormatsBySignatureId;
    private static readonly Dictionary<byte, List<int>> SignatureIdsByFirstByte;

    public static IReadOnlyList<int> SignatureIdsWithoutFixedFirstByte { get; }

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
                    var signature = PronomInternalSignatureParser.ParseInternalSignature(reader.ReadSubtree());
                    signaturesById[signature.Id] = signature;
                    break;
                case "FileFormat":
                    formats.Add(ParseFileFormat(reader.ReadSubtree()));
                    break;
            }
        }

        SignaturesById = signaturesById;

        var formatByPuid = new Dictionary<string, PronomFileFormat>(StringComparer.Ordinal);
        var formatsBySignatureId = new Dictionary<int, List<PronomFileFormat>>();
        foreach (var format in formats)
        {
            if (!formatByPuid.ContainsKey(format.Puid))
            {
                formatByPuid[format.Puid] = format;
            }

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

        FormatByPuid = formatByPuid;
        FormatsBySignatureId = formatsBySignatureId;

        var signatureIdsByFirstByte = new Dictionary<byte, List<int>>();
        var signatureIdsWithoutFixedFirstByte = new List<int>();
        foreach (var signature in signaturesById.Values)
        {
            if (TryGetRequiredFirstByte(signature) is byte firstByte)
            {
                if (!signatureIdsByFirstByte.TryGetValue(firstByte, out var bucket))
                {
                    bucket = [];
                    signatureIdsByFirstByte[firstByte] = bucket;
                }

                bucket.Add(signature.Id);
            }
            else
            {
                signatureIdsWithoutFixedFirstByte.Add(signature.Id);
            }
        }

        SignatureIdsByFirstByte = signatureIdsByFirstByte;
        SignatureIdsWithoutFixedFirstByte = signatureIdsWithoutFixedFirstByte;
    }

    public static PronomInternalSignature GetSignature(int id) => SignaturesById[id];

    public static PronomFileFormat? FindFormatByPuid(string puid) => FormatByPuid.TryGetValue(puid, out var format) ? format : null;

    /// <summary>
    /// Every format that claims <paramref name="signatureId"/> as one of its
    /// alternative <see cref="PronomFileFormat.InternalSignatureIds"/>.
    /// </summary>
    public static IReadOnlyList<PronomFileFormat> FindFormatsBySignatureId(int signatureId) =>
        FormatsBySignatureId.TryGetValue(signatureId, out var bucket) ? bucket : [];

    /// <summary>
    /// Every signature whose <see cref="TryGetRequiredFirstByte"/> is exactly
    /// <paramref name="firstByte"/> - i.e. signatures that can be skipped outright for
    /// a buffer whose own first byte doesn't equal this one. Signatures
    /// <see cref="TryGetRequiredFirstByte"/> can't pin down a single required byte for
    /// are never in here - see <see cref="SignatureIdsWithoutFixedFirstByte"/>.
    /// </summary>
    public static IReadOnlyList<int> FindSignatureIdsByFirstByte(byte firstByte) =>
        SignatureIdsByFirstByte.TryGetValue(firstByte, out var bucket) ? bucket : [];

    /// <summary>
    /// A signature's BOF anchor requires a specific byte at offset 0 only when its
    /// first (closest-to-start) <see cref="PronomByteSequence"/> has a first
    /// <see cref="PronomSubSequence"/> that (a) must sit at exactly offset 0 - no gap
    /// either side (<c>MinOffset == MaxOffset == 0</c>) and nothing chained further
    /// left of it (<see cref="PronomSubSequence.LeftFragmentGroups"/> empty, always
    /// true in practice for a signature's very first unit, but checked rather than
    /// assumed) - and (b) starts with a single plain <see cref="PronomLiteralToken"/>,
    /// not a wildcard. Every other shape (a gap before the anchor, a wildcarded first
    /// byte, EOF-only signatures with no BOF sequence at all, ...) can't be pinned to
    /// one required byte, so is left for <see cref="SignatureIdsWithoutFixedFirstByte"/>
    /// to always evaluate - this only ever narrows candidates, never excludes one that
    /// could legitimately match.
    /// </summary>
    private static byte? TryGetRequiredFirstByte(PronomInternalSignature signature)
    {
        foreach (var byteSequence in signature.ByteSequences)
        {
            if (byteSequence.Anchor != PronomAnchor.BeginningOfFile || byteSequence.SubSequences.Count == 0)
            {
                continue;
            }

            var first = byteSequence.SubSequences[0];
            if (first.MinOffset == 0 && first.MaxOffset == 0 && first.LeftFragmentGroups.Count == 0 &&
                first.Anchor.Tokens is [PronomLiteralToken literal, ..])
            {
                return literal.Value;
            }
        }

        return null;
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

        // See the matching comment in PronomInternalSignatureParser.ParseSubSequence:
        // ReadElementContentAsString() already advances past the sibling it reads, so
        // this can't be a plain while (reader.Read()) loop without silently skipping
        // every other Extension or InternalSignatureID - and some FileFormat elements
        // (no signature, no extension) are entirely empty, so IsEmptyElement has to
        // be checked first.
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

    private static int? ParseIntAttribute(XmlReader reader, string name) => PronomInternalSignatureParser.ParseIntAttribute(reader, name);
}
