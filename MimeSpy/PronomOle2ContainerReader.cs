using System.Text;

namespace MimeSpy;

/// <summary>
/// A minimal read-only MS-CFB (Compound File Binary Format,
/// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/) reader for
/// the container signature engine: given the complete bytes of an OLE2 file, resolves
/// a (possibly nested, e.g. <c>"Document/Page1"</c>) stream path to its full content.
/// Never copies the array it's given (see <see cref="TryOpen"/>'s own doc) - a large
/// file is held once, not once per reader that opens it.
///
/// Unlike <see cref="Ole2ContainerSniffer"/> - which only needs directory entry
/// *names*, flatly, and so skips both stream content and the directory's red-black
/// tree structure entirely - this needs real content (via the regular FAT chain for
/// large streams, or the MiniFAT/mini-stream for small ones) and real path
/// resolution (via that tree), so it's a separate, more complete reader rather than
/// an extension of that one. Like it, this only follows FAT sectors reachable
/// through the header's own embedded DIFAT array (up to 109 entries - good for
/// roughly the first 7MB of a 512-byte-sector file), never the additional-DIFAT-
/// sector chain beyond that.
/// </summary>
internal sealed class PronomOle2ContainerReader
{
    private const int HeaderSize = 512;
    private const int DirectoryEntrySize = 128;
    private const int DifatEntryCount = 109;
    private const int MiniSectorSize = 64;

    // Sector numbers at or above this are FAT chain markers (free/end-of-chain/FAT
    // sector/DIFAT sector), never real sector locations - MS-CFB 2.1.
    private const uint MinReservedSector = 0xFFFFFFFC;
    private const uint NoStream = 0xFFFFFFFF;

    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly byte[] _bytes;
    private readonly int _length;
    private readonly int _sectorSize;
    private readonly uint[] _difat;
    private readonly uint _firstMiniFatSector;
    private readonly uint _miniFatSectorCount;
    private readonly ulong _miniStreamCutoff;
    private readonly List<DirectoryEntry> _entries;
    private readonly int _rootIndex;

    private byte[]? _miniFat;
    private byte[]? _miniStream;

    private readonly record struct DirectoryEntry(string Name, byte ObjectType, uint Left, uint Right, uint Child, uint StartSector, ulong StreamSize);

    private PronomOle2ContainerReader(byte[] bytes, int length, int sectorSize, uint[] difat, uint firstMiniFatSector, uint miniFatSectorCount, ulong miniStreamCutoff, List<DirectoryEntry> entries, int rootIndex)
    {
        _bytes = bytes;
        _length = length;
        _sectorSize = sectorSize;
        _difat = difat;
        _firstMiniFatSector = firstMiniFatSector;
        _miniFatSectorCount = miniFatSectorCount;
        _miniStreamCutoff = miniStreamCutoff;
        _entries = entries;
        _rootIndex = rootIndex;
    }

    /// <summary>
    /// Opens an OLE2 file's <paramref name="length"/> logical bytes, backed by
    /// <paramref name="bytes"/> - which the reader never copies, but which may
    /// itself be larger than <paramref name="length"/> (e.g. a growable buffer's
    /// backing array), so every bounds check is against <paramref name="length"/>,
    /// never <c>bytes.Length</c>.
    /// </summary>
    public static PronomOle2ContainerReader? TryOpen(byte[] bytes, int length)
    {
        if (length < HeaderSize || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
        {
            return null;
        }

        var sectorShift = ReadUInt16(bytes, 30);
        if (sectorShift is not (9 or 12))
        {
            return null;
        }

        var sectorSize = 1 << sectorShift;
        var firstDirectorySector = ReadUInt32(bytes, 48);
        var miniStreamCutoff = ReadUInt32(bytes, 56);
        var firstMiniFatSector = ReadUInt32(bytes, 60);
        var miniFatSectorCount = ReadUInt32(bytes, 64);

        var difat = new uint[DifatEntryCount];
        for (var i = 0; i < DifatEntryCount; i++)
        {
            difat[i] = ReadUInt32(bytes, 76 + (i * 4));
        }

        var entries = ReadDirectoryEntries(bytes, length, sectorSize, difat, firstDirectorySector);
        var rootIndex = entries.FindIndex(e => e.ObjectType == 5);
        if (rootIndex < 0)
        {
            return null;
        }

        return new PronomOle2ContainerReader(bytes, length, sectorSize, difat, firstMiniFatSector, miniFatSectorCount, miniStreamCutoff, entries, rootIndex);
    }

    /// <summary>
    /// The full content of the stream at <paramref name="path"/> (segments separated
    /// by <c>/</c> for a stream nested inside one or more storages), or null if any
    /// segment doesn't exist or isn't the expected kind (storage for every segment
    /// but the last, stream for the last).
    /// </summary>
    public byte[]? TryReadStream(string path)
    {
        var segments = path.Split('/');
        var currentChild = _entries[_rootIndex].Child;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var index = FindChild(currentChild, segments[i]);
            if (index is not int storageIndex || _entries[storageIndex].ObjectType != 1)
            {
                return null;
            }

            currentChild = _entries[storageIndex].Child;
        }

        var leafIndex = FindChild(currentChild, segments[^1]);
        if (leafIndex is not int streamIndex || _entries[streamIndex].ObjectType != 2)
        {
            return null;
        }

        var leaf = _entries[streamIndex];
        return leaf.StreamSize < _miniStreamCutoff
            ? ReadMiniChain(leaf.StartSector, (long)leaf.StreamSize)
            : ReadRegularChain(leaf.StartSector, (long)leaf.StreamSize);
    }

    // Several well-known streams (CompObj, SummaryInformation, ...) are conventionally
    // stored with a leading control character (0x01-0x05) that has no meaning for
    // name matching - PRONOM's own container-signature Paths are written without it
    // (plain "CompObj", not "CompObj"), so comparisons strip it before matching.
    //
    // This searches the whole subtree under nodeId rather than steering by comparison
    // the way a real BST search would: the directory's Left/Right pointers were built
    // by the file's own writer sorting on the *unstripped* names, so blindly trusting
    // comparison-against-a-stripped-name to pick the correct branch could walk past
    // the very entry being looked for. Directory sizes here are always small (a
    // handful of entries), so the lost O(log n) doesn't cost anything real.
    private int? FindChild(uint nodeId, string name)
    {
        if (nodeId == NoStream || nodeId >= (uint)_entries.Count)
        {
            return null;
        }

        var entry = _entries[(int)nodeId];
        var entryName = entry.Name.Length > 0 && entry.Name[0] < 0x20 ? entry.Name[1..] : entry.Name;
        if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
        {
            return (int)nodeId;
        }

        return FindChild(entry.Left, name) ?? FindChild(entry.Right, name);
    }

    private static List<DirectoryEntry> ReadDirectoryEntries(byte[] bytes, int length, int sectorSize, uint[] difat, uint firstDirectorySector)
    {
        var entries = new List<DirectoryEntry>();
        var visited = new HashSet<uint>();
        var sector = firstDirectorySector;

        while (sector < MinReservedSector && visited.Add(sector))
        {
            var offset = SectorByteOffset(sector, sectorSize);
            if (offset < 0 || offset + sectorSize > length)
            {
                break;
            }

            var entriesInSector = sectorSize / DirectoryEntrySize;
            for (var i = 0; i < entriesInSector; i++)
            {
                entries.Add(ParseDirectoryEntry(bytes, (int)offset + (i * DirectoryEntrySize)));
            }

            if (!TryGetNextSector(bytes, length, sectorSize, difat, sector, out sector))
            {
                break;
            }
        }

        return entries;
    }

    private static DirectoryEntry ParseDirectoryEntry(byte[] bytes, int entryOffset)
    {
        // Directory Entry Name Length (offset 64, 2 bytes) counts the name's
        // UTF-16LE bytes including its terminating null character - MS-CFB 2.6.1.
        var nameLengthBytes = ReadUInt16(bytes, entryOffset + 64);
        var name = nameLengthBytes is >= 2 and <= 64 && nameLengthBytes % 2 == 0
            ? Encoding.Unicode.GetString(bytes, entryOffset, (nameLengthBytes - 2))
            : string.Empty;

        return new DirectoryEntry(
            name,
            bytes[entryOffset + 66],
            ReadUInt32(bytes, entryOffset + 68),
            ReadUInt32(bytes, entryOffset + 72),
            ReadUInt32(bytes, entryOffset + 76),
            ReadUInt32(bytes, entryOffset + 116),
            ReadUInt64(bytes, entryOffset + 120));
    }

    private byte[]? ReadRegularChain(uint startSector, long length)
    {
        if (length == 0)
        {
            return [];
        }

        var result = new byte[length];
        var sector = startSector;
        var visited = new HashSet<uint>();
        long written = 0;

        while (written < length)
        {
            if (sector >= MinReservedSector || !visited.Add(sector))
            {
                return null;
            }

            var offset = SectorByteOffset(sector, _sectorSize);
            if (offset < 0 || offset + _sectorSize > _length)
            {
                return null;
            }

            var toCopy = (int)Math.Min(_sectorSize, length - written);
            Array.Copy(_bytes, offset, result, written, toCopy);
            written += toCopy;

            if (written >= length || !TryGetNextSector(_bytes, _length, _sectorSize, _difat, sector, out sector))
            {
                break;
            }
        }

        return written == length ? result : null;
    }

    private byte[]? ReadMiniChain(uint startSector, long length)
    {
        if (length == 0)
        {
            return [];
        }

        var miniStream = GetMiniStream();
        if (miniStream is null)
        {
            return null;
        }

        var result = new byte[length];
        var sector = startSector;
        var visited = new HashSet<uint>();
        long written = 0;

        while (written < length)
        {
            if (sector >= MinReservedSector || !visited.Add(sector))
            {
                return null;
            }

            var offset = (long)sector * MiniSectorSize;
            if (offset < 0 || offset + MiniSectorSize > miniStream.Length)
            {
                return null;
            }

            var toCopy = (int)Math.Min(MiniSectorSize, length - written);
            Array.Copy(miniStream, offset, result, written, toCopy);
            written += toCopy;

            if (written >= length)
            {
                break;
            }

            sector = GetMiniFatNext(sector);
        }

        return written == length ? result : null;
    }

    // The mini-stream is the root storage entry's own data - an ordinary stream, read
    // via the regular FAT chain like any other - and every small stream's bytes live
    // somewhere inside it, addressed by 64-byte mini-sectors instead of regular ones.
    private byte[]? GetMiniStream() => _miniStream ??= ReadRegularChain(_entries[_rootIndex].StartSector, (long)_entries[_rootIndex].StreamSize);

    // The MiniFAT (mini-sector next-pointers, same shape as the regular FAT but for
    // the mini-stream) is itself stored as a chain of ordinary sectors, so it's read
    // once, in full, via the regular FAT, rather than re-walked per lookup.
    private byte[]? GetMiniFat() => _miniFat ??= _miniFatSectorCount == 0
        ? []
        : ReadRegularChain(_firstMiniFatSector, (long)_miniFatSectorCount * _sectorSize);

    private uint GetMiniFatNext(uint miniSector)
    {
        var fat = GetMiniFat();
        var offset = (long)miniSector * 4;
        return fat is null || offset < 0 || offset + 4 > fat.Length ? MinReservedSector : ReadUInt32(fat, (int)offset);
    }

    private static bool TryGetNextSector(byte[] bytes, int length, int sectorSize, uint[] difat, uint sector, out uint next)
    {
        next = 0;

        var entriesPerFatSector = (uint)(sectorSize / 4);
        var fatSectorIndex = sector / entriesPerFatSector;
        if (fatSectorIndex >= (uint)difat.Length)
        {
            return false;
        }

        var fatSector = difat[fatSectorIndex];
        if (fatSector >= MinReservedSector)
        {
            return false;
        }

        var fatSectorOffset = SectorByteOffset(fatSector, sectorSize);
        var entryOffset = fatSectorOffset + ((sector % entriesPerFatSector) * 4);
        if (entryOffset < 0 || entryOffset + 4 > length)
        {
            return false;
        }

        next = ReadUInt32(bytes, (int)entryOffset);
        return true;
    }

    // Sector 0 starts right after the fixed-size header, regardless of the header's
    // own (possibly larger, zero-padded) sector size - MS-CFB 2.2. Computed as a long
    // so a corrupt/huge sector number can't wrap back into the file's actual bounds.
    private static long SectorByteOffset(uint sector, int sectorSize) => (sector + 1L) * sectorSize;

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset)
    {
        var low = ReadUInt32(bytes, offset);
        var high = ReadUInt32(bytes, offset + 4);
        return low | ((ulong)high << 32);
    }
}
