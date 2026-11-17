# Linux development candidate — 2026-11-17

The candidate was built locally on Ubuntu 26.04 x64 with GCC/G++ 15.2.0, Make
4.4.1, Rust/Cargo 1.100.0 and .NET SDK 11.0.100. Source/patch inputs are fixed in
[`source.lock.json`](../native/libnode/source.lock.json). This is local development
evidence; no official package has been published or Jazor dependency migrated.

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
| NuGet package contents | Native library, build/verification record, README, Jazor license, Node and embedding licenses, NOTICE; no host assembly |
| Missing native/RID assets | Packaging fails explicitly |

Runtime identity from the actual NuGet consumer:

| Field | Value |
| --- | --- |
| Node | `v26.13.1` |
| V8 | `14.6.202.34-node.40` |
| Node-API | `10` |
| Module ABI | `147` |
| Temporal | Present (`object`) |
| Candidate SHA256 | `0871e715a25fc1c586a08fa8cb6259012ed3a1b1f9b1720afd5d07890e498dca` |

The local package is
`artifacts/packages/Jazor.LibNode.linux-x64.0.0.0-local.0871e715a25f.nupkg`.
Detailed results, TRX files, loaded-library identity and logs live in
`artifacts/verification/`; the build record is
`artifacts/native/linux-x64/native-build.json`. These generated files are ignored
by Git and collected by the candidate CI lane.

The binary imports symbols up to **GLIBC 2.39**, **GLIBCXX 3.4.32** and
**CXXABI 1.3.11**. Its measured dependencies are libc, libm, libgcc_s, libstdc++,
the ELF loader and linux-vdso. No dynamic libatomic dependency appeared in this
build. This does not establish a minimum tested deployment distribution: a
controlled older build userspace and an actual runtime test are still needed.

Remaining issue #139 acceptance work:

- Build and run the Windows x64 candidate and both native macOS architectures.
- Choose and verify the Linux deployment baseline; test native addon loading.
- Decide migration for the old `win-arm64`, `linux-arm64` and `win-x86` assets.
- Validate target RID versus build-host RID, including cross-publish consumers.
- Run Jazor pnpm/Vite/WebIDL/SSR, browser/runtime and official release gates.
- Verify a subsequent LTS patch update using the same recipe.

The test adapter adaptation updates test SDK/runner versions for .NET 11. The
native fixture adaptation restores `snapshot/create-worker-and-vm.js` from the
pinned fork; the old driver required it and the Node 26 archive omits it. Both
adaptations preserve the upstream assertions and are reviewable in `patches/`.
