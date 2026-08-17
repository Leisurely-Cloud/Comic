# 漫画下载器

面向 Windows 10/11 的禁漫天堂下载与本地阅读工具。应用现已迁移为单进程 C# 架构，无需单独启动后端服务。

![screenshot](./docs/screenshot.png)

## 功能

- 禁漫天堂站内搜索、排行榜、章节解析与图片自动还原
- 批量下载，以及暂停、继续和停止任务
- 本地书库浏览和内置阅读器
- CBZ 导出与进度显示
- 搜索历史和明暗主题

## 架构

核心代码位于 `app/frontend-winui/`：

- `src/Comic.WinUI/`：WinUI 3 桌面应用
- `src/Comic.WinUI/Services/Native/`：站点访问、下载调度、书库、持久化和 CBZ 导出
- `src/Comic.WinUI.Tests/`：协议解析、模型与应用服务测试

界面和核心服务运行在同一个进程中，不再依赖本地 HTTP 服务。发布版本采用框架依赖部署，由系统共享 .NET 和 Windows App Runtime。

## 源码运行

开发环境要求：

- Windows 10/11 x64
- .NET 9 SDK
- Windows App Runtime 1.8 x64

```bat
start-winui.cmd
```

也可以手动构建：

```powershell
dotnet build .\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj -c Debug -r win-x64
```

默认下载目录为 `%USERPROFILE%\Downloads\ComicDownloads`，可通过环境变量 `COMIC_DOWNLOAD_DIR` 自定义。

## 测试

```powershell
dotnet test .\app\frontend-winui\src\Comic.WinUI.Tests\Comic.WinUI.Tests.csproj -c Release -r win-x64
```

## 构建安装包

安装 .NET 9 SDK 和 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 后执行：

```bat
build-installer.bat
```

输出位于 `installer-output/`。安装包不包含系统运行时，目标电脑必须先安装：

- [.NET 9 x64 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0/runtime)
- [Windows App Runtime 1.8 x64](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads-archive#windows-app-sdk-18)

安装器会检查这两个前置条件；缺少时停止安装并提供微软官方下载页面。

## 技术栈

| 组件 | 技术 |
| --- | --- |
| 界面 | WinUI 3、C#、.NET 9、CommunityToolkit.Mvvm |
| 核心服务 | C#、HttpClient、System.Security.Cryptography、System.IO.Compression |
| 打包 | .NET/Windows App SDK 框架依赖发布、Inno Setup |

## 许可证

[MIT](./LICENSE)
