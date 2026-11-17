# Platform support

The published 0.1.0 packages support `linux-x64` and `win-x64`. The planned 0.2.0
release adds `linux-arm64` and `win-arm64` after native runner verification.
This release line keeps Node 26 and modern .NET. Windows x86 and Linux x86 are
excluded; no older Node or legacy managed host is included.
The runtime packages contain native libraries; an AnyCPU .NET application still
needs a native library matching its process architecture.

| RID | Node 26 upstream status | Libnode status |
| --- | --- | --- |
| linux-x64 | Tier 1 | Published in 0.1.0 |
| win-x64 | Tier 1 | Published in 0.1.0 |
| linux-arm64 | Tier 1 | Native ARM64 build and test lane added; verification pending |
| win-arm64 | Tier 2 | Native ARM64 build and test lane added; verification pending |
| linux-arm | Experimental ARMv7 | Requires separate build and 32-bit .NET runtime testing |
| linux-x86 | Experimental | .NET 11 does not list Linux x86; no supported modern .NET host lane |
| win-x86 | Rejected by Node 26 vcbuild | Requires a separate older Node line or an unsupported port |

Node 22 supports Windows x86, but using it would change JavaScript/V8 behavior
and the embedding port. It must be versioned and tested explicitly. No older
Node binary is included silently in the Node 26 package set.

The ARM64 candidate workflow uses Ubuntu 24.04 ARM and Windows 11 ARM runners.
It checks the binary's ELF/PE machine type, runs the original 60 native cases,
22 Microsoft embedding/GC tests, 38 separately counted managed mock tests, and
the actual NodeApi NuGet consumer. Linux ARM64 additionally runs the 14 issue
regressions with a pinned ARM64 Rolldown addon. Release packaging requires
evidence matching each exact binary and runs the final metapackage consumer on
each platform before publishing.

The next release also requires the consumer's 30 Node 26 feature checks on each
architecture. The original native and Microsoft tests remain separate evidence;
feature checks do not replace them.

Sources: [pinned Node 26 BUILDING.md](https://github.com/nodejs/node/blob/e9c277643b0249c50f6e6de5413be8a88ea11b6a/BUILDING.md),
[Node 26 Windows build restriction](https://github.com/nodejs/node/blob/e9c277643b0249c50f6e6de5413be8a88ea11b6a/vcbuild.bat),
[.NET 11 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/11.0/supported-os.md),
and [Node 22 platform support](https://github.com/nodejs/node/blob/v22.x/BUILDING.md).
