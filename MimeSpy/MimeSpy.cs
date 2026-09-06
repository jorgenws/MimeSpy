namespace MimeSpy;

/// <summary>
/// Detects MIME type(s) from the leading bytes of a file.
/// </summary>
public sealed class MimeSpy
{
    // Order matters where two sniffers could both apply to the same tied results
    // (e.g. Zip vs Ole2): whichever runs first and narrows leaves the rest a no-op,
    // since a file's header can only ever belong to one of these format families.
    private static readonly IReadOnlyList<IContainerSniffer> Sniffers =
    [
        new ZipContainerSniffer(),
        new Ole2ContainerSniffer(),
        new OggContainerSniffer(),
        new AsfContainerSniffer(),
    ];

    /// <summary>
    /// Identifies the file format(s) matching the leading bytes of a file.
    /// </summary>
    /// <param name="bytes">
    /// The start of the file. 532 bytes reaches every signature in the embedded
    /// table (the deepest is a subheader at offset 512); a shorter buffer only
    /// risks missing a match, never an incorrect one. Getting the most specific
    /// answer for every format this library can disambiguate needs more: up to
    /// <see cref="AsfContainerSniffer.SearchWindowSize"/> bytes for formats with a
    /// fixed-size disambiguation window, and, for a few container formats (ZIP-based
    /// and OLE2/CFBF), no fixed count at all - see docs/adr/0002 and docs/adr/0011.
    /// Supplying fewer bytes never produces a wrong answer, only a less specific one.
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
                results.Add(new Result(match.Extensions, match.MimeTypes, SniffedMimeType: null, match.Description));
            }
        }

        IReadOnlyList<Result> refined = results;
        foreach (var sniffer in Sniffers)
        {
            refined = sniffer.Refine(bytes, refined);
        }

        return refined;
    }

    /// <summary>
    /// Identifies the file format(s) matching the leading bytes of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">
    /// The stream to read from. Enough leading bytes are read to give
    /// <see cref="Spy(ReadOnlySpan{byte})"/> every fixed-size disambiguation window
    /// this library uses - see its own <c>bytes</c> doc for what that does and
    /// doesn't guarantee - so a caller reading through this overload gets the most
    /// specific answer available without needing to know a byte count itself.
    /// If the stream is seekable, its position is restored afterwards, so this is a
    /// peek rather than a consume; on a non-seekable stream the read bytes are gone
    /// from it.
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
/// <param name="SniffedMimeType">
/// The mime type actually sniffed from this file's content, if a container sniffer
/// found one (docs/adr/0007, docs/adr/0008) - null for every format this library
/// doesn't do content-based sniffing for. Feeds <see cref="PrimaryMimeType"/>; most
/// callers want that instead of this.
/// </param>
/// <param name="Description">A human-readable description of the format.</param>
public sealed record Result(IReadOnlyList<string> Extensions, IReadOnlyList<string> MimeTypes, string? SniffedMimeType, string Description)
{
    /// <summary>
    /// The most representative MIME type for this format, if one is known. When
    /// <see cref="SniffedMimeType"/> is set and is one of this result's own
    /// <see cref="MimeTypes"/> candidates, that real, file-content-derived answer
    /// wins outright. Otherwise falls back to a statistical default: whichever of
    /// <see cref="MimeTypes"/> is backed by the most of <see cref="Extensions"/>'s
    /// aliases (e.g. jpe/jpeg/jpg all resolve to image/jpeg, so it beats jfif's
    /// image/pjpeg 3 to 1) - see docs/adr/0004 and docs/adr/0013.
    /// </summary>
    public string? PrimaryMimeType =>
        SniffedMimeType is not null && MimeTypes.Contains(SniffedMimeType)
            ? SniffedMimeType
            : DefaultMimeTypeByExtensionAliasCount();

    private string? DefaultMimeTypeByExtensionAliasCount()
    {
        var counts = CountMimeTypesByExtensionAlias();

        // Ties keep whichever mime type is first in MimeTypes' own order (its
        // first-seen-extension order from table load, docs/adr/0004).
        string? winner = null;
        var winningCount = 0;
        foreach (var mimeType in MimeTypes)
        {
            var count = counts.TryGetValue(mimeType, out var c) ? c : 0;
            if (count > winningCount)
            {
                winningCount = count;
                winner = mimeType;
            }
        }

        return winner;
    }

    private Dictionary<string, int> CountMimeTypesByExtensionAlias()
    {
        var counts = new Dictionary<string, int>();
        foreach (var extension in Extensions)
        {
            foreach (var mimeType in MimeTypeIndex.FindByExtension(extension))
            {
                counts[mimeType.Name] = counts.TryGetValue(mimeType.Name, out var count) ? count + 1 : 1;
            }
        }

        return counts;
    }

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
    public string? PrimaryExtension
    {
        get
        {
            if (Extensions.Count == 1)
            {
                return Extensions[0];
            }

            if (PrimaryMimeType is null)
            {
                return null;
            }

            var counts = CountMimeTypesByExtensionAlias();
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
}
