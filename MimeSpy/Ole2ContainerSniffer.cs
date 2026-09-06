using System.Text;

namespace MimeSpy;

/// <summary>
/// The OLE2/Compound File Binary Format header (<c>D0 CF 11 E0 A1 B1 1A E1</c>) is
/// shared verbatim by dozens of unrelated legacy formats - Word/Excel/PowerPoint 97,
/// Access, Visio, Publisher, MSI, and more (see docs/adr/0011) - and, unlike ZIP's
/// local file header, none of it identifies which one a given file is: that lives in
/// the named streams inside the file's own directory sector, not in any fixed-offset
/// byte pattern (checked against file_signatures.csv's offset-512 "subheader" rows,
/// see docs/adr/0011 for why those don't match real files). Apache Tika/POI resolve
/// this the same way: fully parse the CFB structure and match on the root directory's
/// entry names ("WordDocument", "Workbook", "PowerPoint Document", ...).
///
/// This implements just enough of MS-CFB
/// (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/) to reach
/// those names: the fixed 512-byte header, the FAT sector chain (DIFAT entries taken
/// only from the header's embedded array - up to 109 FAT sectors, good for roughly the
/// first 7MB of a 512-byte-sector file - never the additional-DIFAT-sector chain
/// needed beyond that), and a linear scan of every 128-byte directory entry reachable
/// through it. It doesn't walk the directory's red-black tree to tell root-level
/// entries from nested ones - like Tika's flat <c>Set&lt;String&gt;</c> of names, any
/// entry with one of the recognized names is enough, since none of the recognized
/// names are expected to occur as a coincidental nested entry in an unrelated format.
///
/// Consistent with <see cref="ZipContainerSniffer"/> (docs/adr/0002): only ever reads
/// bytes already supplied to <see cref="MimeSpy.Spy(ReadOnlySpan{byte})"/>, and any
/// out-of-bounds sector reference (truncated input, or a file bigger than the
/// DIFAT-in-header reach above) just stops the scan and falls back to the full tied
/// list rather than guessing.
/// </summary>
internal sealed class Ole2ContainerSniffer : IContainerSniffer
{
    private const int HeaderSize = 512;
    private const int DirectoryEntrySize = 128;
    private const int DifatEntryCount = 109;
    private const int DifatStartOffset = 76;
    private const int SectorShiftOffset = 30;
    private const int FirstDirectorySectorOffset = 48;

    // Sector numbers at or above this are FAT chain markers (free/end-of-chain/FAT
    // sector/DIFAT sector), never real sector locations - MS-CFB 2.1.
    private const uint MinReservedSector = 0xFFFFFFFC;

    private static readonly Dictionary<string, string> ExtensionsByDirectoryEntryName = new(StringComparer.Ordinal)
    {
        ["WordDocument"] = "doc",
        ["Workbook"] = "xls",
        ["Book"] = "xls",
        ["PowerPoint Document"] = "ppt",
    };

    public IReadOnlyList<Result> Refine(ReadOnlySpan<byte> bytes, IReadOnlyList<Result> results)
    {
        return WrappedExtensionNarrower.Apply(results, SniffWrappedExtension(bytes));
    }

    private static string? SniffWrappedExtension(ReadOnlySpan<byte> bytes)
    {
        if (!TryReadHeader(bytes, out var header))
        {
            return null;
        }

        foreach (var name in EnumerateDirectoryEntryNames(bytes, header))
        {
            if (ExtensionsByDirectoryEntryName.TryGetValue(name, out var extension))
            {
                return extension;
            }
        }

        return null;
    }

    private readonly record struct Ole2Header(int SectorSize, uint FirstDirectorySector, uint[] Difat);

    private static bool TryReadHeader(ReadOnlySpan<byte> bytes, out Ole2Header header)
    {
        header = default;

        if (bytes.Length < HeaderSize)
        {
            return false;
        }

        var sectorShift = ReadUInt16(bytes, SectorShiftOffset);
        // Real-world sector sizes are 512 (major version 3) or 4096 (major version 4);
        // anything else isn't a well-formed CFB header.
        if (sectorShift is not (9 or 12))
        {
            return false;
        }

        var difat = new uint[DifatEntryCount];
        for (var i = 0; i < DifatEntryCount; i++)
        {
            difat[i] = ReadUInt32(bytes, DifatStartOffset + (i * 4));
        }

        header = new Ole2Header(1 << sectorShift, ReadUInt32(bytes, FirstDirectorySectorOffset), difat);
        return true;
    }

    private static IEnumerable<string> EnumerateDirectoryEntryNames(ReadOnlySpan<byte> bytes, Ole2Header header)
    {
        // ReadOnlySpan<T> can't be captured by the iterator state machine a "yield
        // return" method compiles to, so the scan is collected eagerly into a list
        // first - this format's directory stream is small enough (a handful of
        // sectors at most, for the files this sniffer targets) that giving up the
        // early-exit-on-first-match laziness costs nothing in practice.
        var names = new List<string>();
        var visitedSectors = new HashSet<uint>();
        var sector = header.FirstDirectorySector;

        while (sector < MinReservedSector && visitedSectors.Add(sector))
        {
            var sectorOffset = SectorByteOffset(sector, header.SectorSize);
            if (sectorOffset < 0 || sectorOffset + header.SectorSize > bytes.Length)
            {
                break;
            }

            var offset = (int)sectorOffset;
            var entriesInSector = header.SectorSize / DirectoryEntrySize;
            for (var i = 0; i < entriesInSector; i++)
            {
                var entryOffset = offset + (i * DirectoryEntrySize);
                var name = TryReadDirectoryEntryName(bytes, entryOffset);
                if (name is not null)
                {
                    names.Add(name);
                }
            }

            if (!TryGetNextSector(bytes, header, sector, out sector))
            {
                break;
            }
        }

        return names;
    }

    private static string? TryReadDirectoryEntryName(ReadOnlySpan<byte> bytes, int entryOffset)
    {
        // Directory Entry Name Length (offset 64, 2 bytes) counts the name's UTF-16LE
        // bytes including its terminating null character - an empty/unused slot has
        // this at 0. MS-CFB 2.6.1.
        var nameLengthBytes = ReadUInt16(bytes, entryOffset + 64);
        if (nameLengthBytes < 2 || nameLengthBytes > 64 || nameLengthBytes % 2 != 0)
        {
            return null;
        }

        var charCount = (nameLengthBytes - 2) / 2;
        return charCount == 0 ? null : Encoding.Unicode.GetString(bytes.Slice(entryOffset, charCount * 2).ToArray());
    }

    private static bool TryGetNextSector(ReadOnlySpan<byte> bytes, Ole2Header header, uint sector, out uint next)
    {
        next = 0;

        var entriesPerFatSector = (uint)(header.SectorSize / 4);
        var fatSectorIndex = sector / entriesPerFatSector;
        if (fatSectorIndex >= (uint)header.Difat.Length)
        {
            return false;
        }

        var fatSector = header.Difat[fatSectorIndex];
        if (fatSector >= MinReservedSector)
        {
            return false;
        }

        var fatSectorOffset = SectorByteOffset(fatSector, header.SectorSize);
        var entryOffset = fatSectorOffset + ((sector % entriesPerFatSector) * 4);
        if (entryOffset < 0 || entryOffset + 4 > bytes.Length)
        {
            return false;
        }

        next = ReadUInt32(bytes, (int)entryOffset);
        return true;
    }

    // Sector 0 starts right after the fixed-size header, regardless of the header's
    // own (possibly larger, zero-padded) sector size - MS-CFB 2.2. Computed as a long
    // so a corrupt/huge sector number can't wrap back into the file's actual bounds.
    private static long SectorByteOffset(uint sector, int sectorSize)
    {
        return (sector + 1L) * sectorSize;
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
