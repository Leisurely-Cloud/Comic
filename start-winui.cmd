@echo off
setlocal

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"
cd /d "%ROOT_DIR%"

set "BACKEND_URL=http://127.0.0.1:18765/api/health"
set "PYTHON_EXE=%ROOT_DIR%\.venv\Scripts\python.exe"
set "BACKEND_SCRIPT=%ROOT_DIR%\app\backend\run_backend.py"
set "PROJECT_FILE=%ROOT_DIR%\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj"
set "APP_EXE=%ROOT_DIR%\app\frontend-winui\src\Comic.WinUI\bin\Debug\net9.0-windows10.0.26100.0\win-x64\Comic.WinUI.exe"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet not found.
    pause
    exit /b 1
)

if not exist "%PYTHON_EXE%" (
    echo [ERROR] Missing Python runtime: "%PYTHON_EXE%"
    pause
    exit /b 1
)

if not exist "%BACKEND_SCRIPT%" (
    echo [ERROR] Missing backend launcher: "%BACKEND_SCRIPT%"
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "try { Invoke-RestMethod -Uri '%BACKEND_URL%' -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"
if errorlevel 1 (
    echo [INFO] Starting backend on 127.0.0.1:18765...
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
        "Start-Process -FilePath '%PYTHON_EXE%' -ArgumentList '%BACKEND_SCRIPT%' -WorkingDirectory '%ROOT_DIR%\app' -WindowStyle Hidden"
) else (
    echo [INFO] Backend already running.
)

timeout /t 2 /nobreak >nul

if not exist "%APP_EXE%" (
    echo [INFO] Building WinUI 3 app...
    dotnet build "%PROJECT_FILE%" -c Debug
    if errorlevel 1 (
        echo [ERROR] Failed to build WinUI 3 app.
        pause
        exit /b 1
    )
)

if not exist "%APP_EXE%" (
    echo [ERROR] App executable not found: "%APP_EXE%"
    pause
    exit /b 1
)

echo [INFO] Starting WinUI 3 app...
start "" "%APP_EXE%"

exit /b 0
