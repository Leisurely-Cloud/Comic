# Comic Downloader / 漫画下载器

[English](#english) | [中文](#中文)

---

![screenshot](./docs/screenshot.png)

---

## English

A desktop manga/comic downloader with a modern WinUI 3 interface and a local Python backend API. Supports multiple manga sites, batch chapter downloads, CBZ export, and a built-in local library browser.

### Features

- **Multi-site support** — Baozimh, MangaCopy, Manhuagui, and more
- **Batch download** — Select and download multiple chapters at once
- **CBZ export** — Export downloaded manga to CBZ format for comic readers
- **Local library** — Browse and manage your downloaded manga collection
- **Download control** — Pause, resume, and stop downloads with real-time progress
- **Proxy support** — Configure HTTP proxy for region-restricted sites
- **Modern UI** — Native WinUI 3 desktop app with light/dark theme support

### Architecture

```
app/
├── frontend-winui/    # WinUI 3 desktop client (C#, .NET 9, MVVM)
└── backend/           # Local HTTP API (Python)
```

- The WinUI client communicates with the backend via REST API on `http://127.0.0.1:18765/`
- The client can start/stop the backend process automatically
- Download progress is streamed via Server-Sent Events (SSE)

### Quick Start

**Prerequisites:**
- Windows 10/11
- .NET 9 SDK
- Python 3.10+

**One-click launch:**

```bat
start-winui.cmd
```

**Manual run:**

```powershell
# Backend
.\.venv\Scripts\python.exe .\app\backend\run_backend.py

# Frontend
dotnet build .\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj
```

**Download storage:** `%USERPROFILE%\Downloads\ComicDownloads` (configurable via `COMIC_DOWNLOAD_DIR` environment variable)

### Tests

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s .\app\backend\tests -v
```

### Tech Stack

| Component | Technology |
|-----------|------------|
| Frontend | WinUI 3, C# 12, .NET 9, CommunityToolkit.Mvvm |
| Backend | Python 3.10+, stdlib http.server |
| Communication | REST API + SSE |
| Packaging | Self-contained, unpackaged (no MSIX) |

### License

[MIT](./LICENSE)

---

## 中文

一款桌面端漫画下载器，采用现代化的 WinUI 3 界面配合本地 Python 后端 API。支持多站点搜索、批量章节下载、CBZ 导出和本地书库浏览。

### 功能特性

- **多站点支持** — 包子漫画、MangaCopy、漫画柜等
- **批量下载** — 选择多个章节一键下载
- **CBZ 导出** — 将下载的漫画导出为 CBZ 格式，方便在各类阅读器中使用
- **本地书库** — 浏览和管理已下载的漫画
- **下载控制** — 支持暂停、继续、停止，实时显示下载进度
- **代理支持** — 可配置 HTTP 代理访问受限站点
- **现代化界面** — 原生 WinUI 3 桌面应用，支持明暗主题

### 项目结构

```
app/
├── frontend-winui/    # WinUI 3 桌面客户端 (C#, .NET 9, MVVM)
└── backend/           # 本地 HTTP API (Python)
```

- WinUI 客户端通过 REST API 与后端通信，地址 `http://127.0.0.1:18765/`
- 客户端可自动启动/停止后端进程
- 下载进度通过 SSE (Server-Sent Events) 实时推送

### 快速开始

**环境要求：**
- Windows 10/11
- .NET 9 SDK
- Python 3.10+

**一键启动：**

```bat
start-winui.cmd
```

**手动运行：**

```powershell
# 后端
.\.venv\Scripts\python.exe .\app\backend\run_backend.py

# 前端
dotnet build .\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj
```

**下载存储位置：** `%USERPROFILE%\Downloads\ComicDownloads`（可通过环境变量 `COMIC_DOWNLOAD_DIR` 自定义）

### 测试

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s .\app\backend\tests -v
```

### 技术栈

| 组件 | 技术 |
|------|------|
| 前端 | WinUI 3, C# 12, .NET 9, CommunityToolkit.Mvvm |
| 后端 | Python 3.10+, 标准库 http.server |
| 通信 | REST API + SSE |
| 打包 | 自包含，非打包部署（无 MSIX） |

### 许可证

[MIT](./LICENSE)
