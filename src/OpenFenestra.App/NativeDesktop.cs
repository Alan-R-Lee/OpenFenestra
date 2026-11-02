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
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    internal static bool Anchor(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var host = FindWindow("Progman", null);
        if (host == 0 || handle == 0) return false;
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
        // Inserting after the visible window immediately above Explorer places us
        // immediately above the desktop. Skip invisible helper windows and ourselves.
        var above = GetWindow(host, 3); // GW_HWNDPREV
        while (above != 0)
        {
            if (above != handle && IsWindowVisible(above) && !IsIconic(above)
                && GetWindowRect(above, out var r) && r.Right > r.Left && r.Bottom > r.Top) break;
            above = GetWindow(above, 3);
        }
        return SetWindowPos(handle, above, 0, 0, 0, 0, PositionFlags);
    }
}
