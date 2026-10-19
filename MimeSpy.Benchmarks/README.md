# MimeSpy.Benchmarks

Compares `MimeSpy.Investigate()` and `MimeSpy.Spy()` against [Mime-Detective](https://github.com/MediatedCommunications/Mime-Detective)
(NuGet package `Mime-Detective`, by MediatedCommunications - not to be confused with the
unrelated, abandoned `MimeDetective` package last published in 2020), the closest
actively-maintained .NET equivalent: both are signature/magic-byte detectors that take a
whole file's bytes and return every matching definition, rather than a hardcoded
per-extension sniffer.

Both of Mime-Detective's publicly redistributable definition packs are included:

- **Default** - bundled with the base package, a small built-in set.
- **Condensed** - the `Mime-Detective.Definitions.Condensed` add-on package, a larger set
  derived from TrID's signatures. Its own license permits free use only for personal/
  non-commercial purposes (`UsageType.PersonalNonCommercial` below) - a paid license is
  required otherwise, see the package's own documentation. It is built here for a
  closer-to-parity comparison against MimeSpy's much larger PRONOM-derived catalog, not
  because this repo endorses or requires it.

`Investigate()` and Mime-Detective's `Inspect` aren't doing quite the same amount of
work: `Investigate` also runs PRONOM's container-signature engine (opening the ZIP/OLE2
archive to resolve docx/xlsx/ppt/etc.) and evaluates end-of-file-anchored signatures for
real, neither of which Mime-Detective's flat prefix/substring matching does at all - it
has no EOF-anchored or container-aware concept in its own signature model. `Spy()` skips
both of those (see docs/adr/0016, 0017), so it's the closer apples-to-apples comparison
of "guess from raw bytes, no real parsing"; `Investigate()` is kept alongside it to show
what the extra correctness costs.

Reuses `MimeSpy.IntegrationTests/Fixtures` rather than its own copies.

## Running

```
dotnet run --project MimeSpy.Benchmarks -c Release
```

Runs the BenchmarkDotNet suite (`DetectionBenchmarks`) across a handful of fixtures
spanning tiny/large files and plain/container formats. Results land under
`BenchmarkDotNet.Artifacts/results/` (git-ignored).

```
dotnet run --project MimeSpy.Benchmarks -c Release -- accuracy
```

Prints a one-shot table of what each library/pack actually identifies every fixture as -
useful since raw throughput only matters if both sides are answering the same question.
This is not a pass/fail correctness check (there's no single "correct" MIME type per
format to assert against across three different catalogs) - just a side-by-side readout.
