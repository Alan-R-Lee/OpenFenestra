# Native bridge runtime notices

OpenFenestra's application and native bridge source remain MIT-licensed. The
compiler runtime portions linked into `FenestraShellBridge.dll` retain their
own licenses. This directory contains unabridged license/notice documents, not
additional application source code.

The build writes the notices for its selected compiler and a `build-info.json`
to `licenses/native-runtime` beside the native DLL. The application build and
portable package carry that generated directory. Consult `compilerFamily` in
`build-info.json` to identify which compiler-specific notices apply to a binary.
Both compiler families are documented in the source distribution so either can
be built offline.

## MinGW-w64 GCC builds

The build uses `-static -static-libgcc -static-libstdc++`. The audited reference
toolchain is MSYS2 UCRT64 GCC 16.1.0-2 (Rev2) with MinGW-w64 CRT/headers and
winpthreads 14.0.0.r37.g2bfe61fba-1. The resulting bridge contains GCC C++
exception/runtime and winpthreads symbols; it does not redistribute the GCC
compiler executable, libquadmath, libgomp, or a separate GCC/winpthreads DLL.

`mingw/GPL-3.0.txt` must be read together with
`mingw/GCC-Runtime-Library-Exception-3.1.txt`. GCC's libgcc and libstdc++ runtime
code is GPL-3.0-or-later **with GCC Runtime Library Exception 3.1**, as stated in
the copied MSYS2 runtime README. Section 1 of that exception permits eligible
compiled combinations with independent modules to be conveyed under the terms
of those modules. OpenFenestra's ordinary GCC compilation is such a combination;
the GPL text here does not change OpenFenestra's MIT source license. The exception
does not relicense the GCC compiler or permit removing its own copyleft terms.

The MSYS2 gcc-libs package README also discusses libquadmath's LGPL license.
That library is not linked by this build and is not included in this package.
The package-wide license description must not be confused with the licenses of
the individual runtime components actually linked.

`mingw/MinGW-w64-runtime.txt` is the complete runtime notice collection supplied
with the installed CRT. Its accompanying upstream packaging guidance recommends
retaining the whole collection for distributed binaries. The separate CRT
`COPYING` and complete winpthreads `COPYING` are also retained. The latter
includes both the mingw-w64 MIT grant and the embedded Lockless copyright,
redistribution conditions, and disclaimer.

The native build copies the installed toolchain's files from
`<compiler-prefix>/share/licenses/{gcc-libs,crt,winpthreads}` (or
`libwinpthread`). For a distribution with another directory layout, set
`OPENFENESTRA_MINGW_LICENSE_ROOT` to a directory containing those component
subdirectories and their original documents. A MinGW build fails if this
complete runtime notice set cannot be located, instead of silently publishing
an incomplete bundle. The tracked reference copies provide provenance and a
comparison baseline; they are not silently substituted for a different
compiler distribution's notices.

## MSVC builds

MSVC builds use `/MT`, so no separate Visual C++ redistributable installer or
runtime DLL is copied by the native build script. The `msvc` directory retains
the complete Microsoft C++ Standard Library Apache-2.0 license with LLVM
Exception and its complete `NOTICE.txt`, obtained from Microsoft's official
STL repository at the revision recorded in `SOURCES.md`.

Those STL documents apply to the standard library component. They do not grant
rights to redistribute the Visual Studio compiler, Windows SDK, or unrelated
Microsoft components. MSVC/CRT use and redistribution follow the terms of the
Visual Studio installation used for the build; Microsoft's documentation is at
<https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files>.
GCC and MinGW runtime licenses do not apply to an MSVC-built bridge.

## Other application components

The .NET runtime notices are packaged separately under `licenses` by
`scripts/build.ps1`. The root `THIRD_PARTY_NOTICES.md` and the application's MIT
`LICENSE` remain part of the portable distribution. Preserve these materials
when redistributing the package.
