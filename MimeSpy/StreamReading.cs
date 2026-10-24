namespace MimeSpy;

/// <summary>
/// Read-until-full loops over <see cref="Stream.Read(byte[], int, int)"/>, which is
/// allowed to return fewer bytes than asked for even mid-stream - netstandard2.0
/// has no <c>Stream.ReadAtLeast</c>/<c>ReadExactly</c> to lean on instead.
/// </summary>
internal static class StreamReading
{
    /// <summary>Reads until <paramref name="count"/> bytes are in, or the stream ends. Returns how many were read.</summary>
    public static int Fill(Stream stream, byte[] buffer, int offset, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    /// <inheritdoc cref="Fill"/>
    public static async Task<int> FillAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
