# Native Explorer bridge

This is an original MIT-licensed x64 C++ implementation using Win32 SDK interfaces.
It does not contain source copied or translated from DexCorral, Fences, or a hook
framework. It renders no desktop replacement window and never moves files,
changes native icon positions, or hides the desktop ListView globally.

## Build and isolated test

```powershell
./scripts/build-native.ps1 -OutputDirectory ./artifacts/native-win-x64 -SelfTest
```

The script discovers MSVC x64 through `vswhere` and `vcvars64.bat`, or MinGW-w64
`g++.exe` on PATH. `-Compiler msvc|mingw` and `-CompilerPath` are optional overrides.
The DLL uses a static C/C++ runtime. Its PE machine field is checked for AMD64.
MSVC uses `/MT /W4 /WX`; MinGW uses `-static -Wall -Wextra -Werror`.

The optional test executable loads the DLL into its own process. It creates hidden
standard and `LVS_OWNERDATA` ListViews and mock OLE data/drop objects. It never
locates or attaches to Explorer. `OpenFenestraAttachTestHost` rejects Explorer,
foreign-process windows, and windows belonging to another UI thread.

## Minimal bootstrap probe (diagnostic tools only)

`BootstrapProbe.c` deliberately omits the CRT, C++, TLS, COM, allocation, pinning,
window creation, and all drawing/input changes. It exports the same hook callback
and only acknowledges bootstrap messages with stage 1 (executing PID) and stage 2
(executing TID). The explicit entry point returns TRUE without initialization.
It distinguishes a loader/message-delivery failure from full bridge initialization.

`BootstrapProbeHost.c` is an opt-in console launcher. It verifies the real Explorer
desktop hierarchy, creates its own message-only reply HWND, installs a temporary
desktop-thread hook, sends messages for up to five seconds, prints acknowledgments
and architecture/mitigation diagnostics, then removes its hook and reply window.
It does not register Shell extensions or alter the desktop. **Running this host
against the probe is a real temporary Explorer hook test; compiling it is not.**
Keep both diagnostic artifacts outside the production package.

Manual MinGW x64 build from the repository root:

```powershell
$nativeSource = (Resolve-Path './native/ShellBridge').Path
$nativeProbeOutput = (New-Item -ItemType Directory -Path './artifacts/native-win-x64' -Force).FullName
Push-Location -LiteralPath $nativeProbeOutput
try {
    gcc -std=c17 -O2 -Wall -Wextra -Werror -fno-stack-protector -nostdlib -shared '-Wl,--entry,ProbeDllMain' "$nativeSource/BootstrapProbe.c" -o FenestraBootstrapProbe.dll -luser32 -lkernel32
    gcc -std=c17 -O2 -Wall -Wextra -Werror -municode -static -static-libgcc "$nativeSource/BootstrapProbeHost.c" -o NativeBootstrapProbeHost.exe -luser32 -lkernel32 -ladvapi32
} finally { Pop-Location }
```

The probe DLL should import only `KERNEL32.dll` and `USER32.dll`, with an empty TLS
directory. The host itself uses a CRT for console diagnostics. To run the opt-in
test after preparing to observe Explorer safely:

```powershell
./artifacts/native-win-x64/NativeBootstrapProbeHost.exe ((Resolve-Path './artifacts/native-win-x64/FenestraBootstrapProbe.dll').Path)
```

Passing the **full bridge DLL** instead of the probe allows normal bridge startup
and subclassing; that is a different test and must not be described as the minimal
probe's no-state-change behavior.

## Controller protocol, version 1

The controller loads `FenestraShellBridge.dll` and resolves
`OpenFenestraHookProc`. It installs a temporary thread-specific `WH_GETMESSAGE`
hook on the **real Explorer desktop UI thread** and posts a message to wake that
thread. Initialization requires a removed (`PM_REMOVE`) `Bootstrap.v1` message
with a live reply HWND. Unrelated messages, including `WM_NULL`, only continue
the hook chain. Entry diagnostics are posted before any initialization work.
The hook validates the Explorer executable and the matching
`Progman`/`WorkerW` → `SHELLDLL_DefView` → `SysListView32` hierarchy. The controller
removes the temporary hook once the endpoint exists. The DLL is pinned so no live
window procedure or COM callback can point into an unloaded module.

For opt-in startup diagnostics, post the message registered as
`OpenFenestra.NativeShellBridge.Bootstrap.v1` to that desktop ListView, with the
controller HWND in `wParam` and a random 16-bit nonce in `lParam`. The callback
asynchronously posts `OpenFenestra.NativeShellBridge.BootstrapReply.v1` to that
HWND: reply `wParam = (nonce << 16) | stage`, `lParam = value`. The controller may
drain only that reply message using `PeekMessage` while waiting for attachment;
no synchronous callback into the controller is made. No diagnostic file is written.

| Stage | Meaning of value |
| --- | --- |
| 1 / 2 / 3 | Hook callback PID / callback thread ID / executable is Explorer (1 or 0) |
| 10 | Candidate Progman or WorkerW HWND |
| 12 / 13 | Candidate DefView HWND / its thread ID |
| 14 / 15 | Candidate ListView HWND / its thread ID |
| 20 | No matching desktop view (`ERROR_NOT_FOUND`) |
| 30 | Entered Prepare (0), or failed window/thread validation (`ERROR_INVALID_WINDOW_HANDLE`) |
| 31–36 | Pin module / register class / create endpoint / subclass DefView / subclass ListView / create watchdog: 0 on success, Win32 error on failure |
| 37 / 38 | Prepared capability bits / endpoint HWND |
| 39 | An endpoint already exists in this loaded module (HWND) |

If an API reports failure without setting last error, diagnostics use
`ERROR_GEN_FAILURE` rather than ambiguously reporting a zero error. Receiving no
stage 1 does not establish that a security product blocked injection: it means
the diagnostic callback has not acknowledged executing in the target process.

The endpoint is a message-only window with class:

```text
OpenFenestra.NativeShellBridge.v1
```

Locate it with `FindWindowEx(HWND_MESSAGE, ...)`, and validate its process ID
against the intended shell process. All commands use synchronous `WM_COPYDATA`;
the sender should set a finite timeout and never terminate a timed-out receiver.
`wParam` is the controller's live HWND. The payload is a UTF-16LE MULTI_SZ, with a
NUL after each string and one additional final NUL. The maximum payload is 2 MiB.

| `COPYDATASTRUCT.dwData` | Strings in payload | Result |
| --- | --- | --- |
| 1: snapshot | controller PID in decimal; controller HWND in unsigned decimal; random session token of 16–256 characters; zero or more canonical parsing paths | TRUE if the snapshot was accepted |
| 2: heartbeat | session token | TRUE if sender and token match the active controller |
| 3: detach | session token | TRUE if sender and token match; restores native behavior |
| 4: build identity probe | expected SHA-256 as 64 hexadecimal characters | TRUE only if it matches the loaded module's parent directory name; no state changes |

Snapshots **replace** the assigned set. Filesystem paths and namespace parsing
names such as `::{645FF040-5081-101B-9F08-00AA002F954E}` are compared with
`CompareStringOrdinal(..., TRUE)`. Identity resolution never falls back to a
display name. A process handle and sender HWND bind the lease to the original
controller, avoiding PID reuse. A 500 ms watchdog stops integration when that
process exits, the HWND disappears, or no heartbeat arrives for more than 8 s.
The controller should send a heartbeat every 2 s.

Before connecting, the controller copies a verified DLL to its own cache directory
named with that DLL's SHA-256. It sends command 4 and requires a match before any
assignment. The bridge parses its own module path once at attachment; the probe
does not read disk, hash data, change ownership, or unload another module. A DLL
loaded from a directory without a 64-hexadecimal-character name rejects probes.

Packaged/MSIX controller hosts can transparently redirect `%LOCALAPPDATA%` into
their package's `LocalCache`. A cached DLL can then be readable through its
logical path in the controller while unpackaged Explorer cannot resolve that
same path. Before `LoadLibrary` and the thread hook, resolve the cached file's
physical path using `GetFinalPathNameByHandle` on the actual open file handle.
Return that physical path to the loader. Comparing hashes through the redirected
logical path alone does not establish cross-process visibility. This correction
does not require changing permissions, App Control, or any security setting.

Two messages registered using `RegisterWindowMessage` support verification:

| Name | Return value |
| --- | --- |
| `OpenFenestra.NativeShellBridge.Status.v1` | `0x4F460100 OR caps`; draw=1, selection=2, input=4, currently owned OLE wrapper=8 |
| `OpenFenestra.NativeShellBridge.HiddenCount.v1` | Number of actual native item indices matched in the latest identity snapshot |
| `OpenFenestra.NativeShellBridge.Diagnostics.v1` | 0 while queued/not yet resolved; 1 after a successful identity rebuild; otherwise the signed 32-bit HRESULT of the last resolution failure |

Require all four capabilities before enabling native grouping. An accepted
snapshot acknowledges metadata only: matching is asynchronous. Allow a bounded
interval for `HiddenCount`/`Diagnostics` to settle. The snapshot does not by itself
prove that every requested path matched; compare
`HiddenCount` with native desktop membership. If Shell enumeration is incomplete,
the bridge suppresses nothing. A controller can detach and later load/attach
again in the same Explorer session.

## Drawing, selection and drop behavior

Shell identities are resolved on the desktop UI thread through `IShellWindows`,
`IShellBrowser`, `IFolderView`, and `IShellFolder::GetDisplayNameOf`. Painting only
reads a cached per-index bitmap. It never calls the controller, disk, or Shell
COM. Item changes invalidate the bitmap immediately; a queued refresh and a
periodic identity refresh rebuild it outside the paint callback. An unknown or
stale identity stays visible.

`WM_COPYDATA` only validates and swaps assignment metadata, clears the obsolete
bitmap, and posts a normal rebuild message. It does not acquire Shell COM objects:
synchronous cross-process input callbacks can reject outgoing COM calls with
`RPC_E_CANTCALLOUT_ININPUTSYNCCALL`. Rebuild work additionally checks
`InSendMessageEx` and defers itself if reached from such a callback. The acquired
`IFolderView`/`IShellFolder` are reused on the owning desktop UI thread until
resolution fails or the bridge detaches.

The parent subclass requests per-item `NM_CUSTOMDRAW` notifications and returns
`CDRF_SKIPDEFAULT` for matched indices. Input filtering covers hit testing, mouse
activation, hover, selection writes, native keyboard commands, and selection
notifications. Owner-data controls do not send `LVN_ITEMCHANGING`; they are also
protected by selection-write filtering, `LVN_ODSTATECHANGED` cleanup, and cleanup
before native commands and after keyboard navigation. Arrow navigation skips
consecutive hidden items with a bounded loop.

Explorer's internal OLE target can bypass ListView hit-test messages. The bridge
therefore wraps its original `IDropTarget`, preserving COM references and the
DragEnter/DragLeave sequence. **An external file dragged over the original
position of a suppressed icon is rejected with `DROPEFFECT_NONE`; it is not
redirected to blank desktop.** Other positions retain the original behavior.
Private application drags back to the desktop are handled by the managed
controller and do not require that rejected native drop to succeed.

The `OleDropTargetInterface` window property used to obtain the original target
is an implementation dependency, not a Microsoft-supported extension point.
If another component replaces the registration, the bridge immediately stops
suppression on its next watchdog tick and leaves that new registration intact.
On detach, it restores the original target only while it still owns the
registration. A disabled transparent forwarder is retained if restoring the
original registration fails. Subclasses are removed on their owning UI thread.

## Verification boundary

The isolated harness validates both standard and owner-data controls, identity
matching, draw results, internal and bulk selection, hit testing, OLE rejection
and forwarding, controller disappearance, repeated attachment, and failure after
a foreign OLE target replaces the wrapper. This is not equivalent to validating
every Windows Explorer build, third-party Shell extension, accessibility client,
or virtual-desktop transition. The implementation uses C++ exception guards; it
does not promise recovery from arbitrary access violations inside Explorer or
other extensions. Production activation and rollback must be verified separately.

Microsoft reference contracts:

- [ListView custom drawing](https://learn.microsoft.com/en-us/windows/win32/controls/nm-customdraw-list-view)
- [LVN_ITEMCHANGING and owner-data limitation](https://learn.microsoft.com/en-us/windows/win32/controls/lvn-itemchanging)
- [SetWindowSubclass thread requirement](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass)
- [SetWindowsHookEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)
- [Obtaining the desktop Shell view](https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933/)
