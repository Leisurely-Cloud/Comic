# Comic Downloader 打包脚本
# 用法: .\scripts\build-and-package.ps1

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$PublishDir = "$RootDir\publish"
$OutputDir = "$RootDir\installer-output"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Comic Downloader 打包脚本" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# 清理旧的发布目录
if (Test-Path $PublishDir) {
    Write-Host "[1/4] 清理旧的发布目录..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $PublishDir
}

# 发布 .NET 前端（自包含）
Write-Host "[1/4] 发布 .NET 前端..." -ForegroundColor Yellow
dotnet publish "$RootDir\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o "$PublishDir\frontend" `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "前端发布失败!" -ForegroundColor Red
    exit 1
}

# 复制后端代码到发布目录
Write-Host "[2/4] 复制后端代码..." -ForegroundColor Yellow
$BackendDest = "$PublishDir\backend"
if (Test-Path $BackendDest) {
    Remove-Item -Recurse -Force $BackendDest
}
Copy-Item -Recurse "$RootDir\app\backend" $BackendDest

# 复制 Python 运行时（嵌入式 Python）
Write-Host "[3/4] 准备 Python 运行时..." -ForegroundColor Yellow
$PythonEmbedDir = "$PublishDir\python"
if (-not (Test-Path $PythonEmbedDir)) {
    New-Item -ItemType Directory -Path $PythonEmbedDir | Out-Null
}

# 下载嵌入式 Python（如果不存在）
$PythonZip = "$RootDir\tools\python-3.12.4-embed-amd64.zip"
if (-not (Test-Path $PythonZip)) {
    Write-Host "  下载嵌入式 Python 3.12.4..." -ForegroundColor Gray
    New-Item -ItemType Directory -Path "$RootDir\tools" -Force | Out-Null
    Invoke-WebRequest -Uri "https://www.python.org/ftp/python/3.12.4/python-3.12.4-embed-amd64.zip" -OutFile $PythonZip
}

Write-Host "  解压 Python..." -ForegroundColor Gray
Expand-Archive -Path $PythonZip -DestinationPath $PythonEmbedDir -Force

# 启用 pip
$pthFile = "$PythonEmbedDir\python312._pth"
if (Test-Path $pthFile) {
    (Get-Content $pthFile) -replace '#import site', 'import site' | Set-Content $pthFile
}

# 安装后端依赖
Write-Host "  安装后端依赖..." -ForegroundColor Gray
& "$PythonEmbedDir\python.exe" -m pip install --target "$PythonEmbedDir\Lib\site-packages" requests beautifulsoup4 lxml --no-warn-script-location

# 创建启动脚本
Write-Host "[4/4] 创建启动脚本..." -ForegroundColor Yellow
$LaunchScript = @"
@echo off
cd /d "%~dp0"
start "" "frontend\Comic.WinUI.exe"
"@
Set-Content -Path "$PublishDir\ComicDownloader.bat" -Value $LaunchScript -Encoding ASCII

# 创建首次运行配置脚本
$SetupScript = @"
@echo off
chcp 65001 >nul
echo 正在初始化 Comic Downloader...
echo.

:: 设置 Python 环境变量
set "PYTHONPATH=%~dp0python\Lib\site-packages"
set "PATH=%~dp0python;%PATH%"

:: 测试后端
echo 测试后端服务...
"%~dp0python\python.exe" "%~dp0backend\run_backend.py" --test
if %errorlevel% equ 0 (
    echo 后端服务正常!
) else (
    echo 警告: 后端服务测试失败，但应用仍可尝试启动
)

echo.
echo 初始化完成!
timeout /t 2 >nul
"@
Set-Content -Path "$PublishDir\setup.bat" -Value $SetupScript -Encoding ASCII

Write-Host ""
Write-Host "发布完成!" -ForegroundColor Green
Write-Host "发布目录: $PublishDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "下一步: 运行 Inno Setup 编译安装包" -ForegroundColor Yellow
