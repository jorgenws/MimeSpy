# PrimaryExtension is derived from mime.types' own alias order, not a hardcoded table

docs/adr/0011 leaves `doc`/`dot`, `xls`/`xla`, and `ppt`/`pps` tied to each other, since
nothing in the CFB directory structure distinguishes a document from its own
template/add-in/slideshow variant. That's the right call for correctness - but it means
a caller who just wants a single best-guess extension (rather than "is this answer
provably correct") gets no help at all from `Result.Extensions` alone. `PrimaryMimeType`
already solves the equivalent problem for mime types (docs/adr/0004), so the same idea
is added for extensions: `Result.PrimaryExtension()`.

## First attempt: a hardcoded preference table

The first version of this method was a three-entry lookup table -
`{"dot": "doc", "xla": "xls", "pps": "ppt"}` - picked by hand because those are the only
ties we'd looked at closely. That was wrong in a way that only became obvious once
tested against a genuinely common format: `jpe`/`jpeg`/`jpg` (and, for the deeper
`JFIF|JPE|JPEG|JPG` signature, `jfif` alongside them) are tied in exactly the same
"pure spelling variants of one format" shape as doc/dot - `PrimaryMimeType` already
resolves all of them to `image/jpeg` via alias count (docs/adr/0004) - but they weren't
in the hardcoded table, so `PrimaryExtension()` returned `null` for what's likely the
single most common file format this library will ever see. A hand-picked table can only
ever cover the ties its author happened to think of; it was solving "office formats"
when the actual problem was general.

## The general rule

`PrimaryExtension()` is computed from the same alias data that already backs
`PrimaryMimeType` - Apache's `mime.types` (docs/adr/0005) - rather than any table
maintained separately in this codebase, in two steps:

1. **Is `PrimaryMimeType` a genuine majority among this result's tied extensions, not
   just a tiebreak survivor?** Recompute the same per-mime-type alias counts
   `SignatureIndex.ResolveMimeTypes` used to pick it, and check that its count is
   *strictly greater* than every other candidate's. `doc`/`dot` both resolve to the one
   mime type `application/msword` (count 2, no competitor) - a trivial majority.
   `jpe`/`jpeg`/`jpg`/`jfif` split 3-to-1 toward `image/jpeg` - a real majority. But
   `docx`/`pptx`/`xlsx` each resolve to a *different* mime type, one extension apiece -
   `PrimaryMimeType` only "won" that row via `ResolveMimeTypes`'s first-seen tiebreak
   (docs/adr/0004), not an actual majority, so this check fails and `PrimaryExtension()`
   returns `null`. This is the guard that keeps the method honest: it would otherwise
   confidently return `docx` for a file that's equally likely to be a `pptx` or `xlsx`,
   dressing up an arbitrary tiebreak as a considered answer.
2. **Which of the tied extensions actually resolve to that majority mime type?** Usually
   exactly one, in which case that's the answer outright. When more than one does (the
   doc/dot and jpeg-family cases), the tie between *those* is broken using that mime
   type's own declared extension order in `mime.types` - `image/jpeg`'s line lists
   `jpeg jpg jpe`, `application/msword`'s lists `doc dot`, `application/vnd.ms-excel`'s
   lists `xls xlm xla xlc xlt xlw`, `application/vnd.ms-powerpoint`'s lists
   `ppt pps pot` - on the assumption that whichever spelling Apache's maintainers listed
   first is the more canonical one. This reproduces the old hardcoded table's answers
   (`doc`, `xls`, `ppt`) exactly, plus now also resolves `jpeg`, with no format-specific
   knowledge written into this codebase at all.

## Known limitation

This is computed fresh from `Extensions` and the stored `PrimaryMimeType`, so it can't
tell a plain alias-count-derived `PrimaryMimeType` apart from one that content sniffing
(docs/adr/0007, 0008) has since overridden to something the alias count alone wouldn't
have picked. A `.ogv` file sniffed to `video/ogg` still loses the majority check here
(its row's `oga`/`ogg` pair out-counts `ogv` 2-to-1 by raw alias count), so
`PrimaryExtension()` falls back to `null` even though the sniffer already knows more.
Not a regression - the hardcoded table never covered Ogg/ASF rows either - just an
honest gap this design doesn't close.
