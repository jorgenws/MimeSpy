## MimeSpy

This is a C# library for detecting MIME types.

# Tech stack
We are using dotnet 10. Tell me if a newer is available.

# Programming conventions
We use switch expressions over switch statements where possible.

# Testing
We use XUnit v3 on the Microsoft.Testing.Platform runner: `dotnet test`, or `dotnet run --project MimeSpy.Tests` (it builds as an executable).
We prefer Theory over duplicate setups.
`MimeSpy.Tests` is unit tests (mostly hand-built byte arrays). `MimeSpy.IntegrationTests` runs `Spy()` against real, genuinely-encoded sample files under its `Fixtures/` folder - see that project's README before adding or regenerating one.

## Architecture
This is a one layer library. Don't make it complicated.
Only `MimeSpy.Spy()` and the `Result` record in MimeSpy.cs are public. Everything else (`FileSignature`, `MimeType`, `MimeTypeIndex`, `SignatureIndex`, `ZipContainerSniffer`) is `internal` - keep it that way unless there's a real reason to grow the public surface.

## Data
The two source tables live as embedded resources under Resources/ in their native formats: `file_signatures.csv` (Gary Kessler's file signature table) and `mime.types` (Apache httpd's mime-type table). Add new file formats or extension/mime-type mappings there, not as hardcoded C#.

## Decisions
Non-obvious architectural decisions are recorded as ADRs in docs/adr/ - check there before revisiting how ambiguous matches, ZIP disambiguation, or the data format are handled, and add a new one when you make a similarly hard-to-reverse call.

## Experiments
Use the .scratch folder for experiments, throw away test setup and stuff like that.
