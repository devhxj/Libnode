# SigmaGmbH build reference

Reviewed [SigmaGmbH/dotnet-libnode-docker](https://github.com/SigmaGmbH/dotnet-libnode-docker/tree/671604d5adc6fef3e1686fdd84a13499b292eb85)
on 2026-11-17, following the project owner's suggestion.

| Aspect | Reference | Jazor.LibNode decision |
| --- | --- | --- |
| Native source | `jasongin/nodejs`, `napi-libnode-v20.9.0`, Nix content hash | Official Node 26.13.1 archive and a separately pinned embedding port |
| Managed layer | NodeApi 0.7.30, `NodejsPlatform` API | NodeApi 0.9.36 and its matching source/test commit |
| Build | Nix, GCC 11, `--shared --without-npm --without-corepack` | Upstream build system via C#; Node 26 compiler requirements; Corepack configure flag is no longer accepted |
| Linux runtime | `libnode.so.115` copied into Docker image | NuGet native asset named `libnode.so`; track actual SONAME and dynamic dependencies |
| Platforms | `x86_64-linux` only | Four acceptance candidates, each needing independent evidence |
| Verification | Example managed function/Promise calls | Reused native and Microsoft embedding/GC suites plus an actual NuGet consumer |
| libc | README reports GLIBC errors with the Microsoft ASP.NET image | Record linked GLIBC/GLIBCXX requirements and test the intended minimum userspace |
| Licensing | No repository license identified by GitHub API | Reference ideas and commands; do not copy the implementation |

The example confirms that shared Node builds can host Microsoft's managed layer.
It does not demonstrate Node 26 support or the ABI expected by NodeApi 0.9.36.
Its fixed source and Nix dependency lock are useful precedents for traceable input
selection. Its Docker runtime-image warning is concrete evidence that successful
compilation does not establish compatibility with a different libc baseline.

No Nix, Docker publication, npm CLI setup or registry credentials are needed in
the maintained Jazor tooling path. A controlled Linux build image can be added
later after the deployment baseline is chosen and tested.
