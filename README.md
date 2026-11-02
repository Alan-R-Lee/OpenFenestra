# OpenFenestra

**桌面，自有条理。** 受 Fences 启发、独立开发的开源 Windows 桌面图标管理软件。

Native Windows desktop organizer · C# / WPF / .NET 10 · MIT License

## 原生桌面整合的研发状态

本项目正在调整为保留 Explorer 原生桌面的架构。下面的 v0.2 桌面接管功能属于旧的自有图标层实现，**不等同于原生 Shell 整合**；目前没有发布满足“原生桌面逐项隐藏 + 双向拖放 + Windows 虚拟桌面互通”的新版本。试用现有版本时，请保持“桌面接管”关闭。

2026-11-02 的[原生桌面专项研究](docs/NATIVE_DESKTOP_RESEARCH.md)核对了 DexCorral、Windhawk、Pickets、Windows Shell 官方接口及虚拟桌面参考代码。后续主方向是原生 Shell 桥接组件，在 Explorer 的实际图标视图中协调绘制、选择和拖放；离屏放置不作为主方案。本轮新增的 `VirtualDesktopPinning` 仅是尚未接入界面的适配器，不能视为已验证的跨桌面功能。

![管理窗口](docs/images/manager.png)

![桌面分栏](docs/images/panel.png)

## v0.2 历史实现

- **桌面与分栏双向拖拽**：开启桌面接管后，拖入即收纳，拖出即回到桌面；同一图标不会同时在桌面和普通分栏中显示。
- **原文件保留**：改变显示归属，不搬动、删除或覆盖文件。公共桌面快捷方式也能收纳。
- **高清图标**：使用 Windows Shell Image Factory，按屏幕 DPI 请求高分辨率图标，保留原生像素，由 WPF 高质量缩放。
- **轻量分栏**：紧凑栏头、透明背景、标签页、搜索、自由移动和缩放。图标大小、展开与折叠不透明度均可设置。
- **双击折叠、单击展开**：折叠后只保留更透明的栏头；展开恢复原高度和透明度。
- **登录 Windows 后自动运行**：可选启用，后台启动到托盘，恢复图标归属、位置、标签页及折叠状态。
- **布局恢复**：原子保存、上一版备份、快照、导入导出，兼容迁移 v0.1 布局。
- **退出恢复**：正常退出恢复 Windows 原生图标；独立守护进程在应用异常结束后执行同样的恢复。
- **文件夹映射**：只读浏览实时文件夹，支持 FileSystemWatcher 刷新。

本项目没有与 Fences 的完整功能等价保证；采用自有桌面显示层，不注入 Explorer，也不依赖 Stardock 软件。

## 下载与运行

目标平台：Windows 10/11 x64。便携包内置 .NET 运行时，无需另外安装 .NET，也不需要管理员权限。

1. 在 [GitHub Actions](https://github.com/Alan-R-Lee/OpenFenestra/actions) 下载 `OpenFenestra-win-x64` 产物并解压。
2. 运行 `OpenFenestra.exe`。
3. 创建分栏并通过栏头菜单添加标签页；当前原生整合仍在研发，请保持“桌面接管”关闭。
4. 如需自动启动，勾选“开机自动运行，并恢复上次布局”。

默认配置位于 `%LOCALAPPDATA%\OpenFenestra`。关闭管理窗口只收起到托盘，右键托盘可退出或恢复原生桌面。

`Ctrl+Alt+Space` 置顶速览分栏，再按一次取消置顶。

## 便携与可选安装

程序没有写死盘符、用户名或桌面位置。桌面路径和显示器信息在运行时读取。

```powershell
# 真正的目录便携模式：配置保存在程序旁的 data 目录
.\OpenFenestra.exe --portable

# 或在 EXE 旁创建名为 portable.mode 的空文件，再正常启动

# 指定独立配置目录
.\OpenFenestra.exe --data-dir "D:\OpenFenestra-Test"

# 后台启动到托盘
.\OpenFenestra.exe --tray
```

便携模式可整目录复制到另一台 Windows 电脑。布局中的外部文件路径仍指向原文件；迁移电脑后应重新选择失效的文件或文件夹。

可选安装脚本把已构建程序复制到当前用户的固定程序目录。开机启动仅在显式启用时写入当前用户的 Run 项，不创建服务或计划任务：

```powershell
.\scripts\build.ps1
.\scripts\install.ps1 -StartWithWindows
```

更新安装目录前请通过托盘退出应用。程序与用户布局分开保存，替换 EXE 不需要删除配置。

## 从源码构建

在 Windows 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

```powershell
dotnet build OpenFenestra.sln -c Release
.\scripts\test.ps1
.\scripts\build.ps1

# Windows ARM64 交叉构建；尚未在本机验证 ARM64 运行
.\scripts\build.ps1 -Runtime win-arm64
```

`scripts/test.ps1` 运行核心和 WPF 冒烟测试。`scripts/test-desktop.ps1` 是交互桌面测试：会短暂接管桌面，再验证正常及异常退出恢复；不要与另一个已开启接管的实例同时运行。

## 桌面接管的行为

接管期间，Explorer 原生图标列表临时隐藏，自有图标层显示未收纳项目，分栏显示已收纳项目。图标原文件一直保留；普通文件和公共桌面的快捷方式都不需要转移到私有文件夹。

从其他文件夹拖入分栏时保存引用；拖出到接管桌面时作为桌面引用显示，不复制文件内容。退出后恢复原生显示，原本就在 Windows 桌面的图标重新出现；外部文件引用仍由本应用的布局保存。

文件夹映射页是独立的只读目录视图，不参与普通分栏的排他归属。退出前及异常恢复守护进程均使用原来的图标可见状态，不修改“显示桌面图标”的持久系统设置。

## 当前边界

- 不包含 Fences 的所有高级功能，例如桌面分页、自定义脚本、完整 Shell 上下文菜单和企业部署。
- 原生图标与其他桌面增强软件的共存、Explorer 重启、虚拟桌面和混合 DPI 多显示器仍需更多设备验证。
- 图标清晰度受原软件提供的图标素材限制；旧应用只有小尺寸图标时无法生成不存在的细节。
- 单页最多保存 10,000 项，分栏最多显示搜索结果前 300 项，接管桌面最多显示 1,000 项。
- 开机运行指登录当前 Windows 用户后启动；不会在 Windows 登录界面或其他用户会话中运行。

## 文档与许可证

[调研记录](docs/RESEARCH.md) · [原生桌面专项研究](docs/NATIVE_DESKTOP_RESEARCH.md) · [架构](docs/ARCHITECTURE.md) · [验证说明](docs/TESTING.md) · [第三方说明](THIRD_PARTY_NOTICES.md)

[MIT](LICENSE) © 2026 Alan-R-Lee and contributors. 本项目与 Stardock 无隶属或合作关系，不包含其源码、二进制文件、图片或授权数据。
