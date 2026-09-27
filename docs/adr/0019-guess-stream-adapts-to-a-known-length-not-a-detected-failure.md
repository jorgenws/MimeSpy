# Guess(Stream) adapts to a known length, not a detected failure

`Analyze` assumes the bytes it's given are the complete file - it has no way
to confirm that, and doesn't try to. A truncated buffer isn't rejected: its
own end just gets treated as if it were the file's real end. For an
EOF-anchored signature this is usually harmless (the real trailer is missing,
so the check correctly fails to find it - the same "fewer bytes, less
specific, never wrong" behavior `Sniff` relies on), but it isn't a guarantee
the way `Sniff`'s own contract is - a corrupted or adversarially-truncated
file could, in principle, have bytes near the cutoff that happen to satisfy an
EOF check meant for the real end. `Analyze` was never designed to guard
against that, because the whole point of the method is "you're handing me the
complete file"; enforcing that would need the caller to prove something only
they can know in the first place.

That raised an obvious question once `Guess` existed alongside it: could
`Guess`, given a small enough input, just *be* `Analyze` - use the real EOF
check and the container engine instead of skipping them - and fall back to
today's bounded, EOF-skipping peek otherwise? The tempting implementation is
"try `Analyze`, and if it fails, return `Guess`'s answer instead." That
doesn't actually work: there's no reliable signal to catch. The container
engine mostly self-protects already (`ZipArchive` throws on a structurally
broken zip, which `PronomZipContainerReader.TryOpen` already catches and
falls back from), but the core EOF-anchored matching has no analogous
"this looks wrong" signal to detect - per the paragraph above, a truncated
file's matching either quietly succeeds (safe) or quietly fails to notice
what's missing (also safe, just less specific) or, rarely, quietly gives a
false positive (not safe, and not detectable after the fact by definition).
Post-hoc failure detection was the wrong tool for a problem that's really
about information available *before* reading a single byte.

`Stream.Length` is that information, when the stream is seekable: a fact,
not an inference. So `Guess(Stream)`/`GuessAsync` check it first - if the
remaining length is at or under `GuessFullReadThreshold` (8 MiB, chosen the
same way `SniffReadSize` was: comfortably past everyday documents/images/audio,
far short of "unboundedly large" so a caller expecting a cheap peek is never
surprised by `Guess` quietly buffering a multi-gigabyte file) - they read the
whole stream and delegate straight to `Analyze`/`AnalyzeAsyncCore`, getting
its fully-confirmed answer for free. A non-seekable stream, or one reporting a
larger remaining length, falls back to the existing bounded-prefix,
EOF-skipping peek.

`Guess(ReadOnlySpan<byte>)` gets none of this adaptivity, deliberately. A span
carries no "how much of the real file is this" metadata the way a stream's
`Length` does, so there's nothing reliable to gate on - and a caller who
constructed that span already knows whether it's the whole file (they read
it) or not, so if they know it's complete, they can just call
`Analyze(ReadOnlySpan<byte>)` themselves instead of asking this method to
re-derive information they already have.
