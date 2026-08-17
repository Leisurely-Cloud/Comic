# 应用目录

应用采用 WinUI 3 + 内置 C# 服务的单进程架构。

## 目录结构

- `frontend-winui/src/Comic.WinUI/`：桌面应用源码
- `frontend-winui/src/Comic.WinUI/Services/Native/`：禁漫协议、下载调度、书库扫描、持久化和 CBZ 导出
- `frontend-winui/src/Comic.WinUI.Tests/`：自动化测试

## 运行

在仓库根目录执行：

```bat
start-winui.cmd
```

或手动构建：

```powershell
dotnet build .\app\frontend-winui\src\Comic.WinUI\Comic.WinUI.csproj -c Debug -r win-x64
```

应用启动后，核心服务随界面进程一起就绪，不需要配置地址或管理独立后端进程。

应用采用框架依赖部署；运行发布版本的电脑需要预先安装 .NET 9 x64 Runtime 和 Windows App Runtime 1.8 x64。

## 测试

```powershell
dotnet test .\app\frontend-winui\src\Comic.WinUI.Tests\Comic.WinUI.Tests.csproj -c Release -r win-x64
```
