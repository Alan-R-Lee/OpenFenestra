/* SPDX-License-Identifier: MIT
 * Opt-in diagnostic launcher. Creates only its own message-only HWND and a
 * temporary thread-specific WH_GETMESSAGE hook. Never changes desktop state.
 */
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0a00
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

static HWND desktop_view;
static HWND desktop_list;
static DWORD shell_pid;

static BOOL CALLBACK FindDesktop(HWND host, LPARAM unused)
{
    DWORD pid = 0;
    wchar_t class_name[32] = {0};
    HWND view, list;
    (void)unused;
    GetWindowThreadProcessId(host, &pid);
    if (pid != shell_pid) return TRUE;
    GetClassNameW(host, class_name, 32);
    if (wcscmp(class_name, L"Progman") && wcscmp(class_name, L"WorkerW")) return TRUE;
    view = FindWindowExW(host, NULL, L"SHELLDLL_DefView", NULL);
    list = view ? FindWindowExW(view, NULL, L"SysListView32", NULL) : NULL;
    if (list) { desktop_view = view; desktop_list = list; return FALSE; }
    return TRUE;
}

static DWORD Integrity(HANDLE process)
{
    HANDLE token = NULL;
    BYTE bytes[512];
    DWORD needed = 0, level = 0;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return 0;
    if (GetTokenInformation(token, TokenIntegrityLevel, bytes, sizeof(bytes), &needed)) {
        const TOKEN_MANDATORY_LABEL* label = (const TOKEN_MANDATORY_LABEL*)bytes;
        const UCHAR count = *GetSidSubAuthorityCount(label->Label.Sid);
        if (count) level = *GetSidSubAuthority(label->Label.Sid, count - 1);
    }
    CloseHandle(token);
    return level;
}

static void Machines(HANDLE target)
{
    USHORT own_machine = 0, own_native = 0, target_machine = 0, target_native = 0;
    PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY signature = {0};
    PROCESS_MITIGATION_CONTROL_FLOW_GUARD_POLICY cfg = {0};
    PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY extension = {0};
    const BOOL own_ok = IsWow64Process2(GetCurrentProcess(), &own_machine, &own_native);
    const BOOL target_ok = IsWow64Process2(target, &target_machine, &target_native);
    printf("machines controller=%d:%04X/%04X shell=%d:%04X/%04X integrity=%lu/%lu\n",
        own_ok, own_machine, own_native, target_ok, target_machine, target_native,
        Integrity(GetCurrentProcess()), Integrity(target));
    if (GetProcessMitigationPolicy(target, ProcessSignaturePolicy, &signature, sizeof(signature)))
        printf("shell signatureFlags=%lu\n", signature.Flags);
    if (GetProcessMitigationPolicy(target, ProcessControlFlowGuardPolicy, &cfg, sizeof(cfg)))
        printf("shell cfgFlags=%lu\n", cfg.Flags);
    if (GetProcessMitigationPolicy(target, ProcessExtensionPointDisablePolicy, &extension, sizeof(extension)))
        printf("shell extensionFlags=%lu\n", extension.Flags);
}

int wmain(int argc, wchar_t** argv)
{
    HWND shell_window, owner = NULL;
    HANDLE process = NULL;
    HMODULE library = NULL;
    HHOOK hook = NULL;
    HOOKPROC procedure = NULL;
    FARPROC exported;
    DWORD thread, view_pid = 0, list_pid = 0, view_thread;
    DWORD image_length = 32768;
    wchar_t image[32768] = {0};
    const wchar_t* image_name;
    WNDCLASSW cls = {0};
    UINT bootstrap, reply;
    WORD nonce;
    ULONGLONG start;
    BOOL saw_entry = FALSE, saw_target = FALSE;
    int result = 1;
    if (argc != 2) {
        fprintf(stderr, "Usage: NativeBootstrapProbeHost.exe <bootstrap-probe DLL>\n");
        return 2;
    }
    shell_window = GetShellWindow();
    if (!shell_window) { fprintf(stderr, "No shell window.\n"); goto cleanup; }
    GetWindowThreadProcessId(shell_window, &shell_pid);
    FindDesktop(shell_window, 0);
    if (!desktop_list) EnumWindows(FindDesktop, 0);
    if (!desktop_list) { fprintf(stderr, "No validated desktop hierarchy.\n"); goto cleanup; }
    thread = GetWindowThreadProcessId(desktop_list, &list_pid);
    view_thread = GetWindowThreadProcessId(desktop_view, &view_pid);
    if (!thread || thread != view_thread || list_pid != shell_pid || view_pid != shell_pid) {
        fprintf(stderr, "Desktop thread/process mismatch.\n"); goto cleanup;
    }
    process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, shell_pid);
    if (!process || !QueryFullProcessImageNameW(process, 0, image, &image_length)) {
        fprintf(stderr, "Cannot verify shell executable: %lu\n", GetLastError()); goto cleanup;
    }
    image_name = wcsrchr(image, L'\\');
    if (_wcsicmp(image_name ? image_name + 1 : image, L"explorer.exe")) {
        fprintf(stderr, "Desktop is not Explorer; refusing hook.\n"); goto cleanup;
    }
    printf("controller pid=%lu tid=%lu shell pid=%lu desktop tid=%lu view=%p list=%p\n",
        GetCurrentProcessId(), GetCurrentThreadId(), shell_pid, thread, (void*)desktop_view, (void*)desktop_list);
    Machines(process);
    cls.lpfnWndProc = DefWindowProcW;
    cls.hInstance = GetModuleHandleW(NULL);
    cls.lpszClassName = L"OpenFenestra.NativeBootstrapProbeHost";
    if (!RegisterClassW(&cls)) { fprintf(stderr, "RegisterClass: %lu\n", GetLastError()); goto cleanup; }
    owner = CreateWindowExW(0, cls.lpszClassName, NULL, 0, 0, 0, 0, 0,
        HWND_MESSAGE, NULL, cls.hInstance, NULL);
    if (!owner) { fprintf(stderr, "CreateWindow: %lu\n", GetLastError()); goto cleanup; }
    library = LoadLibraryW(argv[1]);
    if (!library) { fprintf(stderr, "LoadLibrary: %lu\n", GetLastError()); goto cleanup; }
    exported = GetProcAddress(library, "OpenFenestraHookProc");
    if (!exported) { fprintf(stderr, "GetProcAddress: %lu\n", GetLastError()); goto cleanup; }
    memcpy(&procedure, &exported, sizeof(procedure));
    hook = SetWindowsHookExW(WH_GETMESSAGE, procedure, library, thread);
    if (!hook) { fprintf(stderr, "SetWindowsHookExW: %lu\n", GetLastError()); goto cleanup; }
    printf("thread hook installed; diagnostic window=%p\n", (void*)owner);
    bootstrap = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.Bootstrap.v1");
    reply = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.BootstrapReply.v1");
    nonce = (WORD)((GetTickCount() ^ GetCurrentProcessId()) | 1);
    start = GetTickCount64();
    while (GetTickCount64() - start < 5000) {
        MSG message;
        if (!PostMessageW(desktop_list, WM_NULL, 0, 0)) printf("PostMessage WM_NULL error=%lu\n", GetLastError());
        if (!PostMessageW(desktop_list, bootstrap, (WPARAM)owner, nonce)) printf("PostMessage bootstrap error=%lu\n", GetLastError());
        if (!PostThreadMessageW(thread, bootstrap, (WPARAM)owner, nonce)) printf("PostThreadMessage error=%lu\n", GetLastError());
        while (PeekMessageW(&message, owner, reply, reply, PM_REMOVE)) {
            WORD stage;
            if (HIWORD(message.wParam) != nonce) continue;
            stage = LOWORD(message.wParam);
            printf("bootstrap stage=%u value=%lld\n", stage, (long long)message.lParam);
            if (stage == 1) { saw_entry = TRUE; if ((DWORD)message.lParam == shell_pid) saw_target = TRUE; }
        }
        if (saw_target) break;
        Sleep(40);
    }
    printf("result entry=%d targetExplorer=%d elapsedMs=%llu\n", saw_entry, saw_target,
        (unsigned long long)(GetTickCount64() - start));
    result = saw_target ? 0 : 1;
cleanup:
    if (hook) UnhookWindowsHookEx(hook);
    if (library) FreeLibrary(library);
    if (owner) DestroyWindow(owner);
    if (process) CloseHandle(process);
    return result;
}
