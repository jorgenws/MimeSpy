using System.Text;

namespace MimeSpy;

/// <summary>
/// A plain ZIP local file header (<c>PK\x03\x04</c>) is shared verbatim by dozens of
/// formats that are "just a zip" underneath - docx/xlsx/pptx, jar, apk, odt/odp/ott,
/// epub, and more. The header bytes alone can't tell them apart; the only thing that
/// can is the name (and, for ODF/EPUB, the stored content) of the archive's first
/// entry that isn't just a zero-length directory placeholder - real jar and docx
/// writers commonly emit one of those (<c>META-INF/</c>, <c>_rels/</c>) before the
/// entry that actually matters, see docs/adr/0009. This peeks at that entry using
/// only the bytes already supplied to <see cref="MimeSpy.Spy(ReadOnlySpan{byte})"/> -
/// it never seeks to the central directory at the end of the file, since callers may
/// only have handed us a truncated prefix of it.
/// </summary>
internal static class ZipContainerSniffer
{
    private const int LocalFileHeaderSize = 30;
    private const ushort Stored = 0;

    public static string? SniffWrappedExtension(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;

        while (TryReadLocalFileHeader(bytes, offset, out var entry))
        {
            if (entry.Name.EndsWith("/", StringComparison.Ordinal) && entry.CompressedSize == 0)
            {
                offset = entry.DataEnd;
                continue;
            }

            return entry.Name switch
            {
                "[Content_Types].xml" => "docx",
                "META-INF/MANIFEST.MF" => "jar",
                "AndroidManifest.xml" => "apk",
                "mimetype" => SniffFromMimetypeEntry(bytes, entry),
                _ => null,
            };
        }

        return null;
    }

    private readonly record struct ZipEntry(string Name, ushort CompressionMethod, uint UncompressedSize, uint CompressedSize, int DataStart, int DataEnd);

    private static bool TryReadLocalFileHeader(ReadOnlySpan<byte> bytes, int offset, out ZipEntry entry)
    {
        entry = default;

        if (bytes.Length - offset < LocalFileHeaderSize
            || bytes[offset] != 0x50 || bytes[offset + 1] != 0x4B || bytes[offset + 2] != 0x03 || bytes[offset + 3] != 0x04)
        {
            return false;
        }

        var compressionMethod = ReadUInt16(bytes, offset + 8);
        var compressedSize = ReadUInt32(bytes, offset + 18);
        var uncompressedSize = ReadUInt32(bytes, offset + 22);
        var nameLength = ReadUInt16(bytes, offset + 26);
        var extraFieldLength = ReadUInt16(bytes, offset + 28);

        var nameStart = offset + LocalFileHeaderSize;
        var nameEnd = nameStart + nameLength;
        if (nameEnd > bytes.Length)
        {
            return false;
        }

        var name = Encoding.ASCII.GetString(bytes.Slice(nameStart, nameLength).ToArray());

        var dataStart = nameEnd + extraFieldLength;
        // A long here (rather than int) so a maliciously large compressedSize can't
        // wrap the addition back into range and slip past the int.MaxValue check.
        var dataEnd = (long)dataStart + compressedSize;
        if (dataEnd > int.MaxValue)
        {
            return false;
        }

        entry = new ZipEntry(name, compressionMethod, uncompressedSize, compressedSize, dataStart, (int)dataEnd);
        return true;
    }

    // The stored "mimetype" entry's exact content names which ODF sub-format this
    // is - odt/ods/odp are different extensions with different signature-table
    // rows (ods/ots/otp only exist in the supplemental one, see docs/adr/0010),
    // so this has to read what it actually says rather than assuming text/odt.
    private static readonly Dictionary<string, string> ExtensionsByOpenDocumentMimeType = new(StringComparer.Ordinal)
    {
        ["application/vnd.oasis.opendocument.text"] = "odt",
        ["application/vnd.oasis.opendocument.text-template"] = "ott",
        ["application/vnd.oasis.opendocument.spreadsheet"] = "ods",
        ["application/vnd.oasis.opendocument.spreadsheet-template"] = "ots",
        ["application/vnd.oasis.opendocument.presentation"] = "odp",
        ["application/vnd.oasis.opendocument.presentation-template"] = "otp",
    };

    private static string? SniffFromMimetypeEntry(ReadOnlySpan<byte> bytes, ZipEntry entry)
    {
        // The "mimetype" entry is only meaningful for ODF detection when it's stored
        // rather than deflated, since ODF requires it to be stored - and only then
        // can its bytes be read directly without decompression. (EPUB uses the same
        // convention, but this source table only ever exposes EPUB as its own
        // longer, more specific signature - "50 4B 03 04 0A 00 02 00" - which already
        // wins outright via the header-length comparison, so it never reaches here
        // as part of a tied, ambiguous match to narrow down.)
        if (entry.CompressionMethod != Stored)
        {
            return null;
        }

        // Both operands are non-negative and bounded well under int.MaxValue, so this
        // can't overflow the way `entry.DataStart + (int)entry.UncompressedSize` could
        // if UncompressedSize were an attacker-controlled value near uint.MaxValue.
        var contentEnd = (long)entry.DataStart + entry.UncompressedSize;
        if (contentEnd > bytes.Length)
        {
            return null;
        }

        var content = Encoding.ASCII.GetString(bytes.Slice(entry.DataStart, (int)contentEnd - entry.DataStart).ToArray());

        return ExtensionsByOpenDocumentMimeType.TryGetValue(content, out var extension) ? extension : null;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
    {
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }
}
