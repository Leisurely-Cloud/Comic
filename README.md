# 漫画下载器 (Comic Downloader)

面向 Windows 10/11 的 [禁漫天堂](https://18comic.vip)(JMComic)漫画下载与在线阅读工具。应用为单进程 C# 架构,集站点访问、下载调度、本地书库与阅读器于一体,无需单独启动后端服务。

![screenshot](./docs/screenshot.png)

[![Build](https://img.shields.io/github/actions/workflow/status/Leisurely-Cloud/Comic/build.yml?label=CI)](https://github.com/Leisurely-Cloud/Comic/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Leisurely-Cloud/Comic)](https://github.com/Leisurely-Cloud/Comic/releases)
[![License](https://img.shields.io/github/license/Leisurely-Cloud/Comic)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-blue)](https://dotnet.microsoft.com/download/dotnet/9.0)

## 功能特性

- **内容发现**:站内关键词搜索,支持直接输入 JM 编号或链接精确解析;排行榜(最新更新 / 最多浏览 / 最多图片 / 最多点赞)带名次与作者信息。
- **下载管理**:批量下载、暂停 / 继续 / 停止,断点续传与失败章节自动重试;图片并发数与重试次数可在设置页调整。
- **在线阅读**:解析漫画后无需下载即可直接翻看,分页模式支持图片预加载与进度记忆。
- **本地书库**:下载内容自动归档,支持收藏置顶、上次阅读位置提示、更新检查与 CBZ 导出。
- **阅读器**:单页 / 条漫双模式,条漫支持滚轮缩放与自动切章;分页模式支持点击翻页、快捷键与 50%–300% 缩放;阅读进度自动保存。
- **其他**:搜索历史管理、明暗主题、文件日志(按天分文件、保留 7 天)。

## 架构

界面与核心服务运行在同一个进程中,按职责划分为独立服务,界面层通过统一的调用契约访问:

| 组件 | 说明 |
| --- | --- |
| `JmComicService` | 站点协议:搜索、解析、排行榜、章节图片解密与乱序还原 |
| `DownloadSchedulerService` | 下载任务调度:生命周期管理、断点续传、速度统计、下载历史 |
| `LibraryStorageService` | 书库存储:存储根目录、漫画元数据、章节目录枚举与路径校验 |
| `CbzExportService` | CBZ 打包导出 |
| `ReaderService` | 本地阅读访问(章节 / 图片) |

站点协议(域名、密钥、签名与图片乱序规则)集中在 `JmSiteOptions` 中,便于站点改版时快速调整。

### 技术栈

| 组件 | 技术 |
| --- | --- |
| 界面 | WinUI 3(Windows App SDK 1.8)、XAML、CommunityToolkit.Mvvm |
| 核心服务 | C#、.NET 9、HttpClient、System.Security.Cryptography、System.IO.Compression |
| 日志 | Microsoft.Extensions.Logging + 文件输出 |
| 打包 | 框架依赖发布 + Inno Setup 6 |
| CI | GitHub Actions(测试 + 安装包 + 自动 Release) |

## 环境要求

- Windows 10/11 x64
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)(开发)
- [Windows App Runtime 1.8 x64](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads-archive#windows-app-sdk-18)(运行,安装包会自动检查)

## 源码运行

```bat
start-winui.cmd
```

或手动构建:

```powershell
dotnet build .\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj -c Debug -r win-x64
```

默认下载目录为 `%USERPROFILE%\Downloads\ComicDownloads`,可通过环境变量 `COMIC_DOWNLOAD_DIR` 自定义。

## 测试

```powershell
dotnet test .\app\frontend-winui\src\Comic.WinUI.Tests\Comic.WinUI.Tests.csproj -c Release -r win-x64
```

## 构建安装包

安装 .NET 9 SDK 和 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 后执行:

```bat
build-installer.bat
```

输出位于 `installer-output/`。安装包为框架依赖部署,目标电脑需先安装:

- [.NET 9 x64 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0/runtime)
- [Windows App Runtime 1.8 x64](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads-archive#windows-app-sdk-18)

安装器会检查上述前置条件,缺少时停止安装并给出官方下载页面。

## 发布流程

打 tag 后由 CI 自动完成测试、打包与发布:

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

CI 会运行全部测试、从 tag 注入版本号构建安装包,并从 `CHANGELOG.md` 提取对应版本说明创建 GitHub Release。

## 项目结构

```
app/frontend-winui/
├── src/Comic.WinUI/           # WinUI 3 桌面应用
│   ├── Services/Native/       # 职责化核心服务(站点/下载/书库/导出/阅读)
│   ├── Services/Logging/      # 文件日志
│   ├── ViewModels/            # MVVM 视图模型
│   ├── Views/                 # 页面
│   └── Controls/              # 复用控件
└── src/Comic.WinUI.Tests/     # 协议、模型与应用服务测试
```

## 贡献

欢迎提交 Issue 与 Pull Request,请参阅 [CONTRIBUTING.md](./CONTRIBUTING.md)。

## 许可证

[MIT](./LICENSE)
