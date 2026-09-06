using System.Text;

namespace MimeSpy;

/// <summary>
/// The ASF Header Object GUID that opens every ASF file is shared verbatim by
/// generic ASF containers, WMA (audio), and WMV (video) - telling them apart
/// properly means walking the header's variable-length object list to find the
/// Stream Properties Object and reading its embedded stream-type GUID, which is
/// substantially more work than <see cref="OggContainerSniffer"/>'s fixed-offset
/// peek. Apache Tika sidesteps that entirely: rather than parsing the object
/// graph, it just searches a chunk of the file for the (UTF-16LE) codec name a
/// Windows Media encoder conventionally writes into the Codec List/Content
/// Description objects - see docs/adr/0008-asf-mime-type-resolved-by-codec-name-search.md
/// for where these exact markers and the search window size came from. This is
/// a heuristic, not a structural guarantee, but it's what an established
/// production implementation does, and it's far cheaper than a real parse.
/// </summary>
internal static class AsfContainerSniffer
{
    /// <summary>
    /// How many leading bytes are searched for a codec-name marker. Matches Tika's
    /// own window - wide enough to reach past the file's other header objects
    /// (content description, codec list, etc.) into the marker text, without
    /// requiring the whole file.
    /// </summary>
    public const int SearchWindowSize = 8192;

    private static readonly byte[] WindowsMediaAudioMarker = Encoding.Unicode.GetBytes("Windows Media Audio");
    private static readonly byte[] WindowsMediaVideoMarker = Encoding.Unicode.GetBytes("Windows Media Video");
    private static readonly byte[] Vc1AdvancedProfileMarker = Encoding.Unicode.GetBytes("VC-1 Advanced Profile");
    private static readonly byte[] Wmv2Marker = Encoding.Unicode.GetBytes("wmv2");

    public static string? SniffMimeType(ReadOnlySpan<byte> bytes)
    {
        var window = bytes.Length > SearchWindowSize ? bytes[..SearchWindowSize] : bytes;

        // A file can carry both an audio and a video stream (i.e. a real WMV with
        // sound); video wins when both markers are present, matching Tika's own
        // WMV magic having a higher priority than its WMA magic.
        if (window.IndexOf(WindowsMediaVideoMarker) >= 0
            || window.IndexOf(Vc1AdvancedProfileMarker) >= 0
            || window.IndexOf(Wmv2Marker) >= 0)
        {
            return "video/x-ms-wmv";
        }

        if (window.IndexOf(WindowsMediaAudioMarker) >= 0)
        {
            return "audio/x-ms-wma";
        }

        return null;
    }
}
