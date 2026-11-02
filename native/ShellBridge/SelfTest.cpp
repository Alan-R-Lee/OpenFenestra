// SPDX-License-Identifier: MIT
// Isolated Win32 controls only. This executable never locates or hooks Explorer.
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <ole2.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {
int assertions = 0;
void Check(bool condition, const char* message) {
    ++assertions;
    if (!condition) { std::fprintf(stderr, "FAIL: %s (lastError=%lu)\n", message, GetLastError()); ExitProcess(1); }
}
LRESULT CALLBACK HostProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == WM_NOTIFY && lParam) {
        auto* header = reinterpret_cast<NMHDR*>(lParam);
        if (header->code == LVN_GETDISPINFOW) {
            auto* request = reinterpret_cast<NMLVDISPINFOW*>(lParam);
            if (request->item.mask & LVIF_TEXT) request->item.pszText = const_cast<wchar_t*>(L"Test icon");
        }
    }
    return DefWindowProcW(window, message, wParam, lParam);
}
class MockData final : public IDataObject {
    LONG refs = 1;
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** p) override {
        if (!p) return E_POINTER;
        *p = nullptr;
        if (id != IID_IUnknown && id != IID_IDataObject) return E_NOINTERFACE;
        *p = static_cast<IDataObject*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs)); }
    ULONG STDMETHODCALLTYPE Release() override { const auto n = static_cast<ULONG>(InterlockedDecrement(&refs)); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE GetData(FORMATETC*, STGMEDIUM*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetDataHere(FORMATETC*, STGMEDIUM*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE QueryGetData(FORMATETC*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCanonicalFormatEtc(FORMATETC*, FORMATETC*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetData(FORMATETC*, STGMEDIUM*, BOOL) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EnumFormatEtc(DWORD, IEnumFORMATETC**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE DAdvise(FORMATETC*, DWORD, IAdviseSink*, DWORD*) override { return OLE_E_ADVISENOTSUPPORTED; }
    HRESULT STDMETHODCALLTYPE DUnadvise(DWORD) override { return OLE_E_ADVISENOTSUPPORTED; }
    HRESULT STDMETHODCALLTYPE EnumDAdvise(IEnumSTATDATA**) override { return OLE_E_ADVISENOTSUPPORTED; }
};
class MockDrop final : public IDropTarget {
    LONG refs = 1;
public:
    int enters = 0, overs = 0, drops = 0, leaves = 0;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** p) override {
        if (!p) return E_POINTER;
        *p = nullptr;
        if (id != IID_IUnknown && id != IID_IDropTarget) return E_NOINTERFACE;
        *p = static_cast<IDropTarget*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs)); }
    ULONG STDMETHODCALLTYPE Release() override { const auto n = static_cast<ULONG>(InterlockedDecrement(&refs)); if (!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE DragEnter(IDataObject*, DWORD, POINTL, DWORD* effect) override { ++enters; *effect = DROPEFFECT_COPY; return S_OK; }
    HRESULT STDMETHODCALLTYPE DragOver(DWORD, POINTL, DWORD* effect) override { ++overs; *effect = DROPEFFECT_COPY; return S_OK; }
    HRESULT STDMETHODCALLTYPE DragLeave() override { ++leaves; return S_OK; }
    HRESULT STDMETHODCALLTYPE Drop(IDataObject*, DWORD, POINTL, DWORD* effect) override { ++drops; *effect = DROPEFFECT_COPY; return S_OK; }
};
std::vector<wchar_t> Multi(const std::vector<std::wstring>& strings) {
    std::vector<wchar_t> result;
    for (const auto& text : strings) { result.insert(result.end(), text.begin(), text.end()); result.push_back(0); }
    result.push_back(0);
    return result;
}
LRESULT Packet(HWND bridge, HWND owner, ULONG_PTR command, const std::vector<std::wstring>& strings) {
    auto data = Multi(strings);
    COPYDATASTRUCT packet{command, static_cast<DWORD>(data.size() * sizeof(wchar_t)), data.data()};
    return SendMessageW(bridge, WM_COPYDATA, reinterpret_cast<WPARAM>(owner), reinterpret_cast<LPARAM>(&packet));
}
POINT ItemPoint(HWND list, int index) {
    RECT bounds{};
    Check(ListView_GetItemRect(list, index, &bounds, LVIR_ICON) != FALSE, "get item icon rectangle");
    POINT point{(bounds.left + bounds.right) / 2, (bounds.top + bounds.bottom) / 2};
    return point;
}
POINTL ScreenItemPoint(HWND list, int index) {
    POINT point = ItemPoint(list, index);
    ClientToScreen(list, &point);
    return {point.x, point.y};
}
void Pump(unsigned milliseconds) {
    const auto end = GetTickCount64() + milliseconds;
    do {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        Sleep(5);
    } while (GetTickCount64() < end);
}
template<class T> T Procedure(HMODULE module, const char* name) {
    const FARPROC address = GetProcAddress(module, name);
    Check(address != nullptr, "test attach export");
    T function = nullptr;
    static_assert(sizeof(function) == sizeof(address));
    std::memcpy(&function, &address, sizeof(function));
    return function;
}
void Run(HMODULE module, bool ownerData) {
    const auto instance = GetModuleHandleW(nullptr);
    HWND parent = CreateWindowExW(0, L"OpenFenestra.IsolatedTestHost", L"Hidden test host", WS_POPUP,
        0, 0, 600, 300, nullptr, nullptr, instance, nullptr);
    HWND owner = CreateWindowExW(0, L"OpenFenestra.IsolatedTestHost", L"Test controller", 0,
        0, 0, 0, 0, HWND_MESSAGE, nullptr, instance, nullptr);
    HWND list = CreateWindowExW(0, WC_LISTVIEWW, nullptr,
        WS_CHILD | LVS_ICON | (ownerData ? LVS_OWNERDATA : 0),
        0, 0, 580, 280, parent, nullptr, instance, nullptr);
    Check(parent && owner && list, "isolated host windows created");
    // Exercise the bootstrap diagnostic callback directly in this non-Explorer
    // process. It must report the actual PID/thread and refuse production attach.
    using HookProcedure = LRESULT(CALLBACK*)(int, WPARAM, LPARAM);
    const auto hookProcedure = Procedure<HookProcedure>(module, "OpenFenestraHookProc");
    const UINT bootstrap = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.Bootstrap.v1");
    const UINT bootstrapReply = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.BootstrapReply.v1");
    MSG bootstrapRequest{}; bootstrapRequest.hwnd = list; bootstrapRequest.message = bootstrap;
    bootstrapRequest.wParam = reinterpret_cast<WPARAM>(owner); bootstrapRequest.lParam = 37;
    hookProcedure(HC_ACTION, PM_REMOVE, reinterpret_cast<LPARAM>(&bootstrapRequest));
    bool sawPid = false, sawThread = false, sawRejection = false;
    MSG bootstrapResponse{};
    while (PeekMessageW(&bootstrapResponse, owner, bootstrapReply, bootstrapReply, PM_REMOVE)) {
        Check(HIWORD(bootstrapResponse.wParam) == 37, "bootstrap reply preserves nonce");
        if (LOWORD(bootstrapResponse.wParam) == 1) sawPid = bootstrapResponse.lParam == GetCurrentProcessId();
        if (LOWORD(bootstrapResponse.wParam) == 2) sawThread = bootstrapResponse.lParam == GetCurrentThreadId();
        if (LOWORD(bootstrapResponse.wParam) == 3) sawRejection = bootstrapResponse.lParam == 0;
    }
    Check(sawPid && sawThread && sawRejection, "bootstrap reports entry and rejects a non-Explorer process");
    if (ownerData) ListView_SetItemCount(list, 3);
    else {
        for (int i = 0; i < 3; ++i) {
            LVITEMW item{}; item.mask = LVIF_TEXT; item.iItem = i;
            item.pszText = const_cast<wchar_t*>(L"Test icon");
            Check(ListView_InsertItem(list, &item) == i, "insert test icon");
        }
    }
    for (int i = 0; i < 3; ++i) ListView_SetItemPosition32(list, i, 40 + 150 * i, 40);
    auto* original = new MockDrop;
    Check(SUCCEEDED(RegisterDragDrop(list, original)), "register original test drop target");
    const auto originalProc = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(list, GWLP_WNDPROC));
    using Attach = BOOL(WINAPI*)(HWND, HWND, const wchar_t*, DWORD);
    const auto attach = Procedure<Attach>(module, "OpenFenestraAttachTestHost");
    const std::vector<std::wstring> identities{L"C:\\Desktop\\Hidden.lnk", L"C:\\Desktop\\Visible.lnk", L"::{645FF040-5081-101B-9F08-00AA002F954E}"};
    const auto names = Multi(identities);
    Check(attach(parent, list, names.data(), static_cast<DWORD>(names.size())) != FALSE, "attach isolated bridge");
    HWND bridge = FindWindowExW(HWND_MESSAGE, nullptr, L"OpenFenestra.NativeShellBridge.v1", nullptr);
    Check(bridge != nullptr, "find message-only protocol window");
    const UINT status = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.Status.v1");
    const UINT hiddenCount = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.HiddenCount.v1");
    Check(SendMessageW(bridge, status, 0, 0) == 0x4F46010f, "draw selection input and OLE capabilities");
    Check(Packet(bridge, owner, 4, {std::wstring(64, L'0')}) == FALSE, "build probe rejects mismatched cached module");
    Check(Packet(bridge, owner, 4, {L"not-a-sha256"}) == FALSE, "build probe rejects malformed build identity");
    wchar_t loadedPath[32768]{};
    GetModuleFileNameW(module, loadedPath, 32768);
    std::wstring parentName(loadedPath);
    parentName.resize(parentName.find_last_of(L"\\/"));
    parentName.erase(0, parentName.find_last_of(L"\\/") + 1);
    if (parentName.size() == 64 && parentName.find_first_not_of(L"0123456789abcdefABCDEF") == std::wstring::npos) {
        Check(Packet(bridge, owner, 4, {parentName}) == TRUE, "build probe matches the actual cached module");
        CharUpperBuffW(parentName.data(), static_cast<DWORD>(parentName.size()));
        Check(Packet(bridge, owner, 4, {parentName}) == TRUE, "build probe comparison ignores hexadecimal letter case");
    }
    const std::wstring token = L"isolated-test-session-0001";
    std::vector<std::wstring> fields{std::to_wstring(GetCurrentProcessId()),
        std::to_wstring(reinterpret_cast<UINT_PTR>(owner)), token, identities[0], identities[2]};
    Check(Packet(bridge, owner, 1, fields) == TRUE, "assign snapshot");
    Pump(20);
    Check(SendMessageW(bridge, hiddenCount, 0, 0) == 2, "actual hidden item count");
    const UINT diagnostics = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.Diagnostics.v1");
    Check(SendMessageW(bridge, diagnostics, 0, 0) == 1, "asynchronous identity rebuild reports success");
    Check(Packet(bridge, owner, 2, {L"incorrect-token-000000"}) == FALSE, "reject wrong session heartbeat");
    Check(Packet(bridge, owner, 2, {token}) == TRUE, "accept heartbeat");
    NMLVCUSTOMDRAW draw{}; draw.nmcd.hdr.hwndFrom = list; draw.nmcd.hdr.code = NM_CUSTOMDRAW;
    draw.nmcd.dwDrawStage = CDDS_ITEMPREPAINT;
    draw.nmcd.dwItemSpec = 0;
    Check(SendMessageW(parent, WM_NOTIFY, 0, reinterpret_cast<LPARAM>(&draw)) == CDRF_SKIPDEFAULT, "suppress assigned item drawing");
    draw.nmcd.dwItemSpec = 1;
    Check(SendMessageW(parent, WM_NOTIFY, 0, reinterpret_cast<LPARAM>(&draw)) != CDRF_SKIPDEFAULT, "preserve unassigned item drawing");
    LVITEMW select{}; select.stateMask = LVIS_SELECTED | LVIS_FOCUSED; select.state = LVIS_SELECTED;
    SendMessageW(list, LVM_SETITEMSTATE, static_cast<WPARAM>(-1), reinterpret_cast<LPARAM>(&select));
    Check(!(ListView_GetItemState(list, 0, LVIS_SELECTED) & LVIS_SELECTED), "bulk selection excludes first hidden item");
    Check((ListView_GetItemState(list, 1, LVIS_SELECTED) & LVIS_SELECTED) != 0, "bulk selection includes visible item");
    Check(!(ListView_GetItemState(list, 2, LVIS_SELECTED) & LVIS_SELECTED), "bulk selection excludes virtual hidden item");
    // Simulate a control-internal selection update, bypassing our list subclass.
    CallWindowProcW(originalProc, list, LVM_SETITEMSTATE, static_cast<WPARAM>(-1), reinterpret_cast<LPARAM>(&select));
    Check(!(ListView_GetItemState(list, 0, LVIS_SELECTED) & LVIS_SELECTED), "notification cleanup protects control-internal selection");
    Check(!(ListView_GetItemState(list, 2, LVIS_SELECTED) & LVIS_SELECTED), "notification cleanup protects owner-data selection");
    LVHITTESTINFO hit{}; hit.pt = ItemPoint(list, 0);
    Check(SendMessageW(list, LVM_HITTEST, 0, reinterpret_cast<LPARAM>(&hit)) == -1 && hit.iItem == -1,
        "hidden item removed from hit testing");
    hit = {}; hit.pt = ItemPoint(list, 1);
    Check(SendMessageW(list, LVM_HITTEST, 0, reinterpret_cast<LPARAM>(&hit)) == 1, "visible item remains hittable");
    auto* target = static_cast<IDropTarget*>(GetPropW(list, L"OleDropTargetInterface"));
    Check(target && target != original, "OLE wrapper installed");
    auto* data = new MockData;
    DWORD effect = DROPEFFECT_COPY;
    const auto hiddenPoint = ScreenItemPoint(list, 0);
    const auto visiblePoint = ScreenItemPoint(list, 1);
    Check(SUCCEEDED(target->DragEnter(data, 0, hiddenPoint, &effect)) && effect == DROPEFFECT_NONE && original->enters == 0,
        "hidden icon rejects native OLE entry");
    effect = DROPEFFECT_COPY;
    Check(SUCCEEDED(target->Drop(data, 0, hiddenPoint, &effect)) && effect == DROPEFFECT_NONE && original->drops == 0,
        "hidden icon never receives a native OLE drop");
    effect = DROPEFFECT_COPY;
    target->DragEnter(data, 0, visiblePoint, &effect);
    Check(effect == DROPEFFECT_COPY && original->enters == 1, "visible native OLE entry forwarded");
    effect = DROPEFFECT_COPY;
    target->DragOver(0, hiddenPoint, &effect);
    Check(effect == DROPEFFECT_NONE && original->leaves == 1, "moving onto hidden icon clears original OLE target");
    effect = DROPEFFECT_COPY;
    target->DragOver(0, visiblePoint, &effect);
    Check(effect == DROPEFFECT_COPY && original->enters == 2, "moving off hidden icon reenters original OLE target");
    target->Drop(data, 0, visiblePoint, &effect);
    Check(original->drops == 1, "visible native OLE drop forwarded");
    data->Release();
    // Removing metadata restores original drawing immediately, without positions/files.
    fields.resize(3);
    Check(Packet(bridge, owner, 1, fields) == TRUE && SendMessageW(bridge, hiddenCount, 0, 0) == 0,
        "empty snapshot releases every assigned item");
    fields.push_back(identities[0]);
    Check(Packet(bridge, owner, 1, fields) == TRUE, "reassign after release");
    Pump(20);
    if (ownerData) {
        // No WM_COPYDATA detach: a disappearing controller must restore controls.
        DestroyWindow(owner); owner = nullptr; Pump(650);
        Check(!IsWindow(bridge), "controller disappearance triggers fail-open cleanup");
    } else {
        Check(Packet(bridge, owner, 3, {token}) == TRUE && !IsWindow(bridge), "explicit detach destroys protocol endpoint");
    }
    Check(GetPropW(list, L"OleDropTargetInterface") == original, "detach restores original OLE target");
    hit = {}; hit.pt = ItemPoint(list, 0);
    Check(SendMessageW(list, LVM_HITTEST, 0, reinterpret_cast<LPARAM>(&hit)) == 0, "detach restores native hit testing");
    // Reattach in the same process, then simulate another shell component
    // replacing our OLE target. Protection loss must release suppression and
    // leave that new component's registration untouched.
    Check(attach(parent, list, names.data(), static_cast<DWORD>(names.size())) != FALSE, "reattach after clean detach");
    bridge = FindWindowExW(HWND_MESSAGE, nullptr, L"OpenFenestra.NativeShellBridge.v1", nullptr);
    if (!owner) owner = CreateWindowExW(0, L"OpenFenestra.IsolatedTestHost", L"New controller", 0,
        0, 0, 0, 0, HWND_MESSAGE, nullptr, instance, nullptr);
    fields[1] = std::to_wstring(reinterpret_cast<UINT_PTR>(owner));
    Check(Packet(bridge, owner, 1, fields) == TRUE, "reattached session accepts assigned metadata");
    Pump(20);
    Check(SendMessageW(bridge, hiddenCount, 0, 0) == 1, "reattached session suppresses assigned item asynchronously");
    auto* foreign = new MockDrop;
    Check(SUCCEEDED(RevokeDragDrop(list)) && SUCCEEDED(RegisterDragDrop(list, foreign)), "simulate foreign OLE registration");
    Pump(650);
    Check(!IsWindow(bridge), "OLE protection loss stops suppression");
    Check(GetPropW(list, L"OleDropTargetInterface") == foreign, "foreign OLE registration preserved on fail-open");
    draw.nmcd.dwItemSpec = 0;
    Check(SendMessageW(parent, WM_NOTIFY, 0, reinterpret_cast<LPARAM>(&draw)) != CDRF_SKIPDEFAULT,
        "OLE protection loss restores item drawing");
    hit = {}; hit.pt = ItemPoint(list, 0);
    Check(SendMessageW(list, LVM_HITTEST, 0, reinterpret_cast<LPARAM>(&hit)) == 0,
        "OLE protection loss restores native input");
    RevokeDragDrop(list); foreign->Release();
    original->Release();
    if (owner) DestroyWindow(owner);
    DestroyWindow(parent);
    std::printf("PASS %s ListView host\n", ownerData ? "owner-data" : "standard");
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) { std::fprintf(stderr, "Usage: NativeShellBridge.SelfTest.exe <DLL path>\n"); return 2; }
    Check(SUCCEEDED(OleInitialize(nullptr)), "initialize isolated OLE apartment");
    INITCOMMONCONTROLSEX controls{}; controls.dwSize = sizeof(controls); controls.dwICC = ICC_LISTVIEW_CLASSES;
    Check(InitCommonControlsEx(&controls) != FALSE, "initialize common controls");
    WNDCLASSEXW cls{}; cls.cbSize = sizeof(cls); cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpfnWndProc = HostProc; cls.lpszClassName = L"OpenFenestra.IsolatedTestHost";
    Check(RegisterClassExW(&cls) != 0, "register isolated host class");
    HMODULE module = LoadLibraryW(argv[1]); Check(module != nullptr, "load bridge in isolated host only");
    Run(module, false); Run(module, true);
    OleUninitialize();
    std::printf("PASS %d native assertions; Explorer was never located, hooked or modified.\n", assertions);
    return 0;
}
