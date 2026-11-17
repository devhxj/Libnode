# Open upstream issue audit — 2026-11-17

Reviewed all **66 open issues** and their available comments in
[microsoft/node-api-dotnet](https://github.com/microsoft/node-api-dotnet/issues).
The count excludes pull requests. This is a snapshot: an open issue can describe
an old version, a resolved case, a feature request, or the opposite hosting direction.
The selected managed package remains **0.9.36**, source revision
`0483aa01a76e88ccb8f9de0b3a89202b538ae41e`; it is not forked or patched by this work.

## Confirmed native fix

A conventional Linux Node-API addon failed before the fix with **exit 127** and
`undefined symbol: napi_create_int32`. It works when the same old library is
preloaded globally, confirming loader scope as the cause. .NET loads native assets
with `RTLD_LOCAL`, whereas addons expect Node-API symbols in global lookup scope.

`0002-linux-addon-symbols.patch` promotes the exact already-loaded library during
platform initialization using `dladdr` and `RTLD_NOLOAD | RTLD_GLOBAL`. It holds
that loader reference until the platform is torn down. It does not load a second
Node instance or require application P/Invoke code.

After rebuilding, the C addon loads, returns 42, and throws a real typed Node-API
TypeError. A pinned **@rolldown/binding-linux-x64-gnu 1.3.2** Rust/napi-rs addon
also loads, minifies `globalThis.result = 21 * 2`, and executes the result as 42.
The fixture archive is verified by SHA512. This is measured Linux evidence related
to [#426](https://github.com/microsoft/node-api-dotnet/issues/426); the original
Windows napi-rs lookup of exports in the executable remains unverified.

## Reproduced managed limitations

- **#355:** `NodeEmbeddingThreadRuntime.Dispose()` hangs with a live `setInterval`.
  The runner kills the isolated repro after 10 seconds. Clearing the interval or
  using `NodeEmbeddingRuntime.TerminateEventLoop()` on its JS thread exits cleanly.
  Native event-loop polling/termination works; the managed convenience disposer
  unreferences only its own completion handle before joining the thread.
- **#464:** both linked-module routes abort with the selected NuGet package.
  The failures identify `ModuleInitializeCallbackAdapter` creating a Root scope
  within another scope, or a later callback with no parent scope. Reused native
  linked-module tests pass. Fixing the managed adapter requires an upstream managed
  change; no native workaround or test-assertion modification conceals the crash.
- **#509:** `JSError.ThrowTypeError(string)` returns JS `Error`. The selected
  managed source calls `ThrowError`, while `napi_throw_type_error` from the C addon
  produces the correct `TypeError` against the same native library.

These remain known failures, not compatibility passes. #505/#486 load CLR inside
Node workers, a different ownership direction from a .NET process embedding Node;
pure JS worker teardown cannot establish that those reports are fixed.

## Reproducible checks

```text
dotnet run --file scripts/csharp/build-libnode.cs -- --jobs 8
dotnet run --file scripts/csharp/verify-libnode.cs
dotnet run --file scripts/csharp/verify-upstream-issues.cs
```

The issue runner executes 14 isolated processes with deadlines: concurrent default
and configured environments, pending-timer disposal, cleared-timer disposal,
explicit termination, retained references, two linked-module paths, managed typed
errors, C addon calls, native typed errors, Rolldown minification, nonblocking event
loop polling, and 20 sequential Worker startups/terminations. The four known
managed failure scenarios are checked explicitly and reported separately. All ten
other scenarios must succeed; an unexpected result fails the command.

`artifacts/upstream-issues/results.json` records the exact candidate hash, fixture
identity, exit codes, timeout flags and elapsed time. Per-case logs are retained.
CI runs this command and uploads its evidence. The full compatibility gate still
requires 60 native cases, 22 real Microsoft embedding/GC cases, 38 separately
identified mock tests, and the actual NuGet consumer.

## All reviewed issues

“Managed” identifies the code layer from the report/source review; it is not a
claim that every managed failure was reproduced. “Unverified” means the original
platform/application scenario has not run here. Linux results do not establish
Windows CET, debugger, macOS, Electron, reverse-host or arbitrary npm compatibility.

| Issue | Classification | Assessment |
| --- | --- | --- |
| [#509 — `JSError.ThrowTypeError` / `ThrowRangeError` / `ThrowSyntaxError` throw a plain `Error`](https://github.com/microsoft/node-api-dotnet/issues/509) | Managed — reproduced | String helper calls ThrowError rather than ThrowTypeError; C addon gets a real TypeError from the same native library. |
| [#505 — JSError.ThrowError(Exception) aborts the process when a Worker terminates during a .NET call](https://github.com/microsoft/node-api-dotnet/issues/505) | Managed/reverse-host — not reproduced | Report concerns JSError during CLR-in-Worker teardown; keeping the unmodified NodeApi package. Pure JS worker termination passes, which does not prove this case. |
| [#504 — dependency resolution on a non-JS thread fails with 0x80131509 instead of falling back](https://github.com/microsoft/node-api-dotnet/issues/504) | Managed host | Assembly resolution emits JS notifications on a non-JS thread; no native source change indicated. |
| [#502 — IDisposable: typings say `dispose()`, dynamic binding exposes `Dispose()`](https://github.com/microsoft/node-api-dotnet/issues/502) | Managed generator/exporter | Dispose name mismatch is in typings versus TypeExporter. |
| [#486 — SIGSEGV (exit 139) on Node worker_threads teardown after require('node-api-dotnet/net10.0') - pthread TLS destructor use-after-free](https://github.com/microsoft/node-api-dotnet/issues/486) | Reverse-host — not reproduced | TLS destructor report loads CLR from a Node Worker. Our existing-CLR embedding direction does not exercise that teardown. |
| [#484 — C# AOT compilation not setting "dotnet" to undefined](https://github.com/microsoft/node-api-dotnet/issues/484) | Managed MSBuild/generator | Generated AOT loader replacement uses the wrong module string. |
| [#479 — System.Management throws PlatformNotSupportedException under hosted CLR on Windows — breaks Hardware.Info, DeviceId.Windows.Wmi,   etc.](https://github.com/microsoft/node-api-dotnet/issues/479) | Managed/.NET platform | System.Management and assembly load context; Windows-only reproduction unavailable. |
| [#475 — Is it possible to develop vscode extension based on this](https://github.com/microsoft/node-api-dotnet/issues/475) | Usage request | VS Code extension deployment choices, not a native failure report. |
| [#474 — Workload manifest dependency 'Microsoft.NET.Workload.Emscripten.Current' version '9.0.4' is lower than version '9.0.10' required by manifest](https://github.com/microsoft/node-api-dotnet/issues/474) | SDK/workload | Emscripten workload manifest mismatch, outside this native build. |
| [#472 — Add support for the current version of Node](https://github.com/microsoft/node-api-dotnet/issues/472) | Native — covered | All 27 selected managed embedding imports exported; actual Node 26.13.1 NuGet consumer passes. Stock Node binaries still lack this experimental API. |
| [#470 — Support for DotNet 10.0](https://github.com/microsoft/node-api-dotnet/issues/470) | Managed build/runtime | Comments report .NET 10 support resolved; local .NET 11 embedding passes. HTTP/3 native CLR dependency resolution is separate. |
| [#466 — Calling ambiguous overloaded methods](https://github.com/microsoft/node-api-dotnet/issues/466) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#464 — Process Crash trying: .NET -> load ESM module -> loading .NET module](https://github.com/microsoft/node-api-dotnet/issues/464) | Managed — reproduced | Both linked binding paths abort in the NodeApi Root/Callback scope adapter. Native linked-module cases pass; do not claim this fixed. |
| [#461 — Example "aot-module" fails with [ERR_MODULE_NOT_FOUND]: Cannot find package 'node-api-dotnet'](https://github.com/microsoft/node-api-dotnet/issues/461) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#460 — Supporting SSR scenarios](https://github.com/microsoft/node-api-dotnet/issues/460) | Mixed — partial evidence | ESM/errors/workers pass. Nested CLR loading (#330) and Windows debugging (#440) remain separate unverified cases. |
| [#459 — Crash when returning a IntPtr when dynamically invoke .NET APIs](https://github.com/microsoft/node-api-dotnet/issues/459) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#458 — C# records are translated to invalid TypeScript types because of the `<Clone>$` member](https://github.com/microsoft/node-api-dotnet/issues/458) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#456 — The double[][] in .NET is spitting out bad conversion code.](https://github.com/microsoft/node-api-dotnet/issues/456) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#455 — TypeScript definition for ValueType not found](https://github.com/microsoft/node-api-dotnet/issues/455) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#450 — Trying to run NodeWorkers from an ASP.NET Core application](https://github.com/microsoft/node-api-dotnet/issues/450) | Host/platform — partial evidence | Worker lifecycle passes on Linux .NET 11; exact ASP.NET/Windows closed-stream case not reproduced. |
| [#449 — semantic-kernel from v1.18.0 upper cannot build](https://github.com/microsoft/node-api-dotnet/issues/449) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#444 — JSValue returned by Import() don't survive dependency injection](https://github.com/microsoft/node-api-dotnet/issues/444) | Usage/lifetime — not reproduced | Strong JSReference survives separate JS callbacks in the same runtime; keep values on their owning JS thread. |
| [#443 — Refactor embedding API and code to separate assembly](https://github.com/microsoft/node-api-dotnet/issues/443) | API organization proposal | Moving embedding types to a managed assembly does not change the native package. |
| [#440 — jsdom blows up on net 9 but works on net 8](https://github.com/microsoft/node-api-dotnet/issues/440) | Windows CET/managed teardown — unverified | Linux startup/disposal passes, but Windows debugging and CET need a Windows runner. Keep CETCompat=false. |
| [#439 — Documentation is wrong and examples don't build](https://github.com/microsoft/node-api-dotnet/issues/439) | Documentation | Our consumer uses NodeEmbeddingPlatformSettings.LibNodePath and builds against the selected actual NuGet API. |
| [#433 — Streams are broken in electron](https://github.com/microsoft/node-api-dotnet/issues/433) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#432 — Non-writeable MemoryStream shows up as Duplex rather than Readable](https://github.com/microsoft/node-api-dotnet/issues/432) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#430 — Generated entry JS file for AOT thinks I'm not using AOT](https://github.com/microsoft/node-api-dotnet/issues/430) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#427 — Multiple NodeJsEnvironments](https://github.com/microsoft/node-api-dotnet/issues/427) | Mixed — partial evidence | Two concurrent managed runtimes and awaited callbacks pass on Linux, both defaults and inspector-disabled settings. Stream/report application not reproduced. |
| [#426 — panic trying to load Vite.createServer()](https://github.com/microsoft/node-api-dotnet/issues/426) | Native — Linux fix verified | RTLD_GLOBAL promotion fixes unresolved napi_* in a C addon; Rolldown 1.3.2 napi-rs minify also passes. Original Windows executable-export lookup remains unverified. |
| [#420 — Error related to global.node_api_dotnet.require when trying to use streams](https://github.com/microsoft/node-api-dotnet/issues/420) | Managed initialization/usage | Module imports need createRequire/base directory; the actual NuGet consumer configures these explicitly. No native patch to managed globals. |
| [#414 — How do I export a C# function with a `JSValue` argument?](https://github.com/microsoft/node-api-dotnet/issues/414) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#412 — Does Memory<T> type work for dynamic JS->.NET invocation?](https://github.com/microsoft/node-api-dotnet/issues/412) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#411 — AOT Build failing at following command dotnet publish](https://github.com/microsoft/node-api-dotnet/issues/411) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#409 — Version upgrade 0.7.6-> 0.7.7 breaks `dotnet build`](https://github.com/microsoft/node-api-dotnet/issues/409) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#408 — Feature Request: Option to Generate TypeScript as export interface or export type](https://github.com/microsoft/node-api-dotnet/issues/408) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#407 — while building node-api-dotnet project using electron-builder using for process architecture ia32 receiving error.](https://github.com/microsoft/node-api-dotnet/issues/407) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#406 — `node-api-dotnet-generator` v0.8.19 misses net8.0 artifacts](https://github.com/microsoft/node-api-dotnet/issues/406) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#399 — Provide a way to eagerly marshal (snapshot) all properties to js object.](https://github.com/microsoft/node-api-dotnet/issues/399) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#398 — Unable to resolve native runtime dlls](https://github.com/microsoft/node-api-dotnet/issues/398) | Managed host/dependency resolution | EFCore/e_sqlite3 and IntPtr marshalling concern CLR loading/export, not libnode native asset selection. |
| [#397 — A possible vite plugin to make this work with electron.](https://github.com/microsoft/node-api-dotnet/issues/397) | Electron/Vite bundling | Community integration example; path rewriting of CLR-host package is outside native library implementation. |
| [#393 — Embedded njs calling back into C#](https://github.com/microsoft/node-api-dotnet/issues/393) | Managed host/usage | Two-way callbacks pass upstream tests; importing the reverse-host npm package inside existing CLR is #330, not promised here. |
| [#391 — Compile error 'CS8078' when a single class has many export properties](https://github.com/microsoft/node-api-dotnet/issues/391) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#386 — Report the full .NET exception stack trace in fatal errors](https://github.com/microsoft/node-api-dotnet/issues/386) | Managed exception policy | JSThreadSafeFunction diagnostic text/async-void handling belongs to the managed package. |
| [#383 — `Memory<T>` to `TypedArray<T>` on Electron 19.1.9 Worked fine. But did not worked on higher Electron](https://github.com/microsoft/node-api-dotnet/issues/383) | Electron platform policy | Electron prohibits external buffers. This package builds Node, not Electron; no change to Electron memory policy. |
| [#381 — How to accomplish two way communication between .NET and JS?](https://github.com/microsoft/node-api-dotnet/issues/381) | Managed host/usage | Callback creation is supported; nested npm CLR loader is #330. Do not infer reverse-host support. |
| [#366 — How to make this work in powershell?](https://github.com/microsoft/node-api-dotnet/issues/366) | PowerShell/thread affinity | Comments identify missing runspace on dedicated JS thread. Native caller-thread API exists; no PowerShell integration tested. |
| [#355 — NodejsEnvironment Dispose() is blocked by pending tasks](https://github.com/microsoft/node-api-dotnet/issues/355) | Managed — reproduced | ThreadRuntime.Dispose waits for live interval. Clear interval or use explicit Runtime.TerminateEventLoop on JS thread; both controls pass. |
| [#348 — Cannot create multiple NodeJSEnvironment for one NodeJSPlatform](https://github.com/microsoft/node-api-dotnet/issues/348) | Native/inspector — not reproduced on Linux | Two concurrent managed runtimes pass, including default flags; reused C tests exercise 12 environments. Preserve inspector-disabled settings for portable hosts. |
| [#332 — Path to Microsoft.JavaScript.NodeApi.DotNetHost.dll in electron binary is incorrect](https://github.com/microsoft/node-api-dotnet/issues/332) | Electron packaging | ASAR path resolution belongs to npm/managed host packaging. |
| [#330 — Importing `node-api-dotnet` into node into a dotnet app](https://github.com/microsoft/node-api-dotnet/issues/330) | Managed reverse host — unverified | NativeHost discovers symbols from main executable and may reinitialize CLR. Requires managed host integration; RTLD_GLOBAL alone is not a demonstrated fix. |
| [#316 — Support C# `decimal` type](https://github.com/microsoft/node-api-dotnet/issues/316) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#307 — Support marshalling C# `dynamic` values](https://github.com/microsoft/node-api-dotnet/issues/307) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#298 — Report warning for exported generic types or methods](https://github.com/microsoft/node-api-dotnet/issues/298) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#289 — Support exporting generic types and methods to JS from a .NET module](https://github.com/microsoft/node-api-dotnet/issues/289) | Managed build/generator | Report concerns generated C#/TypeScript, AOT, SDK, managed build or exported API shape; a native library replacement does not fix it. |
| [#262 — Add full support for types/interfaces/delegates without namespaces](https://github.com/microsoft/node-api-dotnet/issues/262) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#243 — Marshalling: Non-generic collections](https://github.com/microsoft/node-api-dotnet/issues/243) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#213 — Unable to find an entry point named "napi_create_platform" in DLL](https://github.com/microsoft/node-api-dotnet/issues/213) | ABI/package selection — covered | Old napi_create_platform ABI is retired; selected 0.9.36 node_embedding_* imports and actual NuGet startup pass. |
| [#205 — Marshalling: Exception subclasses](https://github.com/microsoft/node-api-dotnet/issues/205) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#164 — Web Assembly Support](https://github.com/microsoft/node-api-dotnet/issues/164) | Feature request | Browser/.NET WebAssembly interop outside the native shared Node package. |
| [#107 — Build libnode and publish nuget packages](https://github.com/microsoft/node-api-dotnet/issues/107) | Native delivery — partial | Linux candidate and runtime asset packing pass. Windows/macOS builds, target-RID migration and official publication remain pending. |
| [#90 — JSImportAttribute for easier invocation of JS APIs from C#](https://github.com/microsoft/node-api-dotnet/issues/90) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#80 — Publish nuget & npm packages to ADO](https://github.com/microsoft/node-api-dotnet/issues/80) | Release infrastructure | Upstream ADO signing/npm release backlog; candidate CI and a tag-driven publishing workflow are configured; account trust settings are required. |
| [#64 — Custom marshalling](https://github.com/microsoft/node-api-dotnet/issues/64) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#63 — Marshalling: Fields](https://github.com/microsoft/node-api-dotnet/issues/63) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
| [#59 — Marshalling: Events](https://github.com/microsoft/node-api-dotnet/issues/59) | Managed marshalling/API | Report concerns type conversion, managed API/export shape or a feature request; no native-library root cause demonstrated. |
