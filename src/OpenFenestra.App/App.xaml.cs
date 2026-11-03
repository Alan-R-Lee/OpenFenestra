using System.Threading;
using System.Windows.Threading;
using OpenFenestra.Core;

namespace OpenFenestra.App;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activate, _quit;
    private RegisteredWaitHandle? _activateWait, _quitWait;
    internal WorkspaceController? Workspace { get; private set; }
    internal static bool IsPortable { get; private set; }
    internal static bool HasCustomDataDirectory { get; private set; }
    internal static string DataDirectory { get; private set; } = "";
    internal static bool DebugWindows { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--inspect-desktop")
        {
            var list = NativeDesktop.IconList();
            var bridge = NativeShellBridgeClient.Inspect();
            var snapshot = NativeDesktopShell.ReadSnapshot();
            File.WriteAllText(Path.GetFullPath(e.Args[1]), System.Text.Json.JsonSerializer.Serialize(new { hasDesktop = list != 0,
                nativeIconsVisible = NativeDesktop.HasVisibleStyle(list), bridgeConnected = bridge.Connected,
                bridgeCapabilities = bridge.Capabilities, hiddenCount = bridge.HiddenCount, nativeItemCount = snapshot.Items.Count,
                folderFlags = snapshot.FolderFlags, windows = NativeDesktop.Diagnostics(), items = snapshot.Items }));
            Shutdown(); return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--desktop-guard")
        {
            try { DesktopRecovery.RunGuard(Path.GetFullPath(e.Args[1]), e.Args[2]); }
            finally { Shutdown(); }
            return;
        }
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        DebugWindows = e.Args.Contains("--debug-windows");
        IsPortable = e.Args.Contains("--portable") || File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.mode"));
        HasCustomDataDirectory = dataIndex >= 0 && dataIndex + 1 < e.Args.Length;
        var data = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1])
            : IsPortable ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenFenestra");
        DataDirectory = data;
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..20];
        _instance = new Mutex(true, "Local\\OpenFenestra-" + id, out var first);
        if (!first)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting("Local\\OpenFenestra-" + (e.Args.Contains("--exit") ? "Quit-" : "Show-") + id);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("OpenFenestra 已在运行。请通过系统托盘打开管理窗口。", "OpenFenestra"); }
            Shutdown(); return;
        }
        if (e.Args.Contains("--exit")) { Shutdown(); return; }
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\OpenFenestra-Show-" + id);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\OpenFenestra-Quit-" + id);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
        { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => Workspace?.ShowManager())); }, null, Timeout.Infinite, false);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) =>
        { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => Workspace?.Exit())); }, null, Timeout.Infinite, false);
        try
        {
            if ((e.Args.Contains("--smoke-test") || e.Args.Contains("--integration-test")) && dataIndex < 0) throw new ArgumentException("界面测试必须提供独立的 --data-dir。");
            Workspace = new WorkspaceController(new LayoutStore(data));
            MainWindow = Workspace.Manager;
            Workspace.Start(!e.Args.Contains("--tray"), collapseOnStart: e.Args.Contains("--autostart"));
            if (e.Args.Contains("--enable-startup")) Workspace.SetStartup(true);
            if (e.Args.Contains("--desktop-mode")) Workspace.SetDesktopIntegration(true);
            if (e.Args.Contains("--organize")) Workspace.OrganizeDesktop();
            if (e.Args.Contains("--arrange")) Workspace.ArrangePanels();
            DispatcherUnhandledException += OnUnhandledException;
            if (e.Args.Contains("--smoke-test"))
            {
                if (dataIndex < 0) throw new ArgumentException("界面测试必须提供独立的 --data-dir。");
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => SmokeTest.Run(Workspace)));
            }
            if (e.Args.Contains("--integration-test"))
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => DesktopIntegrationTest.Run(Workspace, e.Args.Contains("--crash-test"))));
        }
        catch (Exception error)
        {
            MessageBox.Show("启动失败，现有布局文件已保留。\n\n" + error.Message, "OpenFenestra", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Workspace?.Report(e.Exception);
        MessageBox.Show(e.Exception.Message, "OpenFenestra", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Workspace?.Dispose();
        _activateWait?.Unregister(null); _quitWait?.Unregister(null);
        _activate?.Dispose(); _quit?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
