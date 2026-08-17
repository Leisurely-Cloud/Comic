@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 9 SDK was not found.
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-and-package.ps1"
if errorlevel 1 exit /b %errorlevel%

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
    echo Inno Setup 6 was not found.
    exit /b 1
)

"%ISCC%" "%~dp0installer.iss"
exit /b %errorlevel%
