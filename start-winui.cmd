@echo off
setlocal
cd /d "%~dp0"

set "PROJECT=app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj"
set "SOURCE=app\frontend-winui\src\Comic.WinUI"
set "APP=app\frontend-winui\src\Comic.WinUI\bin\x64\Debug\net9.0-windows10.0.26100.0\win-x64\Comic.WinUI.exe"
set "ASSETS=app\frontend-winui\src\Comic.WinUI\obj\project.assets.json"
set "NEEDS_BUILD=0"

if /i "%~1"=="--rebuild" set "NEEDS_BUILD=1"
if not exist "%APP%" set "NEEDS_BUILD=1"

if "%NEEDS_BUILD%"=="0" (
    set "APP_PATH=%CD%\%APP%"
    set "SOURCE_PATH=%CD%\%SOURCE%"
    powershell -NoProfile -Command "$app = Get-Item -LiteralPath $env:APP_PATH; $newer = Get-ChildItem -LiteralPath $env:SOURCE_PATH -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTimeUtc -gt $app.LastWriteTimeUtc } | Select-Object -First 1; if ($newer) { exit 1 }"
    if errorlevel 1 set "NEEDS_BUILD=1"
)

if "%NEEDS_BUILD%"=="1" goto build

echo No source changes. Starting the existing build...
goto launch

:build
where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 9 SDK was not found.
    exit /b 1
)

echo Source changes detected. Building the latest UI...
if exist "%ASSETS%" (
    dotnet build "%PROJECT%" -c Debug -p:Platform=x64 -r win-x64 --no-restore
) else (
    dotnet build "%PROJECT%" -c Debug -p:Platform=x64 -r win-x64
)
if errorlevel 1 exit /b 1

:launch
start "" "%APP%"
