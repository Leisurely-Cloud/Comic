# App Layout

This directory contains the active application implementation: `WinUI 3` frontend plus local Python backend.

## Structure

- `backend/`
  Local HTTP API, real download orchestration, library scanning, metadata persistence, and CBZ export.
- `backend/support/`
  Preserved site adapters and low-level download helpers reused by the new backend.
- `backend/tests/`
  Backend regression tests covering API behavior, task lifecycle, SSE concurrency, and origin checks.
- `frontend-winui/`
  WinUI 3 desktop client source.

## Run

Backend only:

```powershell
.\.venv\Scripts\python.exe .\app\backend\run_backend.py
```

WinUI build:

```powershell
dotnet build .\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj
```

One-click launcher:

```bat
start-winui.cmd
```

## Test

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s .\app\backend\tests -v
```

## Notes

- The WinUI app can manage the backend process directly from the shell view.
- The backend listens on loopback only and rejects non-loopback `Origin` headers.
- Download tasks now run through the real adapter pipeline and update the local library metadata as chapters complete.
