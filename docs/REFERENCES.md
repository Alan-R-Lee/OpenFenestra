# 技术参考

本页保留当前架构需要的公开资料；早期方案、安装目录观察和一次性调试记录已从当前文档集移除，可在 Git 历史中查阅。

## Windows 与 WPF

| 主题 | 资料 | 项目中的用途 |
| --- | --- | --- |
| 原生桌面视图 | [Raymond Chen：桌面图标位置](https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933/) · [IFolderView](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifolderview) | 从实际 Shell 视图取得身份和位置 |
| 自定义绘制 | [Custom Draw](https://learn.microsoft.com/en-us/windows/win32/controls/custom-draw) | 对匹配项目协调绘制，不能只跳过绘制而忽略输入 |
| 指定线程 hook | [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw) | 临时接入 Explorer 桌面线程 |
| 控件 subclass | [SetWindowSubclass](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass) | 桌面视图回调与清理 |
| Shell 图标 | [IShellItemImageFactory](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishellitemimagefactory) | 按 DPI 请求图标 |
| WPF 拖放 | [Drag and Drop Overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/drag-and-drop-overview) | 分栏交互与管理列表排序 |
| 登录任务 | [Logon Trigger XML](https://learn.microsoft.com/en-us/windows/win32/taskschd/logon-trigger-example--xml-) · [Run / RunOnce](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys) | 登录自启动与兼容回退 |

## 社区来源

已适配的 MIT 虚拟桌面定义、研究过的项目及其固定版本统一记录在[第三方说明](../THIRD_PARTY_NOTICES.md)。研究来源和实际依赖分开列出，避免将阅读过的代码误写为项目依赖。

## 实现边界

公开 Win32 API 的存在不意味着 Explorer 提供稳定的逐项隐藏插件协议。`OleDropTargetInterface` 和跨虚拟桌面的私有 COM 层是非公开实现依赖。

当前桥接不移动原文件、不把图标停到屏外、不隐藏整个桌面列表。未知或过期的身份保持可见；保护能力失效时停止抑制。协议与清理顺序见[原生组件说明](NATIVE_PROTOCOL.md)，测试范围见[验证说明](TESTING.md)。
