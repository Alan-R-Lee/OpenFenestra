using System.Threading;
using System.Windows.Threading;
using OpenFenestra.Core;

namespace OpenFenestra.App;

public partial class App : Application
{
    private Mutex? _instance;
    internal WorkspaceController? Workspace { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        var data = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenFenestra");
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..20];
        _instance = new Mutex(true, "Local\\OpenFenestra-" + id, out var first);
        if (!first) { MessageBox.Show("OpenFenestra 已在运行。请通过系统托盘打开管理窗口。", "OpenFenestra"); Shutdown(); return; }
        try
        {
            Workspace = new WorkspaceController(new LayoutStore(data));
            MainWindow = Workspace.Manager;
            Workspace.Start(!e.Args.Contains("--tray"));
            DispatcherUnhandledException += OnUnhandledException;
            if (e.Args.Contains("--smoke-test"))
            {
                if (dataIndex < 0) throw new ArgumentException("界面测试必须提供独立的 --data-dir。");
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => SmokeTest.Run(Workspace)));
            }
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
        _instance?.Dispose();
        base.OnExit(e);
    }
}
