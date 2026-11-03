// SPDX-License-Identifier: MIT
// Copyright (c) 2026 OpenFenestra contributors.
// Original implementation of the documented Win32 custom-draw/subclass contracts.
// No source from GPL desktop organizers or hook frameworks is incorporated.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <shlobj.h>
#include <exdisp.h>
#include <shlwapi.h>
#include <servprov.h>
#include <algorithm>
#include <cstdint>
#include <cwchar>
#include <limits>
#include <set>
#include <string>
#include <vector>

namespace {
constexpr wchar_t ClassName[] = L"OpenFenestra.NativeShellBridge.v1";
constexpr UINT RebuildMessage = WM_APP + 41;
constexpr UINT DetachMessage = WM_APP + 42;
constexpr UINT_PTR SubclassId = 0x4f465301;
constexpr UINT_PTR WatchdogId = 1;
constexpr ULONGLONG LeaseMilliseconds = 8000;
constexpr DWORD MaximumPayloadBytes = 2 * 1024 * 1024;
constexpr int MaximumItems = 20000;
HINSTANCE moduleInstance = nullptr;
struct BootstrapTrace {
    HWND recipient = nullptr;
    UINT reply = 0;
    WORD nonce = 0;
    void Report(WORD stage, LPARAM value = 0) const {
        if (recipient && reply) PostMessageW(recipient, reply,
            (static_cast<WPARAM>(nonce) << 16) | stage, value);
    }
};
DWORD BootstrapFailure() {
    const DWORD error = GetLastError();
    return error ? error : ERROR_GEN_FAILURE;
}

template<class T> struct Com {
    T* value = nullptr;
    ~Com() { reset(); }
    void reset() { if (value) { value->Release(); value = nullptr; } }
    T** out() { reset(); return &value; }
    T* operator->() const { return value; }
    explicit operator bool() const { return value != nullptr; }
};
struct OrdinalLess {
    bool operator()(const std::wstring& a, const std::wstring& b) const {
        return CompareStringOrdinal(a.c_str(), static_cast<int>(a.size()),
            b.c_str(), static_cast<int>(b.size()), TRUE) == CSTR_LESS_THAN;
    }
};
class DropGate;
struct State {
    HWND control = nullptr, view = nullptr, list = nullptr, owner = nullptr;
    HANDLE ownerProcess = nullptr;
    DWORD ownerPid = 0, threadId = 0;
    bool active = false, stopping = false, rebuilding = false, queued = false;
    bool clearing = false, rawQuery = false, testHost = false;
    ULONGLONG heartbeat = 0, lastRebuild = 0;
    UINT statusMessage = 0, countMessage = 0, diagnosticsMessage = 0;
    HRESULT rebuildStatus = S_FALSE; // protocol 0=pending, 1=success, otherwise HRESULT.
    std::wstring token, buildId;
    std::set<std::wstring, OrdinalLess> assigned;
    std::vector<unsigned char> hidden;
    std::vector<std::wstring> testNames;
    Com<IFolderView> folderView;
    Com<IShellFolder> folder;
    HWND dropWindow = nullptr;
    DropGate* drop = nullptr;
};
// The module is pinned. Do not run COM smart-pointer destructors from CRT DLL
// teardown under the loader lock when Explorer terminates. Stop releases these
// resources on the desktop UI thread; the OS reclaims this small process-lifetime
// allocation on process termination.
State& state = *new State;

LRESULT CALLBACK ListSubclass(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
LRESULT CALLBACK ViewSubclass(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
LRESULT CALLBACK ControlProc(HWND, UINT, WPARAM, LPARAM);
void Stop();
void ClearHiddenSelection();

bool Hidden(int index) {
    return state.active && index >= 0 && static_cast<size_t>(index) < state.hidden.size()
        && state.hidden[static_cast<size_t>(index)] != 0;
}
int HiddenCount() {
    return state.active ? static_cast<int>(std::count(state.hidden.begin(), state.hidden.end(), 1)) : 0;
}
struct RawQueryScope {
    bool previous = state.rawQuery;
    RawQueryScope() { state.rawQuery = true; }
    ~RawQueryScope() { state.rawQuery = previous; }
};
bool HitHidden(POINT screenPoint) {
    if (!state.active || !state.list || state.hidden.empty()) return false;
    if (!ScreenToClient(state.list, &screenPoint)) return false;
    LVHITTESTINFO hit{};
    hit.pt = screenPoint;
    RawQueryScope raw;
    const auto index = static_cast<int>(SendMessageW(state.list, LVM_HITTEST, 0,
        reinterpret_cast<LPARAM>(&hit)));
    return Hidden(index);
}
bool MessageHitsHidden(HWND window, LPARAM coordinates, bool screenCoordinates = false) {
    POINT point{GET_X_LPARAM(coordinates), GET_Y_LPARAM(coordinates)};
    if (!screenCoordinates && !ClientToScreen(window, &point)) return false;
    return HitHidden(point);
}
void ScheduleRebuild() {
    // A stale index must never suppress a different file after an Explorer reorder.
    state.hidden.clear();
    state.rebuildStatus = S_OK; // zero represents queued/not yet completed.
    if (!state.queued && state.control) {
        state.queued = true;
        PostMessageW(state.control, RebuildMessage, 0, 0);
    }
}
HRESULT AcquireFolderView() {
    if (state.folderView && state.folder) return S_OK;
    Com<IShellWindows> windows;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER,
        IID_PPV_ARGS(windows.out()));
    if (FAILED(hr)) return hr;
    VARIANT location{}, root{};
    location.vt = VT_I4;
    location.lVal = CSIDL_DESKTOP;
    long ignoredHandle = 0;
    Com<IDispatch> dispatch;
    hr = windows->FindWindowSW(&location, &root, SWC_DESKTOP, &ignoredHandle,
        SWFO_NEEDDISPATCH, dispatch.out());
    if (hr != S_OK || !dispatch) return FAILED(hr) ? hr : HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    Com<IServiceProvider> service;
    Com<IShellBrowser> browser;
    Com<IShellView> view;
    hr = dispatch->QueryInterface(IID_PPV_ARGS(service.out()));
    if (FAILED(hr)) return hr;
    hr = service->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(browser.out()));
    if (FAILED(hr)) return hr;
    hr = browser->QueryActiveShellView(view.out());
    if (FAILED(hr)) return hr;
    HWND actualView = nullptr;
    hr = view->GetWindow(&actualView);
    if (FAILED(hr)) return hr;
    if (actualView != state.view) return E_UNEXPECTED;
    hr = view->QueryInterface(IID_PPV_ARGS(state.folderView.out()));
    if (SUCCEEDED(hr)) hr = state.folderView->GetFolder(IID_PPV_ARGS(state.folder.out()));
    if (FAILED(hr)) {
        state.folderView.reset();
        state.folder.reset();
        return hr;
    }
    return S_OK;
}
HRESULT ReadIdentity(int index, std::wstring& result) {
    if (state.testHost) {
        if (index < 0 || static_cast<size_t>(index) >= state.testNames.size()) return E_INVALIDARG;
        result = state.testNames[static_cast<size_t>(index)];
        return S_OK;
    }
    PITEMID_CHILD child = nullptr;
    const HRESULT itemResult = state.folderView->Item(index, &child);
    if (FAILED(itemResult) || !child) return FAILED(itemResult) ? itemResult : E_FAIL;
    STRRET name{};
    wchar_t* text = nullptr;
    const HRESULT nameResult = state.folder->GetDisplayNameOf(child, SHGDN_FORPARSING, &name);
    HRESULT resultCode = nameResult;
    if (SUCCEEDED(nameResult)) resultCode = StrRetToStrW(&name, child, &text);
    CoTaskMemFree(child);
    if (FAILED(resultCode) || !text) return FAILED(resultCode) ? resultCode : E_FAIL;
    try { result = text; } catch (...) { CoTaskMemFree(text); throw; }
    CoTaskMemFree(text);
    return result.empty() ? E_FAIL : S_OK;
}
bool Rebuild() {
    if (state.rebuilding || !state.active || !state.list) return false;
    if (InSendMessageEx(nullptr) & (ISMEX_SEND | ISMEX_NOTIFY | ISMEX_CALLBACK)) {
        ScheduleRebuild();
        return false;
    }
    state.rebuilding = true;
    state.rebuildStatus = E_FAIL;
    bool success = false;
    bool changed = false;
    try {
        const int count = static_cast<int>(SendMessageW(state.list, LVM_GETITEMCOUNT, 0, 0));
        const HRESULT acquireResult = state.testHost ? S_OK : AcquireFolderView();
        if (FAILED(acquireResult)) state.rebuildStatus = acquireResult;
        if (count >= 0 && count <= MaximumItems && SUCCEEDED(acquireResult)) {
            int folderCount = count;
            const HRESULT countResult = state.testHost ? S_OK : state.folderView->ItemCount(SVGIO_ALLVIEW, &folderCount);
            if (FAILED(countResult)) state.rebuildStatus = countResult;
            else if (folderCount != count) state.rebuildStatus = HRESULT_FROM_WIN32(ERROR_RETRY);
            if (SUCCEEDED(countResult) && folderCount == count) {
                std::vector<unsigned char> replacement(static_cast<size_t>(count), 0);
                bool complete = true;
                for (int i = 0; i < count; ++i) {
                    if (!state.active) { complete = false; break; }
                    std::wstring identity;
                    const HRESULT identityResult = ReadIdentity(i, identity);
                    if (FAILED(identityResult)) { state.rebuildStatus = identityResult; complete = false; break; }
                    replacement[static_cast<size_t>(i)] = state.assigned.count(identity) ? 1 : 0;
                }
                if (complete) { changed = replacement != state.hidden; state.hidden.swap(replacement); success = true; state.rebuildStatus = S_FALSE; }
            }
        }
    } catch (const std::bad_alloc&) { state.hidden.clear(); state.rebuildStatus = E_OUTOFMEMORY; }
    catch (...) { state.hidden.clear(); state.rebuildStatus = E_UNEXPECTED; }
    if (!success) {
        changed = changed || !state.hidden.empty();
        state.hidden.clear();
        state.folder.reset();
        state.folderView.reset();
    }
    state.lastRebuild = GetTickCount64();
    state.rebuilding = false;
    ClearHiddenSelection();
    if (changed && state.list) RedrawWindow(state.list, nullptr, nullptr, RDW_INVALIDATE | RDW_ERASE);
    return success;
}
void ClearHiddenSelection() {
    if (!state.active || state.clearing || !state.list) return;
    state.clearing = true;
    LVITEMW item{};
    item.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
    // Most mouse messages arrive with no selection to repair. Enumerate actual
    // selections, not every hidden icon, while bypassing our own filtered query.
    int previous = -1;
    for (size_t attempt = 0; attempt < state.hidden.size(); ++attempt) {
        int selected;
        { RawQueryScope raw; selected = static_cast<int>(SendMessageW(state.list,
            LVM_GETNEXTITEM, static_cast<WPARAM>(previous), LVNI_SELECTED)); }
        if (selected <= previous || selected < 0) break;
        previous = selected;
        if (Hidden(selected)) SendMessageW(state.list, LVM_SETITEMSTATE,
            static_cast<WPARAM>(selected), reinterpret_cast<LPARAM>(&item));
    }
    int focused;
    { RawQueryScope raw; focused = static_cast<int>(SendMessageW(state.list,
        LVM_GETNEXTITEM, static_cast<WPARAM>(-1), LVNI_FOCUSED)); }
    if (Hidden(focused)) SendMessageW(state.list, LVM_SETITEMSTATE,
        static_cast<WPARAM>(focused), reinterpret_cast<LPARAM>(&item));
    state.clearing = false;
}

// Explorer's OLE implementation can hit-test internally without LVM_HITTEST.
// The window property used to recover its original IDropTarget is an Explorer/OLE
// implementation dependency. No private object layout or vtable is patched.
class DropGate final : public IDropTarget {
    LONG references = 1;
    IDropTarget* original;
    IDataObject* data = nullptr;
    bool entered = false;
    bool enabled = true;
    void ResetData() { if (data) { data->Release(); data = nullptr; } }
    bool Block(POINTL p) const { return enabled && HitHidden({p.x, p.y}); }
    void LeaveOriginal() { if (entered) { entered = false; original->DragLeave(); } }
public:
    explicit DropGate(IDropTarget* target) : original(target) { original->AddRef(); }
    ~DropGate() { ResetData(); original->Release(); }
    IDropTarget* Original() const { return original; }
    void Disable() { enabled = false; LeaveOriginal(); ResetData(); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (id != IID_IUnknown && id != IID_IDropTarget) return E_NOINTERFACE;
        *value = static_cast<IDropTarget*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references)); }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto left = static_cast<ULONG>(InterlockedDecrement(&references));
        if (!left) delete this;
        return left;
    }
    HRESULT STDMETHODCALLTYPE DragEnter(IDataObject* incoming, DWORD keys, POINTL p, DWORD* effect) override {
        if (!effect || !incoming) return E_INVALIDARG;
        LeaveOriginal(); ResetData(); data = incoming; data->AddRef();
        if (Block(p)) { *effect = DROPEFFECT_NONE; return S_OK; }
        const HRESULT result = original->DragEnter(incoming, keys, p, effect);
        entered = SUCCEEDED(result); return result;
    }
    HRESULT STDMETHODCALLTYPE DragOver(DWORD keys, POINTL p, DWORD* effect) override {
        if (!effect) return E_POINTER;
        if (Block(p)) { LeaveOriginal(); *effect = DROPEFFECT_NONE; return S_OK; }
        if (!entered && data) {
            const HRESULT result = original->DragEnter(data, keys, p, effect);
            if (FAILED(result)) return result;
            entered = true;
        }
        return entered ? original->DragOver(keys, p, effect) : E_UNEXPECTED;
    }
    HRESULT STDMETHODCALLTYPE DragLeave() override {
        HRESULT result = S_OK;
        if (entered) { entered = false; result = original->DragLeave(); }
        ResetData(); return result;
    }
    HRESULT STDMETHODCALLTYPE Drop(IDataObject* incoming, DWORD keys, POINTL p, DWORD* effect) override {
        if (!effect) return E_POINTER;
        if (Block(p)) { LeaveOriginal(); ResetData(); *effect = DROPEFFECT_NONE; return S_OK; }
        if (!entered) {
            const HRESULT result = original->DragEnter(incoming, keys, p, effect);
            if (FAILED(result)) { ResetData(); return result; }
        }
        entered = false;
        const HRESULT result = original->Drop(incoming, keys, p, effect);
        ResetData(); return result;
    }
};
void RemoveDropGate() {
    if (!state.drop) return;
    auto* gate = state.drop;
    gate->Disable();
    if (IsWindow(state.dropWindow) && GetPropW(state.dropWindow, L"OleDropTargetInterface") == gate) {
        if (SUCCEEDED(RevokeDragDrop(state.dropWindow))) {
            // If the original target cannot be restored, retain a disabled,
            // transparent forwarder rather than leave a dead desktop drop zone.
            if (FAILED(RegisterDragDrop(state.dropWindow, gate->Original())))
                RegisterDragDrop(state.dropWindow, gate);
        }
    }
    state.drop = nullptr; state.dropWindow = nullptr;
    gate->Release();
}
bool EnsureDropGate() {
    if (state.drop) {
        if (GetPropW(state.dropWindow, L"OleDropTargetInterface") == state.drop) return true;
        // Do not fight another application's new registration. Fail open and
        // report the missing capability; the controller must stop integration.
        RemoveDropGate();
        return false;
    }
    const HWND candidates[]{state.list, state.view, GetAncestor(state.view, GA_ROOT)};
    for (const HWND candidate : candidates) {
        if (!candidate || GetWindowThreadProcessId(candidate, nullptr) != GetCurrentThreadId()) continue;
        auto* original = static_cast<IDropTarget*>(GetPropW(candidate, L"OleDropTargetInterface"));
        if (!original) continue;
        auto* gate = new DropGate(original);
        if (FAILED(RevokeDragDrop(candidate))) { gate->Release(); continue; }
        if (SUCCEEDED(RegisterDragDrop(candidate, gate))) {
            state.drop = gate; state.dropWindow = candidate; return true;
        }
        RegisterDragDrop(candidate, original);
        gate->Release();
    }
    return false;
}

LRESULT ListImpl(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (state.stopping || !state.active) return DefSubclassProc(window, message, wParam, lParam);
    if ((message == LVM_HITTEST || message == LVM_SUBITEMHITTEST) && !state.rawQuery) {
        const LRESULT hit = DefSubclassProc(window, message, wParam, lParam);
        if (Hidden(static_cast<int>(hit)) && lParam) {
            auto* value = reinterpret_cast<LVHITTESTINFO*>(lParam);
            value->iItem = -1; value->iSubItem = -1; value->flags = LVHT_NOWHERE;
            return -1;
        }
        return hit;
    }
    if (message == LVM_GETNEXTITEM && !state.rawQuery) {
        LRESULT found = DefSubclassProc(window, message, wParam, lParam);
        const size_t bound = state.hidden.size();
        for (size_t i = 0; Hidden(static_cast<int>(found)) && i < bound; ++i)
            found = DefSubclassProc(window, message, static_cast<WPARAM>(found), lParam);
        return Hidden(static_cast<int>(found)) ? -1 : found;
    }
    if (message == LVM_SETITEMSTATE && lParam && !state.clearing) {
        auto item = *reinterpret_cast<LVITEMW*>(lParam);
        const UINT selection = LVIS_SELECTED | LVIS_FOCUSED;
        if (item.state & item.stateMask & selection) {
            if (static_cast<int>(wParam) == -1) {
                const int count = static_cast<int>(SendMessageW(window, LVM_GETITEMCOUNT, 0, 0));
                for (int i = 0; i < count; ++i) {
                    auto filtered = item;
                    if (Hidden(i)) filtered.state &= ~selection;
                    DefSubclassProc(window, message, static_cast<WPARAM>(i), reinterpret_cast<LPARAM>(&filtered));
                }
                return TRUE;
            }
            if (Hidden(static_cast<int>(wParam))) {
                item.state &= ~selection;
                return DefSubclassProc(window, message, wParam, reinterpret_cast<LPARAM>(&item));
            }
        }
    }
    if ((message == LVM_SETITEMW || message == LVM_SETITEMA) && lParam) {
        auto item = *reinterpret_cast<LVITEMW*>(lParam); // A/W layout is identical for the state fields.
        if ((item.mask & LVIF_STATE) && Hidden(item.iItem)) {
            item.state &= ~(LVIS_SELECTED | LVIS_FOCUSED);
            return DefSubclassProc(window, message, wParam, reinterpret_cast<LPARAM>(&item));
        }
    }
    const bool click = message == WM_LBUTTONDOWN || message == WM_LBUTTONDBLCLK ||
        message == WM_RBUTTONDOWN || message == WM_RBUTTONDBLCLK || message == WM_MBUTTONDOWN;
    if ((click || message == WM_MOUSEMOVE) && MessageHitsHidden(window, lParam)) {
        ClearHiddenSelection();
        return 0;
    }
    if (message == WM_CONTEXTMENU && lParam != -1 && MessageHitsHidden(window, lParam, true)) return 0;
    const bool key = message == WM_KEYDOWN || message == WM_SYSKEYDOWN ||
        message == WM_CHAR || message == WM_SYSCHAR;
    const bool mouseEnd = message == WM_LBUTTONUP || message == WM_RBUTTONUP;
    if (key || click || message == WM_CONTEXTMENU) ClearHiddenSelection();
    const LRESULT result = DefSubclassProc(window, message, wParam, lParam);
    if (key || mouseEnd) {
        if (message == WM_KEYDOWN && (wParam == VK_LEFT || wParam == VK_RIGHT || wParam == VK_UP || wParam == VK_DOWN)) {
            for (size_t attempt = 0; attempt < state.hidden.size(); ++attempt) {
                int focused;
                { RawQueryScope raw; focused = static_cast<int>(SendMessageW(window, LVM_GETNEXTITEM,
                    static_cast<WPARAM>(-1), LVNI_FOCUSED)); }
                if (!Hidden(focused)) break;
                DefSubclassProc(window, message, wParam, lParam);
            }
        }
        ClearHiddenSelection();
    }
    switch (message) {
    case LVM_SETITEMCOUNT: case LVM_INSERTITEMW: case LVM_INSERTITEMA:
    case LVM_DELETEITEM: case LVM_DELETEALLITEMS: case LVM_SORTITEMS: case LVM_SORTITEMSEX:
    case LVM_SETITEMTEXTW: case LVM_SETITEMTEXTA:
        ScheduleRebuild(); break;
    default: break;
    }
    return result;
}
LRESULT CALLBACK ListSubclass(HWND window, UINT message, WPARAM wParam, LPARAM lParam, UINT_PTR, DWORD_PTR) {
    if (message == WM_NCDESTROY) {
        RemoveWindowSubclass(window, ListSubclass, SubclassId);
        state.list = nullptr;
        if (state.control) PostMessageW(state.control, DetachMessage, 0, 0);
        return DefSubclassProc(window, message, wParam, lParam);
    }
    try { return ListImpl(window, message, wParam, lParam); }
    catch (...) { state.hidden.clear(); if (state.control) PostMessageW(state.control, DetachMessage, 0, 0); return 0; }
}
LRESULT CALLBACK ViewSubclass(HWND window, UINT message, WPARAM wParam, LPARAM lParam, UINT_PTR, DWORD_PTR) {
    if (message == WM_NCDESTROY) {
        RemoveWindowSubclass(window, ViewSubclass, SubclassId);
        state.view = nullptr;
        if (state.control) PostMessageW(state.control, DetachMessage, 0, 0);
        return DefSubclassProc(window, message, wParam, lParam);
    }
    try {
        if (state.active && !state.stopping) {
            if (message == WM_COMMAND || message == WM_CONTEXTMENU || message == WM_KEYDOWN || message == WM_CHAR)
                ClearHiddenSelection();
            if (message == WM_NOTIFY && lParam) {
                auto* header = reinterpret_cast<NMHDR*>(lParam);
                if (header->hwndFrom == state.list) {
                    if (header->code == NM_CUSTOMDRAW) {
                        auto* draw = reinterpret_cast<NMLVCUSTOMDRAW*>(lParam);
                        if (draw->nmcd.dwDrawStage == CDDS_PREPAINT)
                            return DefSubclassProc(window, message, wParam, lParam) | CDRF_NOTIFYITEMDRAW;
                        if ((draw->nmcd.dwDrawStage & CDDS_ITEMPREPAINT) == CDDS_ITEMPREPAINT &&
                            Hidden(static_cast<int>(draw->nmcd.dwItemSpec))) return CDRF_SKIPDEFAULT;
                    }
                    if (header->code == LVN_ITEMCHANGING) {
                        const auto* change = reinterpret_cast<NMLISTVIEW*>(lParam);
                        if (Hidden(change->iItem) && (change->uNewState & (LVIS_SELECTED | LVIS_FOCUSED))) return TRUE;
                    }
                    if (header->code == LVN_HOTTRACK) {
                        if (Hidden(reinterpret_cast<NMLISTVIEW*>(lParam)->iItem)) return TRUE;
                    }
                    if (header->code == LVN_ITEMCHANGED || header->code == LVN_ODSTATECHANGED)
                        ClearHiddenSelection();
                }
            }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    } catch (...) { state.hidden.clear(); if (state.control) PostMessageW(state.control, DetachMessage, 0, 0); return 0; }
}

bool ParseStrings(const COPYDATASTRUCT* packet, std::vector<std::wstring>& strings) {
    if (!packet || !packet->lpData || packet->cbData < 4 || packet->cbData > MaximumPayloadBytes ||
        packet->cbData % sizeof(wchar_t)) return false;
    const auto* data = static_cast<const wchar_t*>(packet->lpData);
    const size_t count = packet->cbData / sizeof(wchar_t);
    if (data[count - 1] || data[count - 2]) return false;
    size_t position = 0;
    while (position < count && data[position]) {
        const wchar_t* end = std::find(data + position, data + count, L'\0');
        if (end == data + count || strings.size() > static_cast<size_t>(MaximumItems + 3)) return false;
        strings.emplace_back(data + position, end);
        position = static_cast<size_t>(end - data) + 1;
    }
    return position + 1 == count; // exactly one final terminator; reject trailing data.
}
bool UnsignedNumber(const std::wstring& text, unsigned long long& value) {
    if (text.empty() || text.size() > 20 || text.find_first_not_of(L"0123456789") != std::wstring::npos) return false;
    wchar_t* end = nullptr;
    value = _wcstoui64(text.c_str(), &end, 10);
    return end && !*end && value != std::numeric_limits<unsigned long long>::max();
}
bool Authorized(HWND sender, const std::vector<std::wstring>& fields) {
    DWORD pid = 0;
    return state.owner && sender == state.owner && GetWindowThreadProcessId(sender, &pid) &&
        pid == state.ownerPid && state.ownerProcess && WaitForSingleObject(state.ownerProcess, 0) == WAIT_TIMEOUT &&
        fields.size() == 1 && fields[0] == state.token;
}
bool IsBuildId(const std::wstring& value) {
    return value.size() == 64 && value.find_first_not_of(L"0123456789abcdefABCDEF") == std::wstring::npos;
}
std::wstring ModuleBuildId() {
    std::vector<wchar_t> path(32768);
    const DWORD length = GetModuleFileNameW(moduleInstance, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    std::wstring name(path.data(), length);
    auto boundary = name.find_last_of(L"\\/");
    if (boundary == std::wstring::npos) return {};
    name.resize(boundary);
    boundary = name.find_last_of(L"\\/");
    if (boundary == std::wstring::npos) return {};
    name.erase(0, boundary + 1);
    return IsBuildId(name) ? name : std::wstring{};
}
bool Snapshot(HWND sender, const std::vector<std::wstring>& fields) {
    if (fields.size() < 3 || fields[2].size() < 16 || fields[2].size() > 256) return false;
    unsigned long long pidValue = 0, hwndValue = 0;
    DWORD actualPid = 0;
    if (!UnsignedNumber(fields[0], pidValue) || pidValue > MAXDWORD ||
        !UnsignedNumber(fields[1], hwndValue) || reinterpret_cast<UINT_PTR>(sender) != hwndValue ||
        !GetWindowThreadProcessId(sender, &actualPid) || actualPid != static_cast<DWORD>(pidValue)) return false;
    if (state.owner && (state.owner != sender || state.ownerPid != actualPid || state.token != fields[2])) return false;
    if (!state.drop || GetPropW(state.dropWindow, L"OleDropTargetInterface") != state.drop) return false;
    HANDLE process = state.ownerProcess;
    if (!process) process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, actualPid);
    if (!process || WaitForSingleObject(process, 0) != WAIT_TIMEOUT) { if (process && process != state.ownerProcess) CloseHandle(process); return false; }
    std::set<std::wstring, OrdinalLess> replacement;
    try {
        for (size_t i = 3; i < fields.size(); ++i) {
            if (fields[i].size() > 32767) { if (process != state.ownerProcess) CloseHandle(process); return false; }
            replacement.insert(fields[i]);
        }
    } catch (...) { if (process != state.ownerProcess) CloseHandle(process); throw; }
    state.ownerProcess = process; state.owner = sender; state.ownerPid = actualPid;
    state.token = fields[2]; state.heartbeat = GetTickCount64();
    state.assigned.swap(replacement); state.active = true;
    // WM_COPYDATA is a synchronous cross-process input callback. Shell COM may
    // reject outbound calls here with RPC_E_CANTCALLOUT_ININPUTSYNCCALL. Only
    // publish metadata and queue work; resolution runs after this callback exits.
    ScheduleRebuild();
    RedrawWindow(state.list, nullptr, nullptr, RDW_INVALIDATE | RDW_ERASE);
    return true;
}
void Stop() {
    if (state.stopping) return;
    // Shell COM can pump messages while resolving an identity. Release no COM
    // pointer from under an in-flight call: immediately stop suppression, then
    // finish teardown after the enumeration unwinds on this same UI thread.
    if (state.rebuilding) {
        state.active = false; state.hidden.clear();
        if (state.control) PostMessageW(state.control, DetachMessage, 0, 0);
        return;
    }
    state.stopping = true; state.active = false; state.hidden.clear(); state.assigned.clear();
    state.rebuildStatus = S_OK;
    RemoveDropGate();
    if (state.list) RemoveWindowSubclass(state.list, ListSubclass, SubclassId);
    if (state.view) RemoveWindowSubclass(state.view, ViewSubclass, SubclassId);
    state.folder.reset(); state.folderView.reset();
    if (state.list) RedrawWindow(state.list, nullptr, nullptr, RDW_INVALIDATE | RDW_ERASE);
    if (state.ownerProcess) { CloseHandle(state.ownerProcess); state.ownerProcess = nullptr; }
    state.owner = nullptr; state.ownerPid = 0; state.token.clear(); state.testNames.clear();
    const HWND control = state.control;
    state.control = nullptr; state.list = nullptr; state.view = nullptr; state.threadId = 0;
    state.queued = false; state.testHost = false;
    if (control) { KillTimer(control, WatchdogId); DestroyWindow(control); }
    state.stopping = false;
}
LRESULT CALLBACK ControlProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    try {
        if (message == state.statusMessage && state.statusMessage)
            return 0x4F460100 | 7 | (state.drop && GetPropW(state.dropWindow, L"OleDropTargetInterface") == state.drop ? 8 : 0);
        if (message == state.countMessage && state.countMessage) return HiddenCount();
        if (message == state.diagnosticsMessage && state.diagnosticsMessage) return static_cast<LRESULT>(state.rebuildStatus);
        if (message == WM_COPYDATA) {
            auto* packet = reinterpret_cast<COPYDATASTRUCT*>(lParam);
            std::vector<std::wstring> fields;
            if (!ParseStrings(packet, fields)) return FALSE;
            if (packet->dwData == 4) return fields.size() == 1 && IsBuildId(fields[0]) &&
                !state.buildId.empty() && CompareStringOrdinal(fields[0].c_str(), -1,
                    state.buildId.c_str(), -1, TRUE) == CSTR_EQUAL ? TRUE : FALSE;
            if (packet->dwData == 1) return Snapshot(reinterpret_cast<HWND>(wParam), fields) ? TRUE : FALSE;
            if (!Authorized(reinterpret_cast<HWND>(wParam), fields)) return FALSE;
            if (packet->dwData == 2) { state.heartbeat = GetTickCount64(); return TRUE; }
            if (packet->dwData == 3) { Stop(); return TRUE; }
            return FALSE;
        }
        if (message == RebuildMessage) { state.queued = false; Rebuild(); return 0; }
        if (message == DetachMessage) { Stop(); return 0; }
        if (message == WM_TIMER && wParam == WatchdogId) {
            const auto now = GetTickCount64();
            if ((state.ownerProcess && (WaitForSingleObject(state.ownerProcess, 0) != WAIT_TIMEOUT ||
                !IsWindow(state.owner) || now - state.heartbeat > LeaseMilliseconds)) ||
                (!state.ownerProcess && now - state.heartbeat > LeaseMilliseconds) ||
                !IsWindow(state.list) || !IsWindow(state.view)) { Stop(); return 0; }
            if (!EnsureDropGate() && state.active) { Stop(); return 0; }
            if (state.active && now - state.lastRebuild >= 1000) Rebuild();
            return 0;
        }
    } catch (...) { PostMessageW(window, DetachMessage, 0, 0); return FALSE; }
    return DefWindowProcW(window, message, wParam, lParam);
}
bool Prepare(HWND view, HWND list, bool test, const BootstrapTrace* trace = nullptr) {
    if (trace) trace->Report(30);
    if (state.control || !view || !list || GetParent(list) != view ||
        GetWindowThreadProcessId(view, nullptr) != GetCurrentThreadId() ||
        GetWindowThreadProcessId(list, nullptr) != GetCurrentThreadId()) {
        if (trace) trace->Report(30, ERROR_INVALID_WINDOW_HANDLE);
        return false;
    }
    HMODULE pinned = nullptr;
    SetLastError(ERROR_SUCCESS);
    const BOOL pinnedOk = GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&ControlProc), &pinned);
    if (trace) trace->Report(31, pinnedOk ? 0 : BootstrapFailure());
    if (!pinnedOk) return false;
    WNDCLASSEXW cls{};
    cls.cbSize = sizeof(cls);
    cls.lpfnWndProc = ControlProc; cls.hInstance = moduleInstance; cls.lpszClassName = ClassName;
    SetLastError(ERROR_SUCCESS);
    const ATOM atom = RegisterClassExW(&cls);
    const DWORD classError = atom || GetLastError() == ERROR_CLASS_ALREADY_EXISTS ? ERROR_SUCCESS : BootstrapFailure();
    if (trace) trace->Report(32, classError);
    if (classError) return false;
    state.statusMessage = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.Status.v1");
    state.countMessage = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.HiddenCount.v1");
    state.diagnosticsMessage = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.Diagnostics.v1");
    state.rebuildStatus = S_OK;
    state.view = view; state.list = list; state.threadId = GetCurrentThreadId(); state.testHost = test;
    state.buildId = ModuleBuildId();
    state.heartbeat = GetTickCount64();
    SetLastError(ERROR_SUCCESS);
    state.control = CreateWindowExW(0, ClassName, nullptr, 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, moduleInstance, nullptr);
    if (trace) trace->Report(33, state.control ? 0 : BootstrapFailure());
    if (!state.control) { Stop(); return false; }
    SetLastError(ERROR_SUCCESS);
    const BOOL viewSubclassed = SetWindowSubclass(view, ViewSubclass, SubclassId, 0);
    if (trace) trace->Report(34, viewSubclassed ? 0 : BootstrapFailure());
    if (!viewSubclassed) { Stop(); return false; }
    SetLastError(ERROR_SUCCESS);
    const BOOL listSubclassed = SetWindowSubclass(list, ListSubclass, SubclassId, 0);
    if (trace) trace->Report(35, listSubclassed ? 0 : BootstrapFailure());
    if (!listSubclassed) { Stop(); return false; }
    SetLastError(ERROR_SUCCESS);
    const UINT_PTR timer = SetTimer(state.control, WatchdogId, 500, nullptr);
    if (trace) trace->Report(36, timer ? 0 : BootstrapFailure());
    if (!timer) { Stop(); return false; }
    EnsureDropGate();
    if (trace) {
        trace->Report(37, 7 | (state.drop ? 8 : 0));
        trace->Report(38, reinterpret_cast<LPARAM>(state.control));
    }
    return true;
}
bool ExplorerProcess() {
    wchar_t executable[32768]{};
    if (!GetModuleFileNameW(nullptr, executable, 32768)) return false;
    const wchar_t* name = wcsrchr(executable, L'\\');
    return _wcsicmp(name ? name + 1 : executable, L"explorer.exe") == 0;
}
struct DesktopSearch {
    HWND view = nullptr;
    const BootstrapTrace* trace = nullptr;
};
BOOL CALLBACK FindDesktop(HWND candidate, LPARAM resultPointer) {
    auto* search = reinterpret_cast<DesktopSearch*>(resultPointer);
    DWORD pid = 0;
    GetWindowThreadProcessId(candidate, &pid);
    if (pid != GetCurrentProcessId()) return TRUE;
    wchar_t name[32]{};
    GetClassNameW(candidate, name, 32);
    if (wcscmp(name, L"Progman") && wcscmp(name, L"WorkerW")) return TRUE;
    if (search->trace) search->trace->Report(10, reinterpret_cast<LPARAM>(candidate));
    const HWND view = FindWindowExW(candidate, nullptr, L"SHELLDLL_DefView", nullptr);
    const HWND list = view ? FindWindowExW(view, nullptr, WC_LISTVIEWW, nullptr) : nullptr;
    if (search->trace) {
        search->trace->Report(12, reinterpret_cast<LPARAM>(view));
        search->trace->Report(13, view ? GetWindowThreadProcessId(view, nullptr) : 0);
        search->trace->Report(14, reinterpret_cast<LPARAM>(list));
        search->trace->Report(15, list ? GetWindowThreadProcessId(list, nullptr) : 0);
    }
    if (list && GetWindowThreadProcessId(view, nullptr) == GetCurrentThreadId()) {
        search->view = view; return FALSE;
    }
    return TRUE;
}
} // namespace

extern "C" __declspec(dllexport) LRESULT CALLBACK OpenFenestraHookProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0) {
        try {
            static UINT bootstrapMessage = 0, replyMessage = 0;
            if (!bootstrapMessage) bootstrapMessage = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.Bootstrap.v1");
            if (!replyMessage) replyMessage = RegisterWindowMessageW(L"OpenFenestra.NativeShellBridge.BootstrapReply.v1");
            const auto* message = reinterpret_cast<const MSG*>(lParam);
            BootstrapTrace trace;
            const BootstrapTrace* diagnostic = nullptr;
            if (message && message->message == bootstrapMessage && wParam == PM_REMOVE) {
                trace.recipient = reinterpret_cast<HWND>(message->wParam);
                trace.reply = replyMessage;
                trace.nonce = LOWORD(message->lParam);
                if (IsWindow(trace.recipient)) diagnostic = &trace;
            }
            if (diagnostic) {
                diagnostic->Report(1, GetCurrentProcessId());
                diagnostic->Report(2, GetCurrentThreadId());
                // Initialization is an explicit bootstrap operation. In particular,
                // never initialize on an earlier WM_NULL or unrelated Explorer
                // message before the controller can receive entry diagnostics.
                const bool explorer = ExplorerProcess();
                diagnostic->Report(3, explorer ? 1 : 0);
                if (!state.control && explorer) {
                    DesktopSearch search{nullptr, diagnostic};
                    EnumWindows(FindDesktop, reinterpret_cast<LPARAM>(&search));
                    if (search.view) Prepare(search.view, FindWindowExW(search.view, nullptr, WC_LISTVIEWW, nullptr), false, diagnostic);
                    else diagnostic->Report(20, ERROR_NOT_FOUND);
                } else if (state.control) diagnostic->Report(39, reinterpret_cast<LPARAM>(state.control));
            }
        } catch (...) { if (state.control) PostMessageW(state.control, DetachMessage, 0, 0); }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

// Isolated harness entry point. It cannot attach to Explorer, another process,
// or another UI thread. Test identities are supplied by the harness, not lParam.
extern "C" __declspec(dllexport) BOOL WINAPI OpenFenestraAttachTestHost(HWND view, HWND list,
    const wchar_t* identities, DWORD characters) {
    DWORD pid = 0;
    GetWindowThreadProcessId(view, &pid);
    if (ExplorerProcess() || pid != GetCurrentProcessId() || !identities || characters > 100000) return FALSE;
    try {
        COPYDATASTRUCT packet{0, characters * static_cast<DWORD>(sizeof(wchar_t)), const_cast<wchar_t*>(identities)};
        std::vector<std::wstring> names;
        if (!ParseStrings(&packet, names) || !Prepare(view, list, true)) return FALSE;
        state.testNames = std::move(names);
        return TRUE;
    } catch (...) { Stop(); return FALSE; }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    // Static CRT/TLS implementations require thread attach/detach notifications.
    // Do not use DisableThreadLibraryCalls with our static-runtime builds.
    if (reason == DLL_PROCESS_ATTACH) moduleInstance = instance;
    return TRUE;
}
