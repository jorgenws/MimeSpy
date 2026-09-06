## MimeSpy

This is a C# library for detecting MIME types.

# Tech stack
We are using dotnet 10. Tell me if a newer is available.

# Programming conventions
We use switch expressions over switch statements.

# Testing
Use XUnit v3 on the Microsoft.Testing.Platform runner: `dotnet test`, or `dotnet run --project MimeSpy.Tests` (it builds as an executable).
Use Theory over duplicate setups.
Use Fact instead of Theory if the Theory needs logic that is just there to force it to be a Theory.
`MimeSpy.Tests` is unit tests (mostly hand-built byte arrays). `MimeSpy.IntegrationTests` runs `Spy()` against real, genuinely-encoded sample files under its `Fixtures/` folder - see that project's README before adding or regenerating one.

## Architecture
This is a one layer library.
Implement `IContainerSniffer` for deeper investigation of formats that share a header byte-for-byte with unrelated formats (ZIP, OLE2/CFBF, Ogg, ASF, ...) - add an instance to the sniffer list in `MimeSpy.Spy()` and nothing else needs to change.
Only `MimeSpy.Spy()`, `MimeSpy.SpyAsync()`, `IMimeSpy` (the interface `MimeSpy` implements, for callers that want a mockable seam), and the `Result` record in MimeSpy.cs are public. Everything else - `FileSignature`, `MimeType`, `MimeTypeIndex`, `SignatureIndex`, the `IContainerSniffer` implementations (`ZipContainerSniffer`, `Ole2ContainerSniffer`, `OggContainerSniffer`, `AsfContainerSniffer`) and their shared helpers (`WrappedExtensionNarrower`, `PrimaryMimeTypeRefiner`) - is `internal`. Keep it that way unless there's a real reason to grow the public surface.

## XML comments
The XML comments are for consumers of the library and need to be informative to them.

## Data
The two source tables live as embedded resources under Resources/ in their native formats: `file_signatures.csv` (Gary Kessler's file signature table) and `mime.types` (Apache httpd's mime-type table). Add new file formats or extension/mime-type mappings there, not as hardcoded C#.

## Decisions
Non-obvious architectural decisions are recorded as ADRs in docs/adr/ - check there before revisiting how ambiguous matches, ZIP disambiguation, or the data format are handled, and add a new one when you make a similarly hard-to-reverse call.

## Experiments
Use the .scratch folder for experiments, throw away test setup and stuff like that

## Handover
Use the .handover folder for handover documents between agent sessions.
