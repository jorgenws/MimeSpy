namespace MimeSpy;

/// <summary>
/// Detects MIME type(s) from the leading bytes of a file.
/// </summary>
public sealed class MimeSpy
{
    /// <summary>
    /// Identifies the file format(s) matching the leading bytes of a file.
    /// </summary>
    /// <param name="bytes">
    /// The start of the file. 532 bytes is enough to reach every signature in the
    /// embedded table (the deepest is a subheader at offset 512), so a shorter buffer
    /// only risks missing a match, never an incorrect one - <see cref="Spy(ReadOnlySpan{byte})"/> simply
    /// skips any signature whose offset and length run past the end of what's supplied.
    /// This bound doesn't extend to ZIP-based formats (docx/jar/apk/odt/...): narrowing
    /// those relies on <see cref="ZipContainerSniffer"/> reading the archive's first
    /// entry name and, for ODF, its stored "mimetype" content, and both sit past an
    /// extra-field length the archiving tool controls - so no fixed byte count
    /// guarantees a narrowed result there. Supplying only 532 bytes just means that
    /// case falls back to the full tied list rather than a wrong guess; see
    /// docs/adr/0002-zip-disambiguation-reads-only-supplied-bytes.md.
    /// </param>
    public IReadOnlyList<Result> Spy(ReadOnlySpan<byte> bytes)
    {
        var matches = SignatureIndex.FindMatches(bytes);
        if (matches.Count == 0)
        {
            return [];
        }

        var longest = matches[0].Header.Length;
        for (var i = 1; i < matches.Count; i++)
        {
            if (matches[i].Header.Length > longest)
            {
                longest = matches[i].Header.Length;
            }
        }

        var results = new List<Result>();
        foreach (var match in matches)
        {
            if (match.Header.Length == longest)
            {
                results.Add(new Result(match.Extensions, match.MimeTypes, match.PrimaryMimeType, match.Description));
            }
        }

        if (results.Count > 1)
        {
            var wrappedExtension = ZipContainerSniffer.SniffWrappedExtension(bytes);
            if (wrappedExtension is not null)
            {
                var narrowed = results.Where(r => r.Extensions.Contains(wrappedExtension)).ToList();
                if (narrowed.Count > 0)
                {
                    return narrowed;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Identifies the file format(s) matching the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read from. Only as many leading bytes as <see cref="Spy(ReadOnlySpan{byte})"/>
    /// can use are read - see its remarks for what that bound does and doesn't guarantee.
    /// If the stream is seekable, its position is restored afterwards, so this is a peek
    /// rather than a consume; on a non-seekable stream the read bytes are gone from it.
    /// </param>
    public IReadOnlyList<Result> Spy(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var startPosition = stream.CanSeek ? stream.Position : -1;

#if NETSTANDARD2_0
        var array = new byte[SignatureIndex.MaxHeaderReach];
        var totalRead = ReadAtLeast(stream, array);
#else
        Span<byte> buffer = stackalloc byte[SignatureIndex.MaxHeaderReach];
        var totalRead = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
#endif

        if (startPosition >= 0)
        {
            stream.Position = startPosition;
        }

#if NETSTANDARD2_0
        return Spy(array.AsSpan(0, totalRead));
#else
        return Spy(buffer[..totalRead]);
#endif
    }

#if NETSTANDARD2_0
    private static int ReadAtLeast(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
#endif
}

/// <summary>
/// A file format that matched the supplied bytes.
/// </summary>
/// <param name="Extensions">File extensions associated with this format.</param>
/// <param name="MimeTypes">MIME types associated with this format.</param>
/// <param name="PrimaryMimeType">The most representative MIME type for this format, if one is known.</param>
/// <param name="Description">A human-readable description of the format.</param>
public sealed record Result(IReadOnlyList<string> Extensions, IReadOnlyList<string> MimeTypes, string? PrimaryMimeType, string Description);
