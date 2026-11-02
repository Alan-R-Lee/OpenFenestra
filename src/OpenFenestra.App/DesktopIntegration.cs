using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using OpenFenestra.Core;
using Forms = System.Windows.Forms;

namespace OpenFenestra.App;

internal sealed class DesktopIntegration(WorkspaceController workspace) : IDisposable
{
    private Mutex? _lease;
    private DesktopRecoveryRecord? _recovery;
    private readonly List<DesktopSurfaceWindow> _surfaces = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IReadOnlyList<DesktopEntry> _shellItems = [];
    private string _monitorKey = "";
    internal bool Enabled => _recovery is not null;
    internal IEnumerable<DesktopSurfaceWindow> Surfaces => _surfaces;
    internal IReadOnlyList<DesktopEntry> VisibleItems => DesktopAssignments.VisibleDesktop(workspace.Layout, _shellItems);

    internal void Enable()
    {
        if (Enabled) return;
        var recoveryPath = DesktopRecovery.PathFor(workspace.Store.DirectoryPath);
        Directory.CreateDirectory(workspace.Store.DirectoryPath);
        DesktopRecovery.RecoverStale(recoveryPath);
        _lease = new Mutex(false, "Local\\OpenFenestra-DesktopSurface");
        var acquired = false;
        try { acquired = _lease.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { _lease.Dispose(); _lease = null; throw new InvalidOperationException("另一个 OpenFenestra 实例正在管理桌面。"); }
        try
        {
            var list = NativeDesktop.IconList();
            if (list == 0) throw new InvalidOperationException("找不到 Explorer 桌面，未改变原图标显示。");
            using var process = Process.GetCurrentProcess();
            _recovery = new(Guid.NewGuid().ToString("N"), process.Id, process.StartTime.ToUniversalTime().Ticks, NativeDesktop.HasVisibleStyle(list));
            _shellItems = ShellFiles.DesktopItems();
            CreateSurfaces();
            File.WriteAllText(recoveryPath, JsonSerializer.Serialize(_recovery));
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定桌面恢复守护进程路径。");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--desktop-guard"); start.ArgumentList.Add(recoveryPath); start.ArgumentList.Add(_recovery.Token);
            using var guard = Process.Start(start) ?? throw new InvalidOperationException("无法启动桌面恢复守护进程。");
            if (!NativeDesktop.SetNativeIconsVisible(false)) throw new InvalidOperationException("无法切换桌面显示，已保留原图标。");
            _refresh.Tick += OnRefresh;
            foreach (var path in WorkspaceController.DesktopPaths)
            {
                var watcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                watcher.Created += OnDesktopChanged; watcher.Deleted += OnDesktopChanged; watcher.Renamed += OnDesktopChanged;
                watcher.Changed += OnDesktopChanged; watcher.Error += (_, _) => QueueRefresh();
                watcher.EnableRaisingEvents = true; _watchers.Add(watcher);
            }
            Refresh(); workspace.ApplyDesktopZOrder();
        }
        catch { Dispose(); throw; }
    }

    private void CreateSurfaces()
    {
        foreach (var surface in _surfaces) surface.DisposeSurface();
        _surfaces.Clear();
        foreach (var screen in Forms.Screen.AllScreens)
        { var surface = new DesktopSurfaceWindow(workspace, screen.WorkingArea); _surfaces.Add(surface); surface.Show(); }
        _monitorKey = MonitorKey();
    }
    private static string MonitorKey() => string.Join(";", Forms.Screen.AllScreens.Select(s => s.WorkingArea.ToString()));
    private void OnDesktopChanged(object sender, FileSystemEventArgs e) => QueueRefresh();
    private void QueueRefresh() => Application.Current.Dispatcher.BeginInvoke(new Action(() => { if (Enabled) { _refresh.Stop(); _refresh.Start(); } }));
    private void OnRefresh(object? sender, EventArgs e) { _refresh.Stop(); if (Enabled) { _shellItems = ShellFiles.DesktopItems(); Refresh(); } }

    internal void Refresh()
    {
        if (!Enabled) return;
        var visible = VisibleItems;
        var screens = Forms.Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
        if (screens.Length == 0) return;
        var primary = Forms.Screen.PrimaryScreen?.WorkingArea ?? screens[0];
        var scale = VisualTreeHelper.GetDpi(_surfaces[0]).DpiScaleX;
        var rowHeight = (int)Math.Round(112 * scale); var columnWidth = (int)Math.Round(104 * scale);
        var rows = Math.Max(1, (primary.Height - 40) / rowHeight);
        var used = workspace.Layout.DesktopPositions.Values.Select(p => (int)(p.X / columnWidth) + ":" + (int)(p.Y / rowHeight)).ToHashSet();
        var slot = 0;
        foreach (var entry in visible)
        {
            if (workspace.Layout.DesktopPositions.TryGetValue(entry.Path, out var p) && screens.Any(s => s.Contains((int)p.X, (int)p.Y))) continue;
            DesktopPosition position;
            do
            {
                position = new(primary.Left + 12 * scale + slot / rows * columnWidth, primary.Top + 12 * scale + slot % rows * rowHeight); slot++;
            } while (used.Contains((int)(position.X / columnWidth) + ":" + (int)(position.Y / rowHeight)) && slot < 10000);
            workspace.Layout.DesktopPositions[entry.Path] = position;
            used.Add((int)(position.X / columnWidth) + ":" + (int)(position.Y / rowHeight));
        }
        foreach (var surface in _surfaces) { surface.UpdateDragSurface(); surface.RenderItems(visible); }
        workspace.ScheduleSave();
    }

    internal void Tick()
    {
        if (!Enabled) return;
        if (_monitorKey != MonitorKey()) { CreateSurfaces(); Refresh(); }
        var list = NativeDesktop.IconList();
        if (list != 0 && NativeDesktop.HasVisibleStyle(list)) NativeDesktop.SetNativeIconsVisible(false);
    }
    internal void DragChanged() { foreach (var surface in _surfaces) surface.UpdateDragSurface(); }

    public void Dispose()
    {
        _refresh.Stop(); _refresh.Tick -= OnRefresh;
        foreach (var watcher in _watchers) watcher.Dispose(); _watchers.Clear();
        foreach (var surface in _surfaces) surface.DisposeSurface(); _surfaces.Clear();
        if (_recovery is not null)
        {
            NativeDesktop.SetNativeIconsVisible(_recovery.WasVisible);
            var path = DesktopRecovery.PathFor(workspace.Store.DirectoryPath);
            if (DesktopRecovery.Read(path)?.Token == _recovery.Token) File.Delete(path);
            _recovery = null;
        }
        if (_lease is not null) { _lease.ReleaseMutex(); _lease.Dispose(); _lease = null; }
    }
}
