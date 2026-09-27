using System.IO.Compression;

namespace MimeSpy;

/// <summary>
/// Reads named entries out of a ZIP file's full bytes on behalf of the container
/// signature engine, via the framework's own <see cref="ZipArchive"/> (which reads
/// the central directory at the end of the file, so this only ever makes sense
/// against the complete file, never a leading prefix). Never copies the array it's
/// given (see <see cref="TryOpen"/>'s own doc).
/// </summary>
internal sealed class PronomZipContainerReader : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly ZipArchive _archive;

    private PronomZipContainerReader(MemoryStream stream, ZipArchive archive)
    {
        _stream = stream;
        _archive = archive;
    }

    /// <summary>
    /// Opens the ZIP file whose bytes are <paramref name="bytes"/>'s first
    /// <paramref name="length"/> bytes - which are never copied, only wrapped, even
    /// when <paramref name="bytes"/> is itself larger (e.g. a growable buffer's
    /// backing array).
    /// </summary>
    public static PronomZipContainerReader? TryOpen(byte[] bytes, int length)
    {
        var stream = new MemoryStream(bytes, 0, length, writable: false);
        try
        {
            return new PronomZipContainerReader(stream, new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true));
        }
        catch (InvalidDataException)
        {
            stream.Dispose();
            return null;
        }
    }

    /// <summary>The full decompressed content of the entry at <paramref name="path"/>, or null if it doesn't exist.</summary>
    public byte[]? TryReadEntry(string path)
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

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }
}
