# DROID's priority relations drop outranked matches

The DROID signature file records 1,231 `HasPriorityOverFileFormatID` relations across
673 formats. Each says that when format A and format B both match a file, A is the
answer: the more specific format over the generic one it also satisfies (WAV over
RIFF, JFIF over Raw JPEG Stream, Word 97-2003 over Word (Generic), ODF 1.2 over 1.1,
RTF 1.8 over 1.0-1.7). MimeSpy read none of them, so all of these came back as ties.

**Decision:** apply them the way DROID does (`removeLowerPriorityHits`): collect every
format ID any matched format outranks, then drop the matches with those IDs. This
happens once on the core signature matches and again on the container-signature
results, since DROID applies the same table to both. It is one pass, not a transitive
closure. The relations form an acyclic graph (checked against this version of the
file), so a non-empty result can never be emptied.

This doesn't contradict docs/adr/0001. That ADR rejects choosing between ties by
something arbitrary such as declaration order. The priority relations are PRONOM's own
statement of which answer is more specific, recorded in the same data the signatures
come from. Ties PRONOM doesn't resolve are still returned as ties.

## `Spy` only lets a fully-checked match outrank another

`Spy` skips EOF-anchored sequences (docs/adr/0017), so a format can match there on
part of its signature. Its priority is only valid if its own evidence was actually
checked. For example, several DNG/TIFF variants outrank plain TIFF, but what tells
them apart is the part `Spy` skips. Applied without a guard, `Spy` reported a plain
ImageMagick TIFF as a set of DNG/GeoTIFF variants with plain TIFF (`fmt/353`)
dropped. So under `Spy` a match may only outrank others if at least one of its
matching signatures has no EOF sequence, which means `Spy` evaluated it in full.
`Investigate` checks every sequence, so every match may outrank.

The guard costs a little: `Spy` leaves JFIF/Raw JPEG and JAR/ZIP tied, because the
outranking signature in each pair has an EOF sequence. `Investigate` resolves both.

## Effect on the integration fixtures

| Fixture | Before | After |
|---|---|---|
| avi, wav, webp | RIFF + the specific format (+ Waveform Audio for wav) | the specific format |
| jpg (`Investigate`) | Raw JPEG Stream + JFIF | JFIF |
| m4a | MPEG-4 Media + M4A Audio | M4A Audio |
| odt, ods, odp | ODF 1.1 + 1.2 | ODF 1.2 |
| ogg, ogv | Ogg container + Vorbis/Theora | Vorbis / Theora |
| rtf | RTF 1.0-1.4, 1.5-1.6, 1.7, 1.8 | RTF 1.8 |
| doc (`Investigate`) | Word 97-2003 + Word (Generic) | Word 97-2003 |
| doc, ppt (`Spy`) | OLE2 + Word/PowerPoint 97-2003 | Word/PowerPoint 97-2003 |
| odt/ods/odp, pptx, xlsx (`Spy`) | ZIP + ODF 1.1/1.2 or Office Open XML | ODF 1.2 or Office Open XML |

No fixture lost its correct format.
