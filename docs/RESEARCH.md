# 调研记录

记录日期：2026-11-02。下面区分本机文件观察、公开文档信息与实现选择。项目使用独立编写的源码，不以任何专有程序为运行依赖。

## 本机 Fences 6

对标准安装目录 `%ProgramFiles(x86)%\Stardock\Fences` 进行了只读检查。

| 观察项 | 结果 |
| --- | --- |
| `FencesVersion.txt` 和 EXE/DLL 版本资源 | 6.5.4.1 |
| `Fences.exe.config` | 声明 .NET Framework v4/v2 启动兼容配置 |
| `Fences.exe` | 版本说明为 Fences Settings |
| `DesktopDock64.dll` | 版本说明为 Stardock Fences |
| 安装目录 | 包含 x86/x64/ARM64 DLL、菜单组件、CLI、图像、字体、布局和 EULA |
| `Layouts/*.fencelayout` | XML 布局描述；读取了一个预设以了解相对尺寸、方向和对齐的概念 |
| 用户配置目录 | 确认存在 Backups、Debug、TroubleshootingLog 目录，仅枚举目录信息 |
| ProgramData 产品目录 | 确认存在版本与授权相关文件；未读取授权文件内容 |
| 可开发源码 | 该安装目录内未发现 C#/C++ 源工程 |

没有反编译、反汇编、加载产品 DLL、调用产品 CLI、执行安装目录脚本、改写 Fences 配置或复制产品资产。目录名及配置格式只用来理解软件组成，不表示可以取得其源码。上述本机文件及用户资料没有加入仓库。

## 官方功能资料

- [Fences 官方产品页](https://www.stardock.com/products/fences/)：分组区域、文件夹入口、规则整理、速览和桌面布局等功能的公开介绍。
- [Stardock Releases Fences 6](https://www.stardock.com/news/535945/stardock-releases-fences-6)：第 6 版引入标签页和图标着色。
- [Fences 6 Welcome 文档](https://support.stardock.com/space/SHC/2814443582)：作为官方帮助入口；该页在工具抓取中只返回导航，因此未据此推断具体实现。

功能灵感映射：

| 目标体验 | OpenFenestra v0.1 实现 | 状态 |
| --- | --- | --- |
| 可移动、可折叠分组 | 独立 WPF 面板、标题拖动、右下角缩放 | 已实现 |
| 标签页 | 每个分组包含普通页或文件夹映射页 | 已实现 |
| 文件夹入口 | 只读文件列表 + FileSystemWatcher | 已实现 |
| 自动整理 | 按文件类型添加引用；可监听新增项目 | 基础实现 |
| 速览 | Ctrl+Alt+Space / 托盘切换置顶 | 已实现 |
| 布局恢复 | JSON 原子写入、备份、快照、导入导出 | 已实现 |
| 原生桌面图标托管 | 不读取/改写 Explorer 图标列表 | 未实现 |
| 多桌面分页、图标着色、完整 Shell 菜单 | 后续研究方向 | 未实现 |

## 社区代码阅读

调研副本保存在被 Git 忽略的 `.research`；仓库没有收录其代码或素材。

### PecoFence

- 仓库：[DayuanJiang/PecoFence](https://github.com/DayuanJiang/PecoFence)
- 当次树 SHA：`368d1a14e07f4aef8421be76a988d4d01b2a1c69`
- 阅读范围：README、LICENSE、`crates/platform/src/desktop.rs`、`crates/platform/src/window.rs`。
- 技术启发：不同 Windows 版本中 `SHELLDLL_DefView` 的父窗口可能不同；桌面集成需要同时考虑宿主发现和窗口顺序。不能假设一个固定 WorkerW 永远有效。
- 本项目选择：独立 Win32 P/Invoke 封装，通过窗口顺序定位自己的面板；不进行跨进程重设父窗口。实验模式默认关闭。

### Palisades

- 仓库：[Walkoud/Palisades](https://github.com/Walkoud/Palisades)
- 当次树 SHA：`d4a34a635ec43c1a3245f8c6c8dfacd0a5453d83`
- 阅读范围：README、LICENSE、`Palisades.Application/Views/DesktopOverlayWindow.xaml.cs` 的结构与开头部分。
- 技术启发：WPF 适合容器、图标视图和配置界面；覆盖层、容器、布局模型需要分开组织。
- 本项目选择：每个分组一个独立面板，将文件与存储逻辑放入可单独测试的 Core；避免首版引入复杂覆盖层、插件与小组件系统。

### SimpleDesktopFence

- 仓库：[AnTeCP100/SimpleDesktopFence](https://github.com/AnTeCP100/SimpleDesktopFence)
- 当次树 SHA：`7815fba7945e736403d5914f96de8b8e5e074fde`
- 阅读范围：README、LICENSE、仓库文件树；没有移植其应用源码。
- 技术启发：文件夹面板、FileSystemWatcher、托盘与折叠交互可以组成一个较小而完整的版本。

### 未选作复用来源

[CipherSnowden/neo-fences](https://github.com/CipherSnowden/neo-fences) 的公开说明指出源码可见但并非开源，因此未将其代码作为可复用来源。

## 官方开发资料

- [WPF Overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [FileSystemWatcher](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher)
- [SHGetFileInfoW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shgetfileinfow)

## 后续研究顺序

1. Windows 10/11 不同 Explorer 版本、多显示器与混合 DPI 的桌面层兼容性。
2. 真正的桌面图标托管或可恢复整理：先完成位置备份与故障回退，再涉及原生桌面数据。
3. 可配置分类规则、图标排序与拖动重排。
4. 原生 Shell 菜单、虚拟 Shell 项目与无障碍优化。
5. 签名、安装器和版本更新。
