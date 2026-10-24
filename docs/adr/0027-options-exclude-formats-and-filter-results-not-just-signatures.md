# `MimeSpyOptions` can exclude formats, and filters results, not just signatures

`MimeSpyOptions.MimeTypes` narrowed which signatures a filtered `MimeSpy` tries. That
left two gaps once callers wanted to shape the answer, not just speed it up: there was
no way to say "every image format except DNG", and narrowing signatures alone doesn't
keep a format out of the results. A signature PRONOM shares between an included and an
excluded format still reported both, and the container check returned whatever PUID its
own table resolved to, filtered or not.

**Decision:**

- `ExcludedMimeTypes` takes patterns like `MimeTypes` - a plain, case-insensitive
  starts-with, kept deliberately: any prefix works (`"image/j"`), at the cost that a full
  type also matches longer ones (`"font/woff"` matches `font/woff2`). It wins over
  `MimeTypes`. A format
  is left out if *any* of its types matches, so `"image/dng"` removes DNG, which lists
  both `image/dng` and `image/tiff`.
- The filter now also removes disallowed formats from results - as signatures match, and
  from container results - before priority (docs/adr/0023) is applied. A left-out format
  is therefore never returned and can't outrank an allowed one; on a real DNG file,
  excluding `image/dng` leaves TIFF rather than nothing.
- The container-trigger formats PRONOM's container file names (generic ZIP, Office Open
  XML and OLE2) are still matched by `Investigate` when the filter leaves them out,
  because opening the container is the only way to reach docx, xlsx, epub, doc, xls and
  the like. They're kept only for that check: they outrank nothing and are removed before
  results are returned. Before this, `MimeTypes = ["application/vnd.openxmlformats"]` could
  never find a docx, and `ExcludedMimeTypes = ["application/zip"]` would have lost it too.
  `Spy` never opens containers, so it doesn't keep them.

Excluding a specific container format falls back to the generic one: excluding the
docx type reports a docx as ZIP, which is what such a caller asked for.
