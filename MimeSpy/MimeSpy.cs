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
    /// only risks missing a match, never an incorrect one - this method simply
    /// skips any signature whose offset and length run past the end of what's supplied.
    /// Content-based mime type refinement (<see cref="OggContainerSniffer"/>,
    /// <see cref="AsfContainerSniffer"/>) similarly just falls back to the
    /// table's default answer for that signature when its own bytes aren't
    /// available - <see cref="AsfContainerSniffer.SearchWindowSize"/> is the
    /// widest window any of them need, so a caller who wants every disambiguation
    /// this library can do should supply at least that many bytes.
    /// This bound doesn't extend to ZIP-based formats (docx/jar/apk/odt/...): narrowing
    /// those relies on <see cref="ZipContainerSniffer"/> reading the archive's first
    /// entry name and, for ODF, its stored "mimetype" content, and both sit past an
    /// extra-field length the archiving tool controls - so no fixed byte count
    /// guarantees a narrowed result there. Supplying only 532 bytes just means that
    /// case falls back to the full tied list rather than a wrong guess; see
    /// docs/adr/0002-zip-disambiguation-reads-only-supplied-bytes.md. Legacy OLE2/CFBF
    /// Office formats (doc/xls/ppt) are the same story: <see cref="Ole2ContainerSniffer"/>
    /// needs to reach the file's directory sector, whose location isn't bounded by any
    /// fixed byte count either - see docs/adr/0011.
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
            // A file's header can only ever match one of these two container
            // families' signature bytes (ZIP's "PK\x03\x04" vs OLE2/CFBF's
            // "D0 CF 11 E0..."), so at most one of these two calls can ever
            // return non-null - trying both costs nothing extra in practice.
            var wrappedExtension = ZipContainerSniffer.SniffWrappedExtension(bytes) ?? Ole2ContainerSniffer.SniffWrappedExtension(bytes);
            if (wrappedExtension is not null)
            {
                var narrowed = results.Where(r => r.Extensions.Contains(wrappedExtension)).ToList();
                if (narrowed.Count > 0)
                {
                    return narrowed;
                }
            }
        }

        // Unlike the zip case above, neither Ogg nor ASF/WMA/WMV is ever tied
        // against another signature - the ambiguity lives entirely inside one
        // match's own MimeTypes (see docs/adr/0007 and docs/adr/0008) - so both
        // of these run regardless of results.Count.
        ApplySniffedPrimaryMimeType(results, OggContainerSniffer.SniffMimeType(bytes));
        ApplySniffedPrimaryMimeType(results, AsfContainerSniffer.SniffMimeType(bytes));

        return results;
    }

    // Only overrides a result that already lists sniffedMimeType as one of its
    // candidates, so a sniffer can never affect a match it has nothing to do with.
    private static void ApplySniffedPrimaryMimeType(List<Result> results, string? sniffedMimeType)
    {
        if (sniffedMimeType is null)
        {
            return;
        }

        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].MimeTypes.Contains(sniffedMimeType))
            {
                results[i] = results[i] with { PrimaryMimeType = sniffedMimeType };
            }
        }
    }

    /// <summary>
    /// Identifies the file format(s) matching the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read from. Only as many leading bytes as <see cref="Spy(ReadOnlySpan{byte})"/>
    /// can use are read - see its remarks for what that bound does and doesn't guarantee.
    /// This is at least <see cref="AsfContainerSniffer.SearchWindowSize"/> bytes, since that's
    /// the widest window any disambiguation step needs (wider than the signature table itself
    /// requires), so a caller reading through this overload gets every disambiguation this
    /// library can do without having to know that number itself.
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
        var readSize = Math.Max(SignatureIndex.MaxHeaderReach, AsfContainerSniffer.SearchWindowSize);

#if NETSTANDARD2_0
        var array = new byte[readSize];
        var totalRead = ReadAtLeast(stream, array);
#else
        Span<byte> buffer = stackalloc byte[readSize];
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
public sealed record Result(IReadOnlyList<string> Extensions, IReadOnlyList<string> MimeTypes, string? PrimaryMimeType, string Description)
{
    /// <summary>
    /// The most likely extension among <see cref="Extensions"/>, if one is known.
    /// Resolved from the same alias data behind <see cref="PrimaryMimeType"/>
    /// (Apache's mime.types, docs/adr/0005) rather than a hardcoded table: when this
    /// result's extensions resolve to more than one mime type, <see cref="PrimaryMimeType"/>
    /// is only trusted as an anchor here if it's a genuine majority among them - strictly
    /// more of these extensions resolve to it than to any other single mime type (e.g.
    /// jpe/jpeg/jpg all resolve to image/jpeg, doc/dot both resolve to application/msword).
    /// A tie with no such majority (e.g. docx/pptx/xlsx, one extension apiece for three
    /// distinct mime types) returns null rather than dressing up an arbitrary first-seen
    /// pick as a considered answer - see docs/adr/0012. Once a majority mime type is
    /// established, if more than one tied extension resolves to it (pure spelling
    /// variants of the same format), the tie between those specifically is broken using
    /// mime.types' own declared extension order for that mime type, on the assumption
    /// that whichever spelling Apache lists first is the more canonical one.
    /// </summary>
    public string? PrimaryExtension()
    {
        if (Extensions.Count == 1)
        {
            return Extensions[0];
        }

        if (PrimaryMimeType is null)
        {
            return null;
        }

        var counts = new Dictionary<string, int>();
        foreach (var extension in Extensions)
        {
            foreach (var mimeType in MimeTypeIndex.FindByExtension(extension))
            {
                counts[mimeType.Name] = counts.TryGetValue(mimeType.Name, out var count) ? count + 1 : 1;
            }
        }

        var winningCount = counts.TryGetValue(PrimaryMimeType, out var winning) ? winning : 0;
        var isMajorityWinner = winningCount > 0 && counts.All(pair => pair.Key == PrimaryMimeType || pair.Value < winningCount);
        if (!isMajorityWinner)
        {
            return null;
        }

        var candidates = Extensions.Where(extension => MimeTypeIndex.FindByExtension(extension).Any(mimeType => mimeType.Name == PrimaryMimeType)).ToList();
        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : null;
        }

        var canonicalOrder = MimeTypeIndex.FindByExtension(candidates[0]).First(mimeType => mimeType.Name == PrimaryMimeType).Extensions;
        foreach (var extension in canonicalOrder)
        {
            if (candidates.Contains(extension))
            {
                return extension;
            }
        }

        return candidates[0];
    }
}
