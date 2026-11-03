# OpenFenestra

**桌面，自有条理。** 一个保留 Windows 原生桌面的开源图标分组工具。

A Windows 11 desktop organizer with native Explorer integration, tabs and collapsible groups.

> 本项目依赖使用人工智能开发，且为本开发者的第一个开源项目，欢迎感兴趣的贡献者提出意见及建议~

[![Build](https://github.com/Alan-R-Lee/OpenFenestra/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/Alan-R-Lee/OpenFenestra/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

**当前版本：v1.0.0 · 首个正式发布版 · Windows 11 x64**

## 可以做什么

- 将桌面图标收纳到透明分栏，支持标签页、搜索、高清 Shell 图标和只读文件夹映射。
- 保留 Explorer 原生桌面；开启原生整合后，已收纳图标不会在桌面重复显示。
- 双击栏头折叠，单击展开；统一调整展开和折叠时的背景透明度。
- 在“我的分组”拖动排序，再按新顺序整理桌面分栏。
- 自动保存布局，支持快照、导入导出；登录启动时分栏默认折叠。
- 原文件保留在原处；桌面外的文件返回桌面时创建快捷方式。

![管理控制台：示例数据](docs/images/manager.png)

<details>
<summary>查看分栏预览</summary>

![透明分栏：示例数据](docs/images/panel.png)

</details>

截图来自程序生成的测试样例，不含个人桌面数据。

## 开始使用

从 [v1.0.0 Releases](https://github.com/Alan-R-Lee/OpenFenestra/releases/tag/v1.0.0) 下载安装包：

| 下载 | 约大小 | 适用情况 |
| --- | ---: | --- |
| [Light 轻量安装版](https://github.com/Alan-R-Lee/OpenFenestra/releases/download/v1.0.0/OpenFenestra-1.0.0-Setup-Light-x64.exe) | 3 MB | 已有 .NET 10 x64 桌面运行时；缺少时向导提供 Microsoft 下载入口 |
| [Offline 离线安装版](https://github.com/Alan-R-Lee/OpenFenestra/releases/download/v1.0.0/OpenFenestra-1.0.0-Setup-Offline-x64.exe) | 50 MB | 首次使用推荐，自带运行时，无需额外下载 |
| [便携 ZIP](https://github.com/Alan-R-Lee/OpenFenestra/releases/download/v1.0.0/OpenFenestra-win-x64.zip) | 70 MB | 完整解压后直接运行，可使用 `--portable` |

安装向导支持简体中文和英文、安装位置、开始菜单、可选桌面快捷方式与登录启动。应用按当前用户安装；卸载保留配置和原文件。首次启动后，在左侧“设置”中按需开启原生桌面整合与自动归类。

[SHA-256 校验文件](https://github.com/Alan-R-Lee/OpenFenestra/releases/download/v1.0.0/SHA256SUMS.txt) 随版本提供。EXE 尚未代码签名，请核对仓库来源和校验值。源码下载包不是可直接运行的程序。

便携包请保留 EXE 旁的 DLL、`licenses` 和 `docs`。持续集成的开发构建也可从 [Actions](https://github.com/Alan-R-Lee/OpenFenestra/actions/workflows/build.yml) 获取。

关闭控制台会收起到托盘；退出应用后，已收纳图标恢复原生显示。更多操作、便携模式和配置位置见[使用指南](docs/USAGE.md)。

## 从源码构建

需要 Windows 11 x64、.NET 10 SDK，以及 MSVC x64 + Windows SDK 或 MinGW-w64 x64。

```powershell
git clone https://github.com/Alan-R-Lee/OpenFenestra.git
cd OpenFenestra
.\scripts\build.ps1
```

输出：`artifacts/OpenFenestra-win-x64.zip`。工具链配置、开发运行和测试命令见[构建指南](docs/BUILDING.md)。

## 实验版边界

原生整合会将自有 C++ 组件加载到 Explorer 的桌面线程，并依赖部分非公开 Shell 行为；Windows 更新或其他桌面增强工具可能影响兼容性。

目前仅提供 **Windows 11 x64** 原生包。多显示器混合 DPI、Explorer 重启、休眠恢复及跨虚拟桌面的实际交互仍需扩大验证。当前能力和测试范围见[验证说明](docs/TESTING.md)。

## 参与项目

欢迎报告问题、分享兼容性结果和提交小范围改进。请先阅读[贡献指南](CONTRIBUTING.md)；涉及漏洞或敏感数据的问题见[安全说明](SECURITY.md)。

[使用](docs/USAGE.md) · [构建](docs/BUILDING.md) · [架构](docs/ARCHITECTURE.md) · [技术参考](docs/REFERENCES.md) · [代码核查](docs/CODE_REVIEW.md) · [发布](docs/RELEASING.md)

## 许可证与致谢

[MIT](LICENSE) © 2026 Alan-R-Lee and contributors。第三方适配与编译器运行时的许可见[第三方说明](THIRD_PARTY_NOTICES.md)，完整许可文本随源码和发行包保留。

受 Fences 的使用体验启发，独立实现；不包含 Stardock 源码、二进制文件、图片或授权数据，与 Stardock 无隶属或合作关系。
