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

> docs/adr/0024 exposes this distinction to callers as `Dossier.Match` (`Full`/`Partial`) and uses the same priority data to order results.

`Spy` skips EOF-anchored sequences (docs/adr/0017), so a format can match there on
part of its signature. Its priority is only valid if its own evidence was actually
checked. For example, several DNG/TIFF variants outrank plain TIFF, but what tells
them apart is the part `Spy` skips. Applied without a guard, `Spy` reported a plain
ImageMagick TIFF as a set of DNG/GeoTIFF variants with plain TIFF (`fmt/353`)
dropped. So under `Spy` a match may only outrank others if at least one of its
matching signatures has no EOF sequence, which means `Spy` evaluated it in full.
`Investigate` checks every sequence, so every match may outrank.

That alone was too strict. It left a JPEG tied between JFIF and Raw JPEG Stream under
`Spy`, although what tells them apart - the `JFIF` marker - is in the header, which
`Spy` checks. The part both skipped is the same check, `FF D9` within the last 64 KiB:
if the file has it both match, if not neither does, so skipping it can't change which
one is right. So a partial match may also outrank another partial match when the two
skipped exactly the same EOF check (structurally identical EOF sequences, not merely
similar ones). 29 of the 1,231 relations have such a pair, including JFIF over Raw JPEG
and the PNG and DXF versions over their generic forms.

What's left: `Spy` still leaves JAR/ZIP tied, since their EOF checks differ, and
DNG/GeoTIFF/Exif stay tied among themselves for the same reason. `Investigate`
resolves both.

## Effect on the integration fixtures

| Fixture | Before | After |
|---|---|---|
| avi, wav, webp | RIFF + the specific format (+ Waveform Audio for wav) | the specific format |
| jpg | Raw JPEG Stream + JFIF | JFIF |
| m4a | MPEG-4 Media + M4A Audio | M4A Audio |
| odt, ods, odp | ODF 1.1 + 1.2 | ODF 1.2 |
| ogg, ogv | Ogg container + Vorbis/Theora | Vorbis / Theora |
| rtf | RTF 1.0-1.4, 1.5-1.6, 1.7, 1.8 | RTF 1.8 |
| doc (`Investigate`) | Word 97-2003 + Word (Generic) | Word 97-2003 |
| doc, ppt (`Spy`) | OLE2 + Word/PowerPoint 97-2003 | Word/PowerPoint 97-2003 |
| odt/ods/odp, pptx, xlsx (`Spy`) | ZIP + ODF 1.1/1.2 or Office Open XML | ODF 1.2 or Office Open XML |

No fixture lost its correct format.
