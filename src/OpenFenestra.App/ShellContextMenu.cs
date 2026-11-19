using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OpenFenestra.App;

// Hosts the item's own Shell verbs, including installed context-menu extensions.
// This is the native classic menu (Windows 11's "Show more options" menu).
internal static class ShellContextMenu
{
    internal sealed class Session : IDisposable
    {
        private IShellItem? _item;
        private IContextMenu? _context;
        private nint _menu;
        internal Session(string path, bool extended = false)
        {
            try
            {
                var iid = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out _item));
                var handler = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5");
                iid = typeof(IContextMenu).GUID;
                Marshal.ThrowExceptionForHR(_item.BindToHandler(0, ref handler, ref iid, out _context));
                _menu = CreatePopupMenu();
                if (_menu == 0) throw new Win32Exception();
                Marshal.ThrowExceptionForHR(_context.QueryContextMenu(_menu, 0, 1, 0x7fff, extended ? 0x100u : 0));
            }
            catch { Dispose(); throw; }
        }

        internal int CommandCount => GetMenuItemCount(_menu);
        internal bool Show(Window owner, Point screen)
        {
            var hwnd = new WindowInteropHelper(owner).EnsureHandle();
            var source = HwndSource.FromHwnd(hwnd)!;
            nint Hook(nint h, int message, nint w, nint l, ref bool handled)
            {
                if (message is not (0x117 or 0x2b or 0x2c or 0x120)) return 0;
                if (_context is IContextMenu3 third && third.HandleMenuMsg2((uint)message, w, l, out var result) == 0)
                { handled = true; return result; }
                if (message != 0x120 && _context is IContextMenu2 second && second.HandleMenuMsg((uint)message, w, l) == 0)
                    handled = true;
                return 0;
            }
            source.AddHook(Hook);
            try
            {
                SetForegroundWindow(hwnd);
                var command = TrackPopupMenuEx(_menu, 0x100 | 0x2, (int)screen.X, (int)screen.Y, hwnd, 0);
                if (command == 0) return false;
                var verb = CommandVerb(command - 1);
                var invoke = new InvokeInfo { Size = Marshal.SizeOf<InvokeInfo>(), Mask = 0x4000 | 0x20000000,
                    Owner = hwnd, Verb = (nint)(command - 1), VerbW = (nint)(command - 1), Show = 1,
                    Point = new NativePoint { X = (int)screen.X, Y = (int)screen.Y } };
                if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) invoke.Mask |= 0x10000000;
                if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0) invoke.Mask |= 0x40000000;
                Marshal.ThrowExceptionForHR(_context!.InvokeCommand(ref invoke));
                return verb?.ToLowerInvariant() is "open" or "explore" or "runas" or "opennewwindow" or "openas";
            }
            finally { source.RemoveHook(Hook); }
        }

        private string? CommandVerb(uint offset)
        {
            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                // GCS_VERBW: optional canonical verb. Unknown extension commands are not counted.
                for (var i = 0; i < 256; i++) Marshal.WriteInt16(buffer, i * 2, 0);
                return _context!.GetCommandString(offset, 4, 0, buffer, 255) == 0 ? Marshal.PtrToStringUni(buffer) : null;
            }
            catch (COMException) { return null; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Dispose()
        {
            if (_menu != 0) { DestroyMenu(_menu); _menu = 0; }
            if (_context is not null) { Marshal.ReleaseComObject(_context); _context = null; }
            if (_item is not null) { Marshal.ReleaseComObject(_item); _item = null; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InvokeInfo
    {
        public int Size; public uint Mask; public nint Owner, Verb, Parameters, Directory;
        public int Show; public uint HotKey; public nint Icon, Title, VerbW, ParametersW, DirectoryW, TitleW;
        public NativePoint Point;
    }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid iid, out IContextMenu result);
    }
    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint command, uint flags, nint reserved, nint text, uint count);
    }
    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint command, uint flags, nint reserved, nint text, uint count);
        [PreserveSig] int HandleMenuMsg(uint message, nint w, nint l);
    }
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint command, uint flags, nint reserved, nint text, uint count);
        [PreserveSig] int HandleMenuMsg(uint message, nint w, nint l);
        [PreserveSig] int HandleMenuMsg2(uint message, nint w, nint l, out nint result);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out IShellItem item);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
