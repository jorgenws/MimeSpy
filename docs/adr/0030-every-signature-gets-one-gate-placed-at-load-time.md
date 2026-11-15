# Every signature gets one gate, placed at load time

`PronomSignatureMatcher` used to try two sets of signatures on a buffer: those indexed
under the buffer's first byte, and every signature that can't be indexed that way,
because its first BOF sub-sequence isn't a literal fixed at offset 0. That second set
held 765 of PRONOM's 2,273 signatures, and it was tried on every file whatever the
file was. A 572-byte JPEG took about 32 µs, almost all of it spent rejecting
signatures that couldn't match.

**Decision:** at load time, `PronomCandidateIndex.Place` gives every signature exactly
one *gate*: the cheapest check that can rule it out without trying it.
`FindCandidates` runs each kind of check, and only the signatures none of them ruled
out go to the matcher, which is unchanged.

| Gate kind | Signatures | Ruled out when | Checked by |
|---|---|---|---|
| `FirstByte` | 1,508 | the buffer's first byte isn't its required one | a lookup on `head[0]` |
| `NearLiteral` | 658 | its gate literal isn't in its window, which ends within 4 KB | the trie scan |
| `FarLiteral` | 64 | its gate literal isn't in its window, which reaches past 4 KB | the trie scan, only when the whole head fits in it |
| `Always` | 43 | never | — |

`FirstByte` wins whenever it applies, since the lookup costs nothing. Otherwise
`PronomLiteralGate` picks a literal gate, and the gate's reach decides near or far.
Candidates are one bitset over all signatures, tried in signature-file order.

## Gates must be conservative

A gate may let a signature through that then fails to match, but it must never rule
out one that would have matched. Results are therefore exactly what trying every
signature gives. Everything in `PronomLiteralGate` is a way of keeping that true while
making gates cheaper:

- Everything in a signature is an AND: its BOF sequences, their sub-sequences'
  anchors, and one alternative of each fragment group. So any one of them is a valid
  gate. The gate takes the most selective one. A fragment group gives a gate with
  several alternatives, and any one of them opens it.
- Literals longer than 8 bytes are cut down to their most varied 8-byte window. A
  file holding the whole literal holds every piece of it. Literals shorter than 3
  bytes, or that are one byte repeated, aren't used: they occur all over ordinary
  content.
- Windows follow `MatchesForward`: an anchor starts at least `SubSeqMinOffset` in.
  Only the first unit has an upper bound, `SubSeqMaxOffset` plus its left-fragment
  chain's reach. Fragment windows follow from their anchor's window.
- Gates come only from BOF sequences. `Spy` skips EOF sequences (docs/adr/0017), so
  a gate taken from one could rule out a signature that `Spy` would have matched.

`PronomCandidateIndexTests` checks that every signature that matches a test fixture
is a candidate. The equivalence check run for this change compared 45,849 results
(fixtures, prefixes of them, shifted copies, byte flips, random buffers, and filtered
instances) against the previous build. Every set of results was identical, and
unfiltered results came out in the same order.

## Ordering

A filtered `MimeSpy` used to list equal-priority matches in the order of a `HashSet`
it was built from, which didn't match the unfiltered order (TIFF's partial matches,
for one). Trying every candidate in signature-file order makes filtered and
unfiltered agree.

## The scan stops at 4 KB

The scan is scalar, about 3 ns a byte, limited by the latency of one table lookup per
byte. The signatures that search far into a file use the matcher's vectorized
`IndexOf`, which is much faster per byte. Past `ScanLimit` (4 KB), the scan costs more
than it saves, so a `FarLiteral` gate is checked only when the whole head fits in the
scan, and is otherwise treated as passed. 4 KB was the fastest of the limits tried
(1, 4, 16 and 64 KB).

On long files those 64 signatures were most of the remaining cost, because their
searches ran to the end of the file. docs/adr/0031 caps such searches at 64 KiB, which
bounds what they can cost however large the file is.

## Filters

A `MimeSpyOptions` filter gets a `Scope`: a mask over the signatures it allows, and
how far to scan for their near gates. When the filter allows fewer than 100
literal-gated signatures (81 for `image/`), trying them directly is cheaper than
scanning, so the scope doesn't scan and lets them all through.

## Why not one mechanism for everything

Putting every signature in the trie was tried first (kept as
`.scratch/aho-corasick/all-trie.patch`). It was about 2 µs faster on tiny files and
removed the bucket, but most of its extra complexity existed only to copy what the
first-byte lookup does for free: a second start state, so that 1–2 byte literals at
offset 0 match only there and not all over the buffer; and a 4 KB scan replacing a
one-byte lookup, which made filtered instances slower. Each gate kind now does the
job it's good at.

## Storage

The automaton is a plain sparse trie with failure links (2,607 states), with a full
256-entry row for the root only. Giving the busiest states full rows as well, with
failure links already followed, saved 0.3–4 µs per file - not worth the code. A fully
dense DFA, tried for the all-signature variant, scanned no faster either: the cost is
each lookup's latency, not the sparse search.

## Costs

The first call in a fresh process takes 10–25 ms longer (about 125 → 140 ms across
runs), to place gates and build the automaton.
