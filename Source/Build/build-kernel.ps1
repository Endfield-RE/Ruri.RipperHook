param(
    [switch]$PrivateHook,
    [string]$DependencySnapshot,
    [string]$PackageSource = 'https://api.nuget.org/v3/index.json',
    [string]$OutputDirectory,
    [string]$DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$Profile = if ($PrivateHook) { 'private' } else { 'public' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $Root "Source/0Bins/$Profile" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $DependencySnapshot -and $PrivateHook) {
    $DependencySnapshot = Join-Path $Root 'Source/Endfield-GameHook/runtime'
}
if (-not $DependencySnapshot) {
    throw 'Supply -DependencySnapshot containing the compatible AssetRipper DLLs. See Source/Build/README.md for the source-build route.'
}
$DependencySnapshot = (Resolve-Path -LiteralPath $DependencySnapshot).Path
$Hook = Join-Path $Root 'Source/Endfield-GameHook'
if ($PrivateHook -and -not (Test-Path -LiteralPath "$Hook/Integration/Statement.targets")) {
    throw 'Initialize Source/Endfield-GameHook first.'
}
if (-not (Test-Path -LiteralPath "$Root/Source/Ruri.ShaderDecompiler/Ruri.ShaderDecompiler.csproj")) {
    throw 'Initialize Source/Ruri.ShaderDecompiler first.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$Common = @('-c','Release','--nologo',"-p:RestoreSources=$PackageSource",
    "-p:RestorePackagesPath=$OutputDirectory/packages",'-p:NuGetAudit=false')
foreach ($Name in @('Ruri.Hook','Ruri.ACL','Ruri.ShaderDecompiler')) {
    & $DotNet build "$Root/Source/$Name/$Name.csproj" @Common `
        "-p:OutputPath=$OutputDirectory/dependencies/" `
        "-p:BaseIntermediateOutputPath=$OutputDirectory/obj/$Name/" `
        "-p:IntermediateOutputPath=$OutputDirectory/obj/$Name/Release/"
    if ($LASTEXITCODE -ne 0) { throw "Dependency build failed: $Name" }
}
& $DotNet build "$Root/Source/Ruri.RipperHook/Ruri.RipperHook.csproj" @Common `
    "-p:PureRelease=$((-not $PrivateHook.IsPresent).ToString().ToLowerInvariant())" `
    "-p:UseEndfieldGameHook=$($PrivateHook.IsPresent.ToString().ToLowerInvariant())" `
    "-p:RuriDeps=$DependencySnapshot" "-p:StatementDeps=$OutputDirectory/dependencies" `
    "-p:DirectoryBuildTargetsPath=$PSScriptRoot/SnapshotDependencies.targets" `
    "-p:OutputPath=$OutputDirectory/bin/" "-p:BaseIntermediateOutputPath=$OutputDirectory/obj/kernel/" `
    "-p:IntermediateOutputPath=$OutputDirectory/obj/kernel/Release/"
if ($LASTEXITCODE -ne 0) { throw 'Kernel build failed' }
$RuntimeConfig = '{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}'
[IO.File]::WriteAllText("$OutputDirectory/bin/Ruri.RipperHook.CLI.runtimeconfig.json", $RuntimeConfig)
Write-Host "KERNEL_BUILD_PASS profile=$Profile bin=$OutputDirectory/bin"
