using System.Globalization;

namespace MimeSpy;

/// <summary>
/// Parses the embedded file-signature table once and buckets the entries by
/// (header offset, first header byte) so a lookup only has to compare against
/// the handful of signatures that could plausibly match, instead of all of them.
/// </summary>
internal static class SignatureIndex
{
    private const string ResourceName = "file_signatures.csv";

    // file_signatures.csv is a straight copy of Gary Kessler's table (see the root
    // README) and needs to stay overwritable by a newer copy of it without losing
    // anything MimeSpy adds on top - so additions live here instead, in the same
    // format, merged in at load time. See docs/adr/0010.
    private const string SupplementalResourceName = "file_signatures_supplemental.csv";

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
        var baseSignatures = LoadSignatures(ResourceName).ToList();
        var supplementalSignatures = LoadSignatures(SupplementalResourceName).ToList();

        return Merge(baseSignatures, supplementalSignatures);
    }

    /// <summary>
    /// A supplemental row sharing a base row's exact header bytes and offset claims
    /// any of its own extensions away from that base row, rather than just tying
    /// alongside it. This is how the upstream "OpenDocument template" row - one row,
    /// bundling odt/odp/ott as if they were interchangeable aliases the way jpg/jpeg
    /// genuinely are - gets split into three independently reachable extensions
    /// (plus the previously entirely-missing ods/ots/otp) without ever editing
    /// file_signatures.csv. See docs/adr/0010.
    /// </summary>
    private static IEnumerable<FileSignature> Merge(List<FileSignature> baseSignatures, List<FileSignature> supplementalSignatures)
    {
        foreach (var signature in baseSignatures)
        {
            var claimed = new HashSet<string>(supplementalSignatures
                .Where(s => s.HeaderOffset == signature.HeaderOffset && s.Header.SequenceEqual(signature.Header))
                .SelectMany(s => s.Extensions));

            if (claimed.Count == 0)
            {
                yield return signature;
                continue;
            }

            var remaining = signature.Extensions.Where(e => !claimed.Contains(e)).ToArray();
            if (remaining.Length == signature.Extensions.Length)
            {
                yield return signature;
                continue;
            }

            if (remaining.Length == 0)
            {
                // Every extension this row had was claimed by the supplemental
                // file - it has nothing left to reach, so drop it rather than
                // keeping a hollowed-out tie member around.
                continue;
            }

            yield return signature with { Extensions = remaining, MimeTypes = ResolveMimeTypes(remaining) };
        }

        foreach (var signature in supplementalSignatures)
        {
            yield return signature;
        }
    }

    private static IEnumerable<FileSignature> LoadSignatures(string resourceName)
    {
        using var stream = typeof(SignatureIndex).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);

        // Columns: File description, Header (hex), File extension, FileClass, Header
        // offset, Trailer (hex). No header row. FileClass and Trailer aren't used by
        // this library. Fields are never quoted or comma-escaped in the source data.
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');

            // A signature with no usable byte pattern can never be matched against a
            // real file, so it's dropped here rather than carried forward as null.
            if (fields.Length < 5 || !TryParseHex(fields[1], out var header))
            {
                continue;
            }

            var extensions = ParseExtensions(fields[2]);

            yield return new FileSignature(
                header,
                ParseOffset(fields[4]),
                extensions,
                ResolveMimeTypes(extensions),
                fields[0]);
        }
    }

    // Which of these is the signature's "primary" mime type isn't decided here -
    // that's request-time information (see docs/adr/0013), computed by Result itself
    // from this same Extensions/MimeTypes data plus whatever a content sniffer found.
    private static string[] ResolveMimeTypes(string[] extensions)
    {
        var seen = new HashSet<string>();
        var order = new List<string>();

        foreach (var extension in extensions)
        {
            foreach (var mimeType in MimeTypeIndex.FindByExtension(extension))
            {
                if (seen.Add(mimeType.Name))
                {
                    order.Add(mimeType.Name);
                }
            }
        }

        return [.. order];
    }

    private static bool TryParseHex(string? raw, out byte[] header)
    {
        header = [];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var tokens = raw!.Split([' '], StringSplitOptions.RemoveEmptyEntries);
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
        while (digitCount < span.Length && span[digitCount] is >= '0' and <= '9')
        {
            digitCount++;
        }

        return digitCount > 0 ? int.Parse(span.Slice(0, digitCount).ToString()) : 0;
    }

    private static string[] ParseExtensions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return [.. raw!.Split(['|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !e.Equals("(none)", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.ToLowerInvariant())];
    }
}
