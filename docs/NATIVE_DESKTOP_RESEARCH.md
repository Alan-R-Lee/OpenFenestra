# 保留 Windows 原生桌面的技术调研

研究日期：2026-11-02。本文件记录公开源码、微软文档和本项目源码的阅读结果，区分可用 API、实验性组合方案与实际验证。文中的参考项目均固定到当次读取的提交；项目更新、许可证和 Windows 行为仍应在以后升级时重新核对。

目标是保留 Explorer 的原生桌面，原生图标拖入分栏后不重复显示，并能拖回桌面；不能创建全屏图标替代窗口，也不能通过删除或移动用户文件制造图标消失的效果。**后续研发方向转为真正的 ShellBridge 原生集成，具体工程路径尚未选定；离屏定位已暂停。** 当前重点研究在 Explorer 内部按项目抑制绘制和命中。本轮没有实现或加载 hook，也没有注册或运行 DexCorral。进程内 DLL 的兼容性、退出恢复和开源许可需要先弄清，不能把社区实现说成已知的 Fences 内部源码。

## 1. 原有实现为什么不能满足需求

本次修改前的 v0.2 有以下行为：

- `DesktopIntegration.cs` 创建每显示器一个 `DesktopSurfaceWindow`，并隐藏 Explorer 的整个原生图标列表。
- `DesktopAssignments.Collect` 只修改布局 JSON 中的项目归属，原文件仍在原路径；`VisibleDesktop` 只过滤应用自己绘制的图标。
- `WorkspaceController.ReturnToDesktop` 自动开启旧桌面接管，随后只写桌面引用和应用坐标；它没有让原生 Explorer 定位图标。
- `DesktopDrag` 提供应用内格式和适用的 `CF_HDROP`，`DragItems` 只允许 Copy/Link，分栏 Drop 也报告 Link。原生桌面无法从这些 JSON 归属得知某个图标应消失。

因此，不能仅换一层透明背景或修改 DropEffect；需要移除全屏替代层，并把图标位置变化接到原生 Shell 视图。文件夹映射页仍应只作为引用视图，不参与原生桌面图标的排他归属。

## 2. 原生桌面窗口和 Shell 视图

常见 Explorer 桌面层级包含 `Progman` 或 `WorkerW`、`SHELLDLL_DefView` 和其下的图标视图 `SysListView32`。父子关系会随 Windows 版本、壁纸与 Explorer 状态改变，不能只找一个固定 WorkerW。窗口发现用于判断桌面区域和放置分栏，不能当作修改 Explorer 内部数据结构的依据。

微软 Raymond Chen 的[桌面图标位置示例](https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933/)使用 `IShellWindows.FindWindowSW(SWC_DESKTOP)`，经 `IServiceProvider`、`IShellBrowser.QueryActiveShellView` 取得 `IFolderView`。这比跨进程发送 ListView 消息、分配远程内存更适合本项目。

| 能力 | 公开依据 | 本项目采用边界 |
| --- | --- | --- |
| 枚举原生项目、读取位置、选择和定位 | [IFolderView](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifolderview)，包括 `Item`、`GetItemPosition`、`SelectAndPositionItems` | 按 Shell 项目标识重新匹配，不持久保存枚举序号或跨重启 PIDL 指针 |
| 获取和局部设置视图标志 | [IFolderView2](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifolderview2) 的 `GetCurrentFolderFlags`、`SetCurrentFolderFlags` | 只修改必要的自动排列/对齐位，并保存原值；恢复时只恢复本应用修改的位 |
| 隐藏某一个 Explorer 桌面项目 | 上述公开接口没有提供一个可直接使用的每项 hide 方法 | 不把位置操作描述为官方 hide 支持 |
| `IShellFolderView.RemoveObject` | 微软[废弃 Shell API](https://learn.microsoft.com/en-us/windows/win32/shell/deprecated-api)明确 `IShellFolderView` 自 Windows 7 起不可用 | 不作为现代 Windows 方案 |
| 给调用者自己的文件夹选择器设置过滤器 | `IFolderFilter` / `IFolderFilterSite` 的典型公开使用面向自有浏览器/选择器 | 不推断可以向已经运行的 Explorer 桌面安装任意项目过滤器 |

部分社区代码会向 Progman 发送 `0x052C` 并寻找壁纸 WorkerW。这个消息不是微软稳定承诺的公开桌面托管协议，不能因为社区注释写了 documented 就把它标为官方支持。跨进程 `SetParent` 也不等于原生图标管理，本项目不需要借此重绘桌面。

### 能确认的 Fences 信息

Stardock 的[官方更新历史](https://www.stardock.com/products/fences/history)出现 `ForceUnloadDllAfterStartupHooks` 和 `FencesMenu64.dll` 等条目，支持其涉及启动 hook/原生 DLL 的判断；这些条目没有公开完整桌面图标抑制与命中实现，不能据此推断它与 DexCorral 使用相同的绘制链。

`docs/RESEARCH.md` 早期记录的完整 Fences 安装目录观察是历史状态。此次再次检查时，本机该目录只剩 Crashreporter、Detour、Taskbar 等七个残留文件；完整的 DesktopDock/FencesMenu 模块不在，Explorer 模块列表也没有发现 Stardock 模块。因此，不能把旧文件记录当成当前已装载 Fences 的证据。本轮未反编译、注入、调用或复制任何 Fences 私有模块。

## 3. 重点参考：DexCorral 的 Explorer 进程内集成

[guHe330/DexCorral](https://github.com/guHe330/DexCorral/tree/47e65ff145a23d49a184e05f5b9a8131f0957bc5)是本次找到的最接近当前目标的实际源码。其 [WIN32_API_AND_HOOKS.md](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/docs/WIN32_API_AND_HOOKS.md)列出使用的 API 和源文件，但本报告同时核对实际 C++ 代码，不仅依据 README。

目前已确认的关键机制如下：

| 行为 | 实际源码 | 与官方 API 的关系 |
| --- | --- | --- |
| 把原生模块加载到 Explorer 桌面线程 | [main.cpp L106–159](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/src/main.cpp#L106-L159)：临时 `WH_GETMESSAGE` hook，唤醒后移除启动 hook | `SetWindowsHookEx` 是公开 API；组合为 Explorer 扩展加载器属于该项目的集成方案 |
| 某些项目不再由原生 ListView 绘制 | [CorralHook.cpp L1641–1662](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp#L1641-L1662)：`NM_CUSTOMDRAW` 项目阶段返回 `CDRF_SKIPDEFAULT` | 微软公开支持该返回值跳过本项绘制；安装到既有 Explorer 控件的全过程没有稳定的桌面插件协议保证 |
| 处理隐藏项目的命中与位置 | [命中过滤 L1349–1361](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp#L1349-L1361)、[位置写入过滤 L1238–1247](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp#L1238-L1247) | 仅跳过绘制不足以解决选择、命中、自动排列，需要这些额外处理 |
| 避免 Explorer 的 OLE 拖放仍命中未绘制项目 | [CorralHook.cpp L1677–1682](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp#L1677-L1682)、[drop target 包装 L1941–1988](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp#L1941-L1988) | 读取 `OleDropTargetInterface` 窗口属性，再撤销/注册包装目标；该属性是未公开依赖，不能标为微软支持的获取 Explorer drop target API |
| Explorer 替换目标后的维持 | [CorralHook.cpp L2002–2057](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp#L2002-L2057) | 有周期维护和重包装上限，说明不能假定原目标在会话内永久稳定 |

这说明原生集成路线有真实开源代码可研究，不需要把“没有公开的 per-item hide 方法”解释为“只能使用离屏或移动文件”。不过，绘制抑制、命中过滤、拖放包装和清理必须整体审计；目前的局部源码证据不能证明已覆盖所有键盘、无障碍、Explorer 刷新和崩溃情形。

**优先边界：上游目前不能安全地随时停止。** 作者在 [issue #50](https://github.com/guHe330/DexCorral/issues/50)明确说明，当前停止路径只在 `DLL_PROCESS_DETACH`、Explorer 本身退出时运行；不重启 Explorer 的暂停/停止尚未实现，活的 Explorer 中的清理顺序必须手工验证。因此，源码里存在 `CleanupCorralHook` 不能被写成“已提供可靠实时卸载”。[issue #77](https://github.com/guHe330/DexCorral/issues/77)还明确，Fences 使其抑制失效的具体原因仍未知，检测探针尚未实现；两者冲突不是二者机制相同的证据。

其他已核对的限制：

- 它仍然创建分栏窗口：[CorralWindow.cpp L143–154](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/src/CorralWindow.cpp#L143-L154)创建以 Progman 为 owner 的 `WS_POPUP` 分栏。分栏图标由它绘制，其余桌面继续由 Explorer 绘制；不能说整个软件没有窗口或分栏本身就是 Explorer 原生 ListView。
- 它会改变原生位置：[App.cpp L1064–1127](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/src/App.cpp#L1064-L1127)把被抑制图标定位到实际分栏区域/非活动页中心；[滚动处理](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/src/CorralWindowIcons.cpp#L1009-L1074)把越界项目放到分栏边缘附近。它的隐藏机制依靠绘制抑制，不依赖统一停到屏外，但也不是完全不改坐标。
- [App.cpp L465–512](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/src/App.cpp#L465-L512)清除隐藏名单并刷新，没有证明恢复各图标最初坐标；[CorralHook.cpp L2195–2264](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp#L2195-L2264)在外部 subclass 链存在时保留 inert 回调，而非强行解除他人的链。
- [箭头键处理 L1395–1409](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp#L1395-L1409)仅额外前进一次，鼠标选择完成后再取消隐藏项选择。本次阅读未确认完整的 Ctrl+A→Delete/Enter 选择隔离，不能宣称这些路径已经安全覆盖。
- [v1.1.1 已知问题](https://github.com/guHe330/DexCorral/releases/tag/v1.1.1)承认用户桌面和公共桌面的同名文件无法区分并默认用户桌面。本项目若采用其思路，必须按完整 Shell 身份/路径解决归属，不能只按文件名匹配。
- 本次未发现其使用 `PinView` 实现跨 Windows 虚拟桌面固定；不能从 owner Progman 或 Win+D 行为推断已支持所有虚拟桌面。

### 官方原语与兼容性边界

- [NM_CUSTOMDRAW](https://learn.microsoft.com/en-us/windows/win32/controls/nm-customdraw-list-view)支持在 `CDDS_ITEMPREPAINT` 返回 `CDRF_SKIPDEFAULT`，使控件不画该项。它只解决绘制，不承诺把项目从 Shell 模型、键盘选择或 OLE 命中中移除。
- [SetWindowSubclass](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass)明确不能跨线程 subclass。外部 WPF 进程不能只拿到 Explorer HWND 就直接安装回调；回调必须在正确进程/线程和模块生命周期内工作。
- [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)支持跨进程 hook DLL；需要匹配目标位数，正确串接 hook 并释放。启动 hook 已移除不等于后续常驻 DLL 和 subclass 已自动卸载。
- [Shell 扩展注册](https://learn.microsoft.com/en-us/windows/win32/shell/reg-shell-exts)使用 COM 类与 `InprocServer32`。DexCorral 的 [Registration.cpp L116–162](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/Registration.cpp#L116-L162)支持 `HKCU\Software\Classes` 的每用户注册。每用户注册与“免安装复制到任意位置”不是一回事：移动 DLL 前要停用、清理其注册，不能让 Explorer 指向过期路径。
- 微软[进程内扩展指导](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/dd758089%28v%3Dvs.85%29)不支持用托管代码实现 Explorer 进程内扩展。因此，管理窗口可继续用 WPF，加载到 Explorer 的模块应是最小原生 C++ DLL，不把 WPF/CLR 放进 Explorer。
- [RegisterDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop)、[RevokeDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-revokedragdrop)要求正确 OLE 线程、消息泵与 AddRef/Release；替换原生目标时必须保持原目标的生命周期，并具备失败回退。[RemoveWindowSubclass](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-removewindowsubclass)也要在相应线程清理，不能在回调仍可被调用时卸载 DLL。

上游 README 的目标是 Windows 11 build 22000+，明确不支持 Windows 10，并说明不能与 Fences 共存；这不能自动扩大为 OpenFenestra 的兼容承诺。其 SECURITY 也明确当前是个人维护的 unsigned 进程内扩展。签名不是上述 Win32 API 默认要求的通行证，但正式发布应建立可追溯构建/签名流程；系统策略阻止加载时应正常回退，不能绕过策略。进程内回调出错会影响 Explorer 的稳定性，不能将其隔离性描述为普通外部 WPF 窗口的水平。

### 可采用的两种工程路径，尚待选择

| 路径 | 保留部分 | 需要新增/承担的工作 |
| --- | --- | --- |
| WPF 管理器 + 独立实现的最小原生 DLL | 当前布局、托盘、自启动、高清图标和分栏交互 | 定义稳定 IPC/版本握手；原生模块只处理绘制/命中/拖放及清理；按官方原语独立实现并测试，不把 GPL 源码改名后视为 MIT |
| 基于 DexCorral fork 原生架构 | 现成 C++ Shell 集成和相关防护 | 按 GPL 条款保留来源、许可、修改说明和相应源码；与现有 MIT 代码组合发布前梳理整体许可，不能继续宣称含复制 GPL 代码的组合仅为 MIT |

DexCorral 的 [LICENSE](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/LICENSE)为 GPLv3，文件[版权头模板](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/COPYRIGHT_HEADER.txt)声明 GPL v3-or-later、Copyright 2026 Gunter Heiss。开源学习和非商业用途不会消除分发时的许可证义务。此次只阅读和记录链接，没有把其源码或 DLL 加入项目。

两种路径都需要：只在确认的 Explorer 桌面线程启用；启动前保存可恢复状态；管理器异常退出后停止抑制并还原输入/拖放；Explorer 重启后重新识别视图；不残留旧 COM/PIDL；识别冲突桌面工具；使用受限且有版本的 IPC；禁止同步等待管理器导致 Explorer UI 卡死；卸载前恢复目标和 subclass。原生坐标、DPI、键盘选择与无障碍必须纳入验收。这些是 OpenFenestra 需要完成的设计要求，不是 DexCorral 已经提供的保证；具体工程路径在证据完成前保持待定。

优先评估外置 WPF/Core + 最小原生桥：管理器独占布局、规则与持久化，通过异步 IPC 推送带 generation 的紧凑快照/增量；绘制和 DragOver 回调只查询内存，不能扫描目录、读取 JSON 或同步等待管理器。COM 对象和 IDataObject 保留在其所属 STA，IPC 只传可序列化的项目身份和操作意图，不传跨进程无效的接口指针。用户桌面与公共桌面必须以完整 Shell 身份区分，不能只按显示名称匹配。

隐藏验收须覆盖悬停、工具提示、鼠标命中、框选、Ctrl+A、方向键、输入名称查找，以及 Enter/F2/Delete/Ctrl+C/X 等命令；只看图标消失或拦截 LVM_HITTEST 不足以证明安全。控制器失联时应清空抑制名单并恢复正常桌面；恢复失败则停止继续接管。这些要求先在独立测试宿主/虚拟机验证，再进入日常桌面。

### 其他方案的研究结论

[Pickets 的 DesktopIconHider.cs](https://github.com/Creeptones/Pickets/blob/38052a30dd2cf9df6776ca03669803df340e1492/DesktopIconHider.cs#L22-L69)通过 `IFolderView.SelectAndPositionItems` 把图标放到屏外，返回时恢复位置。其[批量重新隐藏注释](https://github.com/Creeptones/Pickets/blob/38052a30dd2cf9df6776ca03669803df340e1492/DesktopIconHider.cs#L90-L137)承认 Explorer 会将位置夹回可见区。图标仍在 Shell 模型内，固定负坐标还可能落在左侧显示器上。它是可恢复位置管理参考，**不是本轮选定架构**。

Pickets 的[拖出处理](https://github.com/Creeptones/Pickets/blob/38052a30dd2cf9df6776ca03669803df340e1492/PicketWindow.xaml.cs#L1144-L1193)也不能直接照搬：“未落在其他分栏”不等于“落在原生桌面”。[WPF QueryContinueDrag](https://learn.microsoft.com/en-us/dotnet/api/system.windows.dragdrop.querycontinuedrag?view=windowsdesktop-10.0)适合源端处理 Esc 和释放事件，但不能替代真实目标识别。外部文件引用、原生桌面项目、拖入文件夹和拖到其他应用应有不同语义，不能一律取消 Shell 拖放并返回桌面。

## 4. 物理收纳作为后备研究，当前不采用

[NextMind Desktop 的 ManagedItemService](https://github.com/NextMindHQ/nextmind-desktop/blob/22eef1686888cf0e0df2290931fa8289c663616c/src/NextMind.Desktop.Core/Managed/ManagedItemService.cs#L70-L158)把用户桌面文件实际移入管理目录，采用事前日志；其 [Journal.cs](https://github.com/NextMindHQ/nextmind-desktop/blob/22eef1686888cf0e0df2290931fa8289c663616c/src/NextMind.Desktop.Core/Managed/Journal.cs#L91-L115)使用临时文件和原子替换；[恢复逻辑](https://github.com/NextMindHQ/nextmind-desktop/blob/22eef1686888cf0e0df2290931fa8289c663616c/src/NextMind.Desktop.Core/Managed/ManagedItemService.cs#L384-L462)根据磁盘实际位置恢复配置。它明确拒绝非用户桌面项目和跨卷移动，返回时避免覆盖同名文件。其 [ManagedPaths](https://github.com/NextMindHQ/nextmind-desktop/blob/22eef1686888cf0e0df2290931fa8289c663616c/src/NextMind.Desktop.Core/Managed/ManagedPaths.cs)按稳定项目 ID 分目录，避免分栏改名影响数据位置。

这条路线能让 Explorer 因真实文件离开桌面而自然移除图标，但改变了文件位置、权限或共享语义。当前电脑的公共桌面在普通用户令牌下不能假定具有完整创建/恢复权限：能删除或移出某项不等于能把它重新创建回公共桌面。把公共快捷方式放回用户桌面又会改变其对其他用户的可见范围。因此，本轮不能直接将 NextMind 的用户桌面实现套到公共桌面，也不执行这类迁移。

如果将来单独提供明确的物理收纳模式，至少需要记录原路径/实际路径/来源桌面/项目 ID、处理跨卷复制校验与权限、保留崩溃恢复日志、拒绝静默覆盖，并把收纳快捷方式与移动程序本体分开。微软 [IFileOperation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation)及其进度回调适用于 Shell 文件操作，要求 STA；成功调用 `PerformOperations` 后仍需检查各项结果和中止状态。

尤其不能沿用 PXSR/BirdyFences 的删除路线：[FenceManager.cs 的 DeleteOriginalShortcutsOnDrop 分支](https://github.com/PXSR/DesktopFences/blob/17c2fe75482c399c3c5218f16fd5cf93748539d3/Code/Desktop%20Fences/FenceManager.cs#L5897-L5911)会直接删除桌面 `.lnk`，并吞掉异常。它不是每项原生过滤，也不满足本项目的可恢复原则。本项目禁止把删除桌面快捷方式当作消重实现。

### 文件拖放不能只返回 Move

微软 [Shell 数据传输说明](https://learn.microsoft.com/en-us/windows/win32/shell/datascenarios)区分普通与 optimized move。目标已完成真实移动时，还需要正确反馈 `CFSTR_PERFORMEDDROPEFFECT`，防止源端再次删除；源端不能看到一个 Move 就无条件删除自己的文件。现有文件可使用 `CF_HDROP`，Shell 项目应考虑原生 `IDataObject` / `CFSTR_SHELLIDLIST`，并遵守复制、链接、取消和右键拖放语义。当前“只改归属”的实现不应谎报真实文件 Move。

## 5. 多 Windows 虚拟桌面

Fences 的 Desktop Pages 与 Windows 虚拟桌面是不同机制，不能把产品的分页宣传直接当作 Windows 跨桌面固定实现。Stardock 支持人员在 [2026-09-24 的官方征询](https://forums.stardock.com/542531/windows-virtual-desktops-support-request-for-input)中仍在询问用户希望 Fences 如何与 Windows 虚拟桌面配合；这不支持“已知 Fences 内部就是 PinView”的判断。本项目的目标是同一组分栏和同一份内容在各 Windows 虚拟桌面互通，内容共享由自身布局模型负责。

微软公开的 [IVirtualDesktopManager](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ivirtualdesktopmanager)只提供查询窗口所属桌面、查询是否位于当前桌面、移动窗口三个方法，没有“固定在所有桌面”方法。分栏跨虚拟桌面可见需要额外兼容层；窗口仅在原生桌面附近排序，并不会自动得到跨桌面固定能力。

[MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop/blob/a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6/VirtualDesktop11-24H2.cs#L130-L145)提供 `IApplicationViewCollection.GetViewForHwnd`；其[固定接口](https://github.com/MScholtes/VirtualDesktop/blob/a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6/VirtualDesktop11-24H2.cs#L202-L213)与 [PinWindow](https://github.com/MScholtes/VirtualDesktop/blob/a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6/VirtualDesktop11-24H2.cs#L658-L665)使用非公开的 `IVirtualDesktopPinnedApps.PinView`。这些接口不受微软稳定 ABI 保证，尤其不能把不同版本的 `IVirtualDesktopManagerInternal` 直接复用到所有 Windows。

本项目应只取实现窗口固定所需的最小服务，进行 Windows 版本检查、HRESULT 检查和固定后复核；Explorer 重启后重新获取服务。未知版本或失败时保留分栏在当前桌面，并记日志，不能通过切换用户桌面或改变全部桌面图标状态补偿。运行时成功返回 `IsViewPinned` 也只是接口层证据，不能代替实际切换桌面的显示验证。

[Ciantic/VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor/blob/rust/README.md)的 README 当前写明其 DLL 面向 Windows 11 24H2 `26100.2605+`，并给出测试过的 25H2 build；这说明兼容范围需要具体到 build，而不是只写 Windows 11。[Grabacr07/VirtualDesktop](https://github.com/Grabacr07/VirtualDesktop)可参考 Explorer 重启后重建 provider 的模式，其既有 build 支持不能作为本项目对最新 Windows 已验证的证据。此次读取的本机版本为 25H2 `26200.9711`；尚未据此完成手动切换验证。

## 6. 参考项目矩阵

许可证核对针对下列提交/文件；若复制代码，应保留对应版权与许可证，Apache-2.0 还应核对 NOTICE 和修改说明。只引用研究链接不表示代码已被移植。

| 来源与固定提交 | 精确入口 / 许可证 | 可参考部分与限制 |
| --- | --- | --- |
| guHe330/DexCorral `47e65ff145a23d49a184e05f5b9a8131f0957bc5` | [CorralHook.cpp](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/DexCorral/ShellExtension/src/CorralHook.cpp)、[API 清单](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/docs/WIN32_API_AND_HOOKS.md)、[GPLv3](https://github.com/guHe330/DexCorral/blob/47e65ff145a23d49a184e05f5b9a8131f0957bc5/LICENSE)；版权头为 GPL v3-or-later | 首要原生集成参考：临时加载 hook、绘制抑制、命中/位置过滤、OLE 包装；Win11 限定，实时停止未实现，不能无条件复制进 MIT 项目 |
| Creeptones/Pickets `38052a30dd2cf9df6776ca03669803df340e1492` | [DesktopIconHider.cs](https://github.com/Creeptones/Pickets/blob/38052a30dd2cf9df6776ca03669803df340e1492/DesktopIconHider.cs)、[WorkerWHost.cs](https://github.com/Creeptones/Pickets/blob/38052a30dd2cf9df6776ca03669803df340e1492/WorkerWHost.cs)、[MIT](https://github.com/Creeptones/Pickets/blob/38052a30dd2cf9df6776ca03669803df340e1492/LICENSE)，Copyright 2026 Creeptone | 原生 COM 枚举、离屏定位/还原、Explorer 重建；离屏会被重新布局夹回，拖出落点判定需加强 |
| DayuanJiang/PecoFence `368d1a14e07f4aef8421be76a988d4d01b2a1c69` | [desktop.rs](https://github.com/DayuanJiang/PecoFence/blob/368d1a14e07f4aef8421be76a988d4d01b2a1c69/crates/platform/src/desktop.rs#L202-L258)、[dragdrop.rs](https://github.com/DayuanJiang/PecoFence/blob/368d1a14e07f4aef8421be76a988d4d01b2a1c69/crates/platform/src/dragdrop.rs#L549-L576)、[Apache-2.0](https://github.com/DayuanJiang/PecoFence/blob/368d1a14e07f4aef8421be76a988d4d01b2a1c69/LICENSE) | 原生宿主发现、namespace/OneDrive 特殊项识别；其全局隐藏图标选项不解决单项消重 |
| NextMindHQ/nextmind-desktop `22eef1686888cf0e0df2290931fa8289c663616c` | [ManagedItemService.cs](https://github.com/NextMindHQ/nextmind-desktop/blob/22eef1686888cf0e0df2290931fa8289c663616c/src/NextMind.Desktop.Core/Managed/ManagedItemService.cs)、[恢复测试](https://github.com/NextMindHQ/nextmind-desktop/blob/22eef1686888cf0e0df2290931fa8289c663616c/tests/NextMind.Desktop.Tests/ManagedReturnAndRecoveryTests.cs)、[Apache-2.0](https://github.com/NextMindHQ/nextmind-desktop/blob/22eef1686888cf0e0df2290931fa8289c663616c/LICENSE) | WPF 项目、事前日志、原子持久化和恢复测试；物理移动为后备研究，本轮不采用 |
| jaimitus/ZenDesktop `8371595e24ae8ec2040a7655a8557b25a2291c13` | [dropsource.rs](https://github.com/jaimitus/ZenDesktop/blob/8371595e24ae8ec2040a7655a8557b25a2291c13/src/dropsource.rs#L57-L72)、[ui.rs](https://github.com/jaimitus/ZenDesktop/blob/8371595e24ae8ec2040a7655a8557b25a2291c13/src/ui.rs)、[MIT](https://github.com/jaimitus/ZenDesktop/blob/8371595e24ae8ec2040a7655a8557b25a2291c13/LICENSE) | Rust OLE 拖拽源、Esc/鼠标释放处理；其文件操作与本轮“原路径不变”目标不同，不直接移植 |
| Microsoft/Windows-classic-samples `434f6002bdf9cf9829406c3ff2b33387982d6168` | [FileOperationSample.cpp](https://github.com/microsoft/Windows-classic-samples/blob/434f6002bdf9cf9829406c3ff2b33387982d6168/Samples/Win7Samples/winui/shell/appplatform/fileoperations/FileOperationSample.cpp)、[DragDropVisuals/DataObject.cpp](https://github.com/microsoft/Windows-classic-samples/blob/434f6002bdf9cf9829406c3ff2b33387982d6168/Samples/Win7Samples/winui/shell/appplatform/DragDropVisuals/DataObject.cpp)、[MIT](https://github.com/microsoft/Windows-classic-samples/blob/434f6002bdf9cf9829406c3ff2b33387982d6168/LICENSE) | 官方 IFileOperation 与支持 SetData 的 Shell IDataObject 示例；C++ 样例需按 WPF/COM 生命周期重写，不是现成 Fences |
| MScholtes/VirtualDesktop `a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6` | [VirtualDesktop11-24H2.cs](https://github.com/MScholtes/VirtualDesktop/blob/a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6/VirtualDesktop11-24H2.cs)、[MIT](https://github.com/MScholtes/VirtualDesktop/blob/a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6/LICENSE)，Copyright 2017 Markus Scholtes | 最小 PinView 调用模式；属于非公开兼容接口，需要 build 检查与失败回退 |
| Ciantic/VirtualDesktopAccessor，`rust` 分支 | [README](https://github.com/Ciantic/VirtualDesktopAccessor/blob/rust/README.md)、[Rust 导出](https://github.com/Ciantic/VirtualDesktopAccessor/blob/rust/dll/src/lib.rs)、[MIT](https://github.com/Ciantic/VirtualDesktopAccessor/blob/rust/LICENSE) | build 支持声明、窗口固定；未在本轮固定并引入其 DLL，不能将上游测试声明算作本项目实测 |
| infitis-studio/My-Fancy-Fences `3f15fe804791c601fd963d41e9561d2e0e03172d` | [DesktopWorkerWindowFinder.cs](https://github.com/infitis-studio/My-Fancy-Fences/blob/3f15fe804791c601fd963d41e9561d2e0e03172d/My%20Fancy%20Fences/DesktopWorkerWindowFinder.cs)、[PanelStorageService.cs](https://github.com/infitis-studio/My-Fancy-Fences/blob/3f15fe804791c601fd963d41e9561d2e0e03172d/My%20Fancy%20Fences/PanelStorageService.cs)、[MIT](https://github.com/infitis-studio/My-Fancy-Fences/blob/3f15fe804791c601fd963d41e9561d2e0e03172d/LICENSE) | 桌面宿主与持久化；README 所述拖入创建快捷方式不能证明原生图标消重，本轮未核验其完整 Drop 路径 |
| PXSR/DesktopFences `17c2fe75482c399c3c5218f16fd5cf93748539d3` | [FenceManager.cs](https://github.com/PXSR/DesktopFences/blob/17c2fe75482c399c3c5218f16fd5cf93748539d3/Code/Desktop%20Fences/FenceManager.cs#L5897-L5911)、[License.md](https://github.com/PXSR/DesktopFences/blob/17c2fe75482c399c3c5218f16fd5cf93748539d3/License.md)，MIT，Copyright 2025 LIMBO666，含 BirdyFences attribution | 作为删除原快捷方式的反例；GitHub API 的 NOASSERTION 不代替实际许可文件，本项目不采用该删除分支 |
| venkatsalem/DesktopFences | [仓库](https://github.com/venkatsalem/DesktopFences) | 读取时未发现开源许可证，仅可了解架构，不作为可复用代码来源 |
| CipherSnowden/neo-fences | [仓库](https://github.com/CipherSnowden/neo-fences) | 作者明确源码可见且保留权利，不是本项目可直接复用的开源来源 |

## 7. 验证边界与后续验收

本轮完成的是源码/API/许可证调研、研发文档和一个尚未接入运行路径的虚拟桌面适配器；没有实现 ShellBridge，没有运行/注册 DexCorral，没有向 Explorer 加载任何新 DLL。没有执行新原生功能的手工鼠标拖入拖出、虚拟桌面切换、终止 Explorer 或重启验收，也没有移动/删除桌面文件。Release 构建通过且为 0 warning/0 error，27 项核心检查通过；这些结果只验证现有工程和核心逻辑，不证明原生绘制抑制或跨桌面体验已经完成。上游自述、源码中出现某接口和 COM 返回成功也不等于用户体验已经验收。

| 验收项 | 需要证明的结果 |
| --- | --- |
| 原生桌面保留 | Explorer 原图标视图可见；没有全屏替代图标窗口；原生菜单/框选仍工作 |
| 桌面拖入普通分栏 | 仅原生同项停止绘制且不会被错误命中；分栏高清图标可启动；原文件/快捷方式路径及目标不变 |
| 分栏拖回桌面 | 只在真实桌面落点移除归属并恢复原生位置；Esc、其他应用、任务栏和文件夹目标无误操作 |
| 输入、布局与 DPI | F5、Ctrl+A→Delete/Enter、框选、自动排列/对齐、负坐标多屏和缩放后不误选隐藏项目、不破坏原生桌面 |
| 故障恢复 | 在活的 Explorer 内完成停用与恢复；异常结束、下次启动和 Explorer 重启后不残留失效回调/拖放目标，位置与必要标志可恢复 |
| Windows 虚拟桌面 | 实际建立并切换多个桌面后分栏均可见、可操作；重建窗口后再次固定；失败可回退 |
| 重启与便携模式 | 登录后自动启动并还原归属、分栏/折叠状态；便携配置无开发机用户名/盘符硬编码 |
| 文件不变 | 实际原生收纳/返回前后比较桌面文件路径与快捷方式目标，不以删除或复制文件实现消重 |

进程内原生集成和私有虚拟桌面固定都有 Windows 兼容性边界。若某一 build 无法稳定实现，应禁用对应实验能力并恢复原生状态，保留普通分栏使用能力。不能把尚未完成的原生路线标成可用新版本，也不能退回用户已经否定的全屏桌面替代方案。
