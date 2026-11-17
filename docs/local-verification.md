# Linux development candidate — 2026-11-17

The candidate was built locally on Ubuntu 26.04 x64 with GCC/G++ 15.2.0, Make
4.4.1, Rust/Cargo 1.100.0 and .NET SDK 11.0.100. Source/patch inputs are fixed in
[`source.lock.json`](../native/libnode/source.lock.json). This is local development
evidence; no official package has been published.

| Check | Result |
| --- | --- |
| Official Node source archive SHA256 | Matches the pinned official checksum |
| Modified cached source input | Rejected with SHA256 mismatch |
| Native/managed adaptation patches | Apply cleanly to fresh pinned source |
| Managed embedding imports | All 27 declared in header and exported by the binary |
| Native embedding cases | 60 passed; original C/C++ cases, assertions and fixtures |
| Microsoft embedding/GC suite | 22 discovered, 22 passed, 0 skipped |
| Scope/reference/thread-call suite | 38 passed; mock-only evidence recorded separately |
| Actual NodeApi 0.9.36 NuGet consumer | Passed; package source revision matches the pinned tests |
| Candidate library selection | Loaded from consumer output's NuGet native asset; hash matches |
| Same-process execution | JS PID equals .NET PID |
| NuGet package contents | Native library, build/verification record, README, project license, Node and embedding licenses, NOTICE; no host assembly |
| Missing native/RID assets | Packaging fails explicitly |

Runtime identity from the actual NuGet consumer:

| Field | Value |
| --- | --- |
| Node | `v26.13.1` |
| V8 | `14.6.202.34-node.40` |
| Node-API | `10` |
| Module ABI | `147` |
| Temporal | Present (`object`) |
| Candidate SHA256 | `592f93eae6ae340608d9996c0f5180df103eaa9539afc713a7544ac3b79f6318` |

The local package is
`artifacts/packages/Jazor.LibNode.linux-x64.0.0.0-local.592f93eae6ae.nupkg`.
Detailed results, TRX files, loaded-library identity and logs live in
`artifacts/verification/`; the build record is
`artifacts/native/linux-x64/native-build.json`. These generated files are ignored
by Git and collected by the candidate CI lane.

The binary imports symbols up to **GLIBC 2.39**, **GLIBCXX 3.4.32** and
**CXXABI 1.3.11**. Its measured dependencies are libc, libm, libgcc_s, libstdc++,
the ELF loader and linux-vdso. No dynamic libatomic dependency appeared in this
build. This does not establish a minimum tested deployment distribution: a
controlled older build userspace and an actual runtime test are still needed.

Remaining platform and delivery work:

- Build and run both native macOS architectures. Windows x64 results are recorded below.
- Choose and verify the Linux deployment baseline; extend native addon coverage.
- Decide migration for the old `win-arm64`, `linux-arm64` and `win-x86` assets.
- Validate target RID versus build-host RID, including cross-publish consumers.
- Validate higher-level pnpm/Vite/WebIDL/SSR consumers and release artifacts.
- Verify a subsequent LTS patch update using the same recipe.

The test adapter adaptation updates test SDK/runner versions for .NET 11. The
native fixture adaptation restores `snapshot/create-worker-and-vm.js` from the
pinned fork; the old driver required it and the Node 26 archive omits it. Both
adaptations preserve the upstream assertions and are reviewable in `patches/`.

The candidate includes the Linux addon symbol-scope fix described in the
[upstream issue audit](upstream-issue-audit.md). The issue lane verifies C addon
calls, native TypeError behavior and a real Rolldown napi-rs minification call.
Known managed-layer crashes/hangs are reported separately from passing tests.

## Windows x64 verification

[Windows verification run 44049520239](https://github.com/devhxj/Libnode/actions/runs/44049520239)
imports the DLL and harness built in run 44046412336, then passes all 60 native
cases, 22 Microsoft embedding/GC tests, 38 separate mock tests and the actual
Microsoft.JavaScript.NodeApi 0.9.36 NuGet consumer. No V8 compilation is repeated.

The DLL was built on Windows Server 2022 with Visual Studio 2022 Clang 19.1.5,
Rust 1.100.0 and Python 3.12.10. Its SHA256 is
`0352298641fc2199fa50057e6683829cb91a4fb658e18e4da3829e2a319164d0`.
Windows-specific patches enable the shared harness, fix a pointer move to use
`nullptr`, and export the public snapshot methods required by the harness.
`Synchronization.lib` supplies Rust's Windows synchronization imports.

The DLL depends on the Microsoft VC runtime (`MSVCP140`, `VCRUNTIME140` and
`VCRUNTIME140_1`) and Windows system libraries. Consumers require the Microsoft
Visual C++ x64 Redistributable. Tests run with `CETCompat=false`; Windows debugger,
CET, third-party napi-rs addon loading and older Windows deployment baselines
remain outside this verification result.
