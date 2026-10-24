using System.IO.Compression;

namespace MimeSpy;

// Reads named entries out of a ZIP file on behalf of the container signature engine,
// via the framework's own ZipArchive (which seeks to the central directory at the end
// of the file, so this only ever makes sense against the complete file, never a leading
// prefix). Reads the stream it's given in place - only the central directory and the
// entries actually asked for are ever read.
internal sealed class PronomZipContainerReader : IDisposable
{
    private readonly ZipArchive _archive;

    // PronomContainerMatcher tries every ZIP container signature PRONOM has (not just
    // ones plausible for this file - see PronomContainerMatcher's own comment), and
    // many of them name the same entry (e.g. "[Content_Types].xml" alone appears in
    // 32 of them) - caching by path, for this reader's lifetime (one Refine() call),
    // turns those back into a single decompress-and-copy instead of one per signature
    // that asks for it.
    private readonly Dictionary<string, byte[]?> _entryCache = new(StringComparer.Ordinal);

    private PronomZipContainerReader(ZipArchive archive)
    {
        _archive = archive;
    }

    // Opens the ZIP file that is the whole of stream (which must be seekable, with
    // position 0 at the file's first byte). The stream is left open when this reader is
    // disposed - it belongs to the caller.
    public static PronomZipContainerReader? TryOpen(Stream stream)
    {
        try
        {
            return new PronomZipContainerReader(new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // The full decompressed content of the entry at path, or null if it doesn't exist.
    public byte[]? TryReadEntry(string path)
    {
        if (_entryCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var content = ReadEntry(path);
        _entryCache[path] = content;
        return content;
    }

    private byte[]? ReadEntry(string path)
    {
        var entry = _archive.GetEntry(path);
        if (entry is null)
        {
            return null;
        }

        using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public void Dispose() => _archive.Dispose();
}
