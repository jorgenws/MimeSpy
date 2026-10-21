# `Sniff` is removed; `Spy` and `Investigate` are the only detection methods

`Sniff` (docs/adr/0016) only ever evaluated signatures anchored to the start
of the file, rejecting any signature with an end-of-file-anchored sequence
outright rather than guessing at it - which gave it a real guarantee `Spy`
doesn't have: anything it reported was certain to also be reported by
`Investigate` against the complete file.

In practice, that guarantee bought very little. A "surprising number of
everyday formats" (docs/adr/0016's own phrase) - PNG, JPEG, GIF, and plain
ZIP among them - need an EOF-anchored check in every one of their PRONOM
signatures, which means `Sniff` could never report them from a partial
buffer at all, no matter how perfect the header looked. A caller reaching
for "detect this file's type from what I've read so far" and getting
nothing back for the four most common binary formats imaginable is a worse
default experience than the certainty is worth. `Spy`'s header-only guess
already covers that case - it's wrong only when the file turns out
truncated or corrupted past the header, which `Sniff`'s narrower niche
(streamed-in-pieces reads that need certainty, not just a best guess) was
the only thing protecting against, and `Spy(Stream)`'s existing
length-adaptive promotion to a full `Investigate` (docs/adr/0019) already
removes most of the realistic need for that certainty on the one input type
(`Stream`) where MimeSpy can tell up front whether it's safe to get it for
free.

**Decision:** remove `Sniff`/`SniffAsync` entirely. `Spy` and `Investigate`
are the two methods people actually reach for - `Spy` for a quick guess,
`Investigate` for a confirmed answer - and `Spy`'s own doc comment now
explains the EOF-skipping contract directly, rather than by contrast with a
third method.

## What changed

- `IMimeSpy.Sniff(ReadOnlySpan<byte>)`, `Sniff(Stream)`, and `SniffAsync` are
  gone, along with their implementations in `MimeSpy.Pronom.cs`.
- `PronomSignatureMatcher`'s `EofPolicy.RejectIfPresent` case and
  `FindMatchesForward` are gone - `EofPolicy` is now just `Ignore` (`Spy`) and
  `Require` (`Investigate`).
- `MimeSpy.Tests/MimeSpyTests.cs`: the three `Sniff_*` facts (each paired with
  an equivalent `Spy_*` fact proving the opposite outcome on the same input)
  are gone, and `Sniff`/`SniffAsync` entries were dropped from the
  `SpanMethods`/`StreamMethods`/`AsyncStreamMethods` theory data - every test
  parameterized over those now only runs against `Spy`/`Investigate`.
- README's three-method framing is now two methods.

## docs/adr/0016 and 0017 are left as historical record

Both describe `Sniff`'s own contract and the reasoning for the original
three-way split in detail. Per the precedent docs/adr/0020 set for exactly
this situation (an ADR describing *current* architecture is updated in
place; one describing something since removed is left alone as history) -
now that `Sniff` is gone, those two have moved from the first category to
the second, so they're left untouched rather than rewritten.
