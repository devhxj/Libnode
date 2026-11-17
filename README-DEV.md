# Libnode development

Build and verification instructions for [devhxj/Libnode](https://github.com/devhxj/Libnode).

## Repository layout

- `native/libnode/source.lock.json`: source commits, download hashes, patch hashes and RID decisions.
- `native/libnode/patches/`: reviewable port from the pinned Microsoft embedding implementation source.
- `scripts/csharp/`: source preparation, native builds and compatibility verification.
- `src/Jazor.LibNode/`: native metapackage, without a managed host assembly.
- `src/Jazor.LibNode.Runtime/`: shared RID packaging project.
- `artifacts/`: ignored downloads, sources, builds and reports.

## Source preparation

Requires .NET SDK 11.0.100, Python 3, Git, tar and xz. Run from this directory:

```text
git clone https://github.com/devhxj/Libnode.git
cd Libnode
```

```text
dotnet run --file scripts/csharp/prepare-libnode.cs -- --download-node
dotnet run --file scripts/csharp/prepare-libnode.cs -- --download-node --offline
```

Both commands verify SHA256 hashes against the lock. The second uses cached files
and fails if an input is missing or modified. `artifacts/inputs/abi-inventory.json`
records all 27 imported embedding functions and managed Cdecl signatures. Matching
header names is an input check; it does not establish binary ABI compatibility.

## Linux development build

Node 26 requires GCC >=13.2 or Clang >=19.1, Make, Python and Rust/Cargo for Temporal.
On Ubuntu x64, this optional installer extracts the compiler packages into a user
directory, using apt's selected dependency closure and checksums:

```text
dotnet run --file scripts/csharp/install-linux-toolchain.cs
dotnet run --file scripts/csharp/build-libnode.cs -- --jobs 8
```

The default local compiler directory is `~/.local/opt/jazor-toolchain/usr/bin`;
`--toolchain <directory>` selects another directory. Rust/Cargo can be installed
with the official rustup installer; the builder recognizes `~/.cargo/bin`.
The native recipe currently targets Linux x64 only. Windows x64 and both macOS
architectures still require their own build and runtime evidence.

The builder uses the upstream Python/Make build system with `--shared --without-npm`,
builds the reused embedding harness, checks exports/dependencies, and stages
`artifacts/native/linux-x64`. Native test and actual NuGet consumer results are
required before any compatibility claim.

Run compatibility verification after the candidate build:

```text
dotnet run --file scripts/csharp/verify-libnode.cs
dotnet run --file scripts/csharp/verify-upstream-issues.cs
```

It reuses the native test driver inside the candidate embedding harness, then
Microsoft's original embedding/GC tests with the candidate selected explicitly.
The small recorded adaptation also updates the test SDK and xUnit adapter for
.NET 11. Assertions, test names and fixtures remain upstream-owned. Missing tests,
failed tests, skipped cases and a competing Microsoft native dependency fail the
lane. Mock-only scope/reference tests are not counted as native evidence.

The [audit of 66 upstream issues](docs/upstream-issue-audit.md) records a Linux
addon symbol fix and the remaining managed-layer limitations. The issue runner
uses isolated processes and deadlines for crash/hang reproductions. Expected
managed failures are reported as known limitations; they do not count as passing
native compatibility tests. A pinned Rolldown addon is downloaded and verified by
SHA512, then loaded and called directly without Node/npm CLI tooling.

## Candidate packages

Once real native assets, provenance and licenses have been staged:

```text
dotnet pack src/Jazor.LibNode.Runtime -p:LibNodeRid=linux-x64 -p:JazorReleaseVersion=<release-version> -o artifacts/packages
dotnet pack src/Jazor.LibNode -p:JazorReleaseVersion=<release-version> -p:RuntimePackageDirectory=<absolute-package-directory> -o artifacts/packages
```

RID packages carry `runtimes/<rid>/native/libnode.{dll,so,dylib}`, `native-build.json`,
`NODE-LICENSE`, `EMBEDDING-LICENSE` and `NOTICE`. Packaging fails when required inputs
are absent. The metapackage requires all four candidate RID packages and uses exact
dependency versions. NuGet versions are supplied by the release pipeline; the Node
version is separate upstream metadata. Candidate packing is not release acceptance.

Declared support requires native and managed compatibility tests on each target,
actual NuGet consumers, deployment checks and real native-addon calls. The extra
RIDs carried by the old Microsoft package (`win-arm64`, `linux-arm64`, `win-x86`)
remain unvalidated here. No official publishing workflow is configured in this
repository yet; the checked-in workflow builds and verifies candidates only.

See [source and port notes](native/libnode/README.md),
[the build-reference review](docs/sigma-build-reference.md), and
[the upstream issue audit](docs/upstream-issue-audit.md).
