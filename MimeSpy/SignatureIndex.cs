using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MimeSpy;

/// <summary>
/// Parses the embedded file-signature table once and buckets the entries by
/// (header offset, first header byte) so a lookup only has to compare against
/// the handful of signatures that could plausibly match, instead of all of them.
/// </summary>
internal static class SignatureIndex
{
    private const string ResourceName = "file_signatures.json";

    private static readonly Dictionary<(int Offset, byte FirstByte), List<FileSignature>> Buckets;
    private static readonly int[] Offsets;

    /// <summary>
    /// The number of leading bytes needed to reach every signature in the embedded
    /// table - the offset plus length of whichever signature runs deepest. Derived
    /// from the table itself so it can't drift out of sync as entries change.
    /// </summary>
    public static readonly int MaxHeaderReach;

    static SignatureIndex()
    {
        Buckets = [];
        var maxHeaderReach = 0;

        foreach (var signature in LoadSignatures())
        {
            var key = (signature.HeaderOffset, signature.Header[0]);
            if (!Buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                Buckets[key] = bucket;
            }

            bucket.Add(signature);

            var reach = signature.HeaderOffset + signature.Header.Length;
            if (reach > maxHeaderReach)
            {
                maxHeaderReach = reach;
            }
        }

        Offsets = Buckets.Keys.Select(k => k.Offset).Distinct().ToArray();
        MaxHeaderReach = maxHeaderReach;
    }

    public static IReadOnlyList<FileSignature> FindMatches(ReadOnlySpan<byte> bytes)
    {
        List<FileSignature>? matches = null;

        foreach (var offset in Offsets)
        {
            if (bytes.Length <= offset || !Buckets.TryGetValue((offset, bytes[offset]), out var candidates))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var end = offset + candidate.Header.Length;
                if (end > bytes.Length || !bytes.Slice(offset, candidate.Header.Length).SequenceEqual(candidate.Header))
                {
                    continue;
                }

                matches ??= [];
                matches.Add(candidate);
            }
        }

        return matches is null ? Array.Empty<FileSignature>() : matches;
    }

    private static IEnumerable<FileSignature> LoadSignatures()
    {
        using var stream = typeof(SignatureIndex).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

        var raw = JsonSerializer.Deserialize<RawSignatureFile>(stream)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is empty or invalid.");

        foreach (var entry in raw.FileSigs)
        {
            // A signature with no usable byte pattern can never be matched against a
            // real file, so it's dropped here rather than carried forward as null.
            if (!TryParseHex(entry.HeaderHex, out var header))
            {
                continue;
            }

            var extensions = ParseExtensions(entry.Extension);
            var (mimeTypes, primaryMimeType) = ResolveMimeTypes(extensions);

            yield return new FileSignature(
                header,
                ParseOffset(entry.HeaderOffset),
                extensions,
                mimeTypes,
                primaryMimeType,
                entry.Description ?? string.Empty);
        }
    }

    private static (string[] MimeTypes, string? Primary) ResolveMimeTypes(string[] extensions)
    {
        var counts = new Dictionary<string, int>();
        var order = new List<string>();

        foreach (var extension in extensions)
        {
            foreach (var mimeType in MimeTypeIndex.FindByExtension(extension))
            {
                if (counts.TryGetValue(mimeType.Name, out var count))
                {
                    counts[mimeType.Name] = count + 1;
                }
                else
                {
                    counts[mimeType.Name] = 1;
                    order.Add(mimeType.Name);
                }
            }
        }

        if (order.Count == 0)
        {
            return ([], null);
        }

        // The mime type backed by the most of this signature's extension aliases
        // (e.g. jpg/jpeg/jpe all resolving to image/jpeg, vs jfif alone resolving
        // to image/pjpeg) is treated as the canonical one for the signature.
        // OrderByDescending is a stable sort, so ties keep the first-seen mime type.
        var primary = order.OrderByDescending(name => counts[name]).First();

        return ([.. order], primary);
    }

    private static bool TryParseHex(string? raw, out byte[] header)
    {
        header = [];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Length != 2 || !byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        header = bytes;
        return true;
    }

    private static int ParseOffset(string? raw)
    {
        if (raw is null)
        {
            return 0;
        }

        // A few rows in the source table have junk trailing the offset (e.g. "0(null)");
        // take the leading digits and ignore the rest rather than dropping the whole entry.
        var span = raw.AsSpan().TrimStart();
        var digitCount = 0;
        while (digitCount < span.Length && char.IsAsciiDigit(span[digitCount]))
        {
            digitCount++;
        }

        return digitCount > 0 ? int.Parse(span[..digitCount]) : 0;
    }

    private static string[] ParseExtensions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return [.. raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => !e.Equals("(none)", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.ToLowerInvariant())];
    }

    private sealed class RawSignatureFile
    {
        [JsonPropertyName("filesigs")]
        public List<RawSignature> FileSigs { get; init; } = [];
    }

    private sealed class RawSignature
    {
        [JsonPropertyName("File description")]
        public string? Description { get; init; }

        [JsonPropertyName("Header (hex)")]
        public string? HeaderHex { get; init; }

        [JsonPropertyName("File extension")]
        public string? Extension { get; init; }

        [JsonPropertyName("Header offset")]
        public string? HeaderOffset { get; init; }
    }
}
