# Public legacy backend — Blender 5.2

Branch `codex/blender-5.2` exposes the legacy
`Ruri.RipperHook.Bridge.RipperBlenderBridge` ABI, not Statement.
Generic kernel/host sources derive from the tested `d6b8534` snapshot;
private game files and decoder are excluded from public history.
Matching old public shader-decompiler sources are vendored with their license.

`Source/Endfield-GameHook` pins the private same-branch adapter. Only its gitlink
is public. Game VFS/decoding/type trees/profile selection stay private.
The public kernel exposes generic callbacks and retains stock behavior by default.

## Builds

- Actions initializes only AssetRipper and source-builds Unity GUI/CLI with
  `PureRelease=true`; rejects private checkouts and audits the resulting assembly.
  Artifact: `RipperHook-Blender52-PureRelease`.
- This Blender branch does not rebuild the unrelated FModel host; use main's
  existing FModel Actions for it. Its source is retained.
- Public snapshot: `Source/Build/build-legacy52.ps1 -DependencySnapshot <DLL directory>`.
- Private snapshot: initialize the pinned private submodule, then use the same
  script with `-PrivateHook -DependencySnapshot Source/Endfield-GameHook/runtime`.
- Add `-PackageSource <offline NuGet feed>` for a provisioned offline build.
- Snapshot builds use supplied dependency binaries; hosted PureRelease instead
  builds its public source dependencies.

Use the private repository's `RuriHookBin-Blender-5.2-Windows-x64` artifact
for the accepted Endfield deployment. Source builds do not replace that locked
runtime automatically. Public CI has no private credentials or runtime payloads.
Blender 5.3/main and its Statement chain remain unchanged.
