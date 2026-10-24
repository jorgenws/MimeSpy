namespace MimeSpy;

// One entry a PronomContainerSignature requires to be present inside the container - a
// named entry (ZIP path, or OLE2 stream path, possibly nested like "Document/Page1")
// that must exist, and, if Signature is set, whose own content must match it.
internal sealed record PronomContainerFile(string Path, PronomInternalSignature? Signature);

// A PRONOM container signature: a format identified not by the container's own header
// (shared by every format of that ContainerType) but by which named entries it contains
// and what they contain - all of Files must be satisfied for this signature to match.
internal sealed record PronomContainerSignature(int Id, string ContainerType, IReadOnlyList<PronomContainerFile> Files);
