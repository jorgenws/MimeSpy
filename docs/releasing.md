# Releasing to NuGet.org

Releases are triggered by pushing an annotated git tag named `v<version>`.

```bash
git tag -a v1.0.0 -m "Release 1.0.0"
git push origin v1.0.0
```

This triggers the `publish.yml` GitHub Actions workflow, which:

1. Runs all tests
2. Extracts the version number from the tag (strips the leading `v`)
3. Packs `MimeSpy/MimeSpy.csproj` with that version into a `.nupkg`
4. Pushes the package to NuGet.org using the `NUGETAPIKEY` repository secret
