using System.IO.Compression;

namespace MimeSpy;

// Reads named entries out of a ZIP file on behalf of the container signature engine,
// via the framework's own ZipArchive (which seeks to the central directory at the end
// of the file, so this only ever makes sense against the complete file, never a leading
// prefix). Reads the stream it's given in place - only the central directory and the
// entries actually asked for are ever read.
internal sealed class PronomZipContainerReader : IDisposable
{
    // docs/adr/0028.
    internal const int EntryPrefixLength = 64 * 1024;

    private readonly ZipArchive _archive;

    // PronomContainerMatcher tries every ZIP container signature PRONOM has, not just
    // ones plausible for this file, and many of them name the same entry (e.g. "[Content_Types].xml" alone appears in
    // 32 of them) - caching by path, for this reader's lifetime (one Refine() call),
    // turns those back into a single decompression instead of one per signature that
    // asks for it.
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
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

            // The constructor only reads the end of central directory record. The
            // directory itself is parsed on first use, and a damaged one throws there -
            // so it's forced here, where the failure means "not a readable zip".
            _ = archive.Entries.Count;
            return new PronomZipContainerReader(archive);
        }
        catch (InvalidDataException)
        {
            archive?.Dispose();
            return null;
        }
    }

    // The first EntryPrefixLength bytes of the entry at path, decompressed (all of it,
    // if it's shorter), or null if it doesn't exist or can't be read.
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

        var prefix = new byte[Math.Min(entry.Length, EntryPrefixLength)];
        int read;
        try
        {
            using var entryStream = entry.Open();
            read = StreamReading.Fill(entryStream, prefix, 0, prefix.Length);
        }
        catch (InvalidDataException)
        {
            // A damaged local header, corrupt deflate data, or a compression method the
            // framework can't decode (LZMA, ...). Treated as missing: no container
            // signature can match on it, so the answer falls back to the generic match.
            return null;
        }

        return read == prefix.Length ? prefix : prefix.AsSpan(0, read).ToArray();
    }

    public void Dispose() => _archive.Dispose();
}
