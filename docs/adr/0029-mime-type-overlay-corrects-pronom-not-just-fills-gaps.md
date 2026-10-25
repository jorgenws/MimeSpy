# The MIME type overlay corrects PRONOM's types, not just fills gaps

docs/adr/0026 let `mimespy-mime-types.xml` fill in MIME types only where PRONOM gives
none, and never override one PRONOM does give. Gaps aren't PRONOM's only problem,
though. Where a format has several types, PRONOM lists them alphabetically (all 35 such
formats in the current file). `Dossier.MimeType` is the first of them, so an MP4 file
came back as `application/mp4` rather than `video/mp4`. PRONOM's data is large and
maintained by hand, so more errors of other kinds are to be expected.

PRONOM's files stay unmodified copies (docs/adr/0006), so the fix belongs in the
overlay.

**Decision:** the overlay becomes a list of corrections, keyed by PUID, applied once
PRONOM's file has loaded. `PronomMimeTypeCorrections` does the applying. Each entry
states two things:

- `PronomMimeTypes`: the value PRONOM gave when the entry was written. Empty means
  PRONOM gives none.
- `MimeTypes`: the value MimeSpy uses instead.

A correction may rewrite the value in any way: fill it, reorder it, replace it, add a
type, or remove all of them. There's no rule about which kinds of correction are
allowed. PRONOM catalogues thousands of formats, and the kinds of mistakes in it can't
be listed in advance. Instead, every entry is checked in two ways:

- **It must match PRONOM exactly.** If PRONOM's value for the PUID isn't exactly
  `PronomMimeTypes` (same types, same order), loading throws and names the entry. A
  PRONOM update that touches a corrected format can't be silently overwritten by a
  correction written for older data: someone has to look. 0026's check, that PRONOM
  "now gives this format a type of its own", is now one case of this rule.
- **An override must say why.** An entry that changes a value PRONOM does give needs a
  `Reason`. Filling a gap needs none beyond PRONOM having nothing.

0026's guidance on which types to choose still holds: gap fills use Apache `mime.types`
per format, there's no blanket extension lookup, and generic OOXML (`fmt/189`) and OLE2
(`fmt/111`) stay untyped.

The first two corrections that aren't gap fills reorder PRONOM's own types:

- MP4 (`fmt/199`): `video/mp4` first.
- 3GPP (`fmt/357`): `video/3gpp` first.

WAV is left as `audio/x-wav`, the only type PRONOM gives it. Changing that would be a
judgement about which spelling callers want, not a mistake in PRONOM's data, and nobody
has asked for it.

As before, a caller can't tell a corrected type from one PRONOM gave. Every correction
is listed in one short file, with its reason.
