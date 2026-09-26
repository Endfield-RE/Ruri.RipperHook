param(
    [switch]$PrivateHook,
    [string]$DependencySnapshot,
    [string]$PackageSource = 'https://api.nuget.org/v3/index.json',
    [string]$OutputDirectory
)
if (-not $DependencySnapshot -and $PrivateHook) {
    $DependencySnapshot = Join-Path $PSScriptRoot '../Endfield-GameHook/runtime'
}
& (Join-Path $PSScriptRoot 'build-legacy52.ps1') -PrivateHook:$PrivateHook `
    -DependencySnapshot $DependencySnapshot -PackageSource $PackageSource -OutputDirectory $OutputDirectory
