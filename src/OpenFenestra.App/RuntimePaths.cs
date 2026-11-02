using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenFenestra.App;

internal static class RuntimePaths
{
    internal static string PhysicalFile(string path)
    {
        using var file = File.OpenRead(path);
        var result = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file.SafeFileHandle, result, (uint)result.Capacity, 0);
        if (length == 0 || length >= result.Capacity) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var resolved = result.ToString();
        return resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + resolved[8..]
            : resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }
    internal static string? PackageIdentity
    {
        get
        {
            uint length = 0;
            if (GetCurrentPackageFullName(ref length, null) != 122) return null;
            var name = new StringBuilder(checked((int)length));
            return GetCurrentPackageFullName(ref length, name) == 0 ? name.ToString() : null;
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);
}
