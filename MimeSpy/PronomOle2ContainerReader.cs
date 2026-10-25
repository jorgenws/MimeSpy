using System.Text;

namespace MimeSpy;

// A minimal read-only MS-CFB reader
// (https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/): resolves a
// stream path, possibly nested ("Document/Page1"), to its content. Reads in place,
// sector by sector, never the whole file. Only FAT sectors listed in the header's own
// 109-entry DIFAT array are followed (roughly the first 7MB at 512-byte sectors), not
// the additional DIFAT sector chain.
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

    private readonly Stream _stream;
    private readonly long _length;
    private readonly int _sectorSize;
    private readonly uint[] _difat;
    private readonly uint _firstMiniFatSector;
    private readonly uint _miniFatSectorCount;
    private readonly ulong _miniStreamCutoff;

    // Each FAT sector holds the next-pointers for sectorSize/4 consecutive sectors,
    // so it's read once and kept rather than re-read per pointer followed - indexed
    // like _difat, so at most DifatEntryCount of them (see the class doc).
    private readonly byte[]?[] _fatSectors = new byte[DifatEntryCount][];

    private List<DirectoryEntry> _entries = [];
    private int _rootIndex;

    private byte[]? _miniFat;
    private byte[]? _miniStream;

    // PronomContainerMatcher tries every OLE2 container signature PRONOM has (not
    // just ones plausible for this file - see PronomContainerMatcher's own comment),
    // and many of them name the same stream (e.g. "SummaryInformation" alone appears
    // in 25 of them) - caching by path, for this reader's lifetime (one Refine()
    // call), turns those back into a single sector-chain walk and copy instead of one
    // per signature that asks for it.
    private readonly Dictionary<string, byte[]?> _streamCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct DirectoryEntry(string Name, byte ObjectType, uint Left, uint Right, uint Child, uint StartSector, ulong StreamSize);

    private PronomOle2ContainerReader(Stream stream, int sectorSize, uint[] difat, uint firstMiniFatSector, uint miniFatSectorCount, ulong miniStreamCutoff)
    {
        _stream = stream;
        _length = stream.Length;
        _sectorSize = sectorSize;
        _difat = difat;
        _firstMiniFatSector = firstMiniFatSector;
        _miniFatSectorCount = miniFatSectorCount;
        _miniStreamCutoff = miniStreamCutoff;
    }

    // Opens the OLE2 file that is the whole of stream (which must be seekable, with
    // position 0 at the file's first byte). The reader moves the stream's position as
    // it reads and doesn't restore it.
    public static PronomOle2ContainerReader? TryOpen(Stream stream)
    {
        var header = new byte[HeaderSize];
        if (!TryReadAt(stream, stream.Length, 0, header, HeaderSize) || !header.AsSpan(0, 8).SequenceEqual(Signature))
        {
            return null;
        }

        var sectorShift = ReadUInt16(header, 30);
        if (sectorShift is not (9 or 12))
        {
            return null;
        }

        var sectorSize = 1 << sectorShift;
        var firstDirectorySector = ReadUInt32(header, 48);
        var miniStreamCutoff = ReadUInt32(header, 56);
        var firstMiniFatSector = ReadUInt32(header, 60);
        var miniFatSectorCount = ReadUInt32(header, 64);

        var difat = new uint[DifatEntryCount];
        for (var i = 0; i < DifatEntryCount; i++)
        {
            difat[i] = ReadUInt32(header, 76 + (i * 4));
        }

        var reader = new PronomOle2ContainerReader(stream, sectorSize, difat, firstMiniFatSector, miniFatSectorCount, miniStreamCutoff);
        reader._entries = reader.ReadDirectoryEntries(firstDirectorySector);
        reader._rootIndex = reader._entries.FindIndex(e => e.ObjectType == 5);
        return reader._rootIndex < 0 ? null : reader;
    }

    // The full content of the stream at path (segments separated by / for a stream
    // nested inside one or more storages), or null if any segment doesn't exist or
    // isn't the expected kind (storage for every segment but the last, stream for the
    // last).
    public byte[]? TryReadStream(string path)
    {
        if (_streamCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var content = ReadStream(path);
        _streamCache[path] = content;
        return content;
    }

    private byte[]? ReadStream(string path)
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
    //
    // The walk is iterative and visits each entry at most once: Left/Right come from
    // the file, so a corrupt or hostile one can point back into its own tree, and
    // following that recursively would overflow the stack - which no caller can catch.
    private int? FindChild(uint nodeId, string name)
    {
        var visited = new bool[_entries.Count];
        var pending = new Stack<uint>();
        pending.Push(nodeId);

        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id >= (uint)_entries.Count || visited[id])
            {
                continue;
            }

            visited[id] = true;
            var entry = _entries[(int)id];
            var entryName = entry.Name.Length > 0 && entry.Name[0] < 0x20 ? entry.Name[1..] : entry.Name;
            if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
            {
                return (int)id;
            }

            pending.Push(entry.Right);
            pending.Push(entry.Left);
        }

        return null;
    }

    private List<DirectoryEntry> ReadDirectoryEntries(uint firstDirectorySector)
    {
        var entries = new List<DirectoryEntry>();
        var visited = new HashSet<uint>();
        var sectorBytes = new byte[_sectorSize];
        var sector = firstDirectorySector;

        while (sector < MinReservedSector && visited.Add(sector))
        {
            if (!TryReadAt(_stream, _length, SectorByteOffset(sector, _sectorSize), sectorBytes, _sectorSize))
            {
                break;
            }

            var entriesInSector = _sectorSize / DirectoryEntrySize;
            for (var i = 0; i < entriesInSector; i++)
            {
                entries.Add(ParseDirectoryEntry(sectorBytes, i * DirectoryEntrySize));
            }

            if (!TryGetNextSector(sector, out sector))
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

        // A stream can't be bigger than the file holding it - checked before
        // allocating, so a corrupt size field can't ask for an absurd buffer. Negative
        // is a 64-bit size field with its top bit set, cast to long.
        if (length < 0 || length > _length)
        {
            return null;
        }

        var result = new byte[length];
        var sector = startSector;
        var visited = new HashSet<uint>();
        var written = 0;

        while (written < length)
        {
            if (sector >= MinReservedSector || !visited.Add(sector))
            {
                return null;
            }

            var offset = SectorByteOffset(sector, _sectorSize);
            if (offset + _sectorSize > _length)
            {
                return null;
            }

            var toCopy = (int)Math.Min(_sectorSize, length - written);
            if (!TryReadAt(_stream, _length, offset, result, toCopy, written))
            {
                return null;
            }

            written += toCopy;

            if (written >= length || !TryGetNextSector(sector, out sector))
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
        if (miniStream is null || length < 0 || length > miniStream.Length)
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

    private bool TryGetNextSector(uint sector, out uint next)
    {
        next = 0;

        var entriesPerFatSector = (uint)(_sectorSize / 4);
        var fatSectorIndex = sector / entriesPerFatSector;
        if (fatSectorIndex >= (uint)_difat.Length || GetFatSector(fatSectorIndex) is not { } fatSector)
        {
            return false;
        }

        var entryOffset = (int)(sector % entriesPerFatSector) * 4;
        if (entryOffset + 4 > fatSector.Length)
        {
            return false;
        }

        next = ReadUInt32(fatSector, entryOffset);
        return true;
    }

    // A FAT sector cut short by the end of the file keeps whatever part of it is
    // there - entries in that part are still usable, only ones past the end aren't.
    private byte[]? GetFatSector(uint fatSectorIndex)
    {
        if (_fatSectors[fatSectorIndex] is { } cached)
        {
            return cached;
        }

        var fatSector = _difat[fatSectorIndex];
        if (fatSector >= MinReservedSector)
        {
            return null;
        }

        var offset = SectorByteOffset(fatSector, _sectorSize);
        var available = (int)Math.Min(_sectorSize, _length - offset);
        if (available <= 0)
        {
            return null;
        }

        var buffer = new byte[available];
        return TryReadAt(_stream, _length, offset, buffer, available) ? _fatSectors[fatSectorIndex] = buffer : null;
    }

    // Reads exactly count bytes at offset into buffer[bufferOffset..], or reports
    // false if any of them fall outside the file's length bytes.
    private static bool TryReadAt(Stream stream, long length, long offset, byte[] buffer, int count, int bufferOffset = 0)
    {
        if (offset < 0 || offset + count > length)
        {
            return false;
        }

        stream.Position = offset;
        return StreamReading.Fill(stream, buffer, bufferOffset, count) == count;
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
