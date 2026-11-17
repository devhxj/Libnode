# Jazor.LibNode

Native Node.js LTS builds for Jazor, compatible with `Microsoft.JavaScript.NodeApi`.
Source repository: [devhxj/Libnode](https://github.com/devhxj/Libnode).
Implementation of [Jazor #139](https://github.com/devhxj/Jazor/issues/139).

The first source candidate is **Node v26.13.1 (Lithium)** with **NodeApi 0.9.36**.
This project is under development: no RID has completed compatibility acceptance
and no package has been published. Jazor still uses Microsoft's native package.

The Linux x64 development candidate builds and passes 60 reused native cases,
22 Microsoft embedding/GC tests, 38 separate mock tests, and an isolated actual
NodeApi NuGet consumer. See [local verification](docs/local-verification.md).
The local binary requires GLIBC 2.39 and GLIBCXX 3.4.32; it is not a portable
release build for older Linux distributions.

## Layout

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
The native recipe currently targets Linux x64 only. Windows x64 remains the first
planned acceptance target; Linux development results cannot substitute for it.

The builder uses the upstream Python/Make build system with `--shared --without-npm`,
builds the reused embedding harness, checks exports/dependencies, and stages
`artifacts/native/linux-x64`. Native test and actual NuGet consumer results are
required before any compatibility claim. No Node/npm/pnpm CLI fallback is added to
Jazor's application tooling.

Run compatibility verification after the candidate build:

```text
dotnet run --file scripts/csharp/verify-libnode.cs
```

It reuses the native test driver inside the candidate embedding harness, then
Microsoft's original embedding/GC tests with the candidate selected explicitly.
The small recorded adaptation also updates the test SDK and xUnit adapter for
.NET 11. Assertions, test names and fixtures remain upstream-owned. Missing tests,
failed tests, skipped cases and a competing Microsoft native dependency fail the
lane. Mock-only scope/reference tests are not counted as native evidence.

## Candidate packages

Once real native assets, provenance and licenses have been staged:

```text
dotnet pack src/Jazor.LibNode.Runtime -p:LibNodeRid=linux-x64 -p:JazorReleaseVersion=<Jazor-tag-version> -o artifacts/packages
dotnet pack src/Jazor.LibNode -p:JazorReleaseVersion=<Jazor-tag-version> -p:RuntimePackageDirectory=<absolute-package-directory> -o artifacts/packages
```

RID packages carry `runtimes/<rid>/native/libnode.{dll,so,dylib}`, `native-build.json`,
`NODE-LICENSE`, `EMBEDDING-LICENSE` and `NOTICE`. Packaging fails when required inputs are absent. The
metapackage requires all four candidate RID packages and uses exact dependency
versions. NuGet versions are supplied by Jazor's tag; the Node version is separate
upstream metadata. Candidate packing is not release acceptance.

Declared support requires reused upstream C/managed tests, an isolated actual
NodeApi NuGet consumer and Jazor's pnpm/Vite/WebIDL/SSR gates. The additional old
Microsoft RIDs (`win-arm64`, `linux-arm64`, `win-x86`) require migration decisions.
Official publishing remains owned by Jazor's `nuget-publish-ref.yml`; this project
does not add another publishing or credential entry point.

See [source and port notes](native/libnode/README.md),
[the Sigma build-reference review](docs/sigma-build-reference.md), and
[Jazor's implementation plan](https://github.com/devhxj/Jazor/blob/main/docs/04-roadmap/libnode-lts-implementation-plan.md).
