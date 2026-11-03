# Third-party notices

OpenFenestra application source is licensed under MIT. The virtual desktop adapter
described below includes attributed definitions adapted from an MIT-licensed project.
No Stardock source or assets are included.

## Adapted source

`src/OpenFenestra.App/VirtualDesktopPinning.cs` adapts private COM interface
identifiers and the PinView sequence from [MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop/blob/a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6/VirtualDesktop11-24H2.cs),
copyright (c) 2017 Markus Scholtes, under the MIT License. The complete upstream
license is retained in that source file and in [docs/licenses/VirtualDesktop-MIT.txt](docs/licenses/VirtualDesktop-MIT.txt),
which is included in published packages. The adapter is used for the
application's fence-window lifecycle.

## Native compiler runtimes

`FenestraShellBridge.dll` is original MIT-licensed application code compiled with
either MSVC or MinGW-w64 GCC. Compiler runtime code linked into the DLL keeps its
own license; it is not relicensed by the application's MIT license.

MinGW builds statically link GCC libgcc/libstdc++ runtime portions and
MinGW-w64 runtime/winpthreads portions. The GCC components are
GPL-3.0-or-later **WITH GCC Runtime Library Exception 3.1**. The exception permits
eligible compiled combinations with independent modules to be distributed under
the terms of those modules; it does not require the independent OpenFenestra
application source to change from MIT. The complete GPL text is always paired
with the complete exception. The full MinGW-w64 CRT/runtime and winpthreads
copyright, permission, and disclaimer texts are retained as well.

MSVC builds use `/MT` and carry the complete Microsoft C++ Standard Library
Apache-2.0-with-LLVM-Exception license and component notices. Other Microsoft
compiler/CRT rights remain governed by the Visual Studio installation's terms.
MinGW runtime notices do not apply to an MSVC-produced bridge.

Verbatim reference materials, source versions, and hashes are under
[docs/licenses/native-runtime](docs/licenses/native-runtime/README.md). The native
build also generates `licenses/native-runtime/build-info.json` beside its DLL
and copies the selected compiler's notices there. That generated directory is
included in the application build and portable package so its `compilerFamily`
and hashes identify the materials for the actual binary. Preserve the licenses,
exception, notices, and build information when redistributing the package.

## Research references

The following projects were read for technical research and comparison:

| Project | License observed | Research scope |
| --- | --- | --- |
| [DayuanJiang/PecoFence](https://github.com/DayuanJiang/PecoFence) | [Apache-2.0](https://github.com/DayuanJiang/PecoFence/blob/main/LICENSE) | Native desktop host discovery, window z-order anchoring, product scope |
| [Walkoud/Palisades](https://github.com/Walkoud/Palisades) | [MIT](https://github.com/Walkoud/Palisades/blob/main/LICENSE) | WPF overlay/container architecture, feature comparison |
| [AnTeCP100/SimpleDesktopFence](https://github.com/AnTeCP100/SimpleDesktopFence) | [MIT](https://github.com/AnTeCP100/SimpleDesktopFence/blob/main/LICENSE) | Folder panels, file watchers, tray workflow described in README |
| [guHe330/DexCorral](https://github.com/guHe330/DexCorral/tree/47e65ff145a23d49a184e05f5b9a8131f0957bc5) | GPL-3.0-or-later (source headers) | Explorer-hosted drawing/input/drop integration, lifecycle and limitations; no code incorporated |
| [ramensoftware/windhawk-mods](https://github.com/ramensoftware/windhawk-mods) | MIT for the individual mods reviewed | Desktop custom drawing and subclass lifecycle; no code incorporated |
| [Creeptones/Pickets](https://github.com/Creeptones/Pickets/tree/38052a30dd2cf9df6776ca03669803df340e1492) | MIT | Native icon positioning and off-screen limitations; no code incorporated |
| [NextMindHQ/nextmind-desktop](https://github.com/NextMindHQ/nextmind-desktop) | Apache-2.0 | Transactional file movement and recovery as an alternative; no code incorporated |

Research-only projects are not dependencies. In particular, no GPL-licensed
DexCorral code has been added to this MIT-licensed implementation. Reusing its
implementation would require a separate licensing decision and compliance with
its license, rather than listing it as an MIT component.

The implementation uses standard Windows APIs and the .NET platform. Self-contained
builds redistribute the Microsoft .NET runtime. Its license and third-party notices
are included alongside the executable by the publish process.

Fences and Stardock are trademarks of their respective owners. No Stardock binary,
artwork, font, configuration template, source code, license key, or activation data
is included. OpenFenestra does not require Fences to be installed.

## Installer tooling

The setup wizards are built with [Inno Setup](https://jrsoftware.org/). The installer engine retains its original copyright and website notices. Its complete license is included in [docs/licenses/InnoSetup-LICENSE.txt](docs/licenses/InnoSetup-LICENSE.txt).

`installer/ChineseSimplified.isl` is retained from [Inno Setup is-6_7_4](https://github.com/jrsoftware/issrc/blob/is-6_7_4/Files/Languages/Unofficial/ChineseSimplified.isl), maintained by Zhenghan Yang; the upstream attribution and translation source comments remain in the file. This material and the setup engine are not relicensed as application MIT source.
