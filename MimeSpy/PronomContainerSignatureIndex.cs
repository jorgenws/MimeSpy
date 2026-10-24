using System.Globalization;
using System.Xml;

namespace MimeSpy;

// Parses the embedded PRONOM container-signature file once into
// PronomContainerSignatures grouped by PronomContainerFile's container type, plus the
// two small lookup tables that tie them into the main DROID signature space: which PUID
// a matched container signature resolves to (FindPuid), and which top-level PUIDs are
// even worth opening a ZIP or OLE2 container for in the first place
// (FindTriggerContainerType).
internal static class PronomContainerSignatureIndex
{
    private const string ResourceName = "pronom-container-signatures.xml";

    private static readonly Dictionary<string, List<PronomContainerSignature>> SignaturesByContainerType;
    private static readonly Dictionary<int, string> PuidBySignatureId;
    private static readonly Dictionary<string, string> ContainerTypeByTriggerPuid;

    static PronomContainerSignatureIndex()
    {
        using var stream = typeof(PronomContainerSignatureIndex).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });

        var signaturesByContainerType = new Dictionary<string, List<PronomContainerSignature>>(StringComparer.Ordinal);
        var puidBySignatureId = new Dictionary<int, string>();
        var containerTypeByTriggerPuid = new Dictionary<string, string>(StringComparer.Ordinal);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "ContainerSignature":
                    var signature = ParseContainerSignature(reader.ReadSubtree());
                    if (!signaturesByContainerType.TryGetValue(signature.ContainerType, out var bucket))
                    {
                        bucket = [];
                        signaturesByContainerType[signature.ContainerType] = bucket;
                    }

                    bucket.Add(signature);
                    break;
                case "FileFormatMapping":
                    puidBySignatureId[int.Parse(reader.GetAttribute("signatureId")!, CultureInfo.InvariantCulture)] = reader.GetAttribute("Puid")!;
                    break;
                case "TriggerPuid":
                    containerTypeByTriggerPuid[reader.GetAttribute("Puid")!] = reader.GetAttribute("ContainerType")!;
                    break;
            }
        }

        SignaturesByContainerType = signaturesByContainerType;
        PuidBySignatureId = puidBySignatureId;
        ContainerTypeByTriggerPuid = containerTypeByTriggerPuid;
    }

    // Every container signature of containerType ("ZIP" or "OLE2").
    public static IReadOnlyList<PronomContainerSignature> FindSignaturesByContainerType(string containerType) =>
        SignaturesByContainerType.TryGetValue(containerType, out var bucket) ? bucket : [];

    // The PUID a matched PronomContainerSignature.Id resolves to.
    public static string? FindPuid(int containerSignatureId) => PuidBySignatureId.TryGetValue(containerSignatureId, out var puid) ? puid : null;

    // "ZIP" or "OLE2" if puid is one of the top-level formats worth opening a container
    // for (e.g. the generic ZIP/OLE2 PUIDs themselves), null otherwise.
    public static string? FindTriggerContainerType(string puid) => ContainerTypeByTriggerPuid.TryGetValue(puid, out var containerType) ? containerType : null;

    public static IEnumerable<string> TriggerPuids => ContainerTypeByTriggerPuid.Keys;

    private static PronomContainerSignature ParseContainerSignature(XmlReader reader)
    {
        reader.MoveToContent();
        var id = int.Parse(reader.GetAttribute("Id")!, CultureInfo.InvariantCulture);
        var containerType = reader.GetAttribute("ContainerType")!;

        var files = new List<PronomContainerFile>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "File")
            {
                files.Add(ParseFile(reader.ReadSubtree()));
            }
        }

        return new PronomContainerSignature(id, containerType, files);
    }

    private static PronomContainerFile ParseFile(XmlReader reader)
    {
        reader.MoveToContent();

        string? path = null;
        PronomInternalSignature? signature = null;

        // See PronomInternalSignatureParser.ParseSubSequence for why this can't be a
        // plain while (reader.Read()) loop, and why IsEmptyElement has to be checked
        // first - the same ReadElementContentAsString double-advance hazard applies
        // to <Path> here.
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
                    case "Path":
                        path = reader.ReadElementContentAsString();
                        break;
                    case "BinarySignatures":
                        signature = ParseBinarySignatures(reader.ReadSubtree());
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }

        return new PronomContainerFile(path ?? throw new InvalidOperationException("File is missing its Path."), signature);
    }

    // <BinarySignatures><InternalSignatureCollection><InternalSignature>...
    // Every File that has BinarySignatures at all has exactly one InternalSignature
    // inside it - there's no alternation to resolve here, unlike a DROID FileFormat's
    // list of InternalSignatureIds.
    private static PronomInternalSignature ParseBinarySignatures(XmlReader reader)
    {
        reader.MoveToContent();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "InternalSignature")
            {
                return PronomInternalSignatureParser.ParseInternalSignature(reader.ReadSubtree());
            }
        }

        throw new InvalidOperationException("BinarySignatures has no InternalSignature.");
    }
}
