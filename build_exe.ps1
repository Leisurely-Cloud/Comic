$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Building Manga Downloader EXE" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$pyInstallerCommand = $null
$pyInstallerArgs = @()
$venvPyInstaller = Join-Path $PSScriptRoot ".venv\Scripts\pyinstaller.exe"

if (Get-Command pyinstaller -ErrorAction SilentlyContinue) {
    $pyInstallerCommand = "pyinstaller"
}
elseif (Test-Path $venvPyInstaller) {
    $pyInstallerCommand = $venvPyInstaller
}
elseif (Get-Command python -ErrorAction SilentlyContinue) {
    & python -m PyInstaller --version *> $null
    if ($LASTEXITCODE -eq 0) {
        $pyInstallerCommand = "python"
        $pyInstallerArgs = @("-m", "PyInstaller")
    }
}

if (-not $pyInstallerCommand) {
    Write-Host "[ERROR] pyinstaller was not found." -ForegroundColor Red
    Write-Host "Please run one of the following commands first:" -ForegroundColor Yellow
    Write-Host "  pip install pyinstaller"
    Write-Host "  python -m pip install pyinstaller"
    Write-Host "  .\\.venv\\Scripts\\python.exe -m pip install pyinstaller"
    exit 1
}

$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$workRoot = Join-Path $PSScriptRoot "build_pyinstaller"
$distRoot = Join-Path $PSScriptRoot "dist_build"
$workDir = Join-Path $workRoot $runId
$distDir = Join-Path $distRoot $runId

New-Item -ItemType Directory -Path $workDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Write-Host ""
Write-Host "[1/2] Running PyInstaller..." -ForegroundColor Cyan
& $pyInstallerCommand @pyInstallerArgs --clean --noconfirm --workpath $workDir --distpath $distDir comic_gui.spec
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[ERROR] Build failed. Please check the PyInstaller output above." -ForegroundColor Red
    exit $LASTEXITCODE
}

$builtExe = Join-Path $distDir "comic-downloader.exe"
$finalExe = Join-Path $distDir "漫画下载器.exe"
if (Test-Path $builtExe) {
    if (Test-Path $finalExe) {
        Remove-Item -LiteralPath $finalExe -Force -ErrorAction SilentlyContinue
    }
    Move-Item -LiteralPath $builtExe -Destination $finalExe -Force
}

Write-Host ""
Write-Host "[2/2] Build complete." -ForegroundColor Green
Write-Host "Output folder: $distDir"
Write-Host "EXE file: $finalExe"
