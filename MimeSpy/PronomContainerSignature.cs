namespace MimeSpy;

/// <summary>
/// One entry a <see cref="PronomContainerSignature"/> requires to be present inside
/// the container - a named entry (ZIP path, or OLE2 stream path, possibly nested
/// like <c>"Document/Page1"</c>) that must exist, and, if <see cref="Signature"/> is
/// set, whose own content must match it.
/// </summary>
internal sealed record PronomContainerFile(string Path, PronomInternalSignature? Signature);

/// <summary>
/// A PRONOM container signature: a format identified not by the container's own
/// header (shared by every format of that <see cref="ContainerType"/>) but by which
/// named entries it contains and what they contain - all of <see cref="Files"/> must
/// be satisfied for this signature to match.
/// </summary>
internal sealed record PronomContainerSignature(int Id, string ContainerType, IReadOnlyList<PronomContainerFile> Files);
