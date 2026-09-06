# Legacy OLE2 Office formats (doc/xls/ppt) are disambiguated by directory entry name, not by a fixed-offset byte pattern

`doc`, `xls`, `ppt` (and `dot`, `xla`, `pps`, plus unrelated formats like Access, Visio,
Publisher, MSI) are all OLE2/Compound File Binary Format files sharing the identical
8-byte header `D0 CF 11 E0 A1 B1 1A E1`. `file_signatures.csv`'s "Microsoft Office
document" row already bundles `DOC|DOT|PPS|PPT|XLA|XLS|WIZ` into one row's `Extensions`
list, the same way its "OpenDocument template" row bundled `odt`/`odp`/`ott` before
docs/adr/0010 - as if they were interchangeable aliases rather than unrelated formats
that just happen to share a header.

The table does carry offset-512 "subheader" rows that look purpose-built to break this
tie (`Word document subheader`, `Excel spreadsheet subheader_1..7`, `PowerPoint
presentation subheader_1..6`). Checked byte-for-byte against real fixtures (LibreOffice's
own Word 97/Excel 97/PowerPoint 97 export filters - `MimeSpy.IntegrationTests/Fixtures/sample.{doc,xls,ppt}`):
none of the specific patterns match. All three files' bytes at offset 512 only match the
generic 4-byte `Thumbs.db subheader` pattern (`FD FF FF FF`), which is shared by all
three (so it disambiguates nothing) and is shorter than the 8-byte main header match
anyway, so it never even reaches the returned result under `MimeSpy.cs`'s
longest-match-wins rule. Whatever software wrote the files those subheader rows were
captured from, it isn't what LibreOffice's binary-format writer produces - tweaking or
adding more fixed-offset rows like these would just be guessing at another specific
writer's quirks.

## What actually identifies the format

Apache Tika/POI's `POIFSContainerDetector` doesn't use any fixed-offset byte pattern
either: it fully parses the file as a CFB structured-storage container and matches on
the **directory entry names** it contains - `WordDocument` -> doc, `Workbook`/`Book` ->
xls, `PowerPoint Document` -> ppt (plus others this library doesn't need: `Quill` ->
Publisher, `VisioDocument` -> vsd, `__substg1.0_*` -> Outlook msg, ...). Confirmed
against the same three fixtures: `"WordDocument"` sits at byte 10880 of an 11264-byte
`sample.doc`, `"Workbook"` at byte 5760 of 6656 in `sample.xls`, `"PowerPoint Document"`
at byte 607872 of 608256 in `sample.ppt` - near the *end* of the file every time, unlike
ASF's codec-name search (docs/adr/0008) which lives in a small fixed window near the
start. The directory sector's actual location depends on the file's own sector
allocation, so reaching it needs a real (indexed) parse of the CFB header and FAT, not a
bounded linear scan.

`Ole2ContainerSniffer` implements the minimum slice of MS-CFB needed to reach those
names, mirroring `ZipContainerSniffer`'s scope discipline (docs/adr/0002 - never seeks
beyond what's already been supplied, falls back gracefully on truncated input):

- **Header** (fixed 512 bytes): sector size and the first 109 DIFAT entries (the FAT
  sector locations embedded directly in the header). Files needing more than 109 FAT
  sectors (roughly 7MB+ at 512-byte sectors) would need to walk the additional-DIFAT-sector
  chain, which isn't implemented - such a file just falls through to the full tied list,
  same as any other out-of-reach case.
- **FAT walk**: enough to follow the directory stream's sector chain from its header-given
  starting sector to its end, guarded against cyclic/malformed chains by tracking visited
  sectors.
- **Directory entries**: every 128-byte entry in every directory sector is scanned
  linearly for its UTF-16LE name, without walking the entries' red-black-tree
  sibling/child pointers to tell root-level entries from nested ones - the same
  simplification Tika itself makes by handing `detect()` a flat `Set<String>` of names.
  None of the four recognized names are expected to occur as a coincidental nested entry
  in an unrelated format, so this doesn't risk a false positive in practice.

## Why doc/dot, xls/xla, and ppt/pps stay tied to each other

A `.dot` template's internal structure is identical to a `.doc` document - both contain
a `WordDocument` stream, with nothing in the CFB structure itself marking one as a
template. The same holds for `.xls`/`.xla` (`Workbook`) and `.ppt`/`.pps` (`PowerPoint
Document`) - confirmed against Tika's own mapping, which resolves all of these to the
same handful of media types without a separate template/show variant. So the
supplemental file (`file_signatures_supplemental.csv`, docs/adr/0010) splits the
upstream row into three two-extension rows - `DOC|DOT`, `XLS|XLA`, `PPT|PPS` - not six
fully independent ones: `Ole2ContainerSniffer` can tell a Word file from an Excel file
from a PowerPoint file, but has no way to tell a document from its own template, and
splitting further than the sniffer can actually resolve would silently drop the
untellable-apart sibling from the result instead of keeping it as an honest tie. This is
the same call already made for docx/pptx/xlsx (`MS Office Open XML Format Document`
stays one three-extension row - see `Spy_RealPptxFile_NarrowsToOfficeOpenXmlTiedWithDocxAndXlsx`
in `MimeSpy.IntegrationTests/RealSampleFileTests.cs`), just one level less granular here
because the sniffing signal (a stream name) is coarser than ZIP's (a full entry name).
`WIZ` (Microsoft Wizard template) isn't claimed by any supplemental row and is left
alone in the upstream row, unrelated to Word/Excel/PowerPoint.
