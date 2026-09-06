# Async overload added only for the Stream API, not the Span one

Pipeline scenarios (ASP.NET Core request handling, anything reading a file
upload from a network stream or a temp file) run on async paths end-to-end;
calling a sync `Spy(Stream)` there forces either a blocking read on a
thread-pool thread or an awkward `Task.Run` wrapper at the call site. We added
`SpyAsync(Stream, CancellationToken)` to remove that friction, reading the
same fixed-size window as `Spy(Stream)` (docs/adr's existing byte-count
rationale on that overload still applies) via a manual `ReadAsync` loop rather
than `Stream.ReadAtLeastAsync`, since the latter isn't available on
netstandard2.0 and the loop needs to behave identically on both targets.

`Spy(ReadOnlySpan<byte>)` gets no async twin. Its whole contract is that the
caller already has the bytes in memory - there is no I/O for an async version
to avoid, and `ReadOnlySpan<byte>` can't cross an `await` boundary anyway, so
an "async" overload would just be `Task.FromResult` around the same synchronous
work with a heap-allocated wrapper. Callers on an async path who already hold a
buffer should keep calling the synchronous `Spy(ReadOnlySpan<byte>)` directly.

`SpyAsync` validates `stream` for null synchronously, before doing anything
`async`-state-machine-related, the same way `Spy(Stream)` does - so a null
argument throws immediately from the call, not via a faulted `Task`. This
matches the BCL convention (e.g. `Stream.ReadAsync`) and keeps
`Assert.Throws` usable in tests instead of forcing `Assert.ThrowsAsync` for
what is really a synchronous argument-validation failure.
