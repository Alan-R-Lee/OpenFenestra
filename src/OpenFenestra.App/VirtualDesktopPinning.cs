// The private COM interface identifiers and PinView sequence are adapted from:
// https://github.com/MScholtes/VirtualDesktop/blob/a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6/VirtualDesktop11-24H2.cs
// The rest of this adapter is written for OpenFenestra.
//
// MIT License
// Copyright (c) 2017 Markus Scholtes
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace OpenFenestra.App;

/// <summary>
/// Pins this process's fence HWNDs on all Windows virtual desktops. These shell
/// interfaces are undocumented: an unsupported build or a missing application
/// view disables this feature without changing the native Explorer desktop.
/// </summary>
internal static class VirtualDesktopPinning
{
    private const uint ClsctxLocalServer = 4;
    private const uint CoinitApartmentThreaded = 2;
    private const uint CoinitDisableOle1Dde = 4;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private static readonly object Gate = new();
    private static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid PinnedAppsService = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
    private static readonly (int Build, int Revision) WindowsBuild = ReadWindowsBuild();

    internal static string BuildDescription => $"{WindowsBuild.Build}.{WindowsBuild.Revision}";

    /// <summary>
    /// Call after the fence has been shown, then periodically to restore pinning
    /// after Explorer restarts. The shell may not have a view at SourceInitialized.
    /// Already pinned windows incur no PinView call. No COM proxy is cached.
    /// </summary>
    internal static bool TryPin(nint hwnd, out string? error)
        => WithView(hwnd, pin: true, out _, out error);

    /// <summary>Returns false on probe failure, separately from an unpinned view.</summary>
    internal static bool TryIsPinned(nint hwnd, out bool pinned, out string? error)
        => WithView(hwnd, pin: false, out pinned, out error);

    private static bool WithView(nint hwnd, bool pin, out bool pinned, out string? error)
    {
        pinned = false;
        error = null;
        if (!OperatingSystem.IsWindows() || !IsKnownBuild(WindowsBuild.Build))
        {
            error = $"Virtual desktop pinning is unavailable on Windows build {BuildDescription}.";
            return false;
        }
        if (hwnd == 0 || !IsWindow(hwnd))
        {
            error = "The fence window does not exist yet.";
            return false;
        }
        GetWindowThreadProcessId(hwnd, out var ownerProcessId);
        if (ownerProcessId != (uint)Environment.ProcessId || GetAncestor(hwnd, 2) != hwnd)
        {
            error = "Only this process's top-level fence windows can be pinned.";
            return false;
        }

        lock (Gate)
        {
            IServiceProvider? shell = null;
            object? collectionService = null;
            object? pinnedAppsService = null;
            IApplicationView? view = null;
            var uninitialize = false;
            try
            {
                var initialized = CoInitializeEx(0, CoinitApartmentThreaded | CoinitDisableOle1Dde);
                // An already initialized MTA is also usable for these out-of-process
                // services. Balance only successful calls, including S_FALSE.
                if (initialized != RpcEChangedMode) Marshal.ThrowExceptionForHR(initialized);
                uninitialize = initialized >= 0;

                var shellClass = ImmersiveShell;
                var providerId = typeof(IServiceProvider).GUID;
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref shellClass, 0, ClsctxLocalServer,
                    ref providerId, out shell));

                var collectionId = typeof(IApplicationViewCollection).GUID;
                Marshal.ThrowExceptionForHR(shell.QueryService(ref collectionId, ref collectionId, out collectionService));
                var collection = (IApplicationViewCollection)collectionService;
                Marshal.ThrowExceptionForHR(collection.GetViewForHwnd(hwnd, out view));
                if (view is null)
                {
                    error = "Explorer has not created an application view for the fence yet.";
                    return false;
                }

                var pinnedClass = PinnedAppsService;
                var pinnedId = typeof(IVirtualDesktopPinnedApps).GUID;
                Marshal.ThrowExceptionForHR(shell.QueryService(ref pinnedClass, ref pinnedId, out pinnedAppsService));
                var pinnedApps = (IVirtualDesktopPinnedApps)pinnedAppsService;
                Marshal.ThrowExceptionForHR(pinnedApps.IsViewPinned(view, out pinned));
                if (pin && !pinned)
                {
                    Marshal.ThrowExceptionForHR(pinnedApps.PinView(view));
                    Marshal.ThrowExceptionForHR(pinnedApps.IsViewPinned(view, out pinned));
                    if (!pinned)
                    {
                        error = "Explorer did not retain the fence's virtual desktop pin.";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException
                or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
            {
                error = $"Virtual desktop pinning failed on {BuildDescription}: 0x{ex.HResult:X8} ({ex.Message})";
                pinned = false;
                return false;
            }
            finally
            {
                Release(view);
                Release(pinnedAppsService);
                Release(collectionService);
                Release(shell);
                if (uninitialize) CoUninitialize();
            }
        }
    }

    // The pinning and application-view interfaces have matching definitions in
    // the upstream Windows 10, Windows 11, and 24H2 source variants. Deliberately
    // fail closed for a new release until its private COM contract is checked.
    private static bool IsKnownBuild(int build) => build is >= 19041 and <= 19045
        or 22000 or 22621 or 22631 or 26100 or 26200;

    private static (int Build, int Revision) ReadWindowsBuild()
    {
        var build = Environment.OSVersion.Version.Build;
        var revision = 0;
        if (!OperatingSystem.IsWindows()) return (build, revision);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (int.TryParse(key?.GetValue("CurrentBuildNumber")?.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var registryBuild)) build = registryBuild;
            if (key?.GetValue("UBR") is int ubr) revision = ubr;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // A denied registry read must not prevent startup; the OS version
            // supplies the release build for the same compatibility guard.
        }
        return (build, revision);
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); }
        catch (InvalidComObjectException) { }
    }

    // A marker is sufficient: no IApplicationView methods are called. Keeping
    // its real IID preserves the interface pointer required by PinView without
    // importing its unrelated and more changeable method table.
    [ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
    // IInspectable extends IUnknown. This empty marker only performs QI; using
    // IUnknown also avoids requiring WinRT IInspectable marshalling in .NET.
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationView { }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out nint array);
        [PreserveSig] int GetViewsByZOrder(out nint array);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out nint array);
        [PreserveSig] int GetViewForHwnd(nint hwnd, [MarshalAs(UnmanagedType.Interface)] out IApplicationView? view);
    }

    [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedApps
    {
        [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinAppId([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int UnpinAppId([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int IsViewPinned([MarshalAs(UnmanagedType.Interface)] IApplicationView view, [MarshalAs(UnmanagedType.Bool)] out bool pinned);
        [PreserveSig] int PinView([MarshalAs(UnmanagedType.Interface)] IApplicationView view);
        [PreserveSig] int UnpinView([MarshalAs(UnmanagedType.Interface)] IApplicationView view);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint coinit);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IServiceProvider instance);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
