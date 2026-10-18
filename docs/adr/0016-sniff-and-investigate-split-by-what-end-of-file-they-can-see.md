# Sniff and Investigate are split by whether they can see the end of the file

PRONOM signatures aren't uniformly BOF-anchored the way the old CSV table's
were: a `ByteSequence` can be anchored to either end of the file, and PRONOM's
container-signature table (ZIP/OLE2 sub-format disambiguation - docx vs xlsx
vs plain zip, doc vs xls, ...) works by opening the container and reading a
named entry's content, which for ZIP means reading the central directory at
the end of the file first. Neither of those can be evaluated against a
leading-bytes-only buffer without risking a false match (the bytes past what
was supplied might not be what the signature expects) or an outright crash
(there's no "end of file" to seek to in a partial buffer).

So the new engine exposes two entry points instead of one, split on exactly
that line:

- `Sniff` only evaluates BOF-anchored signatures, against however many leading
  bytes the caller supplies (or `SniffReadSize` (64 KiB) when reading a
  `Stream`) - it never runs the container-signature engine at all, EOF- or
  container-disambiguation-dependent alternatives just don't match. Safe
  against a truncated or streamed-in-pieces buffer, at the cost of a less
  specific (or absent) answer for some formats.
- `Investigate` requires the complete file and evaluates everything: EOF-anchored
  signatures, and, for ZIP/OLE2-triggering matches, the container signatures
  that resolve docx/xlsx/pptx/doc/xls/jar/odt-and-siblings to their specific
  PUID instead of the generic container one.

Unlike the old Gary-Kessler-table engine's docs/adr/0002-style guarantee (532
bytes reaches every signature in that table, because every one of those was
BOF-anchored at a small fixed offset), there's no such number for `Sniff`:
PRONOM's own data means a real, non-empty ZIP or OLE2 file's specific format -
and a surprising number of everyday formats besides, including PNG, JPEG,
GIF, PDF, and even generic ZIP itself - has no BOF-only signature at all in
this version of the DROID table; every alternative PRONOM defines for them
also requires the EOF check. `Sniff` correctly reports no match for these
rather than guessing, which is a real capability gap against that old engine
(which had a plain "PK\x03\x04" BOF rule for ZIP, and equivalent BOF-only
rules for PNG/JPEG/GIF, since that table never modeled EOF anchoring at all) -
callers who need those specifically should prefer `Investigate` and accept
needing the whole file.

Where a format's *specific* sub-type needs the container engine but its
*generic* family has its own independent BOF-only signature (Microsoft Office
Open XML, PUID `fmt/189`, covers this for pptx/xlsx but not docx in the
current data), `Sniff` still reports that generic PUID - it just can't narrow
further the way `Investigate` can.
