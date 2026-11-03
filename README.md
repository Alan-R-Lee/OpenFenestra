# OpenFenestra

**桌面，自有条理。** 受 Fences 启发、独立开发的开源 Windows 桌面图标管理软件。

Windows 11 x64 · C# / WPF / .NET 10 + C++ Shell bridge · MIT License

## v0.3 原生桌面实验版

v0.3 保留 Explorer 原生桌面和未收纳图标，通过原创 C++ 组件
`FenestraShellBridge.dll` 在 Explorer 的实际桌面图标视图中协调逐项绘制、
选择和拖放。管理界面与分栏由 WPF 提供，没有额外的整屏桌面图标窗口。

当前源码已经完成原生整合，并通过本机真实 Explorer 接入、逐项隐藏、
正常退出恢复和控制器异常结束后的恢复检查。这仍是实验版，尚未达到
Fences 的完整功能与兼容性范围。

- **原生桌面拖入分栏**：收纳后，原位置不再重复绘制该图标；未收纳项目继续由 Windows 桌面管理。
- **拖回原生桌面**：从普通分栏拖到桌面空白处，解除归属并放回原生图标。来自桌面之外的文件引用会创建真实的桌面 `.lnk` 快捷方式。
- **原文件保留**：收纳、跨分栏调整和返回桌面不删除、移动或覆盖原文件。外部文件返回桌面时保留其原位置，通过快捷方式访问。
- **高清 Shell 图标**：按屏幕 DPI 请求 Windows Shell 图标；支持公共桌面的快捷方式及实际桌面视图中的系统项目。
- **透明分栏与标签页**：支持搜索、调整位置和尺寸、图标大小、独立透明度设置；双击栏头折叠，单击展开并恢复展开状态。
- **布局保存**：保存分栏、归属、标签页、折叠状态和位置，提供备份、快照与导入导出。
- **恢复原生显示**：关闭原生整合或正常退出后恢复已收纳项目的原生显示；控制器失联或保护失效时，组件停止隐藏项目，让 Windows 桌面继续可用。
- **文件夹映射**：实时只读浏览文件夹；映射页不接受拖入，也不允许拖出文件，不改变文件夹内容。
- **登录后自动运行**：保留“开机自动运行，并恢复上次布局”设置，后台启动到托盘并读取保存的布局，所有分栏以折叠栏头出现，单击后恢复原展开高度。本轮未进行 Windows 重启后的启动验收。

这里的“隐藏”是绘制和交互归属的调整。项目仍保留在 Windows Shell 的桌面视图与
文件系统中，资源管理器的桌面文件夹仍能看到对应文件。

## 下载与运行

当前原生发行目标为 **Windows 11 x64**，不提供 ARM64 原生发行包。
便携包包含 .NET 运行时，运行时无需安装 SDK 或 C++ 编译器。

1. 从 [GitHub Actions](https://github.com/Alan-R-Lee/OpenFenestra/actions) 下载 `OpenFenestra-win-x64` 产物并完整解压。
2. 运行 `OpenFenestra.exe`，创建或恢复分栏。
3. 在设置中开启“原生桌面整合（实验）：收纳后不重复显示图标”，再将原生桌面图标拖入普通分栏。
4. 如需登录后使用，保留或勾选“开机自动运行，并恢复上次布局”。

请保留 EXE 旁的 `FenestraShellBridge.dll`、`licenses` 和 `docs` 目录。
原生组件和对应的 runtime notices 是发行包的一部分。

关闭管理窗口会收起到托盘；通过托盘菜单可退出应用或恢复 Windows 原生桌面图标。
`Ctrl+Alt+Space` 用于置顶速览分栏，再按一次取消置顶。

## 便携、配置与安装

程序在运行时读取桌面路径与显示器信息，不依赖固定盘符或用户名。

```powershell
# 配置保存在程序旁的 data 目录
.\OpenFenestra.exe --portable

# 也可在 EXE 旁创建 portable.mode 空文件，再正常启动

# 显式指定配置目录
.\OpenFenestra.exe --data-dir "D:\OpenFenestra-Test"

# 后台启动到托盘
.\OpenFenestra.exe --tray
```

普通用户环境默认将配置保存在 `%LOCALAPPDATA%\OpenFenestra`。
MSIX 等打包宿主可能重定向 AppData；可使用 `--data-dir` 明确配置位置。
整目录迁移便携包时，布局中的外部文件路径仍指向原文件，需要重新选择迁移后失效的路径。

可选安装脚本将程序复制到当前用户的程序目录，支持通过 `-Destination` 指定位置：

```powershell
.\scripts\build.ps1
.\scripts\install.ps1 -StartWithWindows -DesktopIntegration
```

开机启动优先注册当前用户的登录任务（无附加延迟、普通权限、交互会话，不保存密码），成功后移除旧 Run 项。系统策略不允许任务时回退 Run 项，管理窗口会提示可能被 Windows 延迟。
安装位置和登录任务（或 Run 项）中的程序路径应在实际安装环境中核对；当前开发宿主存在 AppData
重定向情况，不能据此宣称已通过重启验收。开机启动指当前用户登录后运行。

更新前关闭原生整合并通过托盘退出应用；安装脚本检测到 DLL 仍被占用时会停止更新。
用户布局与程序分开保存，更新程序不需要删除配置。

## 从源码构建

需要 Windows x64、[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，
以及以下任一 C++ 工具链：

- Visual Studio / Build Tools 的 MSVC x64 C++ 工具与 Windows SDK；脚本通过 `vswhere` 查找。
- MSYS2 UCRT64 的 MinGW-w64 `g++`，将其 `bin` 加入 `PATH`；也可向原生构建脚本传入 `-CompilerPath`。

```powershell
# 原生组件及隔离测试，不接入真实 Explorer
.\scripts\build-native.ps1 -SelfTest

# 普通构建自动增量构建并复制原生 DLL 和 runtime notices
dotnet build OpenFenestra.sln -c Release

# 核心与界面冒烟检查
.\scripts\test.ps1

# 构建含 .NET runtime 的 win-x64 便携 ZIP
.\scripts\build.ps1
```

`scripts/test-desktop.ps1` 是交互桌面检查：会临时接入真实 Explorer、使用专用测试项，
并验证正常退出与故意结束测试控制器后的恢复。运行时应关闭其他 OpenFenestra 原生整合实例。
隔离原生测试和真实桌面检查的范围见[验证说明](docs/TESTING.md)。

MinGW 构建使用静态 GCC/C++ runtime，并复制当前工具链的完整许可证。
非标准许可目录可通过 `OPENFENESTRA_MINGW_LICENSE_ROOT` 指定，详见
[原生 runtime notices](docs/licenses/native-runtime/README.md)。MSVC 与 MinGW 的
许可材料分别记录，生成的 `licenses/native-runtime/build-info.json` 标明实际编译器和哈希。

v0.3 的原生组件仅支持 x64，`scripts/build.ps1 -Runtime win-arm64` 会明确拒绝。
用于管理界面或核心开发的纯托管构建可使用 `-p:EnableNativeShellBridge=false`；
该构建不包含原生桌面整合能力。

## 当前边界

- 分栏跨 Windows 虚拟桌面的固定适配器已接入；它使用非公开 Shell 接口，实际虚拟桌面切换尚未验收，不能视为已验证的跨桌面兼容性。
- 本机真实桌面检查已通过，更多 Windows 版本、混合 DPI、多显示器热插拔、Explorer 重启及其他桌面增强软件共存仍需验证。
- 已收纳项目的原坐标处会拒绝外部文件投递，避免文件误投到已隐藏的原生目标。
- 不包含 Fences 的全部高级功能，例如桌面分页、自定义脚本、完整 Shell 上下文菜单和企业部署。
- 图标细节受原应用提供的素材限制；只有低分辨率图标的应用无法凭空获得更多细节。
- 单页最多保存 10,000 项，分栏最多显示搜索结果前 300 项；可以通过搜索缩小范围。

## 文档与许可证

[调研记录](docs/RESEARCH.md) · [原生桌面专项研究](docs/NATIVE_DESKTOP_RESEARCH.md) · [架构](docs/ARCHITECTURE.md) · [验证说明](docs/TESTING.md) · [第三方说明](THIRD_PARTY_NOTICES.md)

调研文档保留各研发阶段的方案和判断；当前实现与验证范围以本版说明和验证文档为准。

[MIT](LICENSE) © 2026 Alan-R-Lee and contributors。应用及原生桥接源码独立开发，
不依赖 Stardock 软件；不包含其源码、二进制文件、图片或授权数据。
OpenFenestra 与 Stardock 无隶属或合作关系。

管理控制台的“统一透明度”可分别调整所有分栏展开、折叠时的背景透明度，实时生效并自动保存；数值越大越透明。各分栏设置不同时，滑块旁会显示提示。

“我的分组”支持拖动卡片或左侧 ≡ 调整默认整理顺序；绿色线标示插入位置，列表边缘支持自动滚动。顺序自动保存，点击“整理分栏位置”按新顺序排列桌面；搜索过滤时也可相对目标分组调整顺序。

左侧“设置”独立提供桌面整合、开机自启动、跨虚拟桌面显示、自动归类及整理/折叠/展开/退出操作。

桌面分组页整体滚动，上滑后标题、归类横幅与统计区移出视野，为分组拖动排序腾出空间；下滑即可返回顶部。
