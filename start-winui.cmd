@echo off
setlocal
cd /d "%~dp0"

set "PROJECT=app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj"
set "APP=app\frontend-winui\src\Comic.WinUI\bin\x64\Debug\net9.0-windows10.0.26100.0\win-x64\Comic.WinUI.exe"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 9 SDK was not found.
    exit /b 1
)

echo Building the latest UI...
dotnet build "%PROJECT%" -c Debug -p:Platform=x64 -r win-x64
if errorlevel 1 exit /b %errorlevel%

start "" "%APP%"
