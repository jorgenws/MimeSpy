using System.Globalization;

namespace MimeSpy;

/// <summary>
/// An Ogg page's outer "OggS" signature is shared verbatim by audio (Vorbis, Opus,
/// Speex, FLAC-in-Ogg), video (Theora, OGM, Skeleton), and non-audio/video (Kate)
/// payloads - the header alone can't say which. The only thing that can is the
/// codec identification packet, which starts right after the first page's header
/// - see Resources/ogg_codec_identifiers.csv for where that offset and the
/// per-codec byte patterns come from. This only ever reads the bytes already
/// supplied to <see cref="MimeSpy.Spy(ReadOnlySpan{byte})"/>, matching how
/// <see cref="ZipContainerSniffer"/> treats a possibly-truncated buffer.
/// </summary>
internal sealed class OggContainerSniffer : IContainerSniffer
{
    private const string ResourceName = "ogg_codec_identifiers.csv";
    private const int CodecIdentifierOffset = 28;

    private static readonly IReadOnlyList<(byte[] Identifier, string MimeType)> Codecs = LoadCodecs();

    public IReadOnlyList<Result> Refine(ReadOnlySpan<byte> bytes, IReadOnlyList<Result> results)
    {
        return PrimaryMimeTypeRefiner.Apply(results, SniffMimeType(bytes));
    }

    private static string? SniffMimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= CodecIdentifierOffset)
        {
            return null;
        }

        var identifierBytes = bytes[CodecIdentifierOffset..];
        foreach (var (identifier, mimeType) in Codecs)
        {
            if (identifierBytes.Length >= identifier.Length && identifierBytes[..identifier.Length].SequenceEqual(identifier))
            {
                return mimeType;
            }
        }

        return null;
    }

    private static List<(byte[] Identifier, string MimeType)> LoadCodecs()
    {
        using var stream = typeof(OggContainerSniffer).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);

        var codecs = new List<(byte[], string)>();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length < 3 || !TryParseHex(fields[1], out var identifier))
            {
                continue;
            }

            codecs.Add((identifier, fields[2]));
        }

        return codecs;
    }

    private static bool TryParseHex(string raw, out byte[] identifier)
    {
        identifier = [];

        var tokens = raw.Split([' '], StringSplitOptions.RemoveEmptyEntries);
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

        identifier = bytes;
        return true;
    }
}
