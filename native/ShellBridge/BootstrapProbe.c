/* SPDX-License-Identifier: MIT
 * Diagnostic-only x64 hook callback. No CRT, C++, COM, TLS, allocation, window
 * creation, subclassing, file access, module pinning, or desktop state changes.
 * Compile with an explicit ProbeDllMain entry point and -nostdlib.
 */
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#ifndef _WIN64
#error This diagnostic DLL is intended for Windows x64 only.
#endif

static UINT bootstrap_message;
static UINT reply_message;

#ifdef __cplusplus
extern "C" {
#endif

__declspec(dllexport) LRESULT CALLBACK OpenFenestraHookProc(int code, WPARAM action, LPARAM parameter)
{
    if (code >= 0 && action == PM_REMOVE && parameter != 0) {
        const MSG* message = (const MSG*)parameter;
        if (bootstrap_message == 0)
            bootstrap_message = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.Bootstrap.v1");
        if (reply_message == 0)
            reply_message = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.BootstrapReply.v1");
        if (message->message == bootstrap_message) {
            HWND recipient = (HWND)message->wParam;
            WPARAM prefix = ((WPARAM)LOWORD(message->lParam)) << 16;
            if (IsWindow(recipient)) {
                PostMessageW(recipient, reply_message, prefix | 1, (LPARAM)GetCurrentProcessId());
                PostMessageW(recipient, reply_message, prefix | 2, (LPARAM)GetCurrentThreadId());
            }
        }
    }
    return CallNextHookEx(NULL, code, action, parameter);
}

BOOL WINAPI ProbeDllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance;
    (void)reason;
    (void)reserved;
    return TRUE;
}

#ifdef __cplusplus
}
#endif
