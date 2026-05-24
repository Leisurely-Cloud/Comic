# 更新日志

## v2.2.0 — 2026-05-24

### 新功能

- 启动画面（Splash Screen）
- 页面切换滑动动画
- 加载状态和空状态优化
- 响应式布局改进
- 搜索历史管理

### 修复

- 修正打包安装路径检测
- 安装前自动终止 Python 进程
- CI 构建错误：发布权限和 MVVMTK0045 警告

### 维护

- 将 AGENTS.md 加入 .gitignore
- 使用 partial property 替代 CommunityToolkit.Mvvm 的 ObservableProperty

## v2.1.0 — 2026-05-03

### 架构重构

- 从单文件 Python GUI 迁移至 `app/` 目录结构（WinUI 3 前端 + Python 后端 API）
- 后端：REST API（`127.0.0.1:18765`），支持 SSE 实时下载进度
- 前端：WinUI 3 桌面客户端，使用 MVVM 架构（CommunityToolkit.Mvvm）
- 移除所有旧版根目录 Python GUI 文件

### 新功能

- WinUI 3 现代桌面界面，支持明暗主题
- 自动管理后端进程（客户端启停）
- 通过 Server-Sent Events 流式传输下载进度
- CBZ 导出，包含 ComicInfo.xml 元数据
- 本地书库浏览，支持分页
- 设置持久化存储于 LocalAppData

### 支持站点

- 包子漫画（Baozimh）
- MangaCopy
- 漫画柜（Manhuagui）

## v2.0.0 — 2025

### 新功能

- 多站点 GUI，支持批量下载
- 代理池，支持区域限制访问
- 下载恢复和重试逻辑
- 章节命名规范和元数据追踪
