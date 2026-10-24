using System.Globalization;
using System.Xml;

namespace MimeSpy;

// Parses the embedded DROID signature file once into signature and format tables, plus
// the indexes PronomSignatureMatcher uses to avoid trying every signature on every file:
// signatures by required first byte, and formats by signature ID. The XML's per-fragment
// Boyer-Moore-Horspool shift tables are ignored; ReadOnlySpan<T>'s search does that job.
internal static class PronomSignatureIndex
{
    private const string ResourceName = "pronom-droid-signatures.xml";
    private const string MimeTypeAdditionsResourceName = "mimespy-mime-types.xml";

    private static readonly Dictionary<int, PronomInternalSignature> SignaturesById;
    private static readonly Dictionary<string, PronomFileFormat> FormatByPuid;
    private static readonly Dictionary<int, List<PronomFileFormat>> FormatsBySignatureId;
    private static readonly Dictionary<byte, List<int>> SignatureIdsByFirstByte;
    private static readonly Dictionary<int, byte> RequiredFirstByteBySignatureId;

    public static IReadOnlyList<int> SignatureIdsWithoutFixedFirstByte { get; }

    // Every cataloged format - PronomFormatFilter walks this once, per filtered MimeSpy
    // instance, to build its own narrower version of this index's two buckets above.
    public static IReadOnlyList<PronomFileFormat> AllFormats { get; }

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

        ApplyMimeTypeAdditions(formats);

        SignaturesById = signaturesById;
        AllFormats = formats;

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
        var requiredFirstByteBySignatureId = new Dictionary<int, byte>();
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
                requiredFirstByteBySignatureId[signature.Id] = firstByte;
            }
            else
            {
                signatureIdsWithoutFixedFirstByte.Add(signature.Id);
            }
        }

        SignatureIdsByFirstByte = signatureIdsByFirstByte;
        SignatureIdsWithoutFixedFirstByte = signatureIdsWithoutFixedFirstByte;
        RequiredFirstByteBySignatureId = requiredFirstByteBySignatureId;
    }

    public static PronomInternalSignature GetSignature(int id) => SignaturesById[id];

    // The same required-first-byte fact FindSignatureIdsByFirstByte's buckets are built
    // from, looked up by signature ID rather than by byte - PronomFormatFilter uses
    // this to re-bucket just the signatures its included formats reference, without
    // re-deriving the fact from scratch.
    public static byte? GetRequiredFirstByte(int signatureId) =>
        RequiredFirstByteBySignatureId.TryGetValue(signatureId, out var firstByte) ? firstByte : null;

    public static PronomFileFormat? FindFormatByPuid(string puid) => FormatByPuid.TryGetValue(puid, out var format) ? format : null;

    // Every format that claims signatureId as one of its alternative
    // PronomFileFormat.InternalSignatureIds.
    public static IReadOnlyList<PronomFileFormat> FindFormatsBySignatureId(int signatureId) =>
        FormatsBySignatureId.TryGetValue(signatureId, out var bucket) ? bucket : [];

    // Every signature whose TryGetRequiredFirstByte is exactly firstByte - i.e.
    // signatures that can be skipped outright for a buffer whose own first byte doesn't
    // equal this one. Signatures TryGetRequiredFirstByte can't pin down a single
    // required byte for are never in here - see SignatureIdsWithoutFixedFirstByte.
    public static IReadOnlyList<int> FindSignatureIdsByFirstByte(byte firstByte) =>
        SignatureIdsByFirstByte.TryGetValue(firstByte, out var bucket) ? bucket : [];

    // A signature needs a specific byte at offset 0 only when its first BOF sequence
    // starts with a sub-sequence fixed at exactly offset 0, with no left fragments, whose
    // anchor starts with a plain literal byte. Anything else returns null, and the
    // signature goes in SignatureIdsWithoutFixedFirstByte to always be tried.
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
        var mimeTypes = SplitMimeTypes(reader.GetAttribute("MIMEType"));

        var extensions = new List<string>();
        var internalSignatureIds = new List<int>();
        var hasPriorityOverIds = new List<int>();

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
                    case "HasPriorityOverFileFormatID":
                        hasPriorityOverIds.Add(int.Parse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture));
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }

        return new PronomFileFormat(id, puid, name, version, mimeTypes, extensions, internalSignatureIds, hasPriorityOverIds);
    }

    // PRONOM joins several MIME types into one comma-separated attribute (in
    // alphabetical order, not by preference), e.g. "application/mp4, video/mp4".
    private static string[] SplitMimeTypes(string? mimeTypeList) =>
        mimeTypeList is null ? [] : [.. mimeTypeList.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)];

    // Fills in MIME types PRONOM leaves empty for a handful of common formats
    // (docs/adr/0026). Only fills gaps: an entry for an unknown PUID, or one PRONOM has
    // since given a type, throws, so a stale entry fails the first test run after a
    // signature-file refresh instead of lingering.
    private static void ApplyMimeTypeAdditions(List<PronomFileFormat> formats)
    {
        using var stream = typeof(PronomSignatureIndex).Assembly.GetManifestResourceStream(MimeTypeAdditionsResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{MimeTypeAdditionsResourceName}' not found.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });

        var indexByPuid = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < formats.Count; i++)
        {
            if (!indexByPuid.ContainsKey(formats[i].Puid))
            {
                indexByPuid[formats[i].Puid] = i;
            }
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Format")
            {
                continue;
            }

            var puid = reader.GetAttribute("Puid") ?? throw new InvalidOperationException($"A Format in {MimeTypeAdditionsResourceName} is missing its Puid.");
            var mimeTypes = SplitMimeTypes(reader.GetAttribute("MimeTypes"));
            if (mimeTypes.Length == 0)
            {
                throw new InvalidOperationException($"{MimeTypeAdditionsResourceName} lists {puid} with no MimeTypes.");
            }

            if (!applied.Add(puid))
            {
                throw new InvalidOperationException($"{MimeTypeAdditionsResourceName} lists {puid} twice.");
            }

            if (!indexByPuid.TryGetValue(puid, out var index))
            {
                throw new InvalidOperationException($"{MimeTypeAdditionsResourceName} lists {puid}, which isn't in {ResourceName}.");
            }

            if (formats[index].MimeTypes.Count > 0)
            {
                throw new InvalidOperationException($"{ResourceName} now gives {puid} a MIME type of its own; remove its entry from {MimeTypeAdditionsResourceName}.");
            }

            formats[index] = formats[index] with { MimeTypes = mimeTypes };
        }
    }

    private static int? ParseIntAttribute(XmlReader reader, string name) => PronomInternalSignatureParser.ParseIntAttribute(reader, name);
}
