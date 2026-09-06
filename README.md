# MimeSpy

Library to detect mime type from file headers.

## Usage

```csharp
var spy = new MimeSpy();
IReadOnlyList<Result> results = spy.Spy(bytes);

foreach (var result in results)
{
    Console.WriteLine($"{result.PrimaryMimeType} ({string.Join(", ", result.Extensions)}): {result.Description}");
}
```

`Spy` takes a `ReadOnlySpan<byte>` (a full file or just its leading bytes) and returns every signature that matches at the longest matched header length. A single header can genuinely match more than one format, so callers should expect more than one `Result` back - see [docs/adr/0001-ambiguous-matches-returned-as-ties.md](docs/adr/0001-ambiguous-matches-returned-as-ties.md).

Passing fewer bytes never produces a wrong answer, only a less specific one: 532 bytes is the true minimum, reaching every fixed-offset signature in the embedded table, but a general-purpose caller reading its own byte buffer (rather than going through the `Stream`/`SpyAsync` overloads, which already read this much for you) should read 8192 bytes - that's what's needed for the ASF/WMA/WMV disambiguation below, and it's the number `Spy(Stream)` itself reads.

`Spy` also takes a `Stream`, and that overload has an async twin, `SpyAsync(Stream, CancellationToken)`, for callers on an async path (e.g. reading a file upload in an ASP.NET Core handler) who don't want a blocking read on a thread-pool thread. There's no async overload of the `ReadOnlySpan<byte>` form - see [docs/adr/0014-async-overload-only-on-the-stream-api.md](docs/adr/0014-async-overload-only-on-the-stream-api.md).

### Dependency injection

MimeSpy has no constructor arguments and no mutable state - `Spy`/`SpyAsync` are safe to call concurrently from multiple requests on the same instance - so it only needs registering as a singleton, with no MimeSpy-specific extension method required:

```csharp
services.AddSingleton<IMimeSpy, MimeSpy>();
```

Consumers then take an `IMimeSpy` constructor dependency like any other singleton service. The interface exists purely as a mockable seam for callers whose own flow calls `Spy`/`SpyAsync` inline rather than taking a pre-computed `Result` as input - if your code already separates "call Spy at the boundary" from "react to the Result", you can construct `Result` directly in tests and don't need the interface at all.

### ZIP-based formats

Plain ZIP archives and everything that's "just a zip" underneath (docx/xlsx/pptx, jar, apk, odt/odp/ott, epub, kmz...) share the same 4-byte header. MimeSpy peeks at the name - and, for OpenDocument files, the stored content - of the archive's first entry to narrow this down when those bytes are available, without ever requiring the file's central directory. See [docs/adr/0002-zip-disambiguation-reads-only-supplied-bytes.md](docs/adr/0002-zip-disambiguation-reads-only-supplied-bytes.md).

### Ogg-based formats

Ogg's `OggS` container header is shared by audio (Vorbis, Opus, Speex, FLAC-in-Ogg), video (Theora, OGM, Skeleton), and other payloads (Kate) alike, so MimeSpy peeks at the codec identification packet right after the first page's header to report `audio/ogg`, `video/ogg`, or `application/ogg` correctly instead of always guessing audio. See [docs/adr/0007-ogg-mime-type-resolved-by-content-sniffing.md](docs/adr/0007-ogg-mime-type-resolved-by-content-sniffing.md).

### ASF/WMA/WMV

ASF, WMA, and WMV all share the same Header Object GUID, so MimeSpy searches the first 8192 bytes for the codec-name string a Windows Media encoder writes into the file, the same heuristic Apache Tika uses, to report `audio/x-ms-wma` or `video/x-ms-wmv` instead of always guessing `video/x-ms-asf`. See [docs/adr/0008-asf-mime-type-resolved-by-codec-name-search.md](docs/adr/0008-asf-mime-type-resolved-by-codec-name-search.md).

## Building & testing

```
dotnet build
dotnet test
```

Tests use xunit v3 on the Microsoft.Testing.Platform runner; both test projects build as executables, so `dotnet run --project MimeSpy.Tests` (or `MimeSpy.IntegrationTests`) also works.

`MimeSpy.Tests` is mostly hand-built byte arrays covering specific signature-matching and disambiguation rules. `MimeSpy.IntegrationTests` runs the same `Spy()` API against real, genuinely-encoded sample files - one per mainstream format - committed under `MimeSpy.IntegrationTests/Fixtures/`; see that project's README for where each one came from.

## Data

File headers are from https://www.garykessler.net/software/index.html#filesigs.
Extension to MIME type mapping is from: https://svn.apache.org/repos/asf/httpd/httpd/trunk/docs/conf/mime.types
Ogg codec identification patterns are from `file(1)`'s libmagic rules: https://raw.githubusercontent.com/file/file/master/magic/Magdir/vorbis
ASF/WMA/WMV codec-name markers are from Apache Tika's mime-type table: https://raw.githubusercontent.com/apache/tika/main/tika-core/src/main/resources/org/apache/tika/mime/tika-mimetypes.xml

The first three tables are embedded as resources under [Resources/](Resources/) in their native formats and parsed once into in-memory indexes at first use - see [docs/adr/0003](docs/adr/0003-signature-and-mimetype-data-as-embedded-json.md), [docs/adr/0005](docs/adr/0005-mimetype-table-sourced-from-apache-mimetypes.md), [docs/adr/0006](docs/adr/0006-source-tables-embedded-in-native-format.md), and [docs/adr/0007](docs/adr/0007-ogg-mime-type-resolved-by-content-sniffing.md). The ASF markers are few enough to live as constants directly in `AsfContainerSniffer` rather than a data file - see [docs/adr/0008](docs/adr/0008-asf-mime-type-resolved-by-codec-name-search.md).

`Resources/file_signatures_supplemental.csv` holds signature-table rows MimeSpy adds on top of Gary Kessler's table (same format, no header row) rather than editing them into `file_signatures.csv` directly, so that file stays a straight, overwritable copy of the upstream source - see [docs/adr/0010](docs/adr/0010-supplemental-signature-file-for-additions.md).

## License

[MIT](LICENSE).
