using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OpenFenestra.App;

// Keep our windows above the Explorer desktop and below ordinary applications.
// No injection, hooks in Explorer, or writes to its icon list are required.
internal static class NativeDesktop
{
    private const uint PositionFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private delegate bool EnumWindowProc(nint hwnd, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint param);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLong(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    internal static nint FindHost()
    {
        var host = FindWindow("Progman", null);
        if (host == 0) return 0;
        GetWindowThreadProcessId(host, out var shellPid);
        // On older Explorer versions DefView can move from Progman into WorkerW.
        if (FindWindowEx(host, 0, "SHELLDLL_DefView", null) == 0)
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == shellPid && FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null) != 0)
                { host = hwnd; return false; }
                return true;
            }, 0);
        }
        return host;
    }

    internal static nint IconList()
    {
        var host = FindHost();
        if (host == 0) return 0;
        var view = FindWindowEx(host, 0, "SHELLDLL_DefView", null);
        return view == 0 ? 0 : FindWindowEx(view, 0, "SysListView32", null);
    }
    internal static bool HasVisibleStyle(nint hwnd) => hwnd != 0 && ((long)GetWindowLong(hwnd, -16) & 0x10000000) != 0;
    internal static object Diagnostics()
    {
        var host = FindHost(); var view = FindWindowEx(host, 0, "SHELLDLL_DefView", null); var list = IconList();
        string Class(nint handle) { var value = new System.Text.StringBuilder(128); GetClassName(handle, value, value.Capacity); return value.ToString(); }
        var hostThread = GetWindowThreadProcessId(host, out var hostProcess);
        var viewThread = GetWindowThreadProcessId(view, out var viewProcess);
        var listThread = GetWindowThreadProcessId(list, out var listProcess);
        return new { host = host.ToInt64(), hostClass = Class(host), hostThread, hostProcess,
            view = view.ToInt64(), viewClass = Class(view), viewThread, viewProcess,
            list = list.ToInt64(), listClass = Class(list), listThread, listProcess };
    }
    internal static bool SetNativeIconsVisible(bool visible)
    {
        var list = IconList();
        if (list == 0 || !IsWindow(list)) return false;
        var cls = new System.Text.StringBuilder(64); GetClassName(list, cls, cls.Capacity);
        if (cls.ToString() != "SysListView32") return false;
        ShowWindow(list, visible ? 8 : 0);
        if (visible) InvalidateRect(list, 0, true);
        return HasVisibleStyle(list) == visible;
    }

    internal static bool Anchor(Window window) => AnchorWindows([window]);
    internal static bool IsDesktopOwned(Window window) => GetWindow(new WindowInteropHelper(window).Handle, 4) == FindHost();

    internal static bool AnchorWindows(IEnumerable<Window> topToBottom)
    {
        var handles = topToBottom.Where(w => w.IsVisible).Select(w => new WindowInteropHelper(w).Handle).Where(h => h != 0).ToArray();
        var host = FindHost();
        if (host == 0 || handles.Length == 0) return false;
        var owned = handles.ToHashSet();
        var above = GetWindow(host, 3); // GW_HWNDPREV
        while (above != 0)
        {
            if (!owned.Contains(above) && IsWindowVisible(above) && !IsIconic(above)
                && GetWindowRect(above, out var r) && r.Right > r.Left && r.Bottom > r.Top) break;
            above = GetWindow(above, 3);
        }
        var success = true;
        foreach (var handle in handles)
        {
            // An owned popup belongs to the desktop's window family. This is
            // ownership, not cross-process SetParent or a replacement canvas.
            if (GetWindow(handle, 4) != host) SetWindowLong(handle, -8, host);
            success &= SetWindowPos(handle, above, 0, 0, 0, 0, PositionFlags);
            above = handle;
        }
        return success;
    }
    internal static void SetBounds(Window window, System.Drawing.Rectangle bounds) =>
        SetWindowPos(new WindowInteropHelper(window).Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0004 | 0x0010 | 0x0200);
}
