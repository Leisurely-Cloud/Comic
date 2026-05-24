# 应用目录

本目录包含应用主体实现：WinUI 3 前端 + 本地 Python 后端。

## 目录结构

- `backend/` — 本地 HTTP API、下载调度、书库扫描、元数据持久化、CBZ 导出
- `backend/support/` — 站点适配器和底层下载工具
- `backend/tests/` — 后端测试，覆盖 API 行为、任务生命周期、SSE 并发等
- `frontend-winui/` — WinUI 3 桌面客户端源码

## 运行

**后端：**

```powershell
.\.venv\Scripts\python.exe .\app\backend\run_backend.py
```

**前端：**

```powershell
dotnet build .\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj
```

**一键启动：**

```bat
start-winui.cmd
```

## 测试

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s .\app\backend\tests -v
```

## 说明

- WinUI 应用可直接管理后端进程的启停
- 后端仅监听本地回环地址，拒绝非回环 Origin 请求
- 下载任务通过站点适配器执行，章节完成后自动更新本地书库元数据
