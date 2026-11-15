# Unbounded searches stop at 64 KiB, and `Investigate(Stream)` windows shrink to 2 MiB

docs/adr/0022 gave `Investigate(Stream)` an 8 MiB window at each end of a large file.
Every bounded signature fitted in that with plenty of headroom. The size really mattered
only for the 198 BOF sub-sequences with no `SubSeqMaxOffset`, which PRONOM means as
"anywhere": inside the window, they searched the first 8 MiB. On an in-memory buffer,
they searched the whole buffer. After docs/adr/0030, these "anywhere" searches were most
of what a large file still cost. A file that isn't DXF, BibTeX, email, WARC and so on is
searched to its end for each of those signatures, about 20 times for DXF alone.

**Decision:** a sub-sequence with no `SubSeqMaxOffset` is only searched for within
64 KiB of its end of the file (`PronomSignatureMatcher.MaxBytesToScan`), in every
method. Bounded sub-sequences, including fragment gaps, are matched exactly as PRONOM
gives them. The `Investigate(Stream)` window shrinks from 8 MiB to 2 MiB per end, the
furthest any signature can now reach.

## Why 64 KiB

It's DROID's own default. DROID clips an unbounded search to `maxBytesToScan` bytes
from the relevant end, and its shipped GUI and CLI profiles set that to 65536
(docs/adr/0022). Container entries are already read only to 64 KiB
(docs/adr/0028), and `Spy(Stream)` already reads only the first 64 KiB of a stream it
doesn't fully read. In real files, the markers these signatures look for sit near the
start: DXF opens with its `SECTION`/`$ACADVER` header, an email with its headers, a WARC
file with its record header. PRONOM leaves them unbounded because the formats don't
forbid content before them, not because they're usually far in.

PRONOM's EOF sequences have no unbounded sub-sequences, so at the end of the file the
cap changes nothing today. It applies there too, so that a future PRONOM update gets
the same treatment at both ends.

## What it changed

The equivalence check over the fixtures (and their prefixes, shifted copies, byte flips
and random buffers) found one change. `Spy` on `sample.ppt` (608 KB) now reports
generic OLE2 (`fmt/111`), not PowerPoint 97-2003 (`fmt/126`). That signature looks
anywhere in the file for the UTF-16 name of PowerPoint's directory entry, which in this
file sits past 64 KiB. `Investigate` still reports PowerPoint, because it opens the
container. `Spy` already documents that OLE2 and ZIP files get a less specific answer
from it, and `sample.xls` already got generic OLE2 from `Spy` before this change.

## Why 2 MiB windows

With the cap, the longest reach of any byte sequence from its end of the file is
1,101,151 bytes. That's Nikon NEF raw (`fmt/202`), whose signatures chain IFD entries
back to their MakerNote with PRONOM's 999,999 "effectively anywhere" fragment gap.
docs/adr/0022's table, which gave 152 KB as the maximum, missed those fragment gaps.
Everything else reaches less than 153 KB. A 2 MiB window holds all of it, so
`Investigate(Stream)` gives the same answer as reading the file whole. A file of up to
4 MiB is read whole. A larger one now costs about 4 MiB of buffers, not 16.

A 256 KiB window would have covered everything except NEF. Then a large NEF whose
MakerNote IFD sits past 256 KiB would have been reported as plain TIFF. Without a real
NEF to check, the simpler rule won: the window holds whatever any signature can reach.
`SignatureReachTests` checks that it still does, so a PRONOM update that reaches
further fails a test instead of quietly cutting a signature short.

## `Spy(Stream)` upgrades up to 4 MiB

`SpyFullReadThreshold` is the size up to which `Spy(Stream)` gives a seekable stream
`Investigate`'s answer instead of peeking at its first 64 KiB (docs/adr/0019). It was
8 MiB, chosen so that `Spy` would never quietly read a huge file into memory, back when
`Investigate(Stream)` read the whole stream. That no longer happens at any size, so
the threshold now follows the windows instead: `Spy` upgrades exactly when
`Investigate(Stream)` would read the file whole, 2 × 2 MiB. Past that, `Spy` stays a
64 KiB peek, as its name promises.
