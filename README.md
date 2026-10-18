![MimeSpy](https://raw.githubusercontent.com/jorgenws/MimeSpy/main/mimespy.png)

A library to detect mime type from file headers.

## Usage

```csharp
var spy = new MimeSpy();
IReadOnlyList<PronomFormat> results = spy.Spy(bytes);

foreach (var result in results)
{
    Console.WriteLine($"{result.MimeType} ({string.Join(", ", result.Extensions)}): {result.Name} ({result.Puid})");
}
```

MimeSpy identifies formats using [PRONOM](https://www.nationalarchives.gov.uk/PRONOM/), the UK National Archives' file-format signature registry, through three methods that trade how much of the file you can give them for how confirmed the answer is:

- **`Sniff`** only evaluates signatures anchored to the start of the file. Safe against a partial or streamed-in-pieces buffer - anything it reports is guaranteed to also be reported by `Investigate` against the same file's complete bytes - but it can't report a format whose every PRONOM signature needs to check the file's end (PNG, JPEG, GIF, and plain ZIP among them).
- **`Spy`** reports everything `Sniff` does, plus formats PRONOM can only fully confirm by a trailer near the end of the file - the practical default for a quick "what kind of file is this" guess from a partial read. Unlike `Sniff`, a match here isn't guaranteed to hold up once the whole file is checked. Given a `Stream` whose length is known and small, `Spy` reads it in full and returns `Investigate`'s fully-confirmed answer instead of guessing from a partial read.
- **`Investigate`** requires the complete file. It evaluates end-anchored signatures for real, and runs PRONOM's container-signature engine to resolve a generic ZIP/OLE2 match to its specific format (docx vs xlsx vs plain zip, doc vs xls, ...).

A single header can genuinely match more than one format, so callers should expect more than one `PronomFormat` back from any of these - see [docs/adr/0001-ambiguous-matches-returned-as-ties.md](docs/adr/0001-ambiguous-matches-returned-as-ties.md). The three-way split, what each one can and can't see, and why, is covered in [docs/adr/0016](docs/adr/0016-sniff-and-investigate-split-by-what-end-of-file-they-can-see.md), [docs/adr/0017](docs/adr/0017-spy-skips-eof-confirmation-instead-of-a-uniqueness-flag.md), and [docs/adr/0019](docs/adr/0019-spy-stream-adapts-to-a-known-length-not-a-detected-failure.md).

All three take a `ReadOnlySpan<byte>` or a `Stream`, and the `Stream` overloads have async twins (`SniffAsync`/`SpyAsync`/`InvestigateAsync`). There's no async overload of the `ReadOnlySpan<byte>` form - see [docs/adr/0014-async-overload-only-on-the-stream-api.md](docs/adr/0014-async-overload-only-on-the-stream-api.md).

### Dependency injection

MimeSpy has no constructor arguments and no mutable state - every method here is safe to call concurrently from multiple requests on the same instance - so it only needs registering as a singleton, with no MimeSpy-specific extension method required:

```csharp
services.AddSingleton<IMimeSpy, MimeSpy>();
```

Consumers then take an `IMimeSpy` constructor dependency like any other singleton service. The interface exists purely as a mockable seam for callers whose own flow calls these methods inline rather than taking a pre-computed `PronomFormat` list as input.

## Building & testing

```
dotnet build
dotnet test
```

Tests use xunit v3 on the Microsoft.Testing.Platform runner; both test projects build as executables, so `dotnet run --project MimeSpy.Tests` (or `MimeSpy.IntegrationTests`) also works.

`MimeSpy.Tests` is mostly hand-built byte arrays covering specific signature-matching and disambiguation rules. `MimeSpy.IntegrationTests` runs `Investigate()` against real, genuinely-encoded sample files - one per mainstream format - committed under `MimeSpy.IntegrationTests/Fixtures/`; see that project's README for where each one came from.

See [docs/releasing.md](docs/releasing.md) for how to cut a new NuGet release.

## Data

Both signature tables are [PRONOM](https://www.nationalarchives.gov.uk/PRONOM/)'s own data, published by the UK National Archives: `Resources/pronom-droid-signatures.xml` is DROID's signature file (format/extension/mime-type metadata plus the byte-sequence signatures themselves), and `Resources/pronom-container-signatures.xml` is DROID's container signature file (the rules behind resolving a generic ZIP/OLE2 match to a specific format - docx vs xlsx vs plain zip, doc vs xls, ...). Both are embedded as resources under [Resources/](Resources/) in their native XML and parsed once into in-memory indexes at first use.

## License

[MIT](LICENSE).
