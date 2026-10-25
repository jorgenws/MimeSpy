# Releasing to NuGet.org

Releases are cut by running the `Publish NuGet` workflow (`publish.yml`) by hand from the Actions tab, on `main`, picking which part of the version to increment: `patch`, `minor` or `major`.

```bash
gh workflow run publish.yml -f bump=minor
```

The version lives in `<Version>` in `MimeSpy/MimeSpy.csproj`; release tags are created by the workflow, not pushed by hand. The workflow:

1. Reads `<Version>` from `MimeSpy/MimeSpy.csproj` and increments the chosen part (lower parts reset to 0)
2. Commits the bumped `<Version>` and tags that commit `v<version>`
3. Runs all tests and packs `MimeSpy/MimeSpy.csproj` into a `.nupkg`
4. Pushes the commit and tag to `main`
5. Pushes the package to NuGet.org, logging in through GitHub OIDC as the `NUGET_USER` repository secret

The package is built from exactly the tagged commit, and that commit's hash is embedded in the assembly's informational version (`2.3.1+<sha>`), so `git checkout v<version>` gives the source of any published version.

Git is pushed before NuGet because a NuGet version can't be taken back. If someone pushed to `main` during the run, the git push is rejected and nothing is published - just run it again. If the NuGet push fails, `main` has a tag for a version that was never published; the next run publishes the following version, or delete the tag and revert the bump to reuse it.
