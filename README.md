# Comic

一个面向 Windows 的多站点漫画下载器，提供图形界面主工作流和命令行辅助能力。

它适合用来：

- 直接通过 GUI 下载和管理漫画
- 浏览首页发现、站内搜索或粘贴链接发起下载
- 管理本地漫画库，并导出 ZIP / CBZ
- 使用内置打包脚本生成 Windows EXE 进行分发

当前项目的定位是：

- GUI 负责日常使用，支持站点切换、搜索、首页发现、手动 URL 下载、本地漫画库、ZIP 打包和 CBZ 导出
- CLI 保留轻量下载能力，当前仍以 `baozimh.org` 工作流为主
- 项目内置 PyInstaller 打包脚本，可直接生成可分发的 Windows EXE

## 当前支持范围

### GUI 站点支持

| 站点 | 首页发现 | 站内搜索 | 手动 URL 下载 | 说明 |
| --- | --- | --- | --- | --- |
| 包子漫画 | 支持 | 支持 | 支持 | 保留原有下载流程 |
| 拷贝漫画 | 支持 | 支持 | 支持 | 支持手动代理和连接测试 |
| 漫画柜 | 暂不支持 | 支持 | 支持 | 首页发现暂未接入，章节解析可能稍慢 |

### CLI 支持

| 入口 | 当前状态 |
| --- | --- |
| `downcomic.py` | 主要面向 `baozimh.org` |
| `run_gui.py` | Windows GUI 启动入口 |

## 功能特性

- 多站点 GUI 下载，支持包子漫画、拷贝漫画、漫画柜
- 自动根据链接识别站点，也可以手动切换站点
- 首页发现列表、分区切换、分页浏览
- 站内搜索与手动链接获取漫画信息
- 章节并发下载与图片并发下载
- 暂停、继续、停止下载
- 自动跳过已下载图片，支持断点续传
- 下载完成后可一键打包整部漫画为 ZIP
- 已下载漫画可导出为章节级 CBZ，并写入 `ComicInfo.xml`
- 本地漫画库浏览、搜索、封面回显、更新检查
- 可选手动代理与站点连通性测试
- 自带 EXE 打包脚本与发布压缩包生成脚本

## 项目结构

```text
Comic/
├── build_exe.ps1          # PyInstaller 一键打包脚本
├── comic_gui.py           # GUI 主程序
├── comic_gui.spec         # PyInstaller 配置
├── create_release.ps1     # 发布目录与压缩包生成脚本
├── downcomic.py           # 命令行下载器（当前以包子漫画为主）
├── LICENSE                # MIT License
├── README.md              # 项目说明
├── requirements.txt       # Python 依赖
├── run_gui.py             # GUI 启动入口
├── site_adapters.py       # 多站点适配层
├── version_info.txt       # Windows EXE 版本信息
└── release/               # 本地生成的发布目录，用于上传 GitHub Releases
```

## 环境要求

- Python 3.10+
- Windows 10/11
- 可访问目标漫画站点
- GUI 依赖 `tkinter`
- 打包 EXE 时需要 `pyinstaller`

说明：

- 漫画柜适配里带有 `cscript.exe` 兜底解析逻辑，因此完整 GUI 体验更推荐在 Windows 下运行
- 打包后的 EXE 面向 Windows 使用

## 安装

```bash
git clone https://github.com/Leisurely-Cloud/Comic.git
cd Comic
pip install -r requirements.txt
```

依赖包括：

- `requests`
- `beautifulsoup4`
- `tqdm`
- `lxml`
- `pillow`

## 快速开始

### 启动 GUI

```bash
python run_gui.py
```

进入 GUI 后的典型使用流程：

1. 选择站点，或直接粘贴目标漫画链接
2. 点击“获取信息”确认漫画详情
3. 按需设置章节并发数、图片并发数、代理
4. 点击“开始下载”
5. 下载完成后可直接打包 ZIP，或在本地漫画库里导出 CBZ

### 使用首页发现和搜索

- 包子漫画、拷贝漫画支持首页分区浏览
- 漫画柜当前建议直接搜索或粘贴 URL
- “本地已下载”分区会扫描工作目录下已下载的漫画，并支持搜索和检查更新

### 手动代理与连通性测试

GUI 中提供：

- 手动填写 `HTTP/HTTPS/SOCKS5` 代理地址
- “应用代理”
- “测试连接”

适合用于站点连通性不稳定、被限流或需要代理访问的场景。

## 命令行用法

当前命令行入口 `downcomic.py` 主要服务于 `baozimh.org`。

下载整部漫画：

```bash
python downcomic.py "https://baozimh.org/chapterlist/wozhenmeixiangzhongshenga-pikapi"
```

详情页链接也可以直接传入：

```bash
python downcomic.py "https://baozimh.org/manga/dafengdagengren-chuyingshe"
```

从指定章节开始下载：

```bash
python downcomic.py "https://baozimh.org/chapterlist/wozhenmeixiangzhongshenga-pikapi" --start 10
```

启用代理池：

```bash
python downcomic.py "URL" --proxy
```

调整并发：

```bash
python downcomic.py "URL" --concurrent 3 --image-concurrent 4
```

关闭进度条：

```bash
python downcomic.py "URL" --no-progress
```

### 首页榜单抓取

抓取首页“人气排行”前 5 条：

```bash
python downcomic.py --list-homepage --homepage-section rank --homepage-limit 5
```

以 JSON 输出：

```bash
python downcomic.py --list-homepage --homepage-section recent --homepage-limit 5 --homepage-json
```

直接下载首页筛选结果中的第 1 部漫画：

```bash
python downcomic.py --homepage-section rank --homepage-limit 5 --homepage-download 1
```

### CLI 参数

| 参数 | 说明 | 默认值 |
| --- | --- | --- |
| `url` | 漫画目录页、详情页或章节页链接 | 无 |
| `--start` | 从指定章节序号开始下载 | 从头开始 |
| `--concurrent` | 最大章节并发数 | `5` |
| `--image-concurrent` | 每章节图片最大并发数 | `5` |
| `--proxy` | 启用代理池 | 关闭 |
| `--no-progress` | 禁用进度条 | 关闭 |
| `--list-homepage` | 抓取并输出首页漫画列表 | 关闭 |
| `--homepage-section` | 首页分区筛选：`all/recent/hot-update/rank/new` | `all` |
| `--homepage-limit` | 首页结果数量限制 | `10` |
| `--homepage-json` | 以 JSON 格式输出首页结果 | 关闭 |
| `--homepage-download` | 下载筛选结果中的第 N 项 | 不启用 |

## 本地文件说明

默认情况下，下载内容和运行时缓存不会写到项目目录，而是写到：

- `C:\Users\<用户名>\Downloads\ComicDownloads\`
- 如果系统没有 `Downloads` 目录，则回退到 `C:\Users\<用户名>\ComicDownloads\`
- 也可以通过环境变量 `COMIC_DOWNLOAD_DIR` 自定义保存目录
- 程序会在首次下载或写入缓存时自动创建该目录，以及其中的 `.comic_state/` 子目录

其中常见的本地文件包括：

- `.comic_state/download_resume_data.json`：断点续传信息
- `.comic_state/manga_detail_cache.json`：漫画详情缓存
- `[漫画目录]/元数据.json`：单部漫画的本地库元数据
- `[漫画名].zip`：下载完成后导出的整部漫画 ZIP
- `[漫画名]_CBZ/`：章节级 CBZ 导出目录

这些文件主要用于本地使用和恢复状态，不属于核心源码。

## 打包为 Windows EXE

安装打包工具：

```bash
pip install pyinstaller
```

一键打包：

```powershell
.\build_exe.ps1
```

脚本会：

- 调用 `comic_gui.spec`
- 在 `dist_build/<时间戳>/` 生成构建结果
- 自动把主程序重命名为 `漫画下载器.exe`
- 当前没有内置图标文件，默认按无图标方式打包

手动打包：

```bash
pyinstaller --clean --noconfirm comic_gui.spec
```

## 生成发布包

```powershell
.\create_release.ps1
```

脚本会根据 `version_info.txt` 中的版本号生成本地发布目录，典型产物如下：

- `release/漫画下载器-v2.0.2/漫画下载器.exe`
- `release/漫画下载器-v2.0.2/使用说明.txt`

这些发布文件更适合保存在本地，并按需上传 EXE 到 GitHub Releases，而不是直接放在仓库目录里版本化。

## 故障排查

### 下载失败或站点无法访问

建议按这个顺序检查：

1. 浏览器能否直接打开目标站点
2. GUI 中点击“测试连接”查看是否为网络问题
3. 必要时填写代理并重新测试
4. 适当降低章节并发和图片并发

### 漫画柜停止较慢

漫画柜部分请求是网页抓取型流程，如果刚好卡在网络请求阶段，点击“停止下载”后可能需要等待当前请求超时才会完全结束。

### GUI 无法启动

请确认：

- Python 环境可用
- `tkinter` 已安装
- 已正确安装 `requirements.txt` 中的依赖

## 开发说明

二次开发时建议优先查看：

- `site_adapters.py`：站点适配、搜索、章节获取、下载实现
- `comic_gui.py`：GUI、下载控制、本地库、ZIP/CBZ 导出
- `downcomic.py`：CLI 下载逻辑
- `run_gui.py`：GUI 启动与依赖检查
- `build_exe.ps1` / `create_release.ps1`：打包与发布流程

## 免责声明

本项目仅用于技术学习、研究与个人测试。请在遵守目标站点服务条款、版权规定及所在地法律法规的前提下使用本项目。因不当使用造成的任何后果，由使用者自行承担。

## License

This project is licensed under the MIT License. See the `LICENSE` file for details.
