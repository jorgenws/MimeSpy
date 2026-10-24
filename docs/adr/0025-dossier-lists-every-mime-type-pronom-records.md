# `Dossier` lists every MIME type PRONOM records, not one joined string

PRONOM stores a format's MIME types in a single `MIMEType` attribute. For 38 formats it
holds several, comma-separated: MP4 is `"application/mp4, video/mp4"`, ICO
`"image/vnd.microsoft.icon, image/x-icon"`, RTF `"application/rtf, text/rtf"`, XML,
DNG, DjVu, 3GPP, Ogg Opus and others. `Dossier.MimeType` passed that string through, so
a caller comparing it to `"video/mp4"` got a mismatch, and a `MimeSpyOptions` filter of
`"video/"` excluded MP4 because only the start of the whole string was tested.

**Decision:** split the attribute at parse time. `Dossier.MimeTypes` lists every type in
PRONOM's order, the way `Extensions` lists every extension, and `MimeType` is a computed
shortcut to the first one (null when there are none). `MimeSpyOptions` matches a format
if any of its types matches a pattern.

`MimeType` stays because most callers want one string and most formats have one type.
It is documented as "the first", not "the preferred": PRONOM lists joined types in
alphabetical order, so the first carries no preference - MP4's is `application/mp4`,
not the `video/mp4` a browser expects. Choosing a preferred type would be our call
rather than PRONOM's, and isn't made here.

`MimeTypes` replaces `MimeType` as the positional parameter, a breaking change to
constructing a `Dossier`; it ships in the same major version as docs/adr/0023/0024.
