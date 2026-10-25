using System.Xml;

namespace MimeSpy;

// Rewrites PRONOM's MIME types by PUID once its DROID file has loaded, which itself
// stays an unmodified copy - docs/adr/0029. Each entry in mimespy-mime-types.xml states
// the value PRONOM gave when it was written (PronomMimeTypes, empty for a format PRONOM
// gives none) and the value to use instead (MimeTypes). If PRONOM's value is no longer
// exactly that, loading fails: a correction written against older data must be rechecked,
// not applied over whatever PRONOM says now.
internal static class PronomMimeTypeCorrections
{
    public const string ResourceName = "mimespy-mime-types.xml";

    public static void Apply(List<PronomFileFormat> formats, Stream corrections)
    {
        using var reader = XmlReader.Create(corrections, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });

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

            var puid = reader.GetAttribute("Puid") ?? throw new InvalidOperationException($"A Format in {ResourceName} is missing its Puid.");
            var writtenAgainst = PronomSignatureIndex.SplitMimeTypes(RequiredAttribute(reader, puid, "PronomMimeTypes"));
            var mimeTypes = PronomSignatureIndex.SplitMimeTypes(RequiredAttribute(reader, puid, "MimeTypes"));

            if (!applied.Add(puid))
            {
                throw new InvalidOperationException($"{ResourceName} lists {puid} twice.");
            }

            if (!indexByPuid.TryGetValue(puid, out var index))
            {
                throw new InvalidOperationException($"{ResourceName} lists {puid}, which isn't in PRONOM's DROID file.");
            }

            if (!formats[index].MimeTypes.SequenceEqual(writtenAgainst, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"PRONOM now gives {puid} \"{string.Join(", ", formats[index].MimeTypes)}\", not the \"{string.Join(", ", writtenAgainst)}\" its entry in {ResourceName} was written against. Recheck that entry.");
            }

            // Filling a gap needs no justification beyond PRONOM having nothing;
            // overriding PRONOM does.
            if (writtenAgainst.Length > 0 && string.IsNullOrWhiteSpace(reader.GetAttribute("Reason")))
            {
                throw new InvalidOperationException($"{puid} in {ResourceName} overrides PRONOM's MIME types without a Reason.");
            }

            formats[index] = formats[index] with { MimeTypes = mimeTypes };
        }
    }

    private static string RequiredAttribute(XmlReader reader, string puid, string name) =>
        reader.GetAttribute(name) ?? throw new InvalidOperationException($"{puid} in {ResourceName} is missing its {name} attribute.");
}
