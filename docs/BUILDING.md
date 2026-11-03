# 构建与开发

## 工具链

- Windows 11 x64。
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，版本选择见 `global.json`。
- 任一 C++ 工具链：Visual Studio / Build Tools 的 MSVC x64 + Windows SDK，或 MSYS2 UCRT64 的 MinGW-w64 `g++`。

脚本通过 `vswhere` 寻找 MSVC，或从 `PATH` 寻找 x64 MinGW。可显式指定：

```powershell
.\scripts\build-native.ps1 -Compiler mingw -CompilerPath "D:\msys64\ucrt64\bin\g++.exe" -SelfTest
```

MinGW 需要保留同一工具链的 runtime licenses；非标准位置可设置 `OPENFENESTRA_MINGW_LICENSE_ROOT`。许可文件的来源和哈希见[说明](licenses/native-runtime/README.md)。

## 常用命令

```powershell
# 编译解决方案，自动构建原生 DLL
dotnet build OpenFenestra.sln -c Release

# 运行开发副本，配置与日常安装隔离
dotnet run --project src/OpenFenestra.App -- --data-dir ".qa/dev-data"

# 托管回归与 WPF 冒烟，使用独立样例数据
.\scripts\test.ps1

# 原生隔离验证，不接入 Explorer
.\scripts\build-native.ps1 -SelfTest

# 生成自带 .NET runtime 的便携 ZIP
.\scripts\build.ps1
.\scripts\verify-package.ps1
```

便携包位于 `artifacts/OpenFenestra-win-x64.zip`，同目录生成 SHA-256 校验文件。必须完整保留 DLL、许可证和文档。当前打包脚本仅支持 `win-x64`。

仅开发托管界面或 Core 时可使用 `-p:EnableNativeShellBridge=false`，该构建不提供原生桌面整合。日常实例管理原生桌面时，请勿在开发副本中同时开启原生整合。

真实桌面接入检查为 `scripts/test-desktop.ps1`，会短暂改变实际图标归属并终止专用测试控制器验证恢复。请先阅读[测试边界](TESTING.md)，关闭其他原生整合实例后再运行。

## 可选安装

```powershell
.\scripts\build.ps1
.\scripts\install.ps1 -StartWithWindows -DesktopIntegration

# 或指定安装目录
.\scripts\install.ps1 -Destination "D:\Apps\OpenFenestra"
```

安装前退出已安装副本。默认安装到当前用户的程序目录，不需要把工程目录或开发 SDK 一起分发。

## 目录导航

| 目录 | 内容 |
| --- | --- |
| `src/OpenFenestra.Core` | 布局、排序、归属、校验和持久化 |
| `src/OpenFenestra.App` | WPF 控制台、分栏、托盘、Shell 交互和自启动 |
| `native/ShellBridge` | C++ Explorer 桥接及隔离验证 |
| `tests` | Core 和 Shell 数据测试程序 |
| `scripts` | 构建、测试、安装与图标生成 |
| `docs` | 使用、架构、验证和许可材料 |

编译产物、研究副本、个人配置及 `.qa` 检查结果均不纳入 Git。图标生成脚本只使用 Python 标准库，日常构建无需 Python。

## 安装向导构建

需要 [Inno Setup 6.5+](https://jrsoftware.org/isdl.php)，遵守其使用条款。脚本会发现 PATH 或标准安装位置中的 ISCC，也可传入 `-CompilerPath`。

```powershell
.\scripts\build-installer.ps1
# 已构建便携包时跳过重复发布
.\scripts\build-installer.ps1 -SkipPortableBuild
# 在未注册本产品安装信息的测试用户/环境中验证安装、启动、卸载及数据保留
.\scripts\test-installer.ps1
```

输出两个版本化的 Setup EXE 及 `SHA256SUMS.txt`。Light 使用框架依赖发布，已有 .NET 10 x64 桌面运行时即可安装；否则由向导指向 Microsoft 官方下载。Offline 包含运行时。安装器使用文件白名单，不包含个人配置或开发产物；不为 WPF 启用不受支持的裁剪。

`installer/ChineseSimplified.isl` 来自固定版本的 Inno Setup 社区翻译；版权和来源见第三方说明。
