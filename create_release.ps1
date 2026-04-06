$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$versionFile = Join-Path $PSScriptRoot "version_info.txt"
$releaseRoot = Join-Path $PSScriptRoot "release"
$version = "2.0.1"

if (Test-Path $versionFile) {
    $versionContent = Get-Content -LiteralPath $versionFile -Raw
    $versionMatch = [regex]::Match($versionContent, "StringStruct\('ProductVersion', '([^']+)'\)")
    if ($versionMatch.Success) {
        $version = $versionMatch.Groups[1].Value
    }
}

$releaseDirName = "漫画下载器-v$version"
$releaseDir = Join-Path $releaseRoot $releaseDirName
$targetExe = Join-Path $releaseDir "漫画下载器.exe"
$readmeFile = Join-Path $releaseDir "使用说明.txt"
$zipName = "comic-downloader-v$version-windows.zip"
$zipPath = Join-Path $releaseRoot $zipName
$runId = Get-Date -Format "yyyyMMdd-HHmmss"

$exeNames = @("漫画下载器.exe", "comic-downloader.exe", "漫画下载器_v2.exe")
$exeCandidates = @()

foreach ($searchRoot in @((Join-Path $PSScriptRoot "dist_build"), (Join-Path $PSScriptRoot "dist"))) {
    if (Test-Path $searchRoot) {
        $exeCandidates += Get-ChildItem -LiteralPath $searchRoot -Recurse -File | Where-Object {
            $exeNames -contains $_.Name
        }
    }
}

if (-not $exeCandidates) {
    Write-Host "[ERROR] No packaged EXE was found in dist." -ForegroundColor Red
    Write-Host "Please run .\build_exe.ps1 first."
    exit 1
}

$sourceExe = ($exeCandidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName

if (-not (Test-Path $releaseRoot)) {
    New-Item -ItemType Directory -Path $releaseRoot | Out-Null
}

if (Test-Path $releaseDir) {
    $releaseDir = Join-Path $releaseRoot "$releaseDirName-$runId"
    $targetExe = Join-Path $releaseDir "漫画下载器.exe"
    $readmeFile = Join-Path $releaseDir "使用说明.txt"
}

if (Test-Path $zipPath) {
    $zipName = "comic-downloader-v$version-windows-$runId.zip"
    $zipPath = Join-Path $releaseRoot $zipName
}

New-Item -ItemType Directory -Path $releaseDir | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force

$content = @"
漫画下载器 v$version
==============================

快速使用
1. 双击“漫画下载器.exe”启动程序
2. 在界面中选择站点，或直接粘贴漫画链接
3. 点击“获取信息”确认漫画详情
4. 按需调整并发数、代理等设置
5. 点击“开始下载”

当前 GUI 支持
- 包子漫画：首页发现、搜索、下载
- 拷贝漫画：首页发现、搜索、下载
- 漫画柜：搜索、手动链接下载

补充说明
- 下载内容默认保存在程序所在目录
- 下载完成后可直接打包 ZIP，也可在本地漫画库导出 CBZ
- 支持详情页链接、目录页链接和首页发现列表下载
- 如网络不稳定，可填写 HTTP/HTTPS/SOCKS5 代理并测试连接
- 可在程序内暂停、恢复或停止当前任务
- 如被安全软件拦截，请手动加入信任

版本
- 当前版本: v$version
- 打包时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@

Set-Content -LiteralPath $readmeFile -Value $content -Encoding UTF8
Compress-Archive -LiteralPath $releaseDir -DestinationPath $zipPath -Force

Write-Host "[OK] Release folder created." -ForegroundColor Green
Write-Host "Path: $releaseDir"
Write-Host "EXE : $targetExe"
Write-Host "ZIP : $zipPath"
