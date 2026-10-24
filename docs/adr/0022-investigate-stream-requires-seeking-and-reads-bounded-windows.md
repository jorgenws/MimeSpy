# `Investigate(Stream)` requires a seekable stream and reads bounded windows from each end

`Investigate(Stream)`/`InvestigateAsync` used to copy the whole stream into a
`MemoryStream` and match against that. Peak memory was therefore about the size
of the file, and past roughly 2 GB it failed outright, because `MemoryStream` and
`byte[]` are `int`-indexed and throw "Stream was too long." So there was no way to
get `Investigate`'s EOF and container checks on a large file at all. A caller
could either fall back to `Spy`'s header-only peek or not ask.

Four decisions fix that.

**The stream has to be seekable.** `Investigate(Stream)` and `InvestigateAsync`
throw `ArgumentException` when `!stream.CanSeek`, before reading anything.
Seeking is what lets the method read only the ends of the file it needs, and lets
the container readers jump straight to a ZIP's central directory or an OLE2
sector. A non-seekable source has nothing to seek, so supporting it would mean
buffering it again, which is the old behaviour this replaces. Callers with a
network or pipe stream copy it into a `MemoryStream` or a temporary `FileStream`
(the latter avoids `MemoryStream`'s 2 GB ceiling too), or call
`Investigate(ReadOnlySpan<byte>)`. They know their memory and disk budget, and
the library doesn't. This narrows the public contract: a non-seekable stream that
used to work now throws. `Spy(Stream)` is unaffected, because it only hands a
stream to `Investigate` when the stream is already seekable (docs/adr/0019).

**The stream has to be at position 0.** The whole stream is the file, and a
seekable stream at any other position throws `ArgumentException`. This applies
to `Spy(Stream)`/`SpyAsync` as well as `Investigate`. A non-seekable stream
(which only `Spy` accepts) has no position to check and is read from wherever it
is. The old code treated
"from the current position to the end" as the file. Nobody chose that: it fell out
of copying the stream, and was never a requirement. Keeping it would cost real
code now. Both container formats address the file from its first byte (a ZIP's
central directory offset, OLE2 sector numbers), so the container readers would
need a wrapper that renumbers the stream so the current position reads as 0. That
was tried and dropped. It also only half-supported the case it was for: a file
embedded inside a larger stream also has to end where that stream ends. A
violation is rejected rather than only documented because it would otherwise give
a wrong answer without any error. The realistic way to hit it isn't a file that
starts mid-stream, since an embedded file comes with its own entry stream
(`TarEntry.DataStream`, `ZipArchiveEntry.Open()`). It's a caller who forgot to
rewind, such as a `MemoryStream` just written to and left at its end. The old
behaviour would have inspected zero bytes and reported no match.

For comparison, Mime-Detective (checked at commit `9c17f60`, August 2025) reads
from the current position with no check, and consumes the stream by default
(resetting the position is opt-in). It reads into a `byte[]` capped at 10 MB by
default, and its `BinaryReader` is created without `leaveOpen`, so it closes the
caller's stream either way. MimeSpy rejects a stream it can't read correctly,
and leaves an accepted one open.

**The stream is left wherever reading stopped.** MimeSpy doesn't rewind it
afterwards. Where the caller's stream ends up is the caller's concern, and
restoring it would mean a `try`/`finally` in every stream method for something
the caller can do in one line. Because of the position-0 check, forgetting to
rewind before reusing the stream with MimeSpy fails loudly instead of quietly
reading the wrong bytes.

**The core signature match reads a fixed 8 MiB window from each end.**
`PronomSignatureMatcher` evaluates BOF-anchored sequences against the head window
and EOF-anchored sequences against the tail window. Each side only ever works
with offsets from its own end of the file, so no other part of the matching code
changed. A file of 16 MiB or less (where the windows would overlap) is still read
whole, as before. ZIP/OLE2 refinement reads the caller's stream directly, never
the windows.

## Why 8 MiB, and what it gives up

The handover that proposed this assumed PRONOM signatures reach about 15 KB from
their end of the file. A probe over `pronom-droid-signatures.xml` showed that was
wrong. Measured from each anchored end, adding the max gap + fragment chain +
anchor of every subsequence:

| | Sequences | Unbounded | Max bounded reach |
|---|---|---|---|
| BOF-anchored | 2281 | 198 | 152,399 bytes (signature 2095) |
| EOF-anchored | 295 | 0 | 131,084 bytes (signature 157) |
| No reference ("Variable") | 98 | 98 | - |

Every bounded sequence fits in 8 MiB with roughly 50x headroom, so for those the
window changes nothing. The cost falls on the 285 signatures with an unbounded gap
somewhere in a BOF-side sequence. That's 212 formats where every signature has
one, including PNG, WAV, PDF/A, DOC, RTF, SVG and DXF. Before, on any file they
searched the whole thing. Now, on a file larger than 16 MiB, they search only the
first 8 MiB. A marker that only appears further into the file is missed, and the
answer falls back to whatever less specific match remains.

DROID itself makes the same trade, and more aggressively. Its byte reader can
seek anywhere, but `SubSequence` clips an unbounded search to
`profile.maxBytesToScan` bytes from the relevant end, and DROID's shipped GUI and
CLI profiles default that to 65536 (`default_droid.properties`). Only the bare
library/API default is unlimited. 8 MiB is 128 times DROID's default.

Unlike DROID's lazily-filled cache over full random access, this is a one-shot
fixed read. It's simpler, and a future PRONOM update whose bounded reach grew past
8 MiB would also be silently cut short rather than found slowly. When updating the
signature file, recompute the table above: for each `ByteSequence`, sum
`SubSeqMaxOffset` (missing means unbounded), the anchor length, and each fragment
group's `MaxOffset` plus its longest alternative, over all its `SubSequence`s.

## What this doesn't bound

Container refinement still loads each ZIP entry or OLE2 stream it checks
completely into memory, and checks run against named entries like
`[Content_Types].xml`, `mimetype` and `WordDocument`. Those are usually small, but
nothing guarantees it: a very large `WordDocument` stream is read whole. The OLE2
reader also only follows the FAT sectors listed in the header's own 109-entry DIFAT
array, as before. Only the reading is new, not the reach.

`InvestigateAsync` reads the windows asynchronously, but a file over 16 MiB that
turns out to be ZIP/OLE2 is then refined with synchronous reads on the live
stream. `ZipArchive` has no async open on netstandard2.0, and the OLE2 reader is
synchronous throughout.

This supersedes docs/adr/0018 for the `Stream` overloads. There is no single
buffer to thread through any more. `Investigate(ReadOnlySpan<byte>)` still makes
its one unavoidable copy and hands the container readers a no-copy `MemoryStream`
over it.
