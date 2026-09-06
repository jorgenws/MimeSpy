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

### ZIP-based formats

Plain ZIP archives and everything that's "just a zip" underneath (docx/xlsx/pptx, jar, apk, odt/odp/ott, epub, kmz...) share the same 4-byte header. MimeSpy peeks at the name - and, for OpenDocument files, the stored content - of the archive's first entry to narrow this down when those bytes are available, without ever requiring the file's central directory. See [docs/adr/0002-zip-disambiguation-reads-only-supplied-bytes.md](docs/adr/0002-zip-disambiguation-reads-only-supplied-bytes.md).

## Building & testing

```
dotnet build
dotnet test
```

Tests use xunit v3 on the Microsoft.Testing.Platform runner; `MimeSpy.Tests` builds as an executable, so `dotnet run --project MimeSpy.Tests` also works.

## Data

File headers are from https://www.garykessler.net/software/index.html#filesigs.
Extension to MIME type mapping is from: https://svn.apache.org/repos/asf/httpd/httpd/trunk/docs/conf/mime.types

Both tables are embedded as resources under [Resources/](Resources/) in their native formats and parsed once into in-memory indexes at first use - see [docs/adr/0003](docs/adr/0003-signature-and-mimetype-data-as-embedded-json.md), [docs/adr/0005](docs/adr/0005-mimetype-table-sourced-from-apache-mimetypes.md), and [docs/adr/0006](docs/adr/0006-source-tables-embedded-in-native-format.md).

## License

Not yet chosen. MimeSpy embeds data derived from the two sources above, so their license terms should be checked before picking and publishing under a license.
