# Libnode: Node.js LTS for .NET

This project builds Node.js as a native shared library for embedding JavaScript
in .NET applications, referencing and compatible with
[Microsoft's Node API for .NET](https://github.com/microsoft/node-api-dotnet).

- Build the latest Node.js LTS with the embedding C API required by Microsoft's managed interop layer.
- Load JavaScript packages and call JS APIs in the same process as a .NET application.
- Reuse upstream native and managed tests to verify the compiled library.

The current build is **Node.js v26.13.1 (Lithium LTS)**, verified with
**Microsoft.JavaScript.NodeApi 0.9.36**. Source archives, commits and patches are
pinned; builds do not resolve a floating latest version.

**Version 0.2.0 is published** for Linux and Windows on x64 and ARM64, with native,
managed and actual NuGet consumer tests passing on both platforms. macOS builds
remain future candidates. See the [release](https://github.com/devhxj/Libnode/releases/tag/v0.2.0).

### Documentation

- [Build, test and package instructions](README-DEV.md)
- [Native source and embedding port](native/libnode/README.md)
- [Compatibility results and platform requirements](docs/local-verification.md)
- [Platform and architecture support](docs/platforms.md)
- [Audit of 66 open upstream issues](docs/upstream-issue-audit.md)
- [Releases and NuGet publishing](docs/releases.md)
- [Node API for .NET documentation](https://microsoft.github.io/node-api-dotnet)

### Minimal example — .NET calling JavaScript

Reference `Microsoft.JavaScript.NodeApi` 0.9.36 and `Jazor.LibNode` 0.2.0. The latter
provides the native runtime, serving the same role as `Microsoft.JavaScript.LibNode`.
Reference one native runtime package in the application.

```sh
dotnet add package Microsoft.JavaScript.NodeApi --version 0.9.36
dotnet add package Jazor.LibNode --version 0.2.0
```

NodeApi resolves the appropriate native library from the package automatically:

```csharp
using Microsoft.JavaScript.NodeApi;
using Microsoft.JavaScript.NodeApi.Runtime;

using var platform = new NodeEmbeddingPlatform(new NodeEmbeddingPlatformSettings());
using var runtime = platform.CreateThreadRuntime();
runtime.Run(() =>
{
    var answer = (int)JSValue.RunScript("21 * 2");
    Console.WriteLine(answer); // 42
});
```

Create one platform per process. Dispose its runtimes before disposing the platform.
For module imports, async calls and errors, see the tested
[NuGet consumer](tests/NuGetConsumer/Program.cs).

## Packages

The package projects produce native runtime assets without adding another managed
interop layer:

- `Jazor.LibNode` — metapackage for the complete set of validated runtime packages.
- `Jazor.LibNode.<rid>` — platform library under `runtimes/<rid>/native/`, with source/build metadata and licenses. Version 0.2.0 supports `linux-x64`, `win-x64`, `linux-arm64` and `win-arm64`.

The Linux development candidate passes **60 native embedding cases**, **22 Microsoft
embedding/GC tests**, **38 separate mock tests**, and an isolated consumer using the
actual Microsoft NodeApi NuGet package. The local binary requires **glibc 2.39** and
**GLIBCXX 3.4.32**; older distributions need a separate build and validation.

Windows x64 passes the same 60 native, 22 embedding/GC and 38 separate mock cases,
plus the actual NodeApi NuGet consumer on Windows Server 2022. Install the Microsoft
Visual C++ 2015–2022 x64 Redistributable for its VC runtime dependencies. The tested
Windows .NET host uses `<CETCompat>false</CETCompat>`; see the
[upstream issue audit](docs/upstream-issue-audit.md) for the managed-layer limitations.

All four platforms also pass 30 Node 26 feature checks through the actual NuGet
consumer, including Temporal, Web APIs, crypto, workers, WebAssembly, TypeScript
and SQLite. The final release metapackage repeats these checks on each platform.

Candidate packages are generated under `artifacts/packages/`;
see [development instructions](README-DEV.md) to reproduce them.

## Development

For building, testing and packaging, see [README-DEV.md](README-DEV.md).
The Linux candidate workflow builds from source or imports a verified local build,
then repeats upstream compatibility tests, NuGet consumers and issue regressions
and preserves their reports. Importing a local build skips V8 compilation.
Release tags publish the verified package set through GitHub Actions, using NuGet
trusted publishing. See [the release guide](docs/releases.md) for setup and dry runs.

## License

Build and packaging code is licensed under the [MIT License](LICENSE.txt).
Native distributions include Node.js and embedding-source licenses and third-party
notices. See [NOTICE.txt](NOTICE.txt) for source attribution.

This is an independent build, not a Microsoft release.
