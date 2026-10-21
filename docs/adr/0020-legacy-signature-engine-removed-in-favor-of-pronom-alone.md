# The legacy Gary-Kessler/mime.types engine is removed; PRONOM is now the only engine

Before this change, MimeSpy carried two parallel detection engines: the
original one (`Spy`/`SpyAsync`, returning `Result`), built on Gary Kessler's
file-signature table and Apache's `mime.types`, with a handful of hand-rolled
`IContainerSniffer` implementations (`ZipContainerSniffer`,
`Ole2ContainerSniffer`, `OggContainerSniffer`, `AsfContainerSniffer`) doing
format-specific content sniffing; and the PRONOM-based one introduced by
docs/adr/0016-0019 (`Sniff`/`Guess`/`Analyze`, returning `PronomFormat`),
built on PRONOM's own DROID and container-signature tables. The two existed
side by side while the PRONOM engine was being built out, but kept the
heuristic old engine around past the point it was pulling its weight - it was
more ad hoc (hardcoded per-format sniffing logic, four separate heuristics
for ZIP/OLE2/Ogg/ASF disambiguation, no principled EOF-anchoring model at
all) than the data-driven PRONOM engine, which resolves the same
disambiguation from PRONOM's own signature data instead of bespoke C# per
format.

## Coverage lost by removing the old engine

Before removing it, every extension the old `file_signatures.csv`/
supplemental table covered (446 distinct extensions) was checked against
every `<Extension>` PRONOM's DROID table (`pronom-droid-signatures.xml`)
declares anywhere. 177 have no PRONOM signature at all. All mainstream
formats are unaffected - docx, pdf, zip, jpg/png/gif, dmg, pst, heic, avif,
rtf, elf, tar/gz/7z/rar/iso and the like are all still covered - the gap is a
long tail of legacy/niche formats PRONOM (an archival-preservation registry)
never cataloged: embroidery file formats, old DOS driver/installer formats,
forensic-tool artifacts, and a smaller cluster of moderately common ones
(`msi`, `vhd`, `vmdk`, `torrent`, `xpi`/`crx`, `reg`, `evt`/`evtx`, `fon`,
`gho`/`ghs`, `tar.z`/`tbz2`).

**Decision: accept the loss.** PRONOM's ~2500-format catalog is broader
overall, and any of these 177 can be added back as a PRONOM-style signature
addition later if a real caller needs one specifically - there's no
supplemental-file mechanism to preserve here (docs/adr/0010's mechanism was
specific to the old CSV format, which no longer exists).

## `Guess`/`Analyze` renamed to `Spy`/`Investigate`

With the old `Spy`/`SpyAsync` gone, the `Spy` name was free again. Renaming
`Guess`→`Spy` and `Analyze`→`Investigate` gives the library's three detection
tiers names that read naturally against the `MimeSpy` type itself (a `MimeSpy`
spies, then investigates) instead of the arbitrary `Sniff`/`Guess`/`Analyze`
names chosen while they coexisted with the original `Spy`. `Sniff` keeps its
name - there's no collision, and "sniff" already reads correctly as the
leading-bytes-only tier. docs/adr/0016-0019 (and their filenames) were
updated in place to use the new names throughout, since they document the
current architecture, not a removed one - unlike this ADR and docs/adr/0001-
0015, which are a historical record and were left alone.

## New test coverage

The old engine's test suites (`MimeSpy.Tests/MimeSpyTests.cs`,
`MimeSpy.IntegrationTests/RealSampleFileTests.cs`) were the only tests this
library had - `Sniff`/`Spy`/`Investigate` had none before this change. Both
were rewritten against the renamed API rather than mechanically ported: the
old tests' hand-built byte sequences and expected ties were tuned to Gary
Kessler's table and the old heuristic sniffers, which don't correspond to how
PRONOM's signatures or container engine actually behave. The new tests'
expectations were captured by running the real PRONOM engine against the
existing real-file fixtures first, not assumed to match the old engine's
behavior - in a few cases (notably `epub`, and `doc`'s tie partner) the new
engine's real behavior differs from what the old engine documented.
