# Changelog

## v2.2.0 — 2026-05-24

### Features / 新功能

- Splash screen on app startup
- Slide animation for page navigation
- Loading states and empty states for better UX
- Responsive layout improvements
- Search history management

### Fixes / 修复

- Correct packaged installation path detection
- Auto-kill Python processes before installation
- CI build errors: release permissions and MVVMTK0045 warnings

### Chores / 维护

- Add AGENTS.md to .gitignore
- Use partial properties for CommunityToolkit.Mvvm ObservableProperty

## v2.1.0 — 2026-05-03

### Architecture / 架构重构

- Migrated from single-file Python GUI to `app/` layout (WinUI 3 frontend + Python backend API)
- Backend: REST API on `127.0.0.1:18765` with SSE for real-time download progress
- Frontend: WinUI 3 desktop client with MVVM (CommunityToolkit.Mvvm)
- Removed all legacy root-level Python GUI files

### Features / 新功能

- WinUI 3 modern desktop UI with light/dark theme
- Automatic backend process management (start/stop from client)
- Download progress streaming via Server-Sent Events
- CBZ export with ComicInfo.xml metadata
- Local library browser with pagination
- Settings persistence in LocalAppData

### Supported Sites / 支持站点

- Baozimh (包子漫画)
- MangaCopy
- Manhuagui (漫画柜)

## v2.0.0 — 2025

### Features

- Multi-site GUI with batch download support
- Proxy pool for region-restricted access
- Download resume and retry logic
- Chapter naming conventions and metadata tracking
