# Each `Dossier` says whether it's a full or partial match, and results are ordered

`Spy` skips EOF-anchored sequences (docs/adr/0017), so some of its matches rest on only
part of the format's signature. Until now a caller couldn't tell which. On a plain TIFF,
`Spy` returned 14 formats - plain TIFF plus DNG, GeoTIFF, TIFF/EP and Exif variants - as
an unordered tie, although only plain TIFF had been checked in full: its signature is
header-only, while the others' distinguishing checks are the EOF part `Spy` skipped.

Dropping the partial matches was considered and rejected. "Checked in full" says how
much a signature asks for, not how much evidence there is: TIFF's full check and DNG's
partial check read the same four header bytes. On a real DNG file, dropping partials
would leave only TIFF and hide the format the file actually is.

**Decision:** report it and order by it, but drop nothing extra.

- `Dossier.Match` is `Match.Full` when every part of the signature was checked, and
  `Match.Partial` when `Spy` skipped part of it. A format with several alternative
  signatures is full if any matching one was checked in full. `Investigate`, container
  results, and `Spy(Stream)` when it reads the whole stream (docs/adr/0019) only ever
  return full matches.
- Results are ordered: full matches before partial ones; within each group, a format
  before any format in the same group it has priority over (docs/adr/0023), otherwise
  in the order found. For the TIFF that gives plain TIFF, then DNG/GeoTIFF, then
  TIFF/EP, then Exif; for a `Spy` of a JPEG, JFIF then Raw JPEG Stream. The first
  result is therefore a sensible single answer, without hiding the rest.

docs/adr/0023's guard on which matches may drop another by priority rests on the same
distinction; this ADR makes it visible to callers.

## Naming

The enum is `Match` with `Full` and `Partial`, chosen to describe what was checked
rather than to judge the answer - "confirmed" overclaims, since `Investigate` trusts
the bytes are the whole file. The cost: a consumer file importing both
`System.Text.RegularExpressions` and `MimeSpy` has to qualify one `Match`.

`Dossier`'s new parameter defaults to `Match.Full`, so code that builds a `Dossier`
(as docs/adr/0015 encourages for tests) still compiles. It is still a binary breaking
change, and the priority change in docs/adr/0023 alters results anyway, so it ships in
a new major version.
