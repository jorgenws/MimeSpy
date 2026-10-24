# MimeSpy.IntegrationTests

Runs `MimeSpy.Investigate()` against real files - one per format - in `Fixtures/`, as
opposed to `MimeSpy.Tests`, which is mostly hand-built byte arrays plus a handful of
samples of its own.

## Fixtures

The point of this project is outside validation: a file whose bytes came from me (or an
LLM) proves nothing that a hand-built byte array in `MimeSpy.Tests` doesn't already prove
- both are just "does MimeSpy agree with my own understanding of the format." Every
fixture here instead comes from an independent source: a real encoder/tool actually
implementing the format, or a real-world sample file collected by someone else's test
suite. `dotnet test` itself still runs fully offline - fixtures are committed once, not
fetched per run (`.gitattributes` marks `Fixtures/` as binary - no line-ending
normalization, no diffing).

| Formats | Source |
| --- | --- |
| jpg, png, gif, bmp, ico, tiff, webp | Generated locally by ImageMagick's real codecs: `magick -size 64x64 gradient:skyblue-navy sample.<ext>` |
| wav, mp3, ogg, flac, m4a | Generated locally by `ffmpeg`, encoding a 1s 440Hz sine wave (`-f lavfi -i sine=...`) with each format's real codec (libmp3lame, libvorbis, flac, aac) |
| mp4, webm, avi, mkv, mov, ogv | Generated locally by `ffmpeg`, encoding a 1s `testsrc` test pattern with each container's usual codec (libx264, libvpx-vp9, mpeg4, libtheora) |
| gz, bz2, xz, tar | Generated locally by the real `gzip` / `bzip2` / `xz` / `tar --format=ustar` tools over a small text file |
| ttf, otf | Copied from locally installed, permissively-licensed open fonts: `ttf-bitstream-vera` (Bitstream Vera License) and `cantarell-fonts` (SIL OFL) |
| woff2 | Generated locally by Google's real `woff2_compress` reference tool, from the ttf above |
| pdf, rtf, docx, xlsx, pptx, odt, ods, odp, epub, jar, zip, 7z | Real-world sample files from [Apache Tika's test-documents](https://github.com/apache/tika) (Apache License 2.0) - see below |
| doc, xls, ppt | Generated locally by LibreOffice's real Word 97/Excel 97/PowerPoint 97 export filters, converting the Tika-sourced sample.odt/sample.ods/sample.odp above: `soffice --headless --convert-to doc:"MS Word 97" sample.odt` (same pattern for xls/ppt) |
| heic | Real-world file of unspecified web origin (not locally generated - see below) |

### Apache Tika-sourced fixtures

Tika is an independent, unrelated Apache project that maintains a large collection of
real-world sample files (Office documents, PDFs, archives, ...) to test its own format
detection and parsing - exactly the kind of independently-produced file this project
needs. Each fixture below was fetched once (via the GitHub API's
`download_url`, from `apache/tika`'s `main` branch) and is committed here as-is:

| Fixture | Tika path |
| --- | --- |
| sample.pdf | `tika-parsers/.../tika-parser-pdf-module/src/test/resources/test-documents/testPDF.pdf` |
| sample.rtf | `tika-parsers/.../tika-parser-microsoft-module/.../test-documents/testRTF.rtf` |
| sample.docx | `tika-parsers/.../tika-parser-microsoft-module/.../test-documents/testWORD.docx` |
| sample.xlsx | `tika-parsers/.../tika-parser-microsoft-module/.../test-documents/testEXCEL.xlsx` |
| sample.pptx | `tika-parsers/.../tika-parser-microsoft-module/.../test-documents/testPPT.pptx` |
| sample.odt | `tika-parsers/.../tika-parser-miscoffice-module/.../test-documents/testFooter.odt` |
| sample.ods | `tika-parsers/.../tika-parser-miscoffice-module/.../test-documents/testFooter.ods` |
| sample.odp | `tika-parsers/.../tika-parser-miscoffice-module/.../test-documents/testMasterFooter.odp` |
| sample.epub | `tika-parsers/.../tika-parser-miscoffice-module/.../test-documents/testEPUB.epub` |
| sample.jar | `tika-parsers/.../tika-parser-pkg-module/.../test-documents/testJAR_with_HTML.jar` |
| sample.zip | `tika-parsers/.../tika-parser-pkg-module/.../test-documents/testEmbedded.zip` |
| sample.7z | `tika-parsers/.../tika-parser-pkg-module/.../test-documents/full_encrypted.7z` |

(`...` is `tika-parsers-standard/tika-parsers-standard-modules`, elided for width - the
full path is in each file's git history / the fetch script used at the time.)

Real files catch what hand-built ones don't: real writers put directory placeholders
ahead of the entry that matters, vary box sizes, and emit structures no spec example
shows. The expected results in `RealSampleFileTests.cs` were captured by running the
PRONOM engine against each fixture, and the known gaps (e.g. otf) are documented on
their own tests there. (The fixtures predate the PRONOM engine; what they surfaced in
the old one is in docs/adr/0009-0011, kept as history.)

### Not included

- **woff (v1)**: superseded by woff2, no local tool produces it, and hand-rolling the
  zlib-per-table WOFF1 layout wasn't judged worth it for a legacy format.
- **avif**: the local ImageMagick build has no AVIF delegate compiled in.
- **apk**: not present anywhere in Apache Tika's test-documents corpus.
- **heif**: a file downloaded under this name turned out to declare major brand `heic` -
  byte-for-byte identical to `sample.heic` in the part that encodes format identity (both
  `file(1)`/libmagic and Apache Tika's own brand-based mimetype rules call it
  `image/heic` too). It was really just a second `heic` file wearing a `.heif` filename,
  so it was dropped rather than kept as a duplicate.

### Not locally generated

- **heic**: `libheif`'s `heif-enc` (the real HEVC-based HEIF/AVIF encoder, installed
  specifically to try generating this) refuses to encode below 10-bit
  (`-b`/`--bit-depth` only accepts 9-16 on this build) and so can only ever produce a
  `heix`-branded (10-bit "range extension" profile) file, not the plain `heic`-branded
  files real cameras and phones actually produce almost universally. Rather than swap a
  realistic, correctly-detected fixture for a less representative locally-generated one,
  `sample.heic` stays a real-world file of unspecified web origin.
