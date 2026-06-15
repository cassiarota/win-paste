# PasteNowWin

一个 Windows 上的剪贴板历史管理器,参考 macOS 的 **PasteNow** 设计。解决系统自带剪贴板(Win+V)
**重启即清空**、使用不便的痛点:历史记录用 SQLite **持久化保存**,通过全局快捷键随时调出、搜索、粘贴。

> 平台:Windows 10 / 11 · 技术栈:C# / .NET 8 / WPF

---

## 下载

到 [Releases](https://github.com/cassiarota/win-paste/releases) 下载,**两种都自带 .NET 运行时,无需额外安装**:

| 文件 | 用法 |
|---|---|
| `PasteNowWin-*-Setup.exe` | **安装版**——双击安装(无需管理员、无 UAC,装到用户目录),可勾选开机自启 |
| `PasteNowWin-*-win-x64.zip` | **便携版**——解压后直接双击 `PasteNowWin.exe` 运行 |

程序启动后无主窗口,常驻系统托盘(右下角橙色图标)。

也可从**官网**直接下载(由 VPS 托管,始终是最新构建):
在 Cassian Engines 门户首页(`/`)点 PasteNowWin 卡片进入 `/pastenow/`,页面内即有下载按钮。

> CI 流程见 [`.github/workflows/build-and-release.yml`](.github/workflows/build-and-release.yml):
> 每次 push 到 `main` 自动构建,并把最新 exe / 安装包部署到 VPS(覆盖,只保留最新一份);
> 打 `v*` 标签时额外发布一个带版本号的 GitHub Release。官网静态页见仓库 [`site/`](site/)。

---

## 核心功能

| 快捷键(默认,可自定义) | 功能 |
|---|---|
| **Ctrl + Shift + V** | 弹出剪贴板历史框,搜索并选择要粘贴的内容 |
| **Ctrl + Shift + B** | 将最近一条历史以**纯文本**粘贴到当前窗口 |

- 自动监听剪贴板,保存**文本 / 图片 / 文件**三类内容,跨重启持久化
- **顶部搜索框**实时过滤历史记录
- **类型分组**:弹窗顶部 `全部 / 文本 / 图片 / 文件 / 置顶` 标签一键筛选
- 粘贴后该项自动**置顶**(LRU,最近使用排最前)
- 历史去重(相同内容自动置顶,不重复入库)
- 弹窗内:`↑↓` 选择 · `Enter` 粘贴 · `Ctrl+Enter` 纯文本粘贴 · `Esc` 关闭 · **右键菜单**(粘贴 / 纯文本粘贴 / 置顶 / 删除)
- **自定义快捷键**:在设置里录制任意组合键(需含修饰键)替换默认值
- **提示音**:可选「复制时播放提示音」
- **多屏支持**:弹窗在光标所在的显示器上弹出(per-monitor DPI 自适应)
- **过期自动清理**:可在设置里选择 1 / 7 / 30 / 90 天或永不过期(置顶项不受影响)
- **排除规则**:在设置里按进程名(如 `KeePass`、`1Password`)排除,来自这些程序的复制不被记录
- **自动检查更新**:启动时查询 GitHub Release,有新版本时托盘气泡提示,点击一键打开下载
- 系统托盘菜单:打开历史 / 设置 / 检查更新 / 开机自启 / 退出
- 置顶项常驻列表顶部且不会被自动清理(最多保留 1000 条非置顶记录)

> 快捷键说明:Windows 的 `Win+V` 已被系统占用,故沿用 PasteNow 的 `Ctrl+Shift+V` / `Ctrl+Shift+B`
> 习惯(后续阶段会做成可自定义)。

---

## 构建与运行

### 1. 安装 .NET 8 SDK

到 <https://dotnet.microsoft.com/download/dotnet/8.0> 下载 **.NET 8 SDK (Windows x64)** 并安装。
验证:

```powershell
dotnet --version   # 应输出 8.x.x（或更高）
```

> 工程目标框架为 `net8.0-windows`。若你装的是 .NET 9/10 SDK,把
> `src/PasteNowWin/PasteNowWin.csproj` 里的 `net8.0-windows` 改成对应版本即可。

### 2. 还原 + 运行(开发)

在仓库根目录:

```powershell
dotnet restore src/PasteNowWin/PasteNowWin.csproj
dotnet run --project src/PasteNowWin/PasteNowWin.csproj
```

启动后**没有主窗口**——程序常驻系统托盘(右下角橙色图标)。复制点东西,然后按
`Ctrl+Shift+V` 调出历史框。

### 3. 发布为单文件 exe(自带运行时,免装 .NET)

```powershell
dotnet publish src/PasteNowWin/PasteNowWin.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物在 `src/PasteNowWin/bin/Release/net8.0-windows/win-x64/publish/PasteNowWin.exe`,
双击即可运行;放到开机自启目录或在设置里勾选「开机自启」。

---

## 数据位置

历史数据库:`%LOCALAPPDATA%\PasteNowWin\history.db`(SQLite)。删除该文件即清空全部历史。

---

## 架构

```
src/PasteNowWin/
├── App.xaml(.cs)              # 入口:装配各服务、托盘、生命周期、单实例
├── app.manifest              # DPI 感知 + asInvoker
├── Models/
│   ├── ClipItemType.cs       # Text / Image / Files
│   └── ClipboardItem.cs      # 历史条目模型
├── Interop/
│   └── NativeMethods.cs      # Win32 P/Invoke + SendCtrlV / ForceForeground 组合方法
├── Services/
│   ├── NativeMessageWindow.cs# 隐藏的 message-only 窗口,统一接收 WM_* 消息
│   ├── ClipboardMonitor.cs   # AddClipboardFormatListener → 解析剪贴板为 ClipboardItem
│   ├── HotkeyManager.cs      # RegisterHotKey → WM_HOTKEY 事件
│   ├── HistoryStore.cs       # SQLite 持久化(增删查、去重、置顶、过期清理、设置 meta 表)
│   ├── PasteService.cs       # 写剪贴板 → 还原焦点 → 注入 Ctrl+V
│   ├── StartupManager.cs     # 注册表 Run 键控制开机自启
│   ├── UpdateService.cs      # 查询 GitHub Release API 检查新版本
│   └── TrayIconFactory.cs    # 运行时绘制托盘图标(免二进制 .ico)
└── Views/
    ├── PopupWindow.xaml(.cs)  # Ctrl+Shift+V 历史弹窗
    ├── PopupItemVm.cs        # 列表项展示模型(缩略图/图标/时间)
    └── SettingsWindow.xaml(.cs)
```

**数据流:** 复制 → `WM_CLIPBOARDUPDATE` → `ClipboardMonitor` 解析 → `HistoryStore.Add`。
粘贴 → 弹窗选中 → 隐藏弹窗 → `PasteService` 写入剪贴板并把焦点还给原窗口 → `SendInput(Ctrl+V)`。

---

## 已知限制 / 后续阶段

- 粘贴注入无法作用于**以管理员权限运行**的窗口(本程序是 asInvoker)。如需对管理员窗口粘贴,
  需让本程序也以管理员身份运行。
- `Ctrl+Shift+V` 为全局热键,会覆盖个别应用内置的「无格式粘贴」;**自定义快捷键**计划在 P3 实现。
- **自动更新依赖仓库公开**:更新检查走 GitHub 公共 Release API、不带任何 token。仓库处于私有
  状态时检查会静默返回「无更新」,**公开后即自动生效**(无需改代码)。
- 尚未实现:自定义快捷键、提示音、云同步、自动下载并静默安装。

---

## 开发计划表

| 阶段 | 模块 | 交付物 | 状态 |
|---|---|---|---|
| **P1 MVP** | 监听 / 热键 / 存储 / 粘贴 / 弹窗 / 托盘 | 可构建运行的剪贴板管理器 | ✅ 完成 |
| **P2 体验** | LRU 置顶、搜索、右键菜单(置顶/删除)、多屏跟随光标 + 高 DPI、过期自动清理 | 完善的弹窗交互 | ✅ 完成 |
| **P4 打包** | 单文件发布 + Inno Setup 安装器 + CI 自动发布 Release | `.exe` / `Setup.exe` | ✅ 完成 |
| **P3 配置** | 类型分组、排除规则、自动检查更新、自定义快捷键、提示音 | 完善设置页 | ✅ 完成 |
| **P5 官网** | 静态官网([`site/`](site/))+ VPS 托管下载 + CI 自动部署(只留最新) | 门户卡片 → 介绍页 → 下载 | ✅ 完成 |
| P6 进阶 | 云同步、自动下载安装更新 | — | ⬜ 待做 |
