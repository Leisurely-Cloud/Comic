# Comic

一个面向 `baozimh.org` 的漫画下载工具，支持 **命令行下载** 与 **Windows 图形界面** 两种使用方式。项目提供章节并发下载、图片并发下载、断点续传、首页榜单抓取、代理池支持，以及完整的 PyInstaller 打包与发布流程。

适合以下场景：

- 想通过命令行批量下载漫画
- 想直接双击 EXE 在 Windows 上使用
- 想从首页榜单中浏览并选择漫画下载
- 想将项目进一步打包、分发或二次开发

## 功能特性

- 图形界面启动与下载
- 命令行批量下载漫画章节
- 支持 `manga/...` 详情页、`chapterlist/...` 目录页、章节页链接
- 支持从指定章节开始下载
- 章节与图片双层并发下载
- 自动跳过已下载内容，支持断点续传
- 支持首页榜单抓取与直接下载
- 支持可选代理池，适合网络不稳定场景
- 支持进度条显示与日志输出
- 支持打包为独立 Windows 可执行文件

## 项目结构

```text
Comic/
├── build_exe.ps1        # PowerShell 打包脚本
├── comic_gui.py         # 图形界面主程序
├── comic_gui.spec       # PyInstaller 打包配置
├── create_release.ps1   # 生成发布目录与压缩包
├── downcomic.py         # 命令行下载核心
├── make_icon.py         # PNG 转 ICO 工具
├── README.md            # 项目说明
├── requirements.txt     # Python 依赖
├── run_gui.py           # GUI 启动入口
├── version_info.txt     # Windows EXE 版本信息
├── release/             # 发布产物目录
└── [漫画名称]/          # 下载后的漫画目录
```

## 环境要求

- Python 3.10+ 推荐
- Windows 10/11 推荐用于 GUI 与 EXE 打包
- 需要可访问目标站点及相关资源
- 图形界面依赖 `tkinter`（Windows 下通常随 Python 自带）

## 安装

### 1. 克隆项目

```bash
git clone https://github.com/Leisurely-Cloud/Comic.git
cd Comic
```

### 2. 安装依赖

```bash
pip install -r requirements.txt
```

依赖列表：

- `requests`
- `beautifulsoup4`
- `tqdm`
- `lxml`
- `pillow`

## 快速开始

### 图形界面启动

```bash
python run_gui.py
```

说明：

- 启动器会先检查依赖
- 非打包环境下会尝试提示缺失依赖
- 打包后的 EXE 会直接进入图形界面

### 命令行下载

下载整部漫画：

```bash
python downcomic.py "https://baozimh.org/chapterlist/wozhenmeixiangzhongshenga-pikapi"
```

也支持直接传详情页链接：

```bash
python downcomic.py "https://baozimh.org/manga/dafengdagengren-chuyingshe"
```

从指定章节开始下载：

```bash
python downcomic.py "https://baozimh.org/chapterlist/wozhenmeixiangzhongshenga-pikapi" --start 10
```

## 使用示例

### 常用命令

启用代理池：

```bash
python downcomic.py "URL" --proxy
```

调整章节与图片并发数：

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

以 JSON 输出首页结果：

```bash
python downcomic.py --list-homepage --homepage-section recent --homepage-limit 5 --homepage-json
```

直接下载首页筛选结果中的第 1 部漫画：

```bash
python downcomic.py --homepage-section rank --homepage-limit 5 --homepage-download 1
```

## 命令行参数

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

## 首页分区说明

当前支持抓取以下首页内容：

- `recent`：近期更新
- `hot-update`：热门更新
- `rank`：人气排行
- `new`：最新上架

输出结果通常包含：

- 漫画名称
- 封面地址
- 详情页链接
- 目录页链接
- 最近章节信息
- 更新时间

抓到的详情页或目录页都可以继续传给下载命令。

## 下载输出说明

下载后的漫画会保存在项目根目录下，以漫画名自动创建目录，例如：

```text
大奉打更人/
├── 001_章节名/
│   ├── 001.webp
│   ├── 002.webp
│   └── ...
└── 002_章节名/
    └── ...
```

命名特点：

- 自动清理非法文件名字符
- 章节目录带顺序编号，便于排序
- 已下载图片会自动跳过，支持重复执行恢复下载

## 打包为 Windows EXE

项目已包含完整的 PyInstaller 打包配置。

### 安装打包工具

```bash
pip install pyinstaller
```

### 一键打包

```powershell
.\build_exe.ps1
```

该脚本会：

- 使用 `comic_gui.spec` 打包
- 自动创建新的构建输出目录
- 输出最终 `漫画下载器.exe` 的生成路径

### 手动打包

```bash
pyinstaller --clean --noconfirm comic_gui.spec
```

### 打包说明

- 入口为 `run_gui.py`
- 打包后默认使用窗口模式，不弹出控制台
- 若项目根目录存在 `app.ico`，会优先作为程序图标
- `version_info.txt` 用于写入 EXE 的版本信息与产品信息

## 生成发布包

如果需要整理一个可直接分发给他人的版本，可以执行：

```powershell
.\create_release.ps1
```

脚本会根据 `version_info.txt` 中的版本号，生成发布目录与压缩包，例如：

- `漫画下载器.exe`
- `使用说明.txt`
- `漫画下载器-v2.0.0/`
- `comic-downloader-v2.0.0-windows.zip`

## 图标生成

如果你只有 PNG 图标，可以使用内置脚本转换为 ICO：

```bash
pip install pillow
python make_icon.py
```

指定输入输出文件：

```bash
python make_icon.py my_icon.png app.ico
```

## 性能与实现亮点

- 基于 `ThreadPoolExecutor` 的章节并发与图片并发下载
- 基于 `requests.Session` 的连接复用
- 失败自动重试，提高下载成功率
- 可选代理池抓取与验证机制
- 自动识别首页漫画卡片与章节列表
- 自动跳过已下载文件，适合中断后继续执行

## 故障排查

### 下载失败

可依次检查：

- 网络是否正常
- 目标站点是否可访问
- 是否需要启用 `--proxy`
- 是否需要降低并发参数

示例：

```bash
python downcomic.py "URL" --proxy --concurrent 2 --image-concurrent 2
```

### 进度条显示异常

某些终端环境下进度条可能显示不完整，可以关闭：

```bash
python downcomic.py "URL" --no-progress
```

### GUI 无法启动

请确认：

- Python 环境可用
- `tkinter` 已安装
- 已正确安装 `requirements.txt` 中的依赖

### 代理池不可用

免费代理具有不稳定性，若代理源失效或可用率较低，可暂时关闭代理模式直接下载。

## 开发说明

如果你打算进行二次开发，建议优先查看：

- `downcomic.py`：下载逻辑、参数处理、首页抓取
- `comic_gui.py`：图形界面逻辑
- `run_gui.py`：启动入口与依赖检查
- `comic_gui.spec`：PyInstaller 打包配置

## 免责声明

本项目仅用于技术学习、研究与个人测试用途。请在遵守目标站点服务条款、版权规定及所在地法律法规的前提下使用本项目。因不当使用造成的任何后果，由使用者自行承担。

## License

当前仓库未附带明确开源许可证。若你准备公开分发、接受外部贡献或发布到更大范围，建议补充 `LICENSE` 文件后再正式对外发布。
