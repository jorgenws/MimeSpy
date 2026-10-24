# Spy skips EOF confirmation entirely, rather than precomputing a uniqueness flag

> Still in force for `Spy`. `Sniff`, which this contrasts with, was removed in [ADR 0021](0021-sniff-removed-spy-and-investigate-are-the-only-methods.md). The internal method is now `FindHeaderMatches`.

docs/adr/0016 established that `Sniff` rejects any signature carrying an
end-of-file-anchored `ByteSequence` outright, since a leading-bytes buffer
can't safely evaluate one. That's a real cost: checking the DROID table
directly, 207 of PRONOM's ~2485 formats have *every* alternative signature
requiring an EOF check, including PNG, JPEG, GIF, and even a plain (non-empty)
ZIP - `Sniff` can never report any of them, no matter how many bytes it's given.

Of those 207, 136 have a BOF anchor that's globally unique across the whole
DROID table - nothing else PRONOM catalogs starts the same way, so matching
just the header is a reasonable bet (it's exactly what Gary Kessler's table,
and every other "quick sniff" tool, has always done). The other 71 collide
with another cataloged format's BOF - TIFF and its Digital Negative (DNG)
variant share `II*\0`/`MM\0*`; GIF shares `GIF89a` with an unrelated
"Persuasion Player" format; several PDF/X archival profiles share a plain
PDF's version-stamped header - and for those, the header genuinely can't tell
you which one it is; only the EOF check (or being fed the complete file) can.

The first instinct was a precomputed per-signature flag - "is this BOF unique
enough to trust alone" - set once at index-build time and exposed on the
match result. That turned out to be more machinery than the problem needs.
`FindMatchesGuess` instead just changes how EOF sequences are treated during
the exact same walk `FindMatchesForward`/`FindMatchesFull` already do:
skipped, rather than rejecting the signature (`Sniff`) or requiring them
against the true end of file (`Investigate`). That one change reproduces both
outcomes for free:

- A format with a globally-unique BOF: skipping its EOF check leaves only the
  BOF match, so it's reported alone - same effect the uniqueness flag would
  have had, no precomputation needed.
- A format whose BOF collides with another: skipping EOF for *both* sides
  means *both* match, surfacing the real ambiguity as a tie rather than
  guessing one over the other - consistent with docs/adr/0001's existing
  position that ambiguous matches are returned as ties, not silently resolved.

The tradeoff this buys: `Spy`'s results are no longer guaranteed to be a
subset of what `Investigate` would report for the same file's complete bytes. A
GIF truncated before its trailer byte, or a DNG file, would both still report
as a match under `Spy` even though one is genuinely broken and the other
needs its EOF check to know it's DNG rather than plain TIFF. Callers who need
the stronger guarantee - "if `Sniff` matched, `Investigate` will agree" - keep
using `Sniff`; callers who want the best answer obtainable from a partial
read, and can tolerate PRONOM's own signature not having fully confirmed it,
use `Spy`.
