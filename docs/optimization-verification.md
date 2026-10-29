# 项目分析与优化验证

日期：2026-10-19。保留工作区已有的未提交改动，本轮只调整书库读取、检查更新及 CBZ 打包，并补充回归；未提交、推送或发布。

## 项目判断

项目采用 .NET 9、WinUI 3 和 MVVM，站点协议、下载调度、书库存储、CBZ 导出及阅读服务已分开。应用目录有 87 个 C#/XAML 源文件，测试目录有 35 个 C# 文件。优化前的完整离线回归为 278 项通过，0 失败、0 跳过。

本轮优先处理可复现的数据一致性问题和明确的文件处理开销：

| 问题 | 调整 | 验证 |
| --- | --- | --- |
| 元数据通常读盘 3 次，还会额外序列化、解析；回退备份后兼容字段可能又来自无效主文件 | `AtomicJsonFile` 返回实际接受的 JSON 内容；标准字段和兼容别名使用同一份快照 | `null`、数组、字段类型错误的主文件均正确回退到备份别名，不改写主文件 |
| 检查更新在首次异步等待前同步扫描书库 | 整个检查流程在后台执行，保留页面调用方的正常 await 用法 | 检查不在调用方同步上下文执行；预先取消时不构建索引 |
| 列表请求取消后，已开始的目录扫描仍会继续；中途退出可能丢失缓存失效标记 | 取消信号传递到目录索引，在漫画及文件边界检查；完整重建成功后才清除失效标记；统计文件大小时复用同一次遍历 | 取消重建保留旧缓存和待重建状态，后续查询能正常重建 |
| CBZ 对 JPEG/PNG/WebP/GIF 再次 Deflate，消耗 CPU | 已压缩格式直接存储；BMP 与 ComicInfo.xml 保持压缩 | 大样本多次导出、逐文件解包字节一致；大写 `.BMP` 仍能压缩并正确还原；已有取消与半成品清理测试通过 |

## 本机导出测量

同一个回归使用固定随机种子，生成 24 个 256 KiB 文件，共 6 MiB，扩展名覆盖 JPEG、PNG、WebP、GIF。内容是模拟已压缩分布的随机字节，用于测量打包开销与字节一致性，不用于验证图片解码。

| 指标 | 修改前 | 修改后 |
| --- | --- | --- |
| 第 1 次导出 | 267.0 ms | 19.2 ms |
| 第 2 次导出 | 178.7 ms | 21.3 ms |
| 第 3 次导出 | 175.9 ms | 26.6 ms |
| 三次中位耗时 | 178.7 ms | 21.3 ms |
| CBZ 文件大小 | 6,295,950 字节 | 6,293,978 字节 |

该样本中位耗时降低约 88%，约为原来的 1/8.4。测量包含任务创建和进度轮询，受到磁盘缓存、线程调度及测试进程负载影响；不将结果外推为全部真实漫画的加速比例。

既有 1000 部漫画测试仍验证重复查询完全复用索引。本轮最终运行记录为首次索引 452.7 ms、重复查询 75.5 ms；不以单次耗时断言通过或宣称书库扫描速度提升。

## 回归结果

- 修改前针对性回归：4 个失败场景，分别为 3 种备份别名读取错误及取消后仍构建索引；导出样本当时通过，作为耗时对照。
- 新增 9 项回归，完整离线套件最终 **287 项通过，0 失败、0 跳过**。
- Release 应用及测试项目编译通过，编译器与分析器警告按错误处理。
- 启动器使用的 Debug x64 版本构建通过，0 警告、0 错误。
- 应用项目、测试项目的完整格式检查均通过。
- `git diff --check` 按仓库原有换行设置检查通过。
- 所有导出和书库测试均使用隔离测试目录；本轮未运行 `Live` 分类、公网下载或真实窗口交互。

本机证据位于以下路径，属于开发机临时产物，不纳入发布：

- `.tmp/optimization-results/baseline/optimization-baseline.trx`
- `.tmp/optimization-results/before/optimization-before.trx`
- `.tmp/optimization-results/targeted/optimization-targeted.trx`
- `.tmp/optimization-results/final/optimization-final.trx`
- `.tmp/optimization-before/`：本轮修改前的文件快照，用于区分已有改动与本轮调整。

## 后续建议

1. 优先按职责拆分 `DownloadPageViewModel`（约 1800 行）和 `DownloadSchedulerService`（约 1750 行）。先提取搜索、评论、任务订阅等模块，继续用现有回归约束行为，减少同时调整功能与结构的风险。
2. 对超大章节评估有界 worker 调度。当前图片下载通过 `Select(async ...)` 与信号量限制实际并发，但仍为每张待下载图片创建等待任务；需要先测量长章节的分配量，再选择是否改为固定数量的 worker。
3. 若准备发布，再对真实阅读窗口的首次快捷键焦点、不同 DPI 和常用图片格式做单独验收。当前离线回归与打包字节校验不能代替这些实测。

## 复查命令

```powershell
dotnet test app/frontend-winui/src/Comic.WinUI.Tests/Comic.WinUI.Tests.csproj -c Release -r win-x64 --no-restore -p:TreatWarningsAsErrors=true --filter "TestCategory!=Live" --logger "trx;LogFileName=optimization-final.trx" --results-directory .tmp/optimization-results/final
dotnet format app/frontend-winui/src/Comic.WinUI/Comic.WinUI.csproj --no-restore --verify-no-changes
dotnet format app/frontend-winui/src/Comic.WinUI.Tests/Comic.WinUI.Tests.csproj --no-restore --verify-no-changes
dotnet build app/frontend-winui/src/Comic.WinUI/Comic.WinUI.csproj -c Debug -p:Platform=x64 -r win-x64 --no-restore -p:TreatWarningsAsErrors=true
git diff --check
```
