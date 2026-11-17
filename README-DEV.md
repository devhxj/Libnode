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
The Linux recipe targets Linux x64. Windows x64 uses the Node-supported Visual
Studio 2022/2026 Clang toolchain and is built by `build-windows-libnode.cs` on a
Windows runner. Both platforms require their own build and runtime evidence;
macOS architectures remain candidates.

Run `arm-candidate.yml` manually to build and verify Linux ARM64 and Windows
ARM64 on native GitHub runners. Both build scripts select the process architecture;
`verify-native-architecture.cs` checks native runner and ELF/PE architecture.
The Windows workflow accepts `architecture: arm64` and supports importing an
existing `libnode-win-arm64-build` artifact for fresh checks without rebuilding.
`native-bundle.cs -- pack --rid linux-arm64` bundles a verified local Linux ARM64
build. See [platform support](docs/platforms.md) for 32-bit constraints.

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

The actual NuGet consumer also runs 30 Node 26 feature checks from
`tests/NuGetConsumer/node26-tests.cjs`. Coverage includes Temporal calendar/DST and
nanoseconds, iterator helpers, Set composition, Float16Array, JSON source revivers,
Unicode regex, structured clone/transfer, Web Crypto/OpenSSL, Web Streams and
compression, fetch over data/loopback HTTP, cancellation, WebAssembly, Worker
transfers/Atomics, ESM/top-level await, TypeScript stripping, SQLite, filesystem,
AsyncLocalStorage, ICU, V8 serialization and stream pipelines. These are embedded
runtime checks against the selected native package, not a system Node executable.
The report records every result, Node/process architecture and suite SHA256.
All 30 must pass on every release platform; changing the suite invalidates old
verification evidence. The final release metapackage consumer repeats the suite.

## Candidate packages

After native verification and the issue regression lane pass, pack the complete
release catalog and run its metapackage consumer:

```text
dotnet run --file scripts/csharp/publish-nuget.cs -- --package-version X.Y.Z --skip-push
dotnet run --file scripts/csharp/verify-release.cs
```

For individual candidate package inspection, the underlying pack commands are:

```text
dotnet pack src/Jazor.LibNode.Runtime -p:LibNodeRid=linux-x64 -p:JazorReleaseVersion=<release-version> -o artifacts/packages
dotnet pack src/Jazor.LibNode -p:JazorReleaseVersion=<release-version> -p:RuntimePackageDirectory=<absolute-package-directory> -o artifacts/packages
```

RID packages carry `runtimes/<rid>/native/libnode.{dll,so,dylib}`, `native-build.json`,
`NODE-LICENSE`, `EMBEDDING-LICENSE` and `NOTICE`. Packaging fails when required inputs
are absent. The metapackage requires all RID packages in the release catalog and uses exact
dependency versions. NuGet versions are supplied by the release pipeline; the Node
version is separate upstream metadata. Candidate packing is not release acceptance.

Declared support requires native and managed compatibility tests on each target,
actual NuGet consumers, deployment checks and real native-addon calls. The extra
RIDs carried by the old Microsoft package (`win-arm64`, `linux-arm64`, `win-x86`)
remain unvalidated here. The official publishing entry is
`.github/workflows/nuget-publish-ref.yml`, with tag-driven versions, NuGet trusted
publishing and the same verification gates for manual dry runs.
See [the release guide](docs/releases.md) for setup and release preparation.

To reuse a verified local build in Actions without compiling V8 again, commit
and push the source, then run:

```text
dotnet run --file scripts/csharp/native-bundle.cs -- upload
```

Pass the printed draft release ID and SHA256 to the manual verification or
publishing workflow. Actions imports the exact library and harness and repeats
the verification gates. See [local build publishing](docs/releases.md#reuse-a-local-native-build)
for commands and commit/tag requirements.

`dotnet run --file scripts/csharp/verify-native-bundle.cs` tests the real local
bundle in an isolated clean checkout, including checksum/commit rejection and
the requirement for fresh verification before packaging. It retains the imported
checkout under `artifacts/native-bundle-verification/` for compatibility checks.

See [source and port notes](native/libnode/README.md),
[the build-reference review](docs/sigma-build-reference.md), and
[the upstream issue audit](docs/upstream-issue-audit.md).
