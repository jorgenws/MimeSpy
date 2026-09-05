## MimeSpy

This is a C# library for detecting MIME types.

# Tech stack
We are using dotnet 10. Tell me if a newer is available.

# Testing
We use XUnit v3 on the Microsoft.Testing.Platform runner: `dotnet test`, or `dotnet run --project MimeSpy.Tests` (it builds as an executable).
We prefer Theory over duplicate setups.

## Architecture
This is a one layer library. Don't make it complicated.
Only `MimeSpy.Spy()` and the `Result` record in MimeSpy.cs are public. Everything else (`FileSignature`, `MimeType`, `MimeTypeIndex`, `SignatureIndex`, `ZipContainerSniffer`) is `internal` - keep it that way unless there's a real reason to grow the public surface.

## Data
The two source tables live as embedded JSON under Resources/ (`file_signatures.json`, `mimeTypes.json`). Add new file formats or extension/mime-type mappings there, not as hardcoded C#.

## Decisions
Non-obvious architectural decisions are recorded as ADRs in docs/adr/ - check there before revisiting how ambiguous matches, ZIP disambiguation, or the data format are handled, and add a new one when you make a similarly hard-to-reverse call.
