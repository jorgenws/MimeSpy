namespace MimeSpy;

internal sealed record FileSignature(byte[] Header, int HeaderOffset, string[] Extensions, string[] MimeTypes, string? PrimaryMimeType, string Description);
