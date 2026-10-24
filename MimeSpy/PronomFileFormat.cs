namespace MimeSpy;

// A file format record from PRONOM's DROID signature file. Matches if any one of
// InternalSignatureIds matches (each is an alternative signature for the same format,
// e.g. different versions of the same magic bytes) - unlike a single signature's own
// PronomByteSequence list, which is an AND. HasPriorityOverIds lists the format IDs this
// one outranks when both match (see PronomPriority).
internal sealed record PronomFileFormat(
    int Id,
    string Puid,
    string Name,
    string? Version,
    string? MimeType,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<int> InternalSignatureIds,
    IReadOnlyList<int> HasPriorityOverIds);
