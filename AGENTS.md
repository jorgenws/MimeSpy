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
`MimeSpy.Tests` is unit tests (mostly hand-built byte arrays). `MimeSpy.IntegrationTests` runs `Investigate()` against real, genuinely-encoded sample files under its `Fixtures/` folder - see that project's README before adding or regenerating one.

## Architecture
This is a one layer library, built on PRONOM's own DROID signature data rather than a hardcoded sniffer per format.
There's no plugin interface for adding format support - a new format, or a new byte-sequence signature for an existing one, is a PRONOM data addition (see Data below), not a new C# type. ZIP/OLE2 sub-format disambiguation (docx vs xlsx vs plain zip, doc vs xls, ...) is resolved the same way, by `PronomContainerMatcher` reading PRONOM's own container-signature table - only a genuinely new container *type* (beyond ZIP/OLE2) would need a new reader (`PronomZipContainerReader`/`PronomOle2ContainerReader`'s siblings) and a `PronomContainerMatcher` change.
Only `MimeSpy.Spy()`/`Investigate()` (and their `Stream`/async overloads), `IMimeSpy` (the interface `MimeSpy` implements, for callers that want a mockable seam), `MimeSpyOptions` (the constructor's format filter), and the `Dossier` record and its `Match` enum in MimeSpy.Pronom.cs are public. Everything else - the `Pronom*` signature/container-index, matcher, and reader types - is `internal`. Keep it that way unless there's a real reason to grow the public surface.

## XML comments
The XML comments are for consumers of the library and need to be informative to them.

## Data
The two source tables live as embedded resources under Resources/ in their native XML format: `pronom-droid-signatures.xml` (PRONOM's DROID signature file) and `pronom-container-signatures.xml` (PRONOM's container signature file). Add new file formats, signatures, or extension/mime-type mappings there, not as hardcoded C#. Keep both unmodified copies of PRONOM's files; the one exception to adding data there is MIME types for common formats PRONOM leaves empty, which go in `mimespy-mime-types.xml` (docs/adr/0026).

## Decisions
Non-obvious architectural decisions are recorded as ADRs in docs/adr/ - check there before revisiting how ambiguous matches, ZIP disambiguation, or the data format are handled, and add a new one when you make a similarly hard-to-reverse call.

## Experiments
Use the .scratch folder for experiments, throw away test setup and stuff like that

## Handover
Use the .handover folder for handover documents between agent sessions.
