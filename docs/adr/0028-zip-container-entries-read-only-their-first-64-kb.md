# ZIP container entries are read only up to their first 64 KB

`PronomZipContainerReader` decompressed every entry a container signature names
completely into memory, and docs/adr/0022 left that unbounded on purpose. A zip bomb
exploits exactly that. Deflate expands up to about 1032:1, so a 1 MB
`[Content_Types].xml` holding 1 GB of zeros cost a 3.1 GB peak working set, and
2.5 GB made `Investigate(Stream)` throw `IOException: Stream was too long`. A 64 MiB
entry allocated 336 MB, because `MemoryStream` doubles as it grows and `ToArray`
copies once more.

**Decision:** read at most the first 64 KB of an entry
(`PronomZipContainerReader.EntryPrefixLength`), then stop. Since decompression
happens only as the entry stream is read, nothing past that point is ever
inflated. The 64 MiB bomb now allocates 731 KB in total. The rest of the entry
can't be skipped in any cheaper way: deflate decodes only forward, and the entry
stream can't seek.

## Why a prefix is enough

PRONOM's ZIP container signatures, as of the copy this was written against:

| What the signature checks in an entry | Files |
|---|---|
| Only that it exists | 44 |
| A bounded distance from the start | ~150, reaching at most ~40 KB (Excel OOXML, signature 2030) |
| A string anywhere in it (no `SubSeqMaxOffset`) | ~27: PowerPoint OOXML 3010-3040, OpenDocument `content.xml`, WACZ `datapackage.json` |
| Anything counted from the end | 0 |

So no ZIP signature needs an entry's end. Every bounded one fits inside 64 KB, with
room to spare. Only the unbounded searches lose anything: they now cover the first
64 KB rather than the whole entry. In real files the strings they look for sit near
the start, because ODF writes `office:version` on the root element and
`[Content_Types].xml` is a flat list of about 150 bytes per part. A miss falls back
to the generic ZIP or OOXML match, never to a wrong format. DROID's own shipped
profiles clip unbounded searches to the same 64 KB (`maxBytesToScan`, see
docs/adr/0022).

## Trusting `ZipArchiveEntry.Length`

The buffer is sized `min(entry.Length, 64 KB)`, so small entries don't pay for the
whole prefix. `Length` comes from the archive's central directory, so the file's
author controls it. The read still stops at the buffer's end, so an inflated claim
can't make MimeSpy read more. A claim that's too small makes the read stop early.
.NET 10 does that anyway: it passes the declared size to `DeflateStream` and stops
there (checked by experiment, and in `ZipArchiveEntry`'s source). I found no
legitimate writer that declares sizes larger than the real ones. The buggy ones on
record put zeros in the local header (which .NET ignores) or declare too little.

## Keeping it true

`ContainerSignatureDataTests` fails when a PRONOM update adds a ZIP container
signature anchored to an entry's end, or one whose bounded reach goes past
`EntryPrefixLength`. Either one means revisiting this decision, not just raising the
constant.

OLE2 streams are unaffected. They aren't compressed, and their reads are already
capped at the length of the file holding them. The four end-anchored container
signatures are all OLE2 (Revit 43040/43050, GST Publisher 52000, Minitab 54020), so
an OLE2 stream does need its end.
