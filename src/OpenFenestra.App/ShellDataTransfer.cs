using System.Buffers.Binary;
using System.Runtime.InteropServices;
using OpenFenestra.Core;

namespace OpenFenestra.App;

/// <summary>Reads the documented CFSTR_SHELLIDLIST/CIDA format without trusting its offsets.</summary>
internal static class ShellDataTransfer
{
    internal const string Format = "Shell IDList Array";
    private const int MaxItems = 300;
    private const int MaxBytes = 4 * 1024 * 1024;
    private const uint DesktopAbsoluteParsing = 0x80028000;

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern nint ILCombine(nint parent, nint child);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(nint item);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetNameFromIDList(nint item, uint kind, out nint name);

    internal static string[] Read(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(Format, false)) return [];
            var bytes = ReadBytes(data.GetData(Format, false));
            if (bytes is null || !TryGetPidlOffsets(bytes, out var offsets)) return [];

            var paths = new List<string>();
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var start = pinned.AddrOfPinnedObject();
                var parent = start + offsets[0];
                foreach (var offset in offsets.Skip(1))
                {
                    nint combined = 0, name = 0;
                    try
                    {
                        combined = ILCombine(parent, start + offset);
                        if (combined == 0 || SHGetNameFromIDList(combined, DesktopAbsoluteParsing, out name) < 0 || name == 0)
                            continue;
                        var path = DesktopAssignments.NormalizeItem(Marshal.PtrToStringUni(name));
                        if (path is not null) paths.Add(path);
                    }
                    finally
                    {
                        if (name != 0) Marshal.FreeCoTaskMem(name);
                        if (combined != 0) ILFree(combined);
                    }
                }
            }
            finally { pinned.Free(); }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or InvalidOperationException
            or IOException or NotSupportedException or System.Security.SecurityException)
        { return []; }
    }

    // A CIDA has one absolute parent PIDL and cidl relative child PIDLs. Each
    // SHITEMID starts with its byte length; a zero ushort terminates each PIDL.
    // Validate all ranges before allowing Shell code to read any of those pointers.
    internal static bool TryGetPidlOffsets(byte[] bytes, out int[] offsets)
    {
        offsets = [];
        if (bytes.Length is < 12 or > MaxBytes) return false;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count is 0 or > MaxItems) return false;
        var headerBytes = 4 + ((int)count + 1) * 4;
        if (headerBytes > bytes.Length) return false;

        var result = new int[(int)count + 1];
        var ranges = new List<(int Start, int End)>();
        for (var i = 0; i < result.Length; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 + i * 4, 4));
            if (offset < headerBytes || offset > bytes.Length - 2) return false;
            var cursor = (int)offset;
            while (true)
            {
                if (cursor > bytes.Length - 2) return false;
                var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
                if (size == 0) { cursor += 2; break; }
                if (size < 2 || size > bytes.Length - cursor) return false;
                cursor += size;
            }
            result[i] = (int)offset;
            ranges.Add(((int)offset, cursor));
        }
        var ordered = ranges.OrderBy(range => range.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i].Start < ordered[i - 1].End) return false;
        offsets = result;
        return true;
    }

    private static byte[]? ReadBytes(object? value)
    {
        if (value is byte[] bytes) return bytes.Length <= MaxBytes ? bytes : null;
        if (value is not Stream stream) return null;
        var position = stream.CanSeek ? stream.Position : (long?)null;
        try
        {
            if (position.HasValue)
            {
                if (stream.Length > MaxBytes) return null;
                stream.Position = 0;
            }
            using var copy = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (copy.Length + read > MaxBytes) return null;
                copy.Write(buffer, 0, read);
            }
            return copy.ToArray();
        }
        finally
        {
            if (position.HasValue)
            {
                try { stream.Position = position.Value; }
                catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException) { }
            }
        }
    }
}
