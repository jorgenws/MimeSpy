# PrimaryMimeType is computed on Result, not stored on the signature

`PrimaryMimeType` used to be decided in two unrelated places and just handed off
between them. `SignatureIndex.ResolveMimeTypes` picked it once, at table-load time,
by extension-alias count (docs/adr/0004), and stored it on `FileSignature`. `Spy`
copied that value onto the `Result` it returned. Then, for exactly two formats,
`OggContainerSniffer`/`AsfContainerSniffer` (docs/adr/0007, docs/adr/0008) mutated
that same field in place with a real, content-sniffed answer. The field meant two
different things depending on format - "a statistical guess made before your file
was even read" for most signatures, "we actually looked at your file and know for
sure" for Ogg/WMA/WMV - with no way to tell which one a caller got, and the
decision logic itself split across `SignatureIndex` (the default) and
`PrimaryMimeTypeRefiner` (the override).

We moved the decision onto `Result` instead. `Result` now carries `SniffedMimeType`
- the raw, possibly-null evidence a content sniffer found - and `PrimaryMimeType`
is a computed property: it uses `SniffedMimeType` outright when there is one and
it's among this result's own `MimeTypes`, otherwise falls back to the same
extension-alias-count default as before. `Result` already had everything this
needs - it calls `MimeTypeIndex.FindByExtension` directly for this the same way
`PrimaryExtension` (docs/adr/0012) already did - so there was no technical
reason to keep the default computation in `SignatureIndex` at all;
`ResolveMimeTypes` now only builds the `MimeTypes` candidate list.

The only thing that *can't* move onto `Result` is the actual byte-reading:
`OggContainerSniffer` and `AsfContainerSniffer` still need the file's raw bytes
(the codec identification packet, the codec-name marker search) to produce
`SniffedMimeType` in the first place, and `Result` deliberately doesn't hold a
reference to the buffer `Spy` was called with. So `PrimaryMimeTypeRefiner` still
runs during `Spy` - it just no longer decides anything; it attaches the sniffed
value to every tied result as evidence and lets each `Result`'s own
`PrimaryMimeType` decide whether its own `MimeTypes` actually backs it.

This is a mechanism change, not a behavior change: every existing test still
passes unmodified, and docs/adr/0012's "known limitation" (a sniffed
`PrimaryMimeType` that isn't the alias-count majority still makes
`PrimaryExtension` return null) still applies exactly as before - moving where
the value is computed doesn't change what it evaluates to.
