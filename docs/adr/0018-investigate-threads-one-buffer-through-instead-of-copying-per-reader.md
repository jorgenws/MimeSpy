# Investigate threads one buffer through instead of copying it per reader

> Superseded for the `Stream` overloads by [ADR 0022](0022-investigate-stream-requires-seeking-and-reads-bounded-windows.md) - they no longer buffer the file at all, and the container readers now take a `Stream`. `Investigate(ReadOnlySpan<byte>)` still makes the one copy described below, then hands the readers a no-copy `MemoryStream` over it.

`Investigate` was built up one piece at a time - the core DROID matcher, then the
ZIP reader, then the OLE2 reader, then `PronomContainerMatcher` tying them
together - and each of those pieces originally took a plain
`ReadOnlySpan<byte>` and did whatever was easiest with it locally.
`PronomZipContainerReader.TryOpen` called `bytes.ToArray()` to get something
it could hand `ZipArchive` a `Stream` over; `PronomOle2ContainerReader.TryOpen`
called `bytes.ToArray()` too, to get an array it could keep past the call
(a `ReadOnlySpan<byte>` can't be stored in a field). Neither copy was wrong in
isolation - each reader only knew about its own `ReadOnlySpan<byte>` parameter,
not that the caller might already own a real array underneath it - but stacked
together, a large ZIP/OLE2 file being `Investigate`d meant the same bytes copied
two or three times over: once into whatever buffer the caller/`Stream`
overload built, then again inside whichever container reader actually ran.
For a 3 GB file that's the difference between ~3 GB and ~6-9 GB of peak
memory, which matters a great deal to a caller trying to decide (docs/adr/0016)
whether they can afford to call `Investigate` on a file that size at all.

The fix was to stop passing a bare `ReadOnlySpan<byte>` across these internal
boundaries and instead thread a `(byte[] bytes, int length)` pair through from
wherever the array is first materialized (`Investigate(ReadOnlySpan<byte>)`'s own
unavoidable `bytes.ToArray()`, or a `Stream` overload's `MemoryStream`) all the
way down through `PronomContainerMatcher.Refine`,
`PronomZipContainerReader.TryOpen`, and `PronomOle2ContainerReader.TryOpen` -
none of which copy it again. The `length` half of that pair matters as much as
the `byte[]` half: `Investigate(Stream)`/`InvestigateAsync` get their array via
`MemoryStream.GetBuffer()` rather than `ToArray()` specifically so they don't
have to trim it to size first (that trim would itself be a copy) - but
`GetBuffer()`'s array is the stream's *capacity*, usually larger than its
`Length` (it grows by doubling), so every bounds check downstream has to be
against the explicit `length` passed alongside it, never against the array's
own `.Length`. `PronomOle2ContainerReader` in particular has several of these
(directory-sector and FAT-chain bounds checks) that all needed updating from
`bytes.Length` to a `length` parameter for this to be safe rather than just
convenient.

`Investigate(ReadOnlySpan<byte>)` itself still copies once, and has to: a
`ReadOnlySpan<byte>` is a ref struct that can't outlive the call that produced
it, so if the caller's own bytes aren't already an array this method can keep,
there's no way to avoid materializing one. That single copy is the honest
floor - everything from there on reuses it.
