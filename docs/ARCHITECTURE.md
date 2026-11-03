# 架构与设计

v0.3 保留 Windows 原生桌面，由 Explorer 继续显示未收纳图标。应用只创建分栏和管理窗口；v0.2 的全屏桌面图标替代层已移除，不再通过隐藏整个原生图标列表实现消重。原生整合仍标为实验性功能，不能等同于完整复现 Fences 的内部实现或兼容范围。源码依据和备选方案的历史调研见[原生桌面专项研究](NATIVE_DESKTOP_RESEARCH.md)；当前实现与验证状态以本文及[验证记录](TESTING.md)为准。

## 工程与职责

- `OpenFenestra.Core`：布局校验、文件/系统入口分类、排他归属、位置数据、原子存储；不执行 Shell hook 或移动原项目。
- `OpenFenestra.App`：WPF 分栏、管理窗口、托盘、自启动、高清图标、Shell 项目枚举与原生桥接控制器。
- `native/ShellBridge`：原创 MIT 许可的 x64 C++ Explorer 进程内组件，以及独立进程中的标准/owner-data ListView 验证程序。
- `OpenFenestra.Tests`：不依赖外部测试包的 Core 回归程序。
- `OpenFenestra.Shell.Tests`：链接拖拽解析源码的 STA Console 测试；使用 WPF DataObject，但不创建窗口、不引用应用 WinExe、不加载桌面桥接组件。

分栏仍是应用自己的窗口和图标视图，不是把整个桌面重新画在另一扇窗口里。原生桥接只抑制已收纳项目的 Explorer 绘制和输入，不绘制分栏、不移动文件、不把图标停到屏外，也不改变 Explorer 的自动排列/网格设置。

## 布局与归属

布局版本仍为 v2：`Layout → FenceGroup[] → FenceTab[]`。`DesktopIntegrationEnabled`、`StartWithWindows`、桌面引用及物理像素落点继续保存；分栏保存展开高度、折叠状态、不透明度与图标 DIP 大小。v1 布局可迁移，迁移本身不会注册自启动。

导入限制为 8 MB、100 个分栏、每栏 30 页、每页 10,000 项。项目身份使用绝对文件系统路径或当前模型允许的简单虚拟 Shell CLSID；不把显示名称作为身份。未来布局版本被明确拒绝，不用旧备份覆盖未知的新格式。原生桥接额外限制一次收纳快照最多 10,000 个引用、UTF-16 消息最多 2 MiB；超限显式拒绝，不静默截断。

`DesktopAssignments.Collect` 将项目归属于一个普通标签页，并移除其他普通页及 `DesktopItems` 中的重复归属。文件夹映射是只读实时视图，不独占其内容。`FileCatalog.OrganizeEntries` 接受真实 Shell 桌面项目，保留已有手工归属，把虚拟系统图标归入“系统入口”，并清理多余桌面引用。

`ShellFiles.DesktopItems` 通过 `NativeDesktopShell` 枚举实际 Explorer 桌面视图，而非把 `Shell.Application.NameSpace(0)` 中全部导航入口当作桌面图标。身份来自文件系统路径或桌面绝对 parsing name；中文等友好名称来自 Shell 并缓存用于分栏显示。这样既能识别回收站等非文件项目，也不按同名文件猜测用户桌面与公共桌面的归属。

## 原生绘制、输入与 OLE

`NativeShellBridgeClient` 找到实际 Explorer 桌面 UI 线程，通过短暂、指定线程的 `WH_GETMESSAGE` hook 装入 `FenestraShellBridge.dll`。原生端校验进程与 `Progman`/`WorkerW → SHELLDLL_DefView → SysListView32` 关系，在正确线程设置 subclass 和 message-only 控制窗口；初始化后移除启动 hook。

桥接对已匹配项目使用 `NM_CUSTOMDRAW / CDRF_SKIPDEFAULT` 抑制原生绘制，并配套处理选择状态、焦点遍历、鼠标命中、键盘/菜单操作等路径。项目仍存在于 Shell 和磁盘中。桌面列表始终保持原可见状态；图标索引变化时先清除旧匹配表，再按完整身份重建，避免用旧索引隐藏另一个文件。

Explorer 的 OLE 实现可能绕过 ListView 命中消息。桥接因此保留并包装原 `IDropTarget`：外部对象投递到已隐藏图标的旧坐标时返回不允许投递，防止误投到看不见的文件夹或项目；其余位置转发给原目标。取得原目标所用的 `OleDropTargetInterface` 窗口属性是未公开实现依赖，也是该功能保持实验性的原因。保护目标被其他组件替换时停止抑制；退出时尊重其他组件的注册，尽量恢复原目标，恢复失败时保留禁用的透明转发器。

DLL 在 Explorer 中固定驻留，防止窗口过程或 COM 回调指向已卸载代码。关闭功能会撤销抑制、subclass 和拖放保护；不承诺立即卸载 DLL。COM 引用在所属线程释放；重入枚举期间先停止抑制，待调用退出后再完成清理，不在 loader lock 下析构 Shell COM 对象。

## 控制协议、故障恢复与更新

控制窗口按 Explorer PID 匹配。`WM_COPYDATA` 承载有大小限制的 UTF-16 字符串列表；收纳快照以控制器 PID、存活 HWND、随机 session token 和原进程句柄绑定会话。心跳与停用命令须匹配该会话，不能因 PID 重用接管旧会话。协议及能力位必须通过检查后才提交收纳数据。

`WM_COPYDATA` 的接收本身是同步调用，但接收过程中只校验和发布元数据，随后投递普通消息异步重建 Shell 身份表。同步跨进程输入回调中不进行 Shell COM 调出，避免 `RPC_E_CANTCALLOUT_ININPUTSYNCCALL`。绘制回调也不做逐项 COM 查询。

控制器约每 2 秒续租；原生端每 500 ms 检查控制器进程/窗口和保护能力。控制器退出或 HWND 消失时，在下一次可运行的检查中停止抑制；心跳缺失超过 8 秒也停止抑制。该时限依赖 Explorer 消息线程能够运行，不能视为卡死线程上的硬实时保证。关键绘制/输入/OLE 保护失效时优先恢复原生行为；控制器断线后清理旧连接和互斥体，以 10 秒间隔重试。仅为恢复 v0.2 遗留隐藏状态保留旧恢复入口，不再由它管理正常运行的新桥接。

控制器把随包 DLL 校验后复制到当前用户缓存的 SHA-256 目录，避免 Explorer 锁住便携包中的 DLL。对于 MSIX/打包宿主，先通过打开文件句柄和 `GetFinalPathNameByHandle` 取得 Explorer 可访问的实际文件路径，再装入 DLL，不能仅凭控制器中可读的 AppData 逻辑路径判断跨进程可见性。连接时还校验已加载模块的构建身份；哈希不符拒绝复用，不把旧 DLL 当作更新后的组件。需要卸载固定驻留的旧模块时须等待 Explorer 进程退出，不擅自重启用户的 Explorer。

## 拖入、拖出与文件边界

拖入接受应用私有格式、普通 `CF_HDROP` 和 `Shell IDList Array`。CIDA 解析在调用 Shell 前验证总大小、数量、偏移、PIDL 长度/终止和范围重叠，最多 300 项、4 MiB；通过 `ILCombine` 与 `SHGetNameFromIDList` 得到稳定身份，再用现有路径规则校验。错误的 Shell 格式可回退到普通文件格式。

分栏内部拖出使用 private-only 数据，不向 Explorer 暴露源文件的 `CF_HDROP`，避免其执行真实文件移动或复制。源端确认鼠标在原生桌面释放后取消外部文件投递，再移除普通分栏归属，并通过 `IFolderView` 定位已存在的原生图标。Esc 或无法识别的目标不会被当作成功返回。默认数据对象仍可提供普通 FileDrop 格式用于文件互操作；应用的分栏返回路径主动选择私有格式。

桌面之外的文件引用不能靠 JSON 凭空变成 Explorer 图标。返回这类项目时，在用户桌面创建真实快捷方式；已有 `.lnk`/`.url` 复制其链接文件，其余文件或目录创建指向原项目的 `.lnk`。先在应用临时目录生成，再以禁止覆盖的文件操作提交到桌面，遇同名重试。原文件和目标程序不移动、不删除、不改写。批量创建中途失败可能已留下先前成功创建的新链接，不能把这条路径描述为事务性文件迁移。

## 图标、布局与启动

使用 `IShellItemImageFactory` 的 ICONONLY/BIGGERSIZEOK 获取高分辨率帧，后台 STA 兼容 Shell 图标处理程序，释放 HBITMAP/HICON 和 COM 引用，冻结图像后交回 UI。只在顶层 HWND 的 DPI 变化后重取分辨率，避免对子元素路由事件反复重建列表。

双击展开栏头折叠，单击折叠栏头展开；折叠提高透明度，展开恢复不透明度和单独保存的高度。分栏位置、标签页与状态自动保存。普通模式使用当前用户 LocalApplicationData；`--portable` 或 `portable.mode` 使用程序旁的 `data`，`--data-dir` 优先。发布包不收录开发机路径、已有便携数据或用户桌面文件。

布局采用 500 ms 防抖保存，正常退出立即保存；临时文件持久写入后原子替换，保留 `.bak`。快照和导入导出只包含布局。可选自启动优先使用当前用户无延迟的登录计划任务（InteractiveToken、LeastPrivilege、无电池/空闲/联网限制、无运行超时），成功后移除旧 Run 项；不允许注册时回退 Run 项。迁移保留托盘启动及数据目录参数；登录专用 --autostart 在创建窗口前折叠全部分栏，保留展开高度，避免展开内容闪现。先显示保存的分栏，再枚举 Shell 和连接原生组件；Explorer 未就绪时保留启用偏好并自动重试。未通过重启电脑验收。

## 兼容性边界

原生桥接当前仅支持 Windows 11 x64，不能沿用 v0.2 的 Windows 10/ARM64 构建入口宣称原生支持。分栏跨 Windows 虚拟桌面固定使用最小私有 COM 适配层；按版本、HRESULT 和固定结果检查，失败或未知版本保留当前桌面并记录原因。它不是 Fences 内部机制的证明；本轮尚未进行真实虚拟桌面切换验收。

本机已完成真实 Explorer 中的逐项抑制、还原和控制器异常退出恢复验证；实际鼠标拖入拖出、键盘组合的完整交互、多屏混合 DPI、Explorer 重启、睡眠恢复以及与其他桌面增强软件共存仍需更广验证。没有遥测、账号或运行时网络服务依赖。
