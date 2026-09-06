# ASF's primary mime type is resolved by searching for a codec-name string, not by parsing the header object graph

Like Ogg ([ADR 0007](0007-ogg-mime-type-resolved-by-content-sniffing.md)), one `file_signatures.csv` row covers ASF/WMA/WMV: the 8-byte prefix of the ASF Header Object GUID (`30 26 B2 75 8E 66 CF 11`) is shared verbatim by generic ASF containers, WMA (audio), and WMV (video), and alias-count picks `video/x-ms-asf` for all of them regardless of actual content. Unlike Ogg, there's no fixed offset to peek at - properly telling these apart means walking the header's variable-length list of objects to find the Stream Properties Object and reading its embedded stream-type GUID (Audio Media vs Video Media), which is a real parser, not a peek.

We decided to do what Apache Tika does instead: search a bounded window of the file for the (UTF-16LE) codec-name string a Windows Media encoder conventionally writes into its Codec List/Content Description objects, rather than parsing the object graph. From Tika's `tika-mimetypes.xml` (https://raw.githubusercontent.com/apache/tika/main/tika-core/src/main/resources/org/apache/tika/mime/tika-mimetypes.xml, retrieved 2026-09-06):

```xml
<mime-type type="audio/x-ms-wma">
  <sub-class-of type="video/x-ms-asf" />
  <magic priority="50">
    <match value="Windows Media Audio" type="unicodeLE" offset="0:8192" />
  </magic>
</mime-type>
<mime-type type="video/x-ms-wmv">
  <sub-class-of type="video/x-ms-asf" />
  <magic priority="60">
    <match value="Windows Media Video" type="unicodeLE" offset="0:8192" />
    <match value="VC-1 Advanced Profile" type="unicodeLE" offset="0:8192" />
    <match value="wmv2" type="unicodeLE" offset="0:8192" />
  </magic>
</mime-type>
```

`AsfContainerSniffer` searches the same four UTF-16LE markers over the same 8192-byte window, and video wins when both an audio and a video marker are present (a real WMV normally has both a video and an audio stream) - mirroring Tika's WMV magic having the higher `priority` of the two. This is a heuristic, not a structural guarantee: it depends on the encoder having written a recognizable codec name into that window, so it can miss on an unusual or hand-crafted file. That's an accepted trade-off, on the basis that it's what an established, widely-deployed implementation already does in production, and it's far cheaper than parsing ASF's object graph for a one-signature disambiguation. When no marker is found, this falls back to today's `video/x-ms-asf` alias-count answer rather than guessing, same as every other content-sniffing fallback in this library.

Because this window (8192 bytes) is wider than anything the signature table itself needs (532 bytes), `Spy(Stream)` now reads `Math.Max(SignatureIndex.MaxHeaderReach, AsfContainerSniffer.SearchWindowSize)` bytes instead of just the former - the extra I/O is negligible, and it means a caller going through the stream overload gets every disambiguation this library can do without needing to know either number.
