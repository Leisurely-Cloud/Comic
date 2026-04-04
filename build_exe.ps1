$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Building Manga Downloader EXE" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

if (-not (Get-Command pyinstaller -ErrorAction SilentlyContinue)) {
    Write-Host "[ERROR] pyinstaller was not found." -ForegroundColor Red
    Write-Host "Please run one of the following commands first:" -ForegroundColor Yellow
    Write-Host "  pip install pyinstaller"
    Write-Host "  python -m pip install pyinstaller"
    exit 1
}

if (Test-Path build) {
    Remove-Item -Recurse -Force build
}

if (Test-Path dist) {
    Remove-Item -Recurse -Force dist
}

Write-Host ""
Write-Host "[1/2] Running PyInstaller..." -ForegroundColor Cyan
pyinstaller --clean --noconfirm comic_gui.spec
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[ERROR] Build failed. Please check the PyInstaller output above." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "[2/2] Build complete." -ForegroundColor Green
Write-Host "Output folder: $PSScriptRoot\dist"
Write-Host "EXE file: $PSScriptRoot\dist\漫画下载器.exe"
