# IMimeSpy exists as a mockable seam, not for testing MimeSpy itself

`MimeSpy.Spy`/`SpyAsync` are pure, deterministic functions of the bytes handed
to them - no I/O beyond the caller-supplied stream, no clock, no randomness -
and `Result` (the return type) is already a publicly constructible record.
That means a consumer whose own code separates "call Spy once at the
boundary" from "the logic that reacts to the Result" never needs to fake
anything: they can build a `Result` directly with real extension/mime-type
strings and get correct `PrimaryMimeType`/`PrimaryExtension` values for free,
since those are computed against the real embedded data (docs/adr/0013)
regardless of where the `Result` came from. On its own, that's a complete
argument *against* adding an interface here - there's only ever one
implementation, and the real one is already trivial to call in a test.

The gap that argument misses is code shaped like `read bytes; call Spy; act
on Result` inlined into a single method. A consumer who writes it that way -
which is a common and reasonable shape, not a design smell worth insisting
they refactor - can't substitute a canned `Result` without either a real file
that happens to trigger the exact scenario under test (an empty match list, a
genuine tie, a sniffed-vs-default primary type) or a seam to mock at. Since
we have no users yet to weigh against the cost of a slightly larger public
surface, we added `IMimeSpy` (`Spy(ReadOnlySpan<byte>)`, `Spy(Stream)`,
`SpyAsync(Stream, CancellationToken)`, mirrored 1:1 off `MimeSpy`'s own public
members) purely so those consumers can register `IMimeSpy`/`MimeSpy` in DI and
mock the interface, without being forced to restructure their own method
first. Consumers who *do* take a `Result`/`IReadOnlyList<Result>` as their
method's input don't need this interface at all and should keep testing that
way instead.
