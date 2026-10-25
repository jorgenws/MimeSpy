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

`MimeTypes` patterns are a plain, case-insensitive starts-with against each format's MIME types, so any prefix works - `"image/"` catches every image subtype, `"image/j"` catches every one starting that way, such as `image/jpeg` and `image/jxl`. A full type also catches longer types that start with it: `"font/woff"` includes `font/woff2`, and `"application/vnd.ms-excel"` includes the macro-enabled Excel types. Patterns are additive: a format is included if any of its types matches *any* pattern. The default (no options at all, or an empty `MimeTypes` list) considers every cataloged format, identical to `new MimeSpy()`. The narrowed index is built once, at construction, not per call.

`ExcludedMimeTypes` takes patterns the same way and leaves out every format with a type matching one, even if `MimeTypes` includes it. Combined, they say exactly which answers you want - for example every image format except DNG, so a DNG file reports as the TIFF it's built on rather than as DNG:

```csharp
var spy = new MimeSpy(new MimeSpyOptions { MimeTypes = ["image/"], ExcludedMimeTypes = ["image/dng"] });
```

A left-out format is never returned and never outranks one that's allowed. The generic ZIP and OLE2 formats are still matched behind the scenes when needed, so excluding `application/zip` - or including only `application/vnd.openxmlformats` - still finds a docx by opening the archive; the plain-ZIP answer is what's left out. See [docs/adr/0027](docs/adr/0027-options-exclude-formats-and-filter-results-not-just-signatures.md).

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

Contains public sector information licensed under the [Open Government Licence v3.0](https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/).

PRONOM gives most formats no MIME type, including some common ones (7z, Matroska, xz, AVIF, MPEG video, ISO images). `Resources/mimespy-mime-types.xml` fills those in for a short, hand-checked list, using Apache httpd's `mime.types`. The same file corrects types PRONOM does give where they're unhelpful. For example, PRONOM lists MP4's types alphabetically, so `application/mp4` would come first; MimeSpy puts `video/mp4` first. Each correction states the PRONOM value it was written against, and loading fails if PRONOM has since changed it, so a PRONOM update is never silently overwritten. See [docs/adr/0026](docs/adr/0026-mime-types-for-common-formats-pronom-leaves-empty.md) and [docs/adr/0029](docs/adr/0029-mime-type-overlay-corrects-pronom-not-just-fills-gaps.md).

## License

The code is [MIT](LICENSE). The embedded PRONOM signature files under [Resources/](Resources/) are The National Archives' data, under the [Open Government Licence v3.0](https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/) - see [Data](#data).
