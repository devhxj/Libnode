# Port provenance and review

The original comparison is `artifacts/inputs/embedding/upstream.diff`, verified
against `source.lock.json`. It compares `53a57efd83a18efe7a84bf1b460acc789139939b`
with `3752ce263fab860cb144b7488ee2123fe92614c9` in `vmoroz/nodejs-node`.

| Commit | Original change | Port decision |
| --- | --- | --- |
| `9ac7e3987e52d037e1521b0d7ef5a8be60df0c01` | BinSkim changes | Exclude old security/build flags and unrelated dependency edits; Windows recipe still needs independent review |
| `ddd891813d29fa3c072b1be0fff12a406e8970b0` | Indentation | Exclude unrelated formatting |
| `c9eca4488c105b3606a10432303b63f7add3fcbf` | BinSkim batch entry | Exclude |
| `87f75dd1548d2b5a09fc8605b1a3885ecfef81c6` | VS 17.11 patch | Exclude old compiler workaround; use Node 26 platform settings |
| `5f7de4b618ea7a46b0e696be593a9e5fbc135172` | ARM64 CET setting | Defer to Windows ARM64 migration decision |
| `3bfcac350963af1cf6f68eb76ea3e6c0fea732da` | DLL compilation | Defer Windows build changes until MSVC candidate; Linux recipe needs none |
| `b98281d5a2c749b375cbe88eeca058d705185645` | Embedding C API | Adopt final API/implementation and required scope/event-loop helpers |
| `0d26333dcc05d4bcea532cc83a455f3291134c59` | Whitespace | Keep source-file formatting where adopted; no repository-wide formatting |
| `6591cd46a5dcc9ab44a5d33a866aefd86565c4c1` | macOS/Linux compilation | Use final embedding source; verify with current platform compilers |
| `661b23f6a9dd66124d8c74b78bdaac1b232a38c2` | Linux compilation | Use final embedding source; verify with current Linux compiler |
| `6aa641a9045143073fa901af006a6b4b4cba2fb5` | API/base update | Adopt final embedding API, not its old Node base |
| `0594e0ce4281bf11877661bdc94b8441b8f805d7` | Test type prefixes | Preserve final native tests and their declarations |
| `ad7e59e3addf6f08cc51f9233d18184a5aff9968` | Warning fixes | Preserve final adopted source; do not suppress new port errors |
| `6417c89936ced7e321ff9cdff44ae9c0d10f7c4f` | Remove old tests | Adopt maintained final harness/fixtures |
| `3752ce263fab860cb144b7488ee2123fe92614c9` | User data and format | Preserve final user-data ownership ABI and callbacks |

`0001-node26-embedding.patch` is a combined patch against the official Node 26
archive. It retains the original native C/C++ cases and adds their JS driver as
`test-embedding-c-api.js`, alongside Node 26's driver. The C# verifier launches
the native embedding harness to execute this driver; it does not launch Node CLI.
The driver keeps upstream assertions, fixture paths and child-harness checks.
Its `snapshot/create-worker-and-vm.js` fixture is included byte-for-byte from the
same pinned fork because Node 26's source archive no longer contains that file.

Node 26 changes are described in [the native README](../README.md). Original
Node 26 C++ embedding behavior is kept, with its entry point adapted to the shared
test dispatcher. Failed hunks that only reformatted the old environment destructor
are omitted; the Node 26 destructor is retained.

`nodeapi-tests.patch` changes only SDK/test-runner integration, native dependency
selection and the candidate path. The xUnit assertions and all fixture source
remain unchanged. The runner updates are necessary because the old 2.4.5 adapter
discovered names but matched zero filtered tests under .NET 11; adapter 4.0.1 and
test SDK 18.12.0 correctly discover the 22 embedding/GC cases.

The original Microsoft native packaging recipe remains unconfirmed. This project
owns explicit NuGet packaging around the upstream Node build; it does not claim
to reproduce Microsoft's old native binary or its undocumented build inputs.

`0002-linux-addon-symbols.patch` fixes a reproduced Linux addon failure related
to Microsoft issue #426. .NET's local loader scope hid `napi_*` from a conventional
Node-API addon. Platform initialization promotes the exact loaded library with
`RTLD_NOLOAD | RTLD_GLOBAL`; platform teardown releases its extra loader handle.
Both a C addon and a pinned Rolldown napi-rs addon execute successfully after the
patch, without `LD_PRELOAD` or host-side P/Invoke workarounds. This verifies Linux
behavior; it does not claim to fix #426's Windows executable-export lookup.
