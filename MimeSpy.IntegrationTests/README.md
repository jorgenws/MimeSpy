# MimeSpy.IntegrationTests

Runs `MimeSpy.Spy()` against real files - one per format - in `Fixtures/`, as opposed to
`MimeSpy.Tests`, which is mostly hand-built byte arrays plus a handful of samples of its
own.

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

### Apache Tika-sourced fixtures

Tika is an independent, unrelated Apache project that maintains a large collection of
real-world sample files (Office documents, PDFs, archives, ...) to test its own format
detection and parsing - exactly the kind of independently-produced file this project
needs, and already a source this repo cites for the ASF/WMA/WMV codec markers (see the
root [README](../README.md)). Each fixture below was fetched once (via the GitHub API's
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

Several of these turned out to be more useful for being real than a hand-built file ever
could be:

- `sample.docx`'s first zip entry is a `_rels/` directory placeholder, and `sample.jar`'s
  is a `META-INF/` directory placeholder - neither is the file `ZipContainerSniffer` looks
  for (`[Content_Types].xml`, `META-INF/MANIFEST.MF`). Real writers commonly emit these;
  a hand-built fixture never would have. `ZipContainerSniffer` was fixed to skip past a
  leading zero-length directory entry before checking the name (docs/adr/0009) -
  `sample.jar` now narrows correctly, `sample.docx` still doesn't (its next entry,
  `_rels/.rels`, is a real file that isn't `[Content_Types].xml` either - the fix only
  walks past placeholders, it doesn't hunt through the archive). `RealSampleFileTests`
  documents both outcomes explicitly.
- `sample.ods`/`sample.odp`'s stored `mimetype` entries surfaced that
  `ZipContainerSniffer` always answered `"odt"` for ODF content regardless of what the
  entry actually said, that `ods`/`ots`/`otp` had no signature-table entry at all, and
  that even `odt`/`odp` were tied together in one upstream row despite being unrelated
  formats - all fixed in docs/adr/0010 (a `file_signatures_supplemental.csv` whose rows
  can claim extensions away from a base-file row with the same header, not just add
  alongside it, kept separate so `file_signatures.csv` stays an overwritable copy of its
  upstream source). `sample.odt`/`sample.ods`/`sample.odp` each now narrow to exactly
  their own extension, not a multi-way tie.
- `sample.epub` doesn't get identified as `epub` at all: its header's general-purpose
  flag doesn't match the strict EPUB signature, and its first non-directory entry
  (`META-INF/container.xml`) isn't `mimetype` either, so neither detection path fires.
  Documented as a known gap rather than fixed - see the comment on
  `Spy_RealEpubFile_IsNotIdentifiedAsEpub_KnownGap` in `RealSampleFileTests.cs`.
- `sample.doc`/`sample.xls`/`sample.ppt` (real LibreOffice output, all sharing the OLE2/
  CFBF header `D0 CF 11 E0 A1 B1 1A E1`) originally all produced the exact same 17-way
  tied result - `file_signatures.csv` carries offset-512 "subheader" signatures that look
  meant to disambiguate doc/xls/ppt, but none of the specific ones match bytes
  LibreOffice's writer actually produces at that offset, and the one generic pattern that
  does match (`FD FF FF FF`, the "Thumbs.db subheader") is shorter than the main 8-byte
  header match, so it never affects the result either way. Fixed by `Ole2ContainerSniffer`
  parsing the OLE2 directory sector to find the distinctive stream name
  (`WordDocument`/`Workbook`/`PowerPoint Document`) - confirmed, in these three fixtures,
  to sit near the *end* of the file, not in a small fixed window near the start the way
  ASF's codec name is (docs/adr/0008), so this needed a real (if minimal) CFB/OLE2 reader
  rather than a bounded string search - see docs/adr/0011. Each fixture now narrows to a
  single result, still tied with its own template/add-in/slideshow sibling
  (doc/dot, xls/xla, ppt/pps) since the CFB structure alone can't tell those apart.

### Not included

- **woff (v1)**: superseded by woff2, no local tool produces it, and hand-rolling the
  zlib-per-table WOFF1 layout wasn't judged worth it for a legacy format.
- **heic, avif**: the local ImageMagick build has no HEIC/AVIF delegate compiled in.
- **apk**: not present anywhere in Apache Tika's test-documents corpus.
