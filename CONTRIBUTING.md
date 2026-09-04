# 贡献指南

感谢你对本项目的关注。

## 开始参与

1. Fork 本仓库。
2. 克隆你的 Fork：`git clone https://github.com/your-username/Comic.git`。
3. 创建分支：`git checkout -b feature/your-feature`。
4. 修改并运行测试。
5. 推送分支并发起 Pull Request。

## 报告问题

请提供问题描述、复现步骤、期望与实际行为，以及 Windows 和 .NET 版本。

## 验证

常规测试不会访问外部站点：

```powershell
dotnet test .\app\frontend-winui\src\Comic.WinUI.Tests\Comic.WinUI.Tests.csproj -c Release -r win-x64 --filter "TestCategory!=Live"
```

需要确认 JM 当前接口契约时，可显式运行联网冒烟测试：

```powershell
$env:COMIC_RUN_LIVE_TESTS = "1"
dotnet test .\app\frontend-winui\src\Comic.WinUI.Tests\Comic.WinUI.Tests.csproj -c Release -r win-x64 --filter "TestCategory=Live"
Remove-Item Env:COMIC_RUN_LIVE_TESTS
```

## 提交要求

- 每个提交聚焦单一变更并使用清晰描述。
- 新功能尽量附带测试。
- 行为变化时同步更新文档。
- 面向用户的提示使用中文。

## C# / WinUI 3 规范

- 使用 CommunityToolkit.Mvvm 源生成器。
- XAML 优先使用 `x:Bind`。
- 尽量缩小 code-behind，把交互逻辑放入 ViewModel 或服务。
- 类默认使用 `sealed`，MVVM 需要时使用 `partial`。
- 异步方法传递 `CancellationToken`，网络和文件操作应提供明确错误信息。

## 修改禁漫天堂服务

协议解析、章节下载和图片还原位于 `app/frontend-winui/src/Comic.WinUI/Services/Native/JmComicService.cs`。修改后请同步更新 `app/frontend-winui/src/Comic.WinUI.Tests/Services/JmComicServiceTests.cs`，并运行完整测试。

## 许可证

贡献即表示你的代码将遵循 [MIT 许可证](./LICENSE)。
