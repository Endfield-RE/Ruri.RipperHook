# Public kernel and optional private source adapter

```
KawakazeNotFound/RuriRipperImporter (public host)
  Ruri.RipperHook -> KawakazeNotFound/Ruri.RipperHook (public kernel)
    Source/Endfield-GameHook -> KawakazeNotFound/Endfield-GameHook (private)
```

The private repository is a pinned source submodule, not an independent loaded
assembly. Public history contains its URL and Git commit ID, not its files.
The original upstream `Source/Ruri.GameHook` gitlink is retained for upstream
compatibility; this fork's private kernel profile does not compile that adapter.
Initialize only the submodules needed for the selected profile, not all private
upstream dependencies recursively.

## Tested snapshot build (.NET 10, Windows)

```powershell
git submodule update --init Source/Ruri.ShaderDecompiler
git submodule update --init Source/Endfield-GameHook
./Source/Build/build-kernel.ps1 -PrivateHook
```

The private profile requires private repository access. It compiles kernel, hook
utilities, ACL and ShaderDecompiler from source against the compatible AssetRipper
snapshot in the private module's `runtime`. Use `-PackageSource <local-nuget-feed>`
for offline restore, or the default NuGet service. Provision packages before
disconnecting. Outputs default to `Source/0Bins/private/bin`; point the host's
**Bin Dir** there after validation. Installed applications and the existing
`statement-runtime` snapshot are not overwritten.

Public-only compilation accepts an independently supplied dependency snapshot:

```powershell
./Source/Build/build-kernel.ps1 -DependencySnapshot <compatible-AssetRipper-DLL-directory>
```

Its default output is `Source/0Bins/public/bin`, distinct from private builds.
No private submodule is required for public compilation. Dependency binaries are
not part of this public repository; a snapshot input is not a public distribution
license. Keep private builds and private modules out of public release packages.

## Upstream source-build route

The upstream project references remain intact. With the necessary public source
submodules initialized, the kernel project can be built with
`-p:PureRelease=true`; the private profile instead uses
`-p:UseEndfieldGameHook=true` (without PureRelease). Full upstream dependency
rebuilds are separate from the tested snapshot route above.

Combining `PureRelease=true` and `UseEndfieldGameHook=true` is a build error.
Private sources and their type-tree resource are excluded from PureRelease.
Adapter-specific profile recognition and container decoding live in the private
module; this kernel exposes generic decoding/profile callbacks and mathematical
solvers. The adapter registers and clears callbacks with its session lifecycle.

Preserve upstream licenses. Repository privacy is an access boundary, not a
replacement for license obligations.
