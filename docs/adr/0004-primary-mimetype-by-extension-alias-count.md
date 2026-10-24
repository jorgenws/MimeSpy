# Primary mime type is chosen by extension-alias count

> Superseded by [ADR 0020](0020-legacy-signature-engine-removed-in-favor-of-pronom-alone.md). The Gary Kessler/`mime.types` tables this applied to are gone; PRONOM gives each format its own MIME type.

A single file signature can list several extension aliases that resolve to different mime types (e.g. `jfif|jpe|jpeg|jpg`, where `jpe`/`jpeg`/`jpg` resolve to `image/jpeg` but `jfif` resolves to `image/pjpeg`). We decided to pick the mime type backed by the most aliases as the signature's `PrimaryMimeType`, on the assumption that the more common aliases indicate the more canonical mime type. Ties keep the first-seen mime type, since the selection sort is stable.
