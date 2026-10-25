namespace MimeSpy;

/// <summary>How much of a format's signature was checked against the file.</summary>
public enum Match
{
    /// <summary>Every part of the signature was checked and matched.</summary>
    Full,

    /// <summary>
    /// Only the signature's start-of-file part was checked. <c>Spy</c> skips any check at
    /// the end of the file, so the bytes are consistent with this format but haven't been
    /// shown to be it. <c>Investigate</c> never returns a partial match.
    /// </summary>
    Partial,
}
