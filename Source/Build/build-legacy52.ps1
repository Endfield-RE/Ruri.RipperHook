param(
    [switch]$PrivateHook,
    [Parameter(Mandatory=$true)][string]$DependencySnapshot,
    [string]$PackageSource = 'https://api.nuget.org/v3/index.json',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$Profile = if ($PrivateHook) { 'private' } else { 'public' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $Root "Source/0Bins/legacy52-$Profile" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$DependencySnapshot = (Resolve-Path -LiteralPath $DependencySnapshot).Path
if (-not $PrivateHook) {
    foreach ($path in @('Source/Ruri.GameHook', 'Source/Endfield-GameHook')) {
        if (Test-Path -LiteralPath (Join-Path $Root $path)) {
            if (@(Get-ChildItem -LiteralPath (Join-Path $Root $path) -File -Recurse -Force).Count) {
                throw 'Public validation requires an empty private source tree.'
            }
        }
    }
}
$args = @('-c', 'Release', '--nologo', "-p:RestoreSources=$PackageSource",
    "-p:RestorePackagesPath=$OutputDirectory/packages", '-p:NuGetAudit=false',
    "-p:PureRelease=$((-not $PrivateHook.IsPresent).ToString().ToLowerInvariant())",
    "-p:UseEndfieldGameHook=$($PrivateHook.IsPresent.ToString().ToLowerInvariant())",
    "-p:RuriDeps=$DependencySnapshot", "-p:DirectoryBuildTargetsPath=$PSScriptRoot/Legacy52Dependencies.targets",
    "-p:OutputPath=$OutputDirectory/bin/", "-p:BaseIntermediateOutputPath=$OutputDirectory/obj/",
    "-p:IntermediateOutputPath=$OutputDirectory/obj/Release/")
& dotnet build "$Root/Source/Ruri.RipperHook/Ruri.RipperHook.csproj" @args
if ($LASTEXITCODE -ne 0) { throw 'Legacy kernel build failed' }
Write-Host "LEGACY52_BUILD_PASS profile=$Profile bin=$OutputDirectory/bin"
