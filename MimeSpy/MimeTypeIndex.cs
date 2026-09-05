using System.Text.Json;
using System.Text.Json.Serialization;

namespace MimeSpy;

/// <summary>
/// Parses the embedded mime-type table once and buckets it by file extension so a
/// signature match's extensions can be resolved to their mime type(s) directly,
/// instead of scanning every entry in the table.
/// </summary>
internal static class MimeTypeIndex
{
    private const string ResourceName = "mimeTypes.json";

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

        var raw = JsonSerializer.Deserialize<Dictionary<string, RawMimeType>>(stream)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is empty or invalid.");

        foreach (var (name, entry) in raw)
        {
            // Most entries in the source table have no known file extension at all;
            // they can never be reached from a signature match, so skip them.
            if (entry.Extensions is not { Count: > 0 })
            {
                continue;
            }

            yield return new MimeType(name, [.. entry.Extensions]);
        }
    }

    private sealed class RawMimeType
    {
        [JsonPropertyName("extensions")]
        public List<string>? Extensions { get; init; }
    }
}
