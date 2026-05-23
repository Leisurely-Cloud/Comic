@echo off
chcp 65001 >nul
echo ======================================
echo   Comic Downloader 安装包构建工具
echo ======================================
echo.

:: 检查 .NET SDK
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 未找到 .NET SDK，请先安装 .NET 9 SDK
    echo 下载地址: https://dotnet.microsoft.com/download/dotnet/9.0
    pause
    exit /b 1
)

:: 检查 Inno Setup
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
    echo [提示] 未找到 Inno Setup 6，将只生成发布文件，不生成安装包
    echo 下载地址: https://jrsoftware.org/isinfo.php
    echo.
    set "SKIP_INSTALLER=1"
)

echo [步骤 1] 发布 .NET 前端...
dotnet build app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj -c Release -r win-x64
if %errorlevel% neq 0 (
    echo [错误] 前端构建失败!
    pause
    exit /b 1
)
dotnet publish app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj -c Release -r win-x64 --self-contained true -o publish\frontend
if %errorlevel% neq 0 (
    echo [错误] 前端发布失败!
    pause
    exit /b 1
)

echo   复制 XAML 编译文件...
xcopy /e /i /q app\frontend-winui\src\Comic.WinUI\bin\Release\net9.0-windows10.0.26100.0\win-x64\Assets publish\frontend\Assets
xcopy /e /i /q app\frontend-winui\src\Comic.WinUI\bin\Release\net9.0-windows10.0.26100.0\win-x64\Views publish\frontend\Views
xcopy /e /i /q app\frontend-winui\src\Comic.WinUI\bin\Release\net9.0-windows10.0.26100.0\win-x64\Controls publish\frontend\Controls
copy /y app\frontend-winui\src\Comic.WinUI\bin\Release\net9.0-windows10.0.26100.0\win-x64\*.xbf publish\frontend\
copy /y app\frontend-winui\src\Comic.WinUI\bin\Release\net9.0-windows10.0.26100.0\win-x64\Comic.WinUI.pri publish\frontend\

echo.
echo [步骤 2] 复制后端代码...
if exist publish\backend rmdir /s /q publish\backend
xcopy /e /i /q app\backend publish\backend

echo.
echo [步骤 3] 准备 Python 运行时...
if not exist publish\python mkdir publish\python

:: 检查是否已有嵌入式 Python
if not exist tools\python-3.12.4-embed-amd64.zip (
    echo   下载嵌入式 Python 3.12.4...
    if not exist tools mkdir tools
    curl -L -o tools\python-3.12.4-embed-amd64.zip https://www.python.org/ftp/python/3.12.4/python-3.12.4-embed-amd64.zip
    if %errorlevel% neq 0 (
        echo [错误] Python 下载失败，请手动下载并放到 tools 目录
        echo 下载地址: https://www.python.org/ftp/python/3.12.4/python-3.12.4-embed-amd64.zip
        pause
        exit /b 1
    )
)

echo   解压 Python...
powershell -Command "Expand-Archive -Path 'tools\python-3.12.4-embed-amd64.zip' -DestinationPath 'publish\python' -Force"

:: 启用 pip
powershell -Command "(Get-Content 'publish\python\python312._pth') -replace '#import site', 'import site' | Set-Content 'publish\python\python312._pth'"

echo   安装后端依赖...
publish\python\python.exe -m pip install --target publish\python\Lib\site-packages requests beautifulsoup4 lxml --no-warn-script-location

echo.
echo [步骤 4] 创建启动脚本...
(
echo @echo off
echo cd /d "%%~dp0"
echo start "" "frontend\Comic.WinUI.exe"
) > publish\ComicDownloader.bat

(
echo @echo off
echo chcp 65001 ^>nul
echo echo 正在初始化 Comic Downloader...
echo echo.
echo set "PYTHONPATH=%%~dp0python\Lib\site-packages"
echo set "PATH=%%~dp0python;%%PATH%%"
echo echo 初始化完成!
echo timeout /t 2 ^>nul
) > publish\setup.bat

echo.
echo ======================================
echo   发布完成!
echo ======================================
echo.

if defined SKIP_INSTALLER (
    echo 发布文件位于: publish 目录
    echo 如需生成安装包，请安装 Inno Setup 6 后重新运行此脚本
    pause
    exit /b 0
)

echo [步骤 5] 生成安装包...
"%ISCC%" installer.iss
if %errorlevel% neq 0 (
    echo [错误] 安装包生成失败!
    pause
    exit /b 1
)

echo.
echo ======================================
echo   安装包生成成功!
echo ======================================
echo   位置: installer-output\ComicDownloader-1.0.0-Setup.exe
echo.
pause
