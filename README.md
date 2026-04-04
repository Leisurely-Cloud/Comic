# 🚀 优化版漫画下载器

这是一个功能强大的漫画下载器，支持从 baozimh.org 下载漫画章节。

## ✨ 主要特性

- **⚡ 高速并发下载**：支持章节和图片双重并发
- **🔄 智能代理池**：自动获取和验证免费代理
- **📊 实时进度条**：可视化下载进度
- **💾 断点续传**：自动跳过已下载内容
- **🛡️ 错误重试**：智能重试机制提高成功率
- **🎯 灵活配置**：丰富的命令行参数

## 📦 安装依赖

```bash
# 确保虚拟环境已激活
pip install -r requirements.txt
```

## 🪟 打包为 EXE

这个项目可以直接打包成一个独立的 Windows 可执行文件。

### 1. 安装打包工具

```bash
pip install pyinstaller
```

### 2. 一键打包

在项目根目录运行：

```bash
.\build_exe.ps1
```

脚本会自动：
- 清理旧的 `build/` 和 `dist/`
- 使用 [comic_gui.spec](/d:/GitHub项目/Comic/comic_gui.spec) 打包
- 在 `dist/` 下生成 `漫画下载器.exe`

### 3. 手动打包命令

如果你不想用批处理脚本，也可以直接运行：

```bash
pyinstaller --clean --noconfirm comic_gui.spec
```

### 4. 打包结果

生成后的文件位置：

```text
dist/漫画下载器.exe
```

双击即可启动图形界面，不需要再手动运行 Python 脚本。

### 5. 说明

- 当前使用 `run_gui.py` 作为入口，会先检查运行所需模块，再启动 GUI
- 打包后的 `.exe` 会跳过依赖检查，直接进入图形界面
- 打包后默认是 `windowed` 模式，不会弹出黑色控制台窗口
- 如果项目根目录存在 `app.ico`，打包时会自动作为应用图标写入 exe
- 如果 `app.ico` 不是合法的 ICO 格式，打包时会自动忽略该图标，不会阻塞生成 exe
- 程序窗口也会优先读取根目录下的 `app.ico` 作为左上角图标
- `version_info.txt` 用于写入 exe 的产品名、版本号和文件说明，可按需自行修改

### 6. PNG 转 ICO

如果你手里只有 PNG，可以直接用项目内脚本转换：

```bash
pip install pillow
python make_icon.py
```

默认会把根目录下的 `icon.png` 转成 `app.ico`。

也可以手动指定输入和输出：

```bash
python make_icon.py my_icon.png app.ico
```

### 7. 整理发布目录

如果你想把“给别人发送的成品”和源码目录分开，可以运行：

```bash
.\create_release.ps1
```

脚本会在 `release/漫画下载器/` 下生成一个干净的发布目录，包含：
- `漫画下载器.exe`
- `使用说明.txt`

## 🎯 使用方法

### 基本用法
```bash
# 下载整个漫画
python downcomic.py "https://baozimh.org/chapterlist/wozhenmeixiangzhongshenga-pikapi"

# 从指定章节开始下载
python downcomic.py "https://baozimh.org/chapterlist/wozhenmeixiangzhongshenga-pikapi" --start 10
```

### 高级用法
```bash
# 启用代理池（适合网络环境差的情况）
python downcomic.py "URL" --proxy

# 调整并发数（默认都是5）
python downcomic.py "URL" --concurrent 3 --image-concurrent 4

# 禁用进度条（适合日志记录）
python downcomic.py "URL" --no-progress
```

## ⚙️ 参数说明

| 参数 | 说明 | 默认值 |
|------|------|--------|
| `--start` | 从第几章开始下载 | 从头开始 |
| `--concurrent` | 章节并发下载数 | 5 |
| `--image-concurrent` | 每章节图片并发数 | 5 |
| `--proxy` | 启用代理池 | 关闭 |
| `--no-progress` | 禁用进度条 | 显示 |

## 🚀 性能优化亮点

### 1. **并发下载系统**
- **25倍速度提升**：从串行下载改为5×5并发
- **智能调度**：ThreadPoolExecutor管理任务队列
- **资源控制**：防止过度并发导致封IP

### 2. **网络优化**
- **连接复用**：HTTP连接池减少握手开销
- **代理轮换**：自动切换代理避免IP封禁
- **智能重试**：失败后自动重试，提高成功率

### 3. **用户体验**
- **实时进度**：tqdm进度条显示下载状态
- **断点续传**：自动检测已下载文件
- **友好提示**：清晰的错误信息和状态报告

## 💡 使用建议

### 网络环境好
```bash
python downcomic.py "URL" --concurrent 5 --image-concurrent 5
```

### 网络环境差
```bash
python downcomic.py "URL" --proxy --concurrent 3 --image-concurrent 3
```

### 服务器限制严格
```bash
python downcomic.py "URL" --concurrent 2 --image-concurrent 2
```

## 📁 文件结构

```
Comic/
├── build_exe.bat        # Windows 一键打包脚本
├── build_exe.ps1        # PowerShell 打包脚本
├── comic_gui.spec       # PyInstaller 打包配置
├── create_release.ps1   # 整理发布目录
├── make_icon.py         # PNG 转 ICO 脚本
├── version_info.txt     # EXE 版本信息
├── downcomic.py          # 主程序
├── requirements.txt      # 依赖列表
├── README.md            # 使用说明
└── [漫画名称]/          # 下载的漫画文件夹
    ├── 001_章节名/
    │   ├── 001.webp
    │   ├── 002.webp
    │   └── ...
    └── 002_章节名/
        └── ...
```

## ⚠️ 注意事项

1. **尊重版权**：仅下载有授权的漫画内容
2. **合理使用**：避免过度频繁的请求
3. **网络环境**：根据网络状况调整并发参数
4. **存储空间**：确保有足够的磁盘空间

## 🔧 故障排除

### 下载失败
- 检查网络连接
- 尝试启用 `--proxy` 参数
- 降低并发参数

### 进度条不显示
- 某些终端可能不支持，使用 `--no-progress` 参数

### 代理获取失败
- 检查网络是否能访问GitHub
- 代理源可能暂时不可用，稍后再试

## 📞 支持

如有问题，请检查网络连接和参数设置，或尝试调整并发数。
# -
