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

:warning: _**Status: Development preview** — Linux x64 is built and tested locally.
Windows and macOS builds are not yet validated. No official NuGet package has
been published._

### Documentation

- [Build, test and package instructions](README-DEV.md)
- [Native source and embedding port](native/libnode/README.md)
- [Compatibility results and platform requirements](docs/local-verification.md)
- [Audit of 66 open upstream issues](docs/upstream-issue-audit.md)
- [Releases and NuGet publishing](docs/releases.md)
- [Node API for .NET documentation](https://microsoft.github.io/node-api-dotnet)

### Minimal example — .NET calling JavaScript

Reference `Microsoft.JavaScript.NodeApi` 0.9.36 and use the compiled native library:

```csharp
using Microsoft.JavaScript.NodeApi;
using Microsoft.JavaScript.NodeApi.Runtime;

using var platform = new NodeEmbeddingPlatform(new NodeEmbeddingPlatformSettings
{
    LibNodePath = Path.GetFullPath("libnode.so") // Linux; use the native library for your platform.
});
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
- `Jazor.LibNode.<rid>` — platform library under `runtimes/<rid>/native/`, with source/build metadata and licenses.

The Linux development candidate passes **60 native embedding cases**, **22 Microsoft
embedding/GC tests**, **38 separate mock tests**, and an isolated consumer using the
actual Microsoft NodeApi NuGet package. The local binary requires **glibc 2.39** and
**GLIBCXX 3.4.32**; older distributions need a separate build and validation.

Candidate packages are generated under `artifacts/packages/`;
see [development instructions](README-DEV.md) to reproduce them.

## Development

For building, testing and packaging, see [README-DEV.md](README-DEV.md).
The Linux candidate workflow repeats the native build, upstream compatibility tests,
NuGet consumer and issue regressions, and preserves their reports.
Release tags publish the verified package set through GitHub Actions, using NuGet
trusted publishing. See [the release guide](docs/releases.md) for setup and dry runs.

## License

Build and packaging code is licensed under the [MIT License](LICENSE.txt).
Native distributions include Node.js and embedding-source licenses and third-party
notices. See [NOTICE.txt](NOTICE.txt) for source attribution.

This is an independent build, not a Microsoft release.
