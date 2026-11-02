using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace OpenFenestra.App;

/// <summary>Controller for our native, fail-open Explorer drawing bridge.</summary>
internal sealed class NativeShellBridgeClient(nint owner) : IDisposable
{
    private const string WindowClass = "OpenFenestra.NativeShellBridge.v1";
    private const uint ExpectedStatus = 0x4F460100;
    private const uint RequiredCapabilities = 15;
    private static readonly uint StatusMessage = RegisterWindowMessage("OpenFenestra.NativeShellBridge.Status.v1");
    private static readonly uint HiddenCountMessage = RegisterWindowMessage("OpenFenestra.NativeShellBridge.HiddenCount.v1");
    private static readonly uint BootstrapMessage = RegisterWindowMessage("OpenFenestra.NativeShellBridge.Bootstrap.v1");
    private static readonly uint BootstrapReply = RegisterWindowMessage("OpenFenestra.NativeShellBridge.BootstrapReply.v1");
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private nint _window;
    private uint _shellProcessId;
    private bool _disposed;
    internal bool Connected => !_disposed && _window != 0 && IsWindow(_window);
    internal int HiddenCount => Connected ? checked((int)Query(HiddenCountMessage)) : 0;
    internal uint Capabilities => Connected ? (uint)Query(StatusMessage) & 0xFF : 0;

    internal void Connect()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || Environment.OSVersion.Version.Build < 22000)
            throw new NotSupportedException("原生桌面整合目前仅支持 Windows 11 x64；已保留原生桌面。");
        var list = NativeDesktop.IconList();
        if (list == 0) throw new InvalidOperationException("Explorer 桌面视图尚未就绪。");
        var thread = GetWindowThreadProcessId(list, out _shellProcessId);
        using (var shell = Process.GetProcessById(checked((int)_shellProcessId)))
        {
            if (!shell.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("桌面视图不属于 Explorer，未安装组件。");
        }
        nint library = 0, hook = 0;
        try
        {
            var libraryPath = CacheLibrary();
            _window = FindBridgeWindow();
            if (_window == 0)
            {
                library = LoadLibrary(libraryPath);
                if (library == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法加载原生桌面组件。");
                var procedure = GetProcAddress(library, "OpenFenestraHookProc");
                if (procedure == 0) throw new InvalidOperationException("原生桌面组件版本不匹配。");
                hook = SetWindowsHookEx(3, procedure, library, thread);
                if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法接入 Explorer 桌面线程。");
                var timer = Stopwatch.StartNew();
                var nonce = RandomNumberGenerator.GetInt32(1, 65535);
                var stages = new List<string>();
                using var controller = Process.GetCurrentProcess();
                using var explorer = Process.GetProcessById(checked((int)_shellProcessId));
                IsWow64Process2(controller.Handle, out var controllerMachine, out var controllerNative);
                IsWow64Process2(explorer.Handle, out var explorerMachine, out var explorerNative);
                stages.Add($"machines={controllerMachine:X4}/{controllerNative:X4},{explorerMachine:X4}/{explorerNative:X4}");
                while (timer.Elapsed < TimeSpan.FromSeconds(5))
                {
                    PostMessage(list, 0, 0, 0);
                    if (!PostMessage(list, BootstrapMessage, owner, nonce))
                        stages.Add("post-error=" + Marshal.GetLastWin32Error());
                    PostThreadMessage(thread, BootstrapMessage, owner, nonce);
                    while (PeekMessage(out var message, owner, BootstrapReply, BootstrapReply, 1))
                    {
                        if ((message.WParam.ToUInt64() >> 16) != (uint)nonce) continue;
                        var stage = $"{message.WParam.ToUInt64() & 0xFFFF}:{message.LParam.ToInt64()}";
                        if (!stages.Contains(stage)) stages.Add(stage);
                    }
                    _window = FindBridgeWindow();
                    if (_window != 0) break;
                    Thread.Sleep(40);
                }
                if (_window == 0) throw new InvalidOperationException("Explorer 未响应原生组件初始化；原图标未隐藏。Bootstrap=" + string.Join(",", stages));
            }
            var status = (uint)Query(StatusMessage);
            if ((status & 0xFFFFFF00) != ExpectedStatus) throw new InvalidOperationException("原生桌面协议版本不匹配。");
            Send(4, [Path.GetFileName(Path.GetDirectoryName(libraryPath)) ?? throw new InvalidOperationException("无法识别原生组件版本。")]);
            Update([]);
            var ready = Stopwatch.StartNew();
            while ((Capabilities & RequiredCapabilities) != RequiredCapabilities && ready.Elapsed < TimeSpan.FromSeconds(5))
            { Heartbeat(); PostMessage(list, 0, 0, 0); Thread.Sleep(80); }
            if ((Capabilities & RequiredCapabilities) != RequiredCapabilities)
                throw new InvalidOperationException("原生桌面的绘制、选择或拖放保护尚未就绪，未隐藏图标。");
        }
        catch { Dispose(); throw; }
        finally
        {
            if (hook != 0) UnhookWindowsHookEx(hook);
            if (library != 0) FreeLibrary(library);
        }
    }

    internal void Update(IEnumerable<string> assigned)
    {
        var paths = assigned.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > 10000) throw new InvalidOperationException("原生桌面最多支持 10,000 个收纳引用。");
        Send(1, new[] { Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            owner.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), _token }.Concat(paths));
    }
    internal void Heartbeat() => Send(2, [_token]);

    internal static (bool Connected, uint Capabilities, int HiddenCount) Inspect()
    {
        var list = NativeDesktop.IconList();
        if (list == 0) return (false, 0, 0);
        GetWindowThreadProcessId(list, out var shellPid);
        nint candidate = 0;
        while ((candidate = FindWindowEx(new(-3), candidate, WindowClass, null)) != 0)
        {
            GetWindowThreadProcessId(candidate, out var pid);
            if (pid != shellPid) continue;
            if (SendMessageTimeout(candidate, StatusMessage, 0, 0, 0x0003, 1500, out var status) == 0) return (false, 0, 0);
            if (SendMessageTimeout(candidate, HiddenCountMessage, 0, 0, 0x0003, 1500, out var hidden) == 0) return (false, 0, 0);
            return (true, (uint)status & 0xFF, checked((int)hidden));
        }
        return (false, 0, 0);
    }

    private nint FindBridgeWindow()
    {
        nint candidate = 0;
        while ((candidate = FindWindowEx(new(-3), candidate, WindowClass, null)) != 0)
        { GetWindowThreadProcessId(candidate, out var process); if (process == _shellProcessId) return candidate; }
        return 0;
    }
    private nuint Query(uint message)
    {
        if (_window == 0 || SendMessageTimeout(_window, message, 0, 0, 0x0003, 1500, out var result) == 0)
            throw new InvalidOperationException("Explorer 原生组件响应超时；恢复原生显示后可重试。");
        return result;
    }
    private void Send(nuint command, IEnumerable<string> values)
    {
        var payload = Encoding.Unicode.GetBytes(string.Join('\0', values) + "\0\0");
        if (payload.Length > 2 * 1024 * 1024) throw new InvalidOperationException("桌面图标信息超过组件容量。");
        var dataPointer = Marshal.AllocHGlobal(payload.Length);
        var structurePointer = Marshal.AllocHGlobal(Marshal.SizeOf<CopyData>());
        try
        {
            Marshal.Copy(payload, 0, dataPointer, payload.Length);
            Marshal.StructureToPtr(new CopyData { Command = command, Length = payload.Length, Data = dataPointer }, structurePointer, false);
            if (_window == 0 || SendMessageTimeout(_window, 0x004A, owner, structurePointer, 0x0003, 2000, out var accepted) == 0 || accepted == 0)
                throw new InvalidOperationException("原生桌面组件未接受请求，未改变文件。");
        }
        finally { Marshal.FreeHGlobal(structurePointer); Marshal.FreeHGlobal(dataPointer); }
    }
    private static string CacheLibrary()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "FenestraShellBridge.dll");
        if (!File.Exists(source)) throw new FileNotFoundException("缺少原生组件，请使用包含 DLL 的完整便携包。", source);
        using var input = File.OpenRead(source);
        var hash = Convert.ToHexString(SHA256.HashData(input));
        // Keep the installed/portable DLL free for updates if Explorer retains it.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenFenestra", "ShellBridgeCache", hash);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "FenestraShellBridge.dll");
        if (!File.Exists(target)) File.Copy(source, target, false);
        using var cached = File.OpenRead(target);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(cached), Convert.FromHexString(hash)))
            throw new InvalidDataException("原生组件缓存校验失败。");
        // A packaged development host may transparently redirect AppData.
        // Explorer is outside that package and needs the backing file's path.
        var physical = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(cached.SafeFileHandle, physical, (uint)physical.Capacity, 0);
        if (length == 0 || length >= physical.Capacity)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法解析原生组件的实际路径。");
        return physical.ToString();
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_window != 0 && IsWindow(_window))
        {
            try { Send(3, [_token]); }
            catch (Exception error) { Debug.WriteLine(error); }
        }
        _window = 0; _disposed = true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CopyData { internal nuint Command; internal int Length; internal nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    { internal nint Hwnd; internal uint Message; internal nuint WParam; internal nint LParam; internal uint Time; internal int X, Y; internal uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", ExactSpelling = true, SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, nint procedure, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, nint hwnd, uint minimum, uint maximum, uint remove);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
