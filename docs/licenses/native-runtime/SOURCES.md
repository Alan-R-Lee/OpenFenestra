# Verbatim document provenance

Collected on 2026-11-02. The license/notice files listed below were copied as
unmodified bytes; `SHA256SUMS` records their digests. No runtime implementation
source was copied into OpenFenestra.

| Bundled document | Origin |
| --- | --- |
| `mingw/GPL-3.0.txt` | Installed MSYS2 UCRT64 `share/licenses/gcc-libs/COPYING3`, package `mingw-w64-ucrt-x86_64-gcc-libs` 16.1.0-2. GCC upstream also carries [COPYING3](https://github.com/gcc-mirror/gcc/blob/releases/gcc-16.1.0/COPYING3). |
| `mingw/GCC-Runtime-Library-Exception-3.1.txt` | Same installed package, `share/licenses/gcc-libs/COPYING.RUNTIME`. [GCC 16.1.0 upstream text](https://github.com/gcc-mirror/gcc/blob/releases/gcc-16.1.0/COPYING.RUNTIME); [official libstdc++ license explanation](https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html). |
| `mingw/MSYS2-GCC-runtime-README.txt` | Same installed package, `share/licenses/gcc-libs/README`; distinguishes the licenses of libgcc/libstdc++/libgomp/libatomic from the unlinked libquadmath library. |
| `mingw/MinGW-w64-runtime.txt` | Installed `share/licenses/crt/COPYING.MinGW-w64-runtime.txt`, package `mingw-w64-ucrt-x86_64-crt` 14.0.0.r37.g2bfe61fba-1. Complete distribution notice collection from that package. |
| `mingw/MinGW-w64-CRT-COPYING.txt` | Same installed package, `share/licenses/crt/COPYING`. Byte-identical to [MinGW-w64 upstream COPYING at 2bfe61fba](https://github.com/mingw-w64/mingw-w64/blob/2bfe61fba/COPYING). |
| `mingw/winpthreads-COPYING.txt` | Installed `share/licenses/winpthreads/COPYING`, packages `mingw-w64-ucrt-x86_64-winpthreads` / `libwinpthread` 14.0.0.r37.g2bfe61fba-1; both installed copies are identical. Byte-identical to [upstream winpthreads COPYING at 2bfe61fba](https://github.com/mingw-w64/mingw-w64/blob/2bfe61fba/mingw-w64-libraries/winpthreads/COPYING). |
| `msvc/Microsoft-STL-LICENSE.txt` | [Microsoft STL LICENSE.txt](https://github.com/microsoft/STL/blob/e1ed302683e5e822f663fbb0f44cee7e9ddc6af0/LICENSE.txt), official repository commit `e1ed302683e5e822f663fbb0f44cee7e9ddc6af0`. Includes the LLVM Exception in the same file. |
| `msvc/Microsoft-STL-NOTICE.txt` | [Microsoft STL NOTICE.txt](https://github.com/microsoft/STL/blob/e1ed302683e5e822f663fbb0f44cee7e9ddc6af0/NOTICE.txt), same commit; includes the complete component-specific notices. |

The compiler was resolved through `PATH`; its installation drive is not required
by the project or recorded in these tracked documents. On the audited machine,
GCC reports `x86_64-w64-mingw32`, GCC 16.1.0, Rev2, POSIX thread model. The
installed package records identify the versions above. MSYS2 package metadata is
supporting provenance; the actual included copyright/license text controls.

See also the primary project guidance:

- [MinGW-w64 licensing guidance](https://www.mingw-w64.org/support/#licensing)
- [MSYS2 package license information](https://www.msys2.org/license/)
- [Microsoft Visual C++ redistribution documentation](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)
