using System.IO.Compression;

namespace MimeSpy;

// Reads named entries out of a ZIP file on behalf of the container signature engine,
// via the framework's own ZipArchive (which seeks to the central directory at the end
// of the file, so this only ever makes sense against the complete file, never a leading
// prefix). Reads the stream it's given in place - only the central directory and the
// entries actually asked for are ever read, and of those only their first
// EntryPrefixLength bytes are ever decompressed.
internal sealed class PronomZipContainerReader : IDisposable
{
    // PRONOM's ZIP container signatures only ever look at the start of an entry, and
    // the bounded ones within ~40 KB of it, so inflating further would only ever cost
    // memory - unboundedly so for a zip bomb, whose tiny entry inflates to gigabytes.
    // An unbounded search ("this string anywhere in the entry") covers just this prefix,
    // the same 64 KB DROID's own default profile scans. See docs/adr/0028;
    // ContainerSignatureDataTests fails if a PRONOM update stops this being enough.
    internal const int EntryPrefixLength = 64 * 1024;

    private readonly ZipArchive _archive;

    // PronomContainerMatcher tries every ZIP container signature PRONOM has (not just
    // ones plausible for this file - see PronomContainerMatcher's own comment), and
    // many of them name the same entry (e.g. "[Content_Types].xml" alone appears in
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

        // Length is the archive's own claim. Sizing the buffer by it saves allocating the
        // full prefix for a small entry, but the read stops at the buffer's end either way,
        // so a claim that's too large can't make it read more.
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
