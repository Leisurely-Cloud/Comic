param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj"
$publishRoot = Join-Path $repoRoot "publish"
$frontendOutput = Join-Path $publishRoot "frontend"
$binRoot = Join-Path $repoRoot "app\frontend-winui\src\Comic.WinUI\bin\$Configuration\net9.0-windows10.0.26100.0\$Runtime"

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained false -p:WindowsAppSDKSelfContained=false -o $frontendOutput
if ($LASTEXITCODE -ne 0) {
    throw "WinUI publish failed with exit code $LASTEXITCODE"
}

foreach ($directoryName in @("Assets", "Views", "Controls")) {
    $source = Join-Path $binRoot $directoryName
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $frontendOutput $directoryName) -Recurse -Force
    }
}

Get-ChildItem -LiteralPath $binRoot -Filter "*.xbf" -File -ErrorAction SilentlyContinue |
    Copy-Item -Destination $frontendOutput -Force

$priFile = Join-Path $binRoot "Comic.WinUI.pri"
if (Test-Path -LiteralPath $priFile) {
    Copy-Item -LiteralPath $priFile -Destination $frontendOutput -Force
}

$unexpectedRuntimeFiles = @(
    "coreclr.dll",
    "hostfxr.dll",
    "Microsoft.UI.Xaml.dll"
) | Where-Object { Test-Path -LiteralPath (Join-Path $frontendOutput $_) }
if ($unexpectedRuntimeFiles.Count -gt 0) {
    throw "Framework-dependent publish unexpectedly contains: $($unexpectedRuntimeFiles -join ', ')"
}

$sizeBytes = (Get-ChildItem -LiteralPath $frontendOutput -File -Recurse | Measure-Object -Property Length -Sum).Sum
$sizeMiB = [Math]::Round($sizeBytes / 1MB, 2)
Write-Host "Published framework-dependent C# app to $frontendOutput ($sizeMiB MiB)"
Write-Host "Target machine requirements: .NET 9 x64 Runtime and Windows App Runtime 1.8 x64"
