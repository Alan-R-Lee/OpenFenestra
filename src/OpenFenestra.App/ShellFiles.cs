using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using OpenFenestra.Core;

namespace OpenFenestra.App;

internal static class ShellFiles
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    { [PreserveSig] int GetImage(NativeSize size, uint flags, out nint bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint bitmap);
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new(); // Only accessed on the icon STA.
    private static IReadOnlyDictionary<string, DesktopEntry> _desktopNames = new Dictionary<string, DesktopEntry>(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<System.Windows.Threading.Dispatcher> IconDispatcher = new(() =>
    {
        System.Windows.Threading.Dispatcher? dispatcher = null;
        using var ready = new System.Threading.ManualResetEventSlim();
        var thread = new System.Threading.Thread(() =>
        { dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher; ready.Set(); System.Windows.Threading.Dispatcher.Run(); })
        { IsBackground = true, Name = "OpenFenestra Shell Icons" };
        thread.SetApartmentState(System.Threading.ApartmentState.STA); thread.Start(); ready.Wait();
        return dispatcher!;
    });

    internal static ImageSource? Icon(string path, int pixels = 128)
        => Cache.TryGetValue(IconKey(path, pixels), out var image) ? image : IconDispatcher.Value.Invoke(() => ExtractIcon(path, pixels));

    internal static Task<ImageSource?> IconAsync(string path, int pixels = 128, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested) return Task.FromCanceled<ImageSource?>(cancellation);
        if (Cache.TryGetValue(IconKey(path, pixels), out var image)) return Task.FromResult<ImageSource?>(image);
        return IconDispatcher.Value.InvokeAsync(() => ExtractIcon(path, pixels), DispatcherPriority.Background, cancellation).Task;
    }
    private static string IconKey(string path, int pixels) => path + "|" + Math.Clamp(pixels, 48, 256);

    private static ImageSource? ExtractIcon(string path, int pixels)
    {
        pixels = Math.Clamp(pixels, 48, 256);
        var key = IconKey(path, pixels);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? image = null;
        IShellItemImageFactory? factory = null;
        nint bitmap = 0;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            // ICONONLY | BIGGERSIZEOK: original icon frames, no thumbnail or GDI upscale.
            if (SHCreateItemFromParsingName(path, 0, ref iid, out factory) >= 0
                && factory.GetImage(new() { Width = pixels, Height = pixels }, 0x01 | 0x04, out bitmap) >= 0 && bitmap != 0)
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze(); image = source;
            }
        }
        catch (Exception e) when (e is COMException or ArgumentException) { Debug.WriteLine(e); }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
        if (image is null)
        {
            var flags = 0x100u;
            if (!File.Exists(path) && !Directory.Exists(path) && !DesktopAssignments.IsVirtual(path)) flags |= 0x10;
            if (SHGetFileInfo(path, Directory.Exists(path) ? 0x10u : 0x80u, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags) != 0 && info.Icon != 0)
            {
                try { var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); source.Freeze(); image = source; }
                finally { DestroyIcon(info.Icon); }
            }
        }
        if (image is not null && Cache.TryAdd(key, image))
        {
            CacheOrder.Enqueue(key);
            while (CacheOrder.Count > 1500) Cache.TryRemove(CacheOrder.Dequeue(), out _);
        }
        return image;
    }

    internal static IReadOnlyList<DesktopEntry> DesktopItems()
    {
        // The desktop namespace also contains navigation entries which are not
        // shown on the desktop. Enumerate the actual Explorer view instead.
        var entries = NativeDesktopShell.ReadSnapshot().Items.Select(item => new DesktopEntry(item.Path, item.Name, item.IsFolder)).ToList();
        var names = new Dictionary<string, DesktopEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) names[entry.Path] = entry;
        Volatile.Write(ref _desktopNames, names);
        return entries;
    }

    internal static DesktopEntry Entry(string path) => Volatile.Read(ref _desktopNames).TryGetValue(path, out var entry) ? entry : FileCatalog.Entry(path);
    internal static DesktopEntry Entry(DesktopEntry entry) => Volatile.Read(ref _desktopNames).TryGetValue(entry.Path, out var named) ? named : entry;

    internal static void Open(string path)
    {
        if (DesktopAssignments.IsVirtual(path))
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("shell:" + path); Process.Start(start); return;
        }
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("原文件已移动或删除。", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    internal static void Reveal(string path)
    {
        if (DesktopAssignments.IsVirtual(path)) { Open(path); return; }
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); Process.Start(start);
    }
}
