using System.Text;

namespace MimeSpy;

/// <summary>
/// A plain ZIP local file header (<c>PK\x03\x04</c>) is shared verbatim by dozens of
/// formats that are "just a zip" underneath - docx/xlsx/pptx, jar, apk, odt/odp/ott,
/// epub, and more. The header bytes alone can't tell them apart; the only thing that
/// can is the name (and, for ODF/EPUB, the stored content) of the archive's first
/// entry. This peeks at that first entry using only the bytes already supplied to
/// <see cref="MimeSpy.Spy(ReadOnlySpan{byte})"/> - it never seeks to the central
/// directory at the end of the file, since callers may only have handed us a
/// truncated prefix of it.
/// </summary>
internal static class ZipContainerSniffer
{
    private const int LocalFileHeaderSize = 30;
    private const ushort Stored = 0;

    public static string? SniffWrappedExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < LocalFileHeaderSize
            || bytes[0] != 0x50 || bytes[1] != 0x4B || bytes[2] != 0x03 || bytes[3] != 0x04)
        {
            return null;
        }

        var compressionMethod = ReadUInt16(bytes, 8);
        var uncompressedSize = ReadUInt32(bytes, 22);
        var nameLength = ReadUInt16(bytes, 26);
        var extraFieldLength = ReadUInt16(bytes, 28);

        var nameStart = LocalFileHeaderSize;
        var nameEnd = nameStart + nameLength;
        if (nameEnd > bytes.Length)
        {
            return null;
        }

        var name = Encoding.ASCII.GetString(bytes.Slice(nameStart, nameLength).ToArray());

        switch (name)
        {
            case "[Content_Types].xml":
                return "docx";
            case "META-INF/MANIFEST.MF":
                return "jar";
            case "AndroidManifest.xml":
                return "apk";
            case "mimetype":
                return SniffFromMimetypeEntry(bytes, nameEnd + extraFieldLength, compressionMethod, uncompressedSize);
            default:
                return null;
        }
    }

    private static string? SniffFromMimetypeEntry(ReadOnlySpan<byte> bytes, int contentStart, ushort compressionMethod, uint uncompressedSize)
    {
        // The "mimetype" entry is only meaningful for ODF detection when it's stored
        // rather than deflated, since ODF requires it to be stored - and only then
        // can its bytes be read directly without decompression. (EPUB uses the same
        // convention, but this source table only ever exposes EPUB as its own
        // longer, more specific signature - "50 4B 03 04 0A 00 02 00" - which already
        // wins outright via the header-length comparison, so it never reaches here
        // as part of a tied, ambiguous match to narrow down.)
        if (compressionMethod != Stored)
        {
            return null;
        }

        // Both operands are non-negative and bounded well under int.MaxValue, so this
        // can't overflow the way `contentStart + (int)uncompressedSize` could if
        // uncompressedSize were an attacker-controlled value near uint.MaxValue.
        var contentEnd = (long)contentStart + uncompressedSize;
        if (contentStart < 0 || contentEnd > bytes.Length)
        {
            return null;
        }

        var content = Encoding.ASCII.GetString(bytes.Slice(contentStart, (int)contentEnd - contentStart).ToArray());

        return content.StartsWith("application/vnd.oasis.opendocument", StringComparison.Ordinal) ? "odt" : null;
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
