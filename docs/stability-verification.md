# 下载与阅读器稳定性回归（未发布）

日期：2026-10-04。基于 `main` / v2.8.0（`3e9214b`），保留此前书签、书架及任务删除的未提交改动；未改版本号，未提交、推送或发布。

## 分批实施

1. 下载停止：新增 3 项回归，修复前均失败；恢复任务直接到 `stopped` 应算成功，调用方取消应继续向上传播，页面刷新真实状态而非永久显示“正在停止”。
2. 图片传输与阅读器生命周期：在线图片复用下载的响应/无数据超时；离开阅读器取消元信息、章节、分页图片、条漫、缩略图与预读。新预读独立持有并释放取消源，旧失败不能清掉新缓存。缩略图的取消关联持续到排队解码结束。
3. 公开小样本和实际界面验证；最后运行完整常规测试、格式检查和启动器配置构建。

## 自动化证据

- `.tmp/stability-results/stability-stop-before.trx`：3 项批量停止回归修复前失败。
- `.tmp/stability-results/stability-batch1.trx`：下载、恢复、传输相关 85 项通过。
- `.tmp/stability-results/stability-online-before.trx`：在线图片停在响应头或响应体时，2 项回归修复前超出外层等待期限。
- `.tmp/stability-results/stability-batch2.trx`：阅读器及真实本机 HTTP 连接相关 41 项通过；随后另补下一页预读取消测试。
- `.tmp/stability-results/stability-final.trx`：常规离线 **250 项通过，0 失败、0 跳过**，较本轮开始的 228 项增加 22 项。
- `.tmp/stability-results/stability-live.trx`：明确启用的公开站点 **2 项通过，0 跳过**。

这些报告是开发机文件，不纳入发布。

### 故障与状态覆盖

- 真实 TCP/HTTP 回环连接：响应头停滞、响应体停滞、声明长度未传完便断开、用户取消、持续慢速发送。并非仅由自定义 HTTP handler 模拟；但不代表公网故障注入。
- 在线图片停滞按 3 次尝试收尾，用户取消不再重试。
- 阅读器退出时取消解析、正在读取及排队缩略图；退出后旧解码成功/失败均不得恢复图片或提交进度，同一 ViewModel 可重新加载。
- 单页→双页→条漫→单页连续切换，仅最后有效回调解码并提交进度。
- 1200 页在线章节只创建懒加载元信息，可跳到末页，不读取全章图片。
- 125%、150%、200% 对应逻辑视口的双页宽度和单页恢复，由几何单元测试覆盖；未改变 Windows 显示设置。

### 公开站点补下载

测试由 `COMIC_RUN_LIVE_TESTS=1` 显式开启，从公开排行榜最多检查 20 个候选，限定 2–24 页章节；没有合适样本时报告未验证而非下载大章节。常规离线套件排除 `Live` 分类。

本次实测：17 页首次下载完成；移除测试自己创建的一页后，调度器补回缺页；其余 16 页 SHA-256 未变化；再次请求完整章节正确跳过；未留下 `.part`。

所有文件位于独立随机临时目录，不使用用户的 `COMIC_DOWNLOAD_DIR`、书库或账号。收尾先停止并等待独立任务线程结束，再移除测试下载样本。该检查证明图片字节读取和补下载链路，不证明真实漫画的 WinRT 渲染质量。

## 界面验证与边界

使用 computer-use，启动当前启动器目标 Debug x64 程序，设置独立 `.tmp/stability-ui/settings` 和 `.tmp/stability-ui/library`，仅使用预先准备的中性几何测试页。

- 暗色：单页居中；双页分别保持自身比例；100%→90% 时两页同时缩小；从右向左交换页面顺序；经过条漫后返回单页仍居中、恢复分页 90% 缩放。
- 离开阅读器：第 2 页位置、单页模式、90% 缩放及方向保存到隔离 JSON。
- 亮色：书库显示“上次读到第 2 页”，重新进入阅读器恢复第 2 页、90% 缩放及方向，单页保持居中。
- 亮色全屏：图片按扩大的视口重新适配，页码与缩放不变，F11 可退出全屏。

主项目及测试项目 `dotnet format --verify-no-changes` 均通过；Release 测试配置和启动器目标 Debug x64 构建通过（警告作为错误）；Debug 构建为 0 警告、0 错误。`git diff --check` 通过，仅提示仓库既有的 LF/CRLF 转换。

未通过界面确认删除、未操作登录/退出登录或用户下载任务。长期运行、大型真实书库、Windows 实际多 DPI/多显示器切换和全部真实图片格式仍未验证；不将单元几何测试写成实际系统缩放实测。

## 2026-10-05 阅读器边界修复

本轮只修改阅读器边界逻辑和回归测试，保留此前未提交改动；仍未提交、推送或发布。

- 全屏状态以实际窗口 presenter 为准；进入时记录窗口位置、大小和最大化状态，离开阅读页面也统一恢复。
- 跨章节书签刷新并验证实际图片列表后才切换；直接复用预检列表，避免重复请求及列表数量变化。校验失败保留原章节、页码、书签和持久化进度；章节切换或退出取消预检。
- 章节列表加载完成不再清掉图片加载的忙碌状态；首图解码成功后才能提交分页进度及保存书签，失败仍保留失败保护。

### 自动化

- `.tmp/reader-boundaries-results/reader-boundaries-before.trx`：新增的 6 项回归修复前全部失败（本地/在线 × 解码成功/失败，以及单页/条漫失效跨章书签）。
- `.tmp/reader-boundaries-results/reader-boundaries-after.trx`：阅读器相关 42 项通过。
- 另补 3 项在线预检：有效跳转仅解析目标列表一次；失效页码不改位置；退出取消预检且不恢复章节、不覆盖进度。
- `.tmp/reader-boundaries-results/reader-boundaries-final.trx`：常规离线 **259 项通过，0 失败、0 跳过**。本轮未重跑公网下载测试，上一轮的 2 项公网结果见上文。
- 两个项目格式检查通过，Debug x64 构建 0 警告、0 错误（警告作为错误）。

### 实际窗口

通过 computer-use 操作本轮编译的启动器目标，使用 `.tmp/reader-boundaries-ui` 隔离设置、书库和中性几何图片；测试结束正常关闭测试窗口，未操作用户书库或账号。

- 普通窗口在 `(137,130)`、尺寸 `1426×752`：进入全屏后点“返回”，还原到相同位置和尺寸，并回到书库。
- 最大化窗口：再次打开阅读器，按钮进入全屏，F11 退出后保持最大化（`1920×1040`，标题栏“还原”可用）。
- 阅读区域获得焦点后 F11 可再次进入全屏；从侧栏“我的书库”离开后也恢复最大化，书库阅读位置保留第 1 页。
- 观察到另一个待核对点：重新进入阅读器后、尚未点击阅读区域时，第一次 F11 没有响应；点击“全屏”后 F11 正常。本轮未修改快捷键焦点路由，不将其写成已经修复。

本轮窗口实测为暗色；未更改主题资源，亮色渲染及多显示器移动没有在本轮重测。首图解码等待和在线失效书签通过可控替身回归覆盖，不冒充真实网络及界面故障注入。

## 复查命令

```powershell
dotnet test app/frontend-winui/src/Comic.WinUI.Tests/Comic.WinUI.Tests.csproj -c Release -r win-x64 --no-restore -p:TreatWarningsAsErrors=true --filter "TestCategory!=Live"
$env:COMIC_RUN_LIVE_TESTS='1'
dotnet test app/frontend-winui/src/Comic.WinUI.Tests/Comic.WinUI.Tests.csproj -c Release -r win-x64 --no-build --no-restore --filter "TestCategory=Live"
Remove-Item Env:COMIC_RUN_LIVE_TESTS
dotnet format app/frontend-winui/src/Comic.WinUI/Comic.WinUI.csproj --no-restore --verify-no-changes
dotnet format app/frontend-winui/src/Comic.WinUI.Tests/Comic.WinUI.Tests.csproj --no-restore --verify-no-changes
dotnet build app/frontend-winui/src/Comic.WinUI/Comic.WinUI.csproj -c Debug -p:Platform=x64 -r win-x64 --no-restore -p:TreatWarningsAsErrors=true
git diff --check
```
