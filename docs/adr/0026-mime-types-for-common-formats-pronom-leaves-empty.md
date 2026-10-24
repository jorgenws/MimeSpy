# MIME types for common formats PRONOM leaves empty come from a small overlay file

1,790 of the 2,571 formats in PRONOM's DROID file have no MIME type, among them formats
people meet daily: 7z, Matroska, xz, AVIF, MPEG-1/2 video, MPEG transport streams, ISO
images. For a MIME-type library, `null` for a 7z file is a miss. The engine removed in
docs/adr/0020 always had an answer, because it took MIME types from Apache's
`mime.types` (docs/adr/0005).

**Decision:** `Resources/mimespy-mime-types.xml` lists MIME types for a hand-picked set of
common formats PRONOM leaves empty, and is applied once at load, right after the DROID
file is parsed. The DROID file itself stays an unmodified copy of PRONOM's, so a refresh
is still "drop in the new file" (docs/adr/0006).

- **Only gaps are filled.** An entry never overrides a type PRONOM gives. If an entry
  names an unknown PUID, or one PRONOM has since given a type of its own, loading
  throws - the first test run after a refresh says which entry to remove.
- **Hand-checked, not looked up by extension.** Each type is Apache `mime.types`'
  entry for the format's own extension, chosen per format. Mapping every untyped format
  through Apache's extension table was considered and rejected: many obscure PRONOM
  formats share an extension with a common one, and would be mislabeled - Image
  Cytometry Standard (`.ics`) as `text/calendar`, Variant Call Format (`.vcf`) as a
  vCard, an Electronic Arts music format (`.asf`) as Windows Media.
- **Common formats only.** Generic containers stay untyped where no single type is
  right: Office Open XML (`fmt/189`) and OLE2 (`fmt/111`), which `Spy` reports for
  xlsx/pptx/xls before `Investigate` opens them. ELF, Mach-O and RPM have no Apache
  entry and are left out rather than given an unofficial `x-` type of our own.

Applying it at load, rather than when each `Dossier` is built, keeps one source of
truth: `MimeSpyOptions` filters on MIME types when it's constructed, and sees the
filled-in values like everything else.

A caller can't tell an overlay type from a PRONOM one. That was judged not worth a flag:
the types are the conventional ones, and the file is short enough to read.
