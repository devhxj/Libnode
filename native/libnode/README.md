# Native source and port

`source.lock.json` fixes the official LTS archive, matching NodeApi source/tests,
embedding fork, local port patch, and consulted build reference. The official
archive was downloaded and its SHA256 verified on 2026-11-17.

The fork adds 15 commits beyond Microsoft's recorded Node source baseline. The
current port adopts the embedding API files, event-loop/scope helpers and native
embedding cases. It does not adopt unrelated formatting, BinSkim, ngtcp2 or old
MSVC changes. The exact original comparison is downloaded with a locked hash.

Port adaptations for Node 26:

- Use `node_napi_env__::New`, replacing the old fork's exposed `v8impl::NewEnv`.
- Keep explicit Node-API scope open/close support and non-cleanup event-loop runs.
- Initialize cppgc with the selected V8 platform's page allocator and release it
  before disposing that platform.
- Validate the requested Node-API version against `NODE_API_SUPPORTED_VERSION_MAX`.
- Use `UV_RUN_NOWAIT` in `RunEventLoopNoWait`; the old fork called `UV_RUN_ONCE`.
- Build the existing C/C++ embedding cases and dispatch through their original
  harness. Adapt the official Node 26 C++ example's entry point, preserving its
  body, rather than replacing it with the old Node 20 example.
- Restore the fork's exact `test/fixtures/snapshot/create-worker-and-vm.js`, which
  its snapshot assertions require and the Node 26 source archive does not contain.
- On Linux, promote the exact loaded shared library to `RTLD_GLOBAL` during
  platform initialization so Node-API addons can resolve `napi_*`. `RTLD_NOLOAD`
  prevents loading another library; the platform retains the extra loader handle
  until teardown. See [the upstream issue audit](../../docs/upstream-issue-audit.md).

The patch is an implementation candidate. Enum values, callback signatures,
ownership and binary behavior must still be verified through the original tests
and actual managed NuGet package. The embedding API remains experimental upstream.

| RID | Current decision |
| --- | --- |
| win-x64 | First planned acceptance target; MSVC build/run pending |
| linux-x64 | Initial local development build; glibc minimum not yet established |
| osx-x64 | Candidate; native Intel build/run pending; upstream minimum macOS 13.5 |
| osx-arm64 | Candidate; Apple Silicon build/run pending; upstream minimum macOS 13.5 |
| win-arm64, linux-arm64 | Existing Microsoft assets; migration assessment pending |
| win-x86 | Not in Node 26's supported-platform table; explicit migration decision needed |
| musl / others | Outside the initial acceptance scope |

Seven old native asset directories do not constitute seven validated platforms.
