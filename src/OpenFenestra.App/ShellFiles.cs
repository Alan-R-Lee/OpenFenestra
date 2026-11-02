using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OpenFenestra.App;

internal static class ShellFiles
{
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static ImageSource? Icon(string path)
    {
        // Generic files share an extension icon; shortcuts and executables have per-file icons.
        var ext = Path.GetExtension(path);
        var key = Directory.Exists(path) ? "<folder>" : ext.ToLowerInvariant() is ".lnk" or ".exe" or ".ico" ? path : ext;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var handle = SHGetFileInfo(path, Directory.Exists(path) ? 0x10u : 0x80u,
            out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | (ext.ToLowerInvariant() is ".lnk" or ".exe" or ".ico" ? 0u : 0x10u));
        ImageSource? image = null;
        if (handle != 0 && info.Icon != 0)
        {
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(40, 40));
                bitmap.Freeze(); image = bitmap;
            }
            finally { DestroyIcon(info.Icon); }
        }
        if (Cache.Count > 1500) Cache.Clear();
        if (image is not null) Cache[key] = image;
        return image;
    }

    internal static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("原文件已移动或删除。", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    internal static void Reveal(string path)
    {
        // ArgumentList keeps commas, quotes and spaces inside a single path argument.
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path);
        Process.Start(start);
    }
}
