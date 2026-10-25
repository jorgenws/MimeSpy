using System.IO.Compression;
using System.Text;

namespace MimeSpy.Tests;

// Hostile or corrupt files must never take the caller down: the worst they may do is
// fall back to a less specific match (or none).
public class MalformedInputTests
{
    private const uint NoStream = 0xFFFFFFFF;

    private readonly MimeSpy _sut = new();

    [Theory]
    [InlineData(1u, NoStream, NoStream, NoStream)]
    [InlineData(NoStream, 1u, NoStream, NoStream)]
    [InlineData(2u, NoStream, 1u, NoStream)]
    [InlineData(NoStream, 2u, NoStream, 1u)]
    public void Investigate_Ole2DirectoryWithCyclicSiblingPointers_FallsBackToGenericOle2(uint firstLeft, uint firstRight, uint secondLeft, uint secondRight)
    {
        // None of the container signatures' stream names are in this directory, so
        // every lookup walks the whole sibling tree - and the tree loops back on itself.
        var bytes = Ole2WithDirectory(
            ("Root Entry", 5, NoStream, NoStream, 1, 0),
            ("Junk", 2, firstLeft, firstRight, NoStream, 0),
            ("Other", 2, secondLeft, secondRight, NoStream, 0));

        var result = _sut.Investigate(bytes);

        Assert.Contains(result, f => f.Puid == "fmt/111");
    }

    [Fact]
    public void Investigate_Ole2StreamDeclaringAnImpossibleSize_FallsBackToGenericOle2()
    {
        // 2^63 is negative as a signed 64-bit length - and far beyond any real file.
        var bytes = Ole2WithDirectory(
            ("Root Entry", 5, NoStream, NoStream, 1, 0),
            ("WordDocument", 2, NoStream, NoStream, NoStream, 0x8000000000000000));

        var result = _sut.Investigate(bytes);

        Assert.Contains(result, f => f.Puid == "fmt/111");
    }

    [Fact]
    public void Investigate_ZipEntryThatInflatesFarBeyondItsCompressedSize_IsMatchedWithoutInflatingAllOfIt()
    {
        // A zip bomb that is also a valid docx: the Word content type sits at the start
        // of [Content_Types].xml, followed by 64 MiB of zeros that compress to ~64 KB.
        var bytes = ZipWithEntry(
            "[Content_Types].xml",
            "<Types><Override ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"u8,
            paddingBytes: 64 * 1024 * 1024);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = _sut.Investigate(bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Contains(result, f => f.Puid == "fmt/412");
        Assert.InRange(allocated, 0, 8 * 1024 * 1024);
    }

    public static TheoryData<ZipCorruption> ZipCorruptions => new()
    {
        // Deflate block type 11 is reserved, so the first byte (BFINAL=1, BTYPE=11) can
        // never decode.
        new ZipCorruption("invalid deflate data", (zip, local, _) => zip[local + 30 + ReadUInt16(zip, local + 26) + ReadUInt16(zip, local + 28)] = 0x07),
        new ZipCorruption("unsupported compression method", (zip, local, central) =>
        {
            WriteUInt16(zip, local + 8, 14);    // LZMA
            WriteUInt16(zip, central + 10, 14);
        }),
        new ZipCorruption("local header offset pointing at garbage", (zip, _, central) => WriteUInt32(zip, central + 42, 1)),
        // Both counts, so ZipArchive's constructor accepts the record (one alone reads as a
        // split archive and is rejected there) and only parsing the directory fails.
        new ZipCorruption("end of central directory counting entries that aren't there", (zip, _, _) =>
        {
            var endOfCentralDirectory = LastIndexOf(zip, [0x50, 0x4B, 0x05, 0x06]);
            WriteUInt16(zip, endOfCentralDirectory + 8, 2);
            WriteUInt16(zip, endOfCentralDirectory + 10, 2);
        }),
    };

    [Theory]
    [MemberData(nameof(ZipCorruptions))]
    public void Investigate_ZipWhoseContainerEntryCantBeRead_FallsBackToGenericZip(ZipCorruption corruption)
    {
        var bytes = ZipWithEntry(
            "[Content_Types].xml",
            "<Types><Override ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"u8,
            paddingBytes: 4096);
        var centralHeader = LastIndexOf(bytes, [0x50, 0x4B, 0x01, 0x02]);
        corruption.Apply(bytes, 0, centralHeader);

        var result = _sut.Investigate(bytes);

        Assert.Contains(result, f => f.Puid == "x-fmt/263");
    }

    // A one-entry zip only has one central directory header (and one end of central
    // directory record), so the last match is it.
    private static int LastIndexOf(byte[] bytes, byte[] value) => bytes.AsSpan().LastIndexOf(value);

    // Apply breaks the zip given the offsets of its only entry's local header and its
    // central directory header.
    public sealed record ZipCorruption(string Name, Action<byte[], int, int> Apply)
    {
        public override string ToString() => Name;
    }

    private static byte[] ZipWithEntry(string path, ReadOnlySpan<byte> content, int paddingBytes)
    {
        using var file = new MemoryStream();
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
            entry.Write(content);
            var zeros = new byte[1024 * 1024];
            for (var written = 0; written < paddingBytes; written += zeros.Length)
            {
                entry.Write(zeros, 0, Math.Min(zeros.Length, paddingBytes - written));
            }
        }

        return file.ToArray();
    }

    // A minimal 512-byte-sector compound file: header, one FAT sector (sector 0) and
    // one directory sector (sector 1) holding up to four entries.
    private static byte[] Ole2WithDirectory(params (string Name, byte ObjectType, uint Left, uint Right, uint Child, ulong StreamSize)[] entries)
    {
        var bytes = new byte[512 * 3];
        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(bytes, 0);
        WriteUInt16(bytes, 24, 0x003E);         // minor version
        WriteUInt16(bytes, 26, 3);              // major version
        WriteUInt16(bytes, 28, 0xFFFE);         // little-endian byte order mark
        WriteUInt16(bytes, 30, 9);              // sector shift: 512-byte sectors
        WriteUInt16(bytes, 32, 6);              // mini sector shift: 64-byte mini sectors
        WriteUInt32(bytes, 44, 1);              // FAT sector count
        WriteUInt32(bytes, 48, 1);              // first directory sector
        WriteUInt32(bytes, 56, 4096);           // mini stream cutoff
        WriteUInt32(bytes, 60, 0xFFFFFFFE);     // no mini FAT
        for (var i = 0; i < 109; i++)
        {
            WriteUInt32(bytes, 76 + (i * 4), NoStream);
        }

        WriteUInt32(bytes, 76, 0);              // DIFAT[0]: the FAT is sector 0

        const int fat = 512;
        for (var i = 0; i < 128; i++)
        {
            WriteUInt32(bytes, fat + (i * 4), NoStream);
        }

        WriteUInt32(bytes, fat, 0xFFFFFFFD);     // sector 0 is a FAT sector
        WriteUInt32(bytes, fat + 4, 0xFFFFFFFE); // sector 1 ends the directory chain

        const int directory = 1024;
        for (var i = 0; i < entries.Length; i++)
        {
            var offset = directory + (i * 128);
            var name = Encoding.Unicode.GetBytes(entries[i].Name + "\0");
            name.CopyTo(bytes, offset);
            WriteUInt16(bytes, offset + 64, (ushort)name.Length);
            bytes[offset + 66] = entries[i].ObjectType;
            WriteUInt32(bytes, offset + 68, entries[i].Left);
            WriteUInt32(bytes, offset + 72, entries[i].Right);
            WriteUInt32(bytes, offset + 76, entries[i].Child);
            WriteUInt32(bytes, offset + 116, 0xFFFFFFFE);
            BitConverter.GetBytes(entries[i].StreamSize).CopyTo(bytes, offset + 120);
        }

        return bytes;
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) => BitConverter.ToUInt16(bytes, offset);

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
}
