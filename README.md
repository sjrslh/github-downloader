# GitHub 下载器

Windows 桌面版 GitHub 下载工具：粘贴链接 → 自动解析 Release 附件 → 按这台电脑的环境标出该下哪个 → 用多连接脚本拉下来。

![界面截图](docs/screenshot.png)

## 特性

- **无边框暗色界面**：圆角卡片、胶囊按钮，Per-Monitor V2 DPI 感知，150% 缩放下字体依然锐利
- **智能解析**：粘贴 `owner/repo`、Release 页面或资产直链都能识别；自动拉取附件列表（名称、大小、下载次数）
- **按环境推荐**：按 Windows + 架构打分，`x64` / `.exe` / `.msi` 加分，`arm64` / `linux` / `macos` / 源码包扣分，最高分那一条挂「推荐」并自动选中
- **记住下载目录**：首次启动会引导选一个默认目录，之后点右上角 ⚙ 随时改，设置写在 `%LOCALAPPDATA%\GitHub下载器\settings.ini`
- **多连接下载**：调用 `scripts/` 里的 PowerShell 引擎（分段并发、断点续传、镜像回退）
- **实时日志**：下载过程逐行滚动，可取消（结束整棵进程树），完成后一键打开目录
- **单实例 + 纯 popup 窗口**：不会重复开窗，也不会冒出系统标题栏
- **命令行参数**：`--url` / `--out` / `--auto` / `--exit`，方便脚本化调用

## 下载

到 [Releases](../../releases) 页下载：

- `GitHubDownloader.exe` —— 单文件免安装（首次运行会自动在同目录找 `scripts\gh.ps1`，找不到再回退到 `%LOCALAPPDATA%\GitHub下载器\gh.ps1`）
- `GitHubDownloader-v1.5.0-win-x64.zip` —— 完整包（exe + scripts + README + LICENSE）

## 使用

1. 双击 `GitHubDownloader.exe`
2. 第一次启动会弹出「第一次使用」，选一个默认下载目录；以后点右上角 ⚙ 可以改
3. 粘贴链接（或直接把链接拖进窗口），点「解析附件」
4. 列表里默认已经选中推荐项，点「开始下载」，日志区实时滚动；完成后点「打开文件夹」

![第一次使用](docs/first-run.png)

![设置](docs/settings.png)

支持三种输入：

| 输入 | 行为 |
|---|---|
| `https://github.com/owner/repo/releases/download/...` | 直接下这个文件 |
| `https://github.com/owner/repo`（或 `owner/repo`） | 解析 latest Release，列出附件 |
| `https://github.com/owner/repo/releases/tag/v1.2.3` | 解析指定 tag 的附件 |

### 命令行

```powershell
# 直接下载一个资产到指定目录，下完自动退出
GitHubDownloader.exe --url "https://github.com/owner/repo/releases/download/v1.0/app.zip" --out "D:\Downloads" --auto --exit

# 解析仓库的 latest Release 并列出附件（打开窗口，不自动下载）
GitHubDownloader.exe --url owner/repo
```

| 参数 | 说明 |
|---|---|
| `--url <链接或 owner/repo>` | 启动时填入目标 |
| `--out <目录>` | 临时指定下载目录（不写入设置） |
| `--auto` | 启动后立刻开始下载 |
| `--exit` | 下载结束后自动退出 |

## 目录结构

```
.
├─ src/
│  ├─ Program.cs          # 全部界面与逻辑（WinForms，单文件）
│  └─ app.manifest        # DPI 感知 + 主题
├─ scripts/               # 下载引擎（PowerShell）
│  ├─ gh.ps1              # 入口：判断下文件还是拉仓库
│  ├─ gh-dl.ps1           # 多连接分段下载
│  ├─ gh-clone.ps1        # 仓库打包下载
│  └─ Get-GhClip.ps1      # 从剪贴板取链接
├─ assets/
│  ├─ icon.png            # 图标原图（AI 生成，四角已抠透明）
│  ├─ icon.ico            # 多尺寸图标（16–128 BMP 条目 + 256 PNG）
│  └─ icon-src.webp       # 生成原稿
├─ docs/
├─ build.ps1              # 编译脚本
├─ make-icon.py           # WebP → 去白角 → PNG
└─ make-ico.py            # PNG → 多尺寸 ICO
```

## 构建

需要 Windows 自带的 .NET Framework 4.8（`csc.exe`）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
# 产物：dist\GitHubDownloader.exe（并复制 scripts\ 到 dist\scripts\）
```

加 `-Install` 会把 exe 复制到桌面。

## 图标

图标由 **FLUX.1-schnell**（Hugging Face 免费 Space）生成：`make-icon.py` 先把 WebP 转成 PNG，并用圆角蒙版把四角的白底抠成透明；`make-ico.py` 再拼出多尺寸 ICO（16–128 用 BMP 条目、256 用 PNG——Explorer 只认 BMP 条目的小图标，纯 PNG 的 ICO 在桌面上会退回旧图标或通用图标）。

## 说明

- 仅支持 Windows（用到了 DWM、WinForms 与 PowerShell）
- 下载引擎依赖机器上可用的 `curl.exe` / PowerShell 5.1（Windows 10 1803+ 自带）
- 未登录的 GitHub API 每小时 60 次，解析过于频繁会限流，界面会提示
- 设置文件：`%LOCALAPPDATA%\GitHub下载器\settings.ini`；日志：同目录 `app.log`（超过 256 KB 自动轮转）

## English

A small Windows GUI for GitHub releases and repositories: paste a link, resolve the release assets, get a recommendation that matches your OS and architecture, and download with a multi-connection PowerShell engine. Remembers your download folder (first-run setup + a gear to change it later), has a tiny CLI (`--url/--out/--auto/--exit`), single-file exe, no installer. MIT licensed.

## License

[MIT](LICENSE)
