![MimeSpy](https://raw.githubusercontent.com/jorgenws/MimeSpy/main/mimespy.png)

A library to detect mime type from file headers.

## Usage

```csharp
var spy = new MimeSpy();
IReadOnlyList<Dossier> results = spy.Spy(bytes);

foreach (var result in results)
{
    Console.WriteLine($"{result.MimeType} ({string.Join(", ", result.Extensions)}): {result.Name} ({result.Puid}), {result.Match} match");
}
```

MimeSpy identifies formats using [PRONOM](https://www.nationalarchives.gov.uk/PRONOM/), the UK National Archives' file-format signature registry, through two methods that trade how much of the file you can give them for how confirmed the answer is:

- **`Spy`** is the practical default for a quick "what kind of file is this" guess from a partial read. It evaluates a signature's header (start-of-file) portion and skips any trailer check it would otherwise need - so it also reports formats PRONOM can only fully confirm by a trailer near the end of the file (PNG, JPEG, GIF, and plain ZIP among them). That means a match here isn't guaranteed to hold up once the whole file is checked - a truncated or corrupted file could pass the header check and still fail `Investigate`.
- **`Investigate`** requires the complete file. It evaluates end-anchored signatures for real, and runs PRONOM's container-signature engine to resolve a generic ZIP/OLE2 match to its specific format (docx vs xlsx vs plain zip, doc vs xls, ...). `Investigate(Stream)` needs a seekable stream at position 0, and doesn't read it into memory: past 16 MiB it reads 8 MiB from each end, plus the container parts it needs, so a 50 GB file costs about the same as a 20 MB one. See [docs/adr/0022](docs/adr/0022-investigate-stream-requires-seeking-and-reads-bounded-windows.md) for the trade-off that involves.

When two matching formats are related by PRONOM's priority data (WAV over generic RIFF, say), only the more specific one is returned ([docs/adr/0023](docs/adr/0023-droid-priority-relations-drop-outranked-matches.md)). Bytes can still genuinely match more than one format, so callers should expect more than one `Dossier` back from either method - see [docs/adr/0001-ambiguous-matches-returned-as-ties.md](docs/adr/0001-ambiguous-matches-returned-as-ties.md).

Each `Dossier` has a `Match`: `Full` when every part of the format's signature was checked, `Partial` when `Spy` skipped its end-of-file check. Results are ordered full matches first, then more specific formats before the ones they refine, so `results[0]` is the best single answer. A plain TIFF under `Spy`, for example, comes back as TIFF (full) followed by the DNG, GeoTIFF and Exif variants its header is also consistent with (partial) - see [docs/adr/0024](docs/adr/0024-dossier-reports-full-or-partial-match-and-results-are-ordered.md). The split, what each one can and can't see, and why, is covered in [docs/adr/0017](docs/adr/0017-spy-skips-eof-confirmation-instead-of-a-uniqueness-flag.md) and [docs/adr/0019](docs/adr/0019-spy-stream-adapts-to-a-known-length-not-a-detected-failure.md).

Both take a `ReadOnlySpan<byte>` or a `Stream`, and the `Stream` overloads have async twins (`SpyAsync`/`InvestigateAsync`). There's no async overload of the `ReadOnlySpan<byte>` form - see [docs/adr/0014-async-overload-only-on-the-stream-api.md](docs/adr/0014-async-overload-only-on-the-stream-api.md).

### `Spy`'s two overloads aren't the same guess

`Spy(ReadOnlySpan<byte>)` always does the header-only guess above, no matter how much of the span you hand it - a span's length doesn't tell MimeSpy whether it's the complete file or a truncated prefix, so it never tries to infer that. If you already know you're holding the complete file, call `Investigate(ReadOnlySpan<byte>)` directly instead of asking `Spy` to guess at something you already know.

`Spy(Stream)` is smarter, because a `Stream` *can* answer that question honestly (a seekable stream must be at position 0 - the stream is the whole file): if the stream is seekable and its reported length is small (≤ 8 MB), `Spy` reads it in full and returns `Investigate`'s fully-confirmed answer - real end-of-file checks, real container disambiguation - instead of a partial-read guess. Only a non-seekable or larger stream falls back to the bounded, header-only peek. See [docs/adr/0019](docs/adr/0019-spy-stream-adapts-to-a-known-length-not-a-detected-failure.md) for why this is gated on a fact the stream already reports, not on trying `Investigate` and detecting whether it "worked".

### Limiting which formats are considered

If you already know you'll only ever hand MimeSpy one broad category of file - say, a service that only ever ingests images - `MimeSpyOptions` lets a `MimeSpy` instance skip the rest of PRONOM's catalog entirely on every call, rather than matching against it and discarding the result:

```csharp
var spy = new MimeSpy(new MimeSpyOptions { MimeTypes = ["image/", "audio/"] });
```

`MimeTypes` patterns are matched by case-insensitive starting-with against each format's own MIME type - `"image/"` catches every image subtype, `"application/pdf"` catches that one exactly - and are additive: a format is included if it matches *any* pattern. The default (no options at all, or an empty `MimeTypes` list) considers every cataloged format, identical to `new MimeSpy()`. The narrowed index is built once, at construction, not per call.

### Dependency injection

The default `MimeSpy()` constructor takes no arguments, and a `MimeSpy` instance - filtered or not - has no mutable state once built, so every method is safe to call concurrently from multiple requests on the same instance. Register it as a singleton, with no MimeSpy-specific extension method required:

```csharp
services.AddSingleton<IMimeSpy, MimeSpy>();
```

Consumers then take an `IMimeSpy` constructor dependency like any other singleton service. The interface exists purely as a mockable seam for callers whose own flow calls these methods inline rather than taking a pre-computed `Dossier` list as input. To register a filtered instance instead, resolve `MimeSpyOptions` through DI or build the singleton yourself:

```csharp
services.AddSingleton<IMimeSpy>(new MimeSpy(new MimeSpyOptions { MimeTypes = ["image/"] }));
```

## Performance

`MimeSpy.Benchmarks` has the full BenchmarkDotNet suite and its own README; these are a couple of representative numbers from it (per-call mean / allocated, `Investigate` unless noted).

`Spy` vs `Investigate` on the same file - usually close (the extra work `Investigate` does, EOF confirmation and container disambiguation, is cheap for most formats), but not always:

| File | `Investigate` | `Spy` |
| --- | --- | --- |
| sample.mp3 | 244 µs / 675 KB | 120 µs / 134 KB |
| sample.zip | 89 µs / 109 KB | 83 µs / 91 KB |

`MimeSpyOptions` scoping to `image/` - an order of magnitude, on both a file it matches and one it doesn't (the filtered instance still has to conclude "not an image," just over a far smaller candidate set):

| File | Unfiltered | `MimeTypes = ["image/"]` |
| --- | --- | --- |
| sample.png | 84 µs / 74 KB | 5.9 µs / 8.2 KB |
| sample.pdf | 751 µs / 138 KB | 55 µs / 40 KB |

## Building & testing

```
dotnet build
dotnet test
```

Tests use xunit v3 on the Microsoft.Testing.Platform runner; both test projects build as executables, so `dotnet run --project MimeSpy.Tests` (or `MimeSpy.IntegrationTests`) also works.

`MimeSpy.Tests` is mostly hand-built byte arrays covering specific signature-matching and disambiguation rules. `MimeSpy.IntegrationTests` runs `Investigate()` against real, genuinely-encoded sample files - one per mainstream format - committed under `MimeSpy.IntegrationTests/Fixtures/`; see that project's README for where each one came from.

`MimeSpy.Benchmarks` has the full BenchmarkDotNet suite behind the [Performance](#performance) numbers above, plus a comparison against [Mime-Detective](https://github.com/MediatedCommunications/Mime-Detective), the closest actively-maintained .NET equivalent - see that project's README.

See [docs/releasing.md](docs/releasing.md) for how to cut a new NuGet release.

## Data

Both signature tables are [PRONOM](https://www.nationalarchives.gov.uk/PRONOM/)'s own data, published by the UK National Archives: `Resources/pronom-droid-signatures.xml` is DROID's signature file (format/extension/mime-type metadata plus the byte-sequence signatures themselves), and `Resources/pronom-container-signatures.xml` is DROID's container signature file (the rules behind resolving a generic ZIP/OLE2 match to a specific format - docx vs xlsx vs plain zip, doc vs xls, ...). Both are embedded as resources under [Resources/](Resources/) in their native XML and parsed once into in-memory indexes at first use.

## License

[MIT](LICENSE).
