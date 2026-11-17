# Releases and NuGet publishing

Libnode follows the same release pattern as
[Jazor](https://github.com/devhxj/Jazor/blob/main/docs/03-guides/release-and-versioning.md):
a semantic version tag selects the package version, every package uses that same
version, quality and consumer checks run first, and one GitHub Actions workflow
publishes NuGet, GitHub Packages and a GitHub Release.

## Version and package set

Use `vMAJOR.MINOR.PATCH`, optionally followed by a SemVer prerelease suffix such
as `v0.1.0-preview.1`. The tag without `v` is the NuGet version. Versions are not
hardcoded in project files and do not follow the embedded Node version.

- MINOR adds capabilities, packages or supported platforms.
- PATCH fixes existing behavior and does not introduce new public capabilities.
- After 1.0, breaking compatibility requires MAJOR. Before 1.0, document breaking
  changes and migration in a MINOR release.
- Release when the change is verified, rather than on a calendar. Explain skipped
  version numbers and platform or minimum-OS changes in the release notes.

[`LibNodePackages.props`](../src/Packaging/LibNodePackages.props) is the release
catalog. The 0.1.0 closure is `Jazor.LibNode` plus
`Jazor.LibNode.linux-x64` and `Jazor.LibNode.win-x64`. macOS remains a build
candidate; the metapackage does not depend on empty or unverified RID packages.
Add a RID to the catalog and metapackage only after its native build, runtime
tests and actual NuGet consumer pass. Packaging checks
that both declarations agree and that every dependency pins the exact same version.

## Release notes

Maintain dated, attributed, bilingual entries in [CHANGELOG.md](../CHANGELOG.md),
using the same structure as Jazor:

```markdown
## YYYY-MM-DD

### Libnode X.Y.Z

中文 | English

#### 问题修复

- 用户可观察的问题修复。by @contributor

Bug Fixes

- The corresponding user-visible fix. by @contributor
```

Categories are 新增功能 / New Features, 体验优化 / Improvements, 问题修复 /
Bug Fixes and 其他变更 / Chores, in that order, omitting unused categories. Keep
Chinese and English entries aligned and describe migration when needed. Unreleased
work uses `### 未发布 | Unreleased`; replace it with the chosen version heading
and date before tagging. A missing, empty or undated section blocks publication.

```text
dotnet run --file scripts/csharp/release-notes.cs -- --tag vX.Y.Z
```

The workflow also checks that the tag resolves to the checked-out commit. GitHub
Release notes come directly from that version's curated section.

## Candidate and packaging checks

The reusable Linux and Windows candidate workflows check out the resolved release
**commit**, build native code from locked inputs (or import the local Linux build),
then run:

```text
dotnet build Jazor.LibNode.slnx
dotnet run --file scripts/csharp/build-libnode.cs
dotnet run --file scripts/csharp/verify-libnode.cs
dotnet run --file scripts/csharp/verify-upstream-issues.cs
dotnet run --file scripts/csharp/verify-release.cs
dotnet run --file scripts/csharp/publish-nuget.cs -- --package-version X.Y.Z --skip-push
```

The package command requires matching native source/library hashes, 60 native
cases, 22 real managed embedding/GC tests, 38 separately counted mock tests and
the issue regression report. It checks package IDs, exact dependency versions,
native asset hashes, licenses and provenance, then runs an isolated consumer of
the **release metapackage and version**. Missing RID assets or stale/unexpected
packages fail. Its output is `artifacts/release/packages/`, including a manifest
with the source commit and package hashes.

`--skip-push` is mandatory locally. No local NuGet key is required or read. The
publishing helper can verify an artifact set with `--verify-only`; actual uploads
are restricted to the official release workflow.

## GitHub Actions

The only publishing entry is
[nuget-publish-ref.yml](../.github/workflows/nuget-publish-ref.yml). Trigger it by
pushing a release tag, or select a tag with `workflow_dispatch`. The manual
`dry_run` input runs the same notes, build, test, package and artifact validation
steps, while skipping authentication, package uploads and GitHub Release creation.

The publish job depends on successful verification. It downloads packages built
by that same run and checks their exact version, source commit, source lock,
catalog and package hashes **before requesting credentials**. Failed, cancelled
or skipped gates cannot publish. Prerelease tags create prerelease GitHub Releases.
Retries use `--skip-duplicate`, and release notes/assets are updated for the same tag.

Configure the same authentication arrangement as Jazor:

1. Create the `production` GitHub environment and set `NUGET_USER` to the NuGet
   account name, as a repository or environment secret.
2. On nuget.org, configure trusted publishing for owner `devhxj`, repository
   `Libnode`, workflow `nuget-publish-ref.yml` and environment `production`, with
   the account authorized for the package IDs.
3. Permit the workflow's `GITHUB_TOKEN` to write packages and releases. NuGet uses
   `NuGet/login` with `id-token: write`; no permanent NuGet API key is stored here.

GitHub environment rules and the nuget.org trust policy are account settings;
checking in the workflow does not create them. No release tag or public package
is created by local verification.

## Reuse a local native build

A local Linux x64 build can supply the release binaries. Actions downloads and
tests those binaries, packs the NuGet packages and publishes through the same
trusted-publishing workflow. V8 and the native harness are not rebuilt.

After building and running both compatibility scripts locally, commit and push
the source and release notes. From that clean checkout, upload the verified build:

```sh
dotnet run --file scripts/csharp/native-bundle.cs -- upload
```

This creates a draft release containing a compressed build input bundle, not a
product release. The command prints the exact source commit, `native_release_id`
and `native_archive_sha256`. It includes the library, embedding harness, licenses,
build record and checksums. `pack` creates the same ZIP locally without uploading.
The locked source and fixtures are prepared again for verification; no compiler
build of Node/V8 runs when importing the bundle.

First check the handoff without choosing a product version or publishing packages:

```sh
gh workflow run local-native.yml --repo devhxj/Libnode \
  -f source_ref=COMMIT \
  -f native_release_id=RELEASE_ID \
  -f native_archive_sha256=SHA256
```

For a release, prepare the dated changelog entry and commit it **before uploading
the bundle**, so the bundle's commit matches the release tag. Create the tag
locally, then dispatch the publishing workflow with that tag and the two printed
values. Pushing `v*` tags immediately starts the default source-build workflow.
Cancel that automatic run in Actions before starting the manual run with the
local bundle:

```sh
git tag vX.Y.Z COMMIT
git push origin vX.Y.Z
# Cancel the automatic tag run in Actions, then:
gh workflow run nuget-publish-ref.yml --repo devhxj/Libnode \
  -f release_ref=vX.Y.Z -f dry_run=true \
  -f native_release_id=RELEASE_ID \
  -f native_archive_sha256=SHA256
```

The manual run with the bundle skips V8 compilation. After inspecting its results,
dispatch the same command with `-f dry_run=false` to publish.

The bundle must match the checkout commit and source lock exactly. Altered
archives/files, unexpected entries and inconsistent build records are rejected.
Import clears local verification status; all native, managed, addon and package
consumer gates must pass again on the Actions runner. Local results are retained
as provenance. Publishing still uses GitHub OIDC; no permanent NuGet key is
needed locally or in GitHub.

The binary retains its build machine's OS requirements. The current local build
requires GLIBC 2.39 and GLIBCXX 3.4.32, suitable for the Ubuntu 24.04 verification
runner. Reusing it does not establish compatibility with older distributions.

Windows x64 builds once in `windows-candidate.yml` using Visual Studio Clang and
Rust. The workflow preserves the DLL, import library, native harness and build
record as `libnode-win-x64-build`. Pass that run's numeric ID as
`windows_build_run_id` to the manual publishing workflow to reuse it. The Windows
job verifies its source lock and patch, prepares source fixtures, imports the
native outputs and repeats compatibility tests without rebuilding V8. Both RID
packages and the `Jazor.LibNode` metapackage publish as 0.1.0 only after the
platform gates, package hashes and actual release metapackage consumers pass on
Linux and Windows.

The 0.2.0 workflow also verifies Linux ARM64 and Windows ARM64 on native runners.
`linux_arm64_build_run_id` selects a candidate run containing a verified native
ZIP, and `windows_arm64_build_run_id` selects an ARM64 Windows build artifact.
Both imports require matching source locks and reset verification before testing.
Linux bundle reuse across a different release commit is explicit through
`--reuse-locked-source`: original build and verification commits remain recorded,
and source lock, archive/file hashes, native tests and consumers still gate packing.
The final metapackage is tested on all release architectures. An ARM64 label alone
does not establish support; the ARM64 binary and test evidence must be present.
