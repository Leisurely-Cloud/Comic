# 阅读状态与批量导入验证

验证日期：2026-10-21。功能尚未发布，保留工作区已有改动。

## 行为

- 书库提供待读、在读、已读三类状态，分类计数、组合筛选、查看在读和最近在读续读入口。阅读状态在分页前筛选；计数遵循其他筛选条件，覆盖全部页。
- 详情菜单可手动标记状态。标记待读保留页码，同页重新阅读也会转为在读；显式已读在重新阅读时保留。
- 扩展原 `reading-progress.json` 的 `Statuses`，旧 `Entries` 自动推导状态。空字段的旧条目仍逐项忽略，保留其他有效记录；状态字段损坏可回退有效 `.bak`。
- 阅读状态包含在现有备份、恢复及损坏检查中，手动标记不要求先有阅读进度。保存失败会回滚并在界面提示。
- 找漫画页面增加批量导入。支持正整数、JM 编号和 HTTP(S) `/album/编号` 链接；按换行、空格、中英文逗号/分号分隔，按编号规范化去重，最多 100 条原始输入、32768 字符。
- 预览只解析元数据，提交只处理勾选的成功项。成功回执保留，失败项可重试；修改输入使旧预览失效，关闭或导航离开取消剩余操作，已创建任务保留。
- 沿用现有下载参数、调度器及任务订阅。按漫画与章节集合复用任务，已有单章任务不会吞掉整部请求；本地完整章节由调度器跳过。

## 自动化结果

| 检查 | 结果 |
| --- | --- |
| 改动前旧程序集离线基线 | 287 / 287 通过 |
| Release 离线回归（警告视为错误） | 334 / 334 通过 |
| Debug 构建及启动脚本使用的 Debug x64 构建（警告视为错误） | 均为 0 警告，0 错误 |
| 应用项目 `dotnet format --verify-no-changes --no-restore` | 通过 |
| 测试项目 `dotnet format --verify-no-changes --no-restore` | 通过 |
| `git diff --check` | 通过 |

新增 47 个测试用例，覆盖旧文件迁移、状态持久化/并发/失败回滚、同页续读、筛选与分页、备份往返与损坏状态修复；以及批量输入校验、预览不写入、勾选提交、失败重试、取消竞态、输入变更、释放后的迟到结果、精确章节复用、已有部分任务和本地完整章节跳过。

本轮测试使用项目内隔离 `TEMP` / `TMP`，避开受限环境对系统临时目录原子移动的限制；未修改生产文件写入逻辑。格式检查的 Roslyn 命名管道在沙箱中被拒绝，改以标准本地权限执行只读检查后通过。

```powershell
$env:TEMP = Join-Path $PWD '.tmp\reading-batch-test-temp'
$env:TMP = $env:TEMP
dotnet test app/frontend-winui/src/Comic.WinUI.Tests/Comic.WinUI.Tests.csproj -c Release -r win-x64 --no-restore -p:TreatWarningsAsErrors=true --filter 'TestCategory!=Live'
dotnet build app/frontend-winui/src/Comic.WinUI/Comic.WinUI.csproj -c Debug -p:Platform=x64 -r win-x64 --no-restore -p:TreatWarningsAsErrors=true
```

日志与 TRX 位于 `.tmp/reading-batch-results/`；改动前代码快照位于 `.tmp/reading-batch-before-20261021-1ebd9ddf/`。

## 真实窗口验收

使用 `COMIC_DATA_DIR=.tmp/reading-batch-ui/data` 与 `COMIC_DOWNLOAD_DIR=.tmp/reading-batch-ui/library`，以三部几何图片样本隔离验证。

- 找漫画入口可打开批量导入对话框，提示、输入区、预览列表及操作按钮完整显示。
- 输入 5 条混合内容显示有效 2 部、重复 1 条、无效 2 条；未解析时加入队列按钮禁用。只含无效内容时，解析与提交均禁用。
- 书库初始显示待读 1、在读 1、已读 1，卡片将阅读状态与下载完成状态分别展示。
- 将已读到第二页的 B 标记为待读，计数变为待读 2、在读 0、已读 1，原页码保留，继续阅读按钮禁用。
- 从详情重新打开 B 后，真实图片与页码均恢复第二页；返回书库后自动成为在读，计数恢复 1 / 1 / 1。磁盘文件保留 `PageIndex=1`，待读覆盖状态已移除。
- 查看在读只列出 B；书库顶部继续阅读打开 B 的第二页。
- 关闭隔离窗口，以启动脚本对应的 `bin/x64/Debug` 版本重新启动；再次进入书库，仍为待读 1、在读 1、已读 1，B 保留第二页进度，C 保留显式已读状态。

本轮未调用真实站点的批量解析或下载。入队、订阅和本地跳过使用隔离服务与 HTTP 替身验证，不能视为当前站点可用性或真实网络下载验收。
