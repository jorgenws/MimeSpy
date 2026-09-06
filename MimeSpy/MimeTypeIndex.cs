namespace MimeSpy;

/// <summary>
/// Parses the embedded Apache-style mime.types table once and buckets it by file
/// extension so a signature match's extensions can be resolved to their mime
/// type(s) directly, instead of scanning every entry in the table.
/// </summary>
internal static class MimeTypeIndex
{
    private const string ResourceName = "mime.types";

    private static readonly Dictionary<string, List<MimeType>> ByExtension;

    static MimeTypeIndex()
    {
        ByExtension = new Dictionary<string, List<MimeType>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mimeType in LoadMimeTypes())
        {
            foreach (var extension in mimeType.Extensions)
            {
                if (!ByExtension.TryGetValue(extension, out var bucket))
                {
                    bucket = [];
                    ByExtension[extension] = bucket;
                }

                bucket.Add(mimeType);
            }
        }
    }

    public static IReadOnlyList<MimeType> FindByExtension(string extension)
    {
        return ByExtension.TryGetValue(extension, out var matches) ? matches : Array.Empty<MimeType>();
    }

    private static IEnumerable<MimeType> LoadMimeTypes()
    {
        using var stream = typeof(MimeTypeIndex).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // Most entries in the source table have no unique file extension at all;
            // they can never be reached from a signature match, so skip them.
            if (tokens.Length < 2)
            {
                continue;
            }

            var extensions = new string[tokens.Length - 1];
            Array.Copy(tokens, 1, extensions, 0, extensions.Length);
            yield return new MimeType(tokens[0], extensions);
        }
    }
}
