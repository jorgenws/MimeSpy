using System.Globalization;
using System.Xml;

namespace MimeSpy;

// Parses the embedded DROID signature file once into signature and format tables, plus
// what PronomSignatureMatcher uses to avoid trying every signature on every file: the
// candidate index, and formats by signature ID. The XML's per-fragment
// Boyer-Moore-Horspool shift tables are ignored; ReadOnlySpan<T>'s search does that job.
internal static class PronomSignatureIndex
{
    private const string ResourceName = "pronom-droid-signatures.xml";

    private static readonly Dictionary<int, PronomInternalSignature> SignaturesById;
    private static readonly Dictionary<string, PronomFileFormat> FormatByPuid;
    private static readonly Dictionary<int, List<PronomFileFormat>> FormatsBySignatureId;
    public static PronomCandidateIndex Candidates { get; }

    // Every cataloged format - PronomFormatFilter walks this once, per filtered MimeSpy
    // instance, to build its candidate scope.
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

        using (var corrections = typeof(PronomSignatureIndex).Assembly.GetManifestResourceStream(PronomMimeTypeCorrections.ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{PronomMimeTypeCorrections.ResourceName}' not found."))
        {
            PronomMimeTypeCorrections.Apply(formats, corrections);
        }

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

        Candidates = new PronomCandidateIndex([.. signaturesById.Values]);
    }

    public static PronomInternalSignature GetSignature(int id) => SignaturesById[id];

    public static PronomFileFormat? FindFormatByPuid(string puid) => FormatByPuid.TryGetValue(puid, out var format) ? format : null;

    // Every format that claims signatureId as one of its alternative
    // PronomFileFormat.InternalSignatureIds.
    public static IReadOnlyList<PronomFileFormat> FindFormatsBySignatureId(int signatureId) =>
        FormatsBySignatureId.TryGetValue(signatureId, out var bucket) ? bucket : [];

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

    // PRONOM joins several MIME types into one comma-separated attribute, e.g.
    // "application/mp4, video/mp4".
    public static string[] SplitMimeTypes(string? mimeTypeList) =>
        mimeTypeList is null ? [] : [.. mimeTypeList.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)];

    private static int? ParseIntAttribute(XmlReader reader, string name) => PronomInternalSignatureParser.ParseIntAttribute(reader, name);
}
