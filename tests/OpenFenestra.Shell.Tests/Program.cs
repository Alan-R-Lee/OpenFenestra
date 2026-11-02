using System.Buffers.Binary;
using System.Runtime.InteropServices;
using OpenFenestra.App;

namespace OpenFenestra.Shell.Tests;

// Runs on an STA for WPF DataObject/OLE. No Application, Window, desktop bridge,
// or Explorer injection is created; Shell calls only parse/read a known PIDL.
internal static class Program
{
    private const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private static int _passed, _failed;

    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OpenFenestra.Shell.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(directory, "fixture.txt");
        const string contents = "Shell drag-data fixture";
        File.WriteAllText(fixture, contents);
        try
        {
            Test("CIDA accepts a desktop parent and one relative PIDL", () =>
                Assert(Valid(EmptyCida(1))));
            Test("CIDA rejects an offset into its header", () =>
            {
                var data = EmptyCida(1); WriteUInt32(data, 4, 4);
                Assert(!Valid(data));
            });
            Test("CIDA rejects an overflowing offset", () =>
            {
                var data = EmptyCida(1); WriteUInt32(data, 8, uint.MaxValue);
                Assert(!Valid(data));
            });
            Test("CIDA rejects overlapping PIDL ranges", () =>
            {
                var data = EmptyCida(1); WriteUInt32(data, 8, 12);
                Assert(!Valid(data));
            });
            Test("CIDA rejects an SHITEMID shorter than its length field", () =>
            {
                var data = EmptyCida(1); data[14] = 1;
                Assert(!Valid(data));
            });
            Test("CIDA rejects a PIDL with no terminating zero", () =>
            {
                var data = EmptyCida(1);
                // This two-byte item fits exactly, leaving no terminal ushort.
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 2);
                Assert(!Valid(data));
            });
            Test("CIDA rejects an item extending past its buffer", () =>
            {
                var data = EmptyCida(1);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), ushort.MaxValue);
                Assert(!Valid(data));
            });
            Test("CIDA rejects a truncated offset table", () =>
            {
                var data = new byte[11]; WriteUInt32(data, 0, 1);
                Assert(!Valid(data));
            });
            Test("CIDA rejects a transfer with no children", () =>
            {
                var data = new byte[12]; WriteUInt32(data, 4, 8);
                Assert(!Valid(data));
            });
            Test("CIDA accepts the 300 item capacity boundary", () =>
                Assert(Valid(EmptyCida(300))));
            Test("CIDA rejects 301 items", () =>
                Assert(!Valid(EmptyCida(301))));
            Test("CIDA rejects a payload beyond four MiB", () =>
                Assert(!Valid(new byte[4 * 1024 * 1024 + 1])));
            Test("CIDA permits disjoint PIDLs stored out of offset order", () =>
            {
                var data = EmptyCida(1); WriteUInt32(data, 4, 14); WriteUInt32(data, 8, 12);
                Assert(Valid(data));
            });
            Test("Private virtual drag data round trips without FileDrop", () =>
            {
                var data = new DesktopDrag([RecycleBin]).Data(false);
                Assert(!data.GetDataPresent(DataFormats.FileDrop, false));
                Assert(DesktopDrag.Read(data).Single() == RecycleBin);
            });
            Test("Malformed CIDA falls back to an ordinary file drop", () =>
            {
                using var malformed = new MemoryStream(new byte[11]);
                var data = new DataObject();
                data.SetData(ShellDataTransfer.Format, malformed);
                data.SetData(DataFormats.FileDrop, new[] { fixture });
                Assert(DesktopDrag.Read(data).Single() == fixture);
            });
            Test("Ordinary external file drags retain FileDrop", () =>
            {
                var data = new DesktopDrag([fixture]).Data();
                Assert(data.GetDataPresent(DataFormats.FileDrop, false));
                Assert(data.GetData(DataFormats.FileDrop, false) is string[] files && files.Single() == fixture);
            });
            Test("Private-only file drags omit external file formats", () =>
            {
                var data = new DesktopDrag([fixture]).Data(false);
                Assert(!data.GetDataPresent(DataFormats.FileDrop, false));
                Assert(DesktopDrag.Read(data).Single() == fixture);
            });
            Test("A real Recycle Bin PIDL resolves to a stable virtual identity", () =>
            {
                using var stream = new MemoryStream(RecycleBinCida());
                var data = new DataObject(); data.SetData(ShellDataTransfer.Format, stream);
                Assert(DesktopDrag.Read(data).Single() == RecycleBin);
            });
            Test("Reading Shell data restores the source stream position", () =>
            {
                using var stream = new MemoryStream(RecycleBinCida()); stream.Position = 7;
                var data = new DataObject(); data.SetData(ShellDataTransfer.Format, stream);
                Assert(DesktopDrag.Read(data).Single() == RecycleBin);
                Assert(stream.Position == 7);
            });
            Test("Reading drag data leaves original file bytes intact", () =>
            {
                var data = new DataObject(); data.SetData(DataFormats.FileDrop, new[] { fixture });
                Assert(DesktopDrag.Read(data).Single() == fixture);
                Assert(File.ReadAllText(fixture) == contents);
            });
        }
        finally
        {
            File.Delete(fixture);
            Directory.Delete(directory);
        }
        Console.WriteLine($"{_passed} passed; {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); _passed++; }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error.Message); _failed++; }
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }

    private static bool Valid(byte[] data) => ShellDataTransfer.TryGetPidlOffsets(data, out _);
    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    // Encodes the documented CIDA layout. Empty PIDLs are structural fixtures;
    // they are never passed to native Shell APIs by these validation tests.
    private static byte[] EmptyCida(int children)
    {
        var header = 4 + (children + 1) * 4;
        var data = new byte[header + (children + 1) * 2];
        WriteUInt32(data, 0, (uint)children);
        for (var i = 0; i <= children; i++) WriteUInt32(data, 4 + i * 4, (uint)(header + i * 2));
        return data;
    }

    private static byte[] RecycleBinCida()
    {
        Marshal.ThrowExceptionForHR(SHParseDisplayName(RecycleBin, 0, out var item, 0, out _));
        try
        {
            var size = checked((int)ILGetSize(item));
            if (size < 2 || size > 4 * 1024 * 1024 - 14) throw new InvalidDataException("Unexpected Shell PIDL size.");
            var data = new byte[14 + size];
            WriteUInt32(data, 0, 1); WriteUInt32(data, 4, 12); WriteUInt32(data, 8, 14);
            Marshal.Copy(item, data, 14, size);
            return data;
        }
        finally { Marshal.FreeCoTaskMem(item); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint context, out nint item, uint attributes, out uint result);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern uint ILGetSize(nint item);
}
