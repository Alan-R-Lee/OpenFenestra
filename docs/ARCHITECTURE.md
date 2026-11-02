# 架构与设计

## 工程

- `OpenFenestra.Core`：布局版本、校验、文件引用、桌面排他归属、位置数据、原子存储。
- `OpenFenestra.App`：WPF 分栏、桌面图标层、Windows Shell、原生桌面切换、恢复守护进程、托盘和自启动。
- `OpenFenestra.Tests`：不依赖外部测试包的核心回归程序。

## v2 布局模型

`Layout → FenceGroup[] → FenceTab[]`

新增 `DesktopIntegrationEnabled`、`StartWithWindows`、桌面额外引用及物理像素位置。分栏保存展开不透明度、折叠不透明度和图标 DIP 大小。v1 布局自动迁移为 v2，但不会因迁移自动接管桌面或注册自启动。

导入限制 8 MB、100 个分栏、每栏 30 页、每页 10,000 项。路径必须是绝对文件系统路径，或限定格式的虚拟 Shell CLSID。未来版本拒绝读取，不会退回旧备份后覆盖未来版本文件。

## 桌面归属

`DesktopAssignments.Collect` 将项目移入一个普通标签页，并移除其他普通标签页和桌面额外引用中的重复归属。

`ReturnToDesktop` 移除普通分栏归属并保存桌面引用及落点。`VisibleDesktop` 合并 Shell 原生项目和额外引用，再排除所有已收纳项目。文件夹映射不参与排他归属。

这些操作不调用文件移动、删除或覆盖 API。拖拽携带应用格式以及适用的 CF_HDROP 数据；应用内按显示归属处理，外部接收方只获得 Copy/Link 能力。

## 显示层与恢复

每个显示器创建一个透明 WPF 桌面图标窗口。图标使用绝对物理坐标保存，在各窗口中转换为 DIPs；空白区域穿透到 Explorer。应用内拖拽期间临时接收空白区域的落点。分栏始终排在桌面图标层上方。

接管前记录 Explorer 原生图标列表的可见状态，创建显示层并启动守护进程，然后临时隐藏原生列表。此操作不修改注册表中的桌面显示偏好，不注入 Explorer。

恢复记录含随机 token、父进程 PID 和启动时间，防止 PID 重用或旧守护进程影响新会话。正常关闭恢复原状态并清理记录；异常结束由独立进程恢复。下次启动还会检查未完成的恢复记录。全局互斥体限制一个实例接管当前桌面。

## 图标和分栏

使用 `IShellItemImageFactory` 的 ICONONLY / BIGGERSIZEOK 获取高分辨率帧，后台 STA Dispatcher 兼容旧式 Shell 处理程序。HBITMAP、HICON 和 COM 对象均释放，缓存中的 BitmapSource 冻结后交回 UI。

展开/折叠直接切换最终几何值，避免被遮挡窗口的动画时钟暂停导致状态延后。双击展开栏头折叠，单击折叠栏头展开；展开高度单独持久化。

## 配置与可移植性

普通模式用当前用户 LocalApplicationData，`--portable` 或 `portable.mode` 使用 EXE 旁的 `data` 目录；`--data-dir` 优先级最高。没有开发电脑盘符或用户名依赖。

500 ms 防抖保存，正常退出立即保存。同目录临时文件 Flush 后原子替换，保留上一版 `.bak`。快照包含布局，不包含文件内容。损坏主文件可从备份恢复，原损坏文件另存。

可选自启动使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\OpenFenestra`，命令保留 `--tray` 和便携/自定义数据目录参数。其他电脑默认不启用。用户可在管理窗口关闭该项。

## 兼容性边界

Windows 10/11 x64 为主要目标；ARM64 提供构建入口，未在本机验证。混合 DPI、多显示器热插拔、虚拟桌面和与其他桌面工具共存需要更广泛验证。无遥测、账号、网络服务或插件执行系统。
