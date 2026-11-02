using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenFenestra.App;

internal readonly record struct NativeDesktopPoint(int X, int Y);
internal sealed record NativeDesktopItem(string Path, string Name, bool IsFolder, NativeDesktopPoint Position);
internal sealed record NativeDesktopSnapshot(uint FolderFlags, IReadOnlyList<NativeDesktopItem> Items);

/// <summary>
/// Accesses the existing Explorer desktop view through documented Shell COM
/// interfaces. Coordinates are native view/client pixels, or physical screen
/// pixels for the explicitly named Screen methods; they are not WPF DIPs.
/// No enumeration uses Shell.Application.NameSpace(0).
/// </summary>
/// <remarks>
/// Independently implemented from the documented interface contracts and the
/// discovery sequence described by Raymond Chen, March 18, 2013:
/// https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933
/// No hooks, off-screen positioning, namespace removal, file operations, or
/// folder-flag changes are performed. Only PositionItems writes icon positions.
/// </remarks>
internal static class NativeDesktopShell
{
    private const uint AllViewItems = 2; // SVGIO_ALLVIEW
    private const uint PositionItem = 0x80; // SVSI_POSITIONITEM; does not select.
    private const uint FolderAttribute = 0x20000000; // SFGAO_FOLDER
    private const uint NormalDisplayName = 0;
    private const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH
    private const uint DesktopParsingName = 0x80028000; // SIGDN_DESKTOPABSOLUTEPARSING
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    internal static NativeDesktopSnapshot ReadSnapshot() => OnSta(() =>
    {
        using var desktop = DesktopView.Open();
        Marshal.ThrowExceptionForHR(desktop.View.GetCurrentFolderFlags(out var flags));
        var items = new List<NativeDesktopItem>();
        desktop.Enumerate((pidl, item) =>
        {
            // An item can disappear while Explorer is servicing the enumeration.
            if (desktop.View.GetItemPosition(pidl, out var position) >= 0)
                items.Add(item with { Position = position.ToPoint() });
        });
        return new NativeDesktopSnapshot(flags, items.AsReadOnly());
    });

    /// <summary>
    /// Explicitly repositions existing view items and returns their actual
    /// positions after Explorer applies its current arrangement/grid settings.
    /// Missing paths are absent from the result. Existing folder flags are kept.
    /// Every destination must map onto an attached monitor; off-screen requests
    /// are rejected before any icon is moved.
    /// </summary>
    internal static IReadOnlyDictionary<string, NativeDesktopPoint> PositionItems(
        IReadOnlyDictionary<string, NativeDesktopPoint> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        // Copy before dispatching to an STA so caller mutations cannot change
        // destinations during a move or between validation and application.
        var requested = new Dictionary<string, NativeDesktopPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in positions)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) throw new ArgumentException("An item path is empty.", nameof(positions));
            requested.Add(pair.Key, pair.Value);
        }
        if (requested.Count == 0) return new Dictionary<string, NativeDesktopPoint>();

        return OnSta<IReadOnlyDictionary<string, NativeDesktopPoint>>(() =>
        {
            using var desktop = DesktopView.Open();
            using var dpi = new DpiScope();
            foreach (var destination in requested.Values)
            {
                var screen = desktop.ViewToScreen(destination);
                if (MonitorFromPoint(NativePoint.From(screen), 0) == 0)
                    throw new ArgumentOutOfRangeException(nameof(positions), "An icon destination is outside all attached monitors.");
            }

            var pidls = new List<nint>();
            var points = new List<NativePoint>();
            var paths = new List<string>();
            try
            {
                desktop.Enumerate((pidl, item) =>
                {
                    if (!requested.TryGetValue(item.Path, out var destination)) return;
                    // Enumerate owns its PIDL. Clone requested identities for the
                    // single positioning call and its readback, then free them.
                    var clone = ILClone(pidl);
                    if (clone == 0) throw new OutOfMemoryException("The Shell item identity could not be cloned.");
                    pidls.Add(clone);
                    points.Add(NativePoint.From(destination));
                    paths.Add(item.Path);
                });
                if (pidls.Count > 0)
                    Marshal.ThrowExceptionForHR(desktop.View.SelectAndPositionItems((uint)pidls.Count,
                        pidls.ToArray(), points.ToArray(), PositionItem));

                var actual = new Dictionary<string, NativeDesktopPoint>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < pidls.Count; i++)
                    if (desktop.View.GetItemPosition(pidls[i], out var point) >= 0)
                        actual[paths[i]] = point.ToPoint();
                return actual;
            }
            finally
            {
                foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
            }
        });
    }

    internal static NativeDesktopPoint ScreenToView(NativeDesktopPoint screenPoint) => OnSta(() =>
    {
        using var desktop = DesktopView.Open();
        using var dpi = new DpiScope();
        var native = NativePoint.From(screenPoint);
        if (!ScreenToClient(desktop.CoordinateWindow, ref native)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return native.ToPoint();
    });

    internal static NativeDesktopPoint ViewToScreen(NativeDesktopPoint viewPoint) => OnSta(() =>
    {
        using var desktop = DesktopView.Open();
        using var dpi = new DpiScope();
        return desktop.ViewToScreen(viewPoint);
    });

    /// <summary>False when a normal application covers this screen point.</summary>
    internal static bool IsDesktopAtScreenPoint(NativeDesktopPoint screenPoint) => OnSta(() =>
    {
        using var desktop = DesktopView.Open();
        using var dpi = new DpiScope();
        var hit = WindowFromPoint(NativePoint.From(screenPoint));
        if (hit == 0) return false;
        if (hit == desktop.ShellViewWindow || IsChild(desktop.ShellViewWindow, hit)) return true;
        // When Explorer's icon view is hidden, wallpaper/desktop-host windows
        // can receive the hit instead. Do not accept unrelated Explorer windows.
        var host = GetAncestor(desktop.ShellViewWindow, 2); // GA_ROOT
        if (hit != host) return false;
        GetWindowThreadProcessId(hit, out var hitPid);
        GetWindowThreadProcessId(desktop.ShellViewWindow, out var viewPid);
        return hitPid != 0 && hitPid == viewPid;
    });

    private static T OnSta<T>(Func<T> action)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native Explorer desktop requires Windows.");
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            using var com = new ComScope();
            return action();
        }
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var com = new ComScope();
                result = action();
            }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true, Name = "OpenFenestra desktop Shell STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }

    private sealed class DesktopView : IDisposable
    {
        private object? _windows, _dispatch, _browser, _shellView, _folder;
        internal IFolderView2 View { get; private set; } = null!;
        internal nint ShellViewWindow { get; private set; }
        internal nint CoordinateWindow { get; private set; }

        internal static DesktopView Open()
        {
            var result = new DesktopView();
            try
            {
                result._windows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!)
                    ?? throw new InvalidOperationException("Shell windows are unavailable.");
                object location = 0; // CSIDL_DESKTOP as VT_I4
                object? root = null; // The reserved argument must be VT_EMPTY.
                result._dispatch = ((IShellWindows)result._windows).FindWindowSW(ref location, ref root, 8, out _, 1)
                    ?? throw new InvalidOperationException("Explorer has not created its desktop view yet.");
                var service = TopLevelBrowserService;
                var browserId = typeof(IShellBrowser).GUID;
                Marshal.ThrowExceptionForHR(((IServiceProvider)result._dispatch).QueryService(ref service, ref browserId, out result._browser));
                Marshal.ThrowExceptionForHR(((IShellBrowser)result._browser).QueryActiveShellView(out result._shellView));
                result.View = (IFolderView2)result._shellView;
                Marshal.ThrowExceptionForHR(((IShellView)result._shellView).GetWindow(out var viewWindow));
                if (viewWindow == 0 || !IsWindow(viewWindow)) throw new InvalidOperationException("The desktop view window is unavailable.");
                result.ShellViewWindow = viewWindow;
                // Explorer's desktop IFolderView uses its ListView client origin.
                // Keep the queried ShellView HWND as the lifecycle/identity root.
                result.CoordinateWindow = FindWindowEx(viewWindow, 0, "SysListView32", null);
                if (result.CoordinateWindow == 0) result.CoordinateWindow = viewWindow;
                var folderId = typeof(IShellFolder).GUID;
                Marshal.ThrowExceptionForHR(result.View.GetFolder(ref folderId, out result._folder));
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal NativeDesktopPoint ViewToScreen(NativeDesktopPoint point)
        {
            // Compute through the client origin with checked arithmetic so an
            // extreme request cannot wrap around into an apparently valid screen.
            var origin = new NativePoint();
            if (!ClientToScreen(CoordinateWindow, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new NativeDesktopPoint(checked(origin.X + point.X), checked(origin.Y + point.Y));
        }

        internal void Enumerate(Action<nint, NativeDesktopItem> consume)
        {
            object? enumerationObject = null;
            try
            {
                var enumId = typeof(IEnumIDList).GUID;
                Marshal.ThrowExceptionForHR(View.Items(AllViewItems, ref enumId, out enumerationObject));
                var enumeration = (IEnumIDList)enumerationObject;
                for (var count = 0; ; count++)
                {
                    nint pidl = 0;
                    try
                    {
                        var status = enumeration.Next(1, out pidl, out var fetched);
                        Marshal.ThrowExceptionForHR(status);
                        if (status != 0 || fetched == 0 || pidl == 0) break;
                        if (count >= 100000) throw new InvalidOperationException("Explorer returned an excessive desktop item count.");
                        var item = ReadItem(pidl);
                        if (item is not null) consume(pidl, item);
                    }
                    finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
                }
            }
            finally { Release(enumerationObject); }
        }

        private NativeDesktopItem? ReadItem(nint pidl)
        {
            IShellItem? item = null;
            try
            {
                var itemId = typeof(IShellItem).GUID;
                var created = SHCreateItemWithParent(0, (IShellFolder)_folder!, pidl, ref itemId, out item);
                if (created < 0 || item is null) return null; // Concurrent removal.
                var path = GetName(item, FileSystemPath) ?? GetName(item, DesktopParsingName);
                if (string.IsNullOrWhiteSpace(path)) return null;
                var name = GetName(item, NormalDisplayName) ?? path;
                Marshal.ThrowExceptionForHR(item.GetAttributes(FolderAttribute, out var attributes));
                return new NativeDesktopItem(path, name, (attributes & FolderAttribute) != 0, default);
            }
            finally { Release(item); }
        }

        public void Dispose()
        {
            Release(_folder); _folder = null;
            // View and IShellView are QI aliases of this one acquired RCW.
            Release(_shellView); _shellView = null;
            Release(_browser); _browser = null;
            Release(_dispatch); _dispatch = null;
            Release(_windows); _windows = null;
        }
    }

    private static string? GetName(IShellItem item, uint kind)
    {
        nint text = 0;
        try
        {
            var status = item.GetDisplayName(kind, out text);
            return status >= 0 && text != 0 ? Marshal.PtrToStringUni(text) : null;
        }
        finally { if (text != 0) Marshal.FreeCoTaskMem(text); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private sealed class ComScope : IDisposable
    {
        internal ComScope() => Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2 | 4));
        public void Dispose() => CoUninitialize();
    }

    private sealed class DpiScope : IDisposable
    {
        private readonly nint _previous = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
        public void Dispose() { if (_previous != 0) SetThreadDpiAwarenessContext(_previous); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X, Y;
        internal static NativePoint From(NativeDesktopPoint point) => new() { X = point.X, Y = point.Y };
        internal readonly NativeDesktopPoint ToPoint() => new(X, Y);
    }

    // Only the used prefix of each documented vtable is declared. Unused slots
    // remain present in their SDK order; no undocumented Explorer interfaces.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [DispId(0x60020008)]
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object location,
            [In, MarshalAs(UnmanagedType.Struct)] ref object? root, int windowClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out nint hwnd);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
        [PreserveSig] int InsertMenusSB(nint menu, nint widths);
        [PreserveSig] int SetMenuSB(nint shared, nint reserved, nint active);
        [PreserveSig] int RemoveMenusSB(nint menu);
        [PreserveSig] int SetStatusTextSB(nint text);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAcceleratorSB(nint message, ushort id);
        [PreserveSig] int BrowseObject(nint pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint message, nint wParam, nint lParam, out nint result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out nint hwnd);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder { }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint result);
        [PreserveSig] int GetParent(out nint parent);
        [PreserveSig] int GetDisplayName(uint kind, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig] int Next(uint count, out nint pidl, out uint fetched);
    }

    [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object folder);
        [PreserveSig] int Item(int index, out nint pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object items);
        [PreserveSig] int GetSelectionMarkedItem(out int index);
        [PreserveSig] int GetFocusedItem(out int index);
        [PreserveSig] int GetItemPosition(nint pidl, out NativePoint point);
        [PreserveSig] int GetSpacing(ref NativePoint point);
        [PreserveSig] int GetDefaultSpacing(out NativePoint point);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int index, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] nint[] pidls,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativePoint[] points, uint flags);
        [PreserveSig] int SetGroupBy(nint key, [MarshalAs(UnmanagedType.Bool)] bool ascending);
        [PreserveSig] int GetGroupBy(nint key, out bool ascending);
        [PreserveSig] int SetViewProperty(nint pidl, nint key, nint value);
        [PreserveSig] int GetViewProperty(nint pidl, nint key, nint value);
        [PreserveSig] int SetTileViewProperties(nint pidl, nint properties);
        [PreserveSig] int SetExtendedTileViewProperties(nint pidl, nint properties);
        [PreserveSig] int SetText(uint kind, nint text);
        [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
        [PreserveSig] int GetCurrentFolderFlags(out uint flags);
    }

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int SHCreateItemWithParent(nint parentPidl,
        IShellFolder folder, nint childPidl, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern nint ILClone(nint pidl);
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
}
