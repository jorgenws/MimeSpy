namespace MimeSpy;

/// <summary>One format that matched, as PRONOM records it.</summary>
/// <param name="Puid">PRONOM's stable identifier for the format, e.g. <c>"fmt/412"</c>.</param>
/// <param name="Name">The format's name, e.g. <c>"Microsoft Word for Windows"</c>.</param>
/// <param name="Version">The format's version, if PRONOM records one.</param>
/// <param name="MimeTypes">
/// The format's MIME types as PRONOM records them, with a few corrections for common
/// formats: MimeSpy supplies the conventional type where PRONOM gives none, and puts it
/// first where PRONOM's order would not (MP4 lists <c>video/mp4</c> first). Empty if there
/// is none.
/// </param>
/// <param name="Extensions">File extensions associated with the format.</param>
/// <param name="Match">Whether every part of the format's signature was checked against the file.</param>
public sealed record Dossier(string Puid, string Name, string? Version, IReadOnlyList<string> MimeTypes, IReadOnlyList<string> Extensions, Match Match = Match.Full)
{
    /// <summary>
    /// The first of <see cref="MimeTypes"/>, or null if there are none. PRONOM lists several
    /// types in alphabetical order, not by preference. MimeSpy reorders them where that puts
    /// an unconventional type first for a common format, but for others the first is not
    /// necessarily the preferred one.
    /// </summary>
    public string? MimeType => MimeTypes.Count > 0 ? MimeTypes[0] : null;
}
