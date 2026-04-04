$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$releaseRoot = Join-Path $PSScriptRoot "release"
$releaseDir = Join-Path $releaseRoot "漫画下载器"
$distDir = Join-Path $PSScriptRoot "dist"
$preferredExe = Join-Path $distDir "漫画下载器_v2.exe"
$fallbackExe = Join-Path $distDir "漫画下载器.exe"
$targetExe = Join-Path $releaseDir "漫画下载器.exe"
$readmeFile = Join-Path $releaseDir "使用说明.txt"

if (Test-Path $preferredExe) {
    $sourceExe = $preferredExe
}
elseif (Test-Path $fallbackExe) {
    $sourceExe = $fallbackExe
}
else {
    Write-Host "[ERROR] No packaged EXE was found in dist." -ForegroundColor Red
    Write-Host "Please run .\build_exe.ps1 first."
    exit 1
}

if (Test-Path $releaseDir) {
    Remove-Item -Recurse -Force $releaseDir
}

New-Item -ItemType Directory -Path $releaseDir | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force

$content = @"
漫画下载器
==============================

使用方法
1. 双击“漫画下载器.exe”启动程序
2. 粘贴 baozimh.org 的漫画链接
3. 按需调整并发数、起始章节等设置
4. 点击“开始下载”

说明
- 下载内容默认保存在程序所在目录
- 如果看到旧图标，通常是 Windows 图标缓存未刷新
- 如被安全软件拦截，请手动加入信任

版本
- 打包时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@

Set-Content -LiteralPath $readmeFile -Value $content -Encoding UTF8

Write-Host "[OK] Release folder created." -ForegroundColor Green
Write-Host "Path: $releaseDir"
Write-Host "EXE : $targetExe"
