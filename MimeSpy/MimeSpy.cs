namespace MimeSpy;

public sealed class MimeSpy
{
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
}

public sealed record Result(IReadOnlyList<string> Extensions, IReadOnlyList<string> MimeTypes, string? PrimaryMimeType, string Description);
