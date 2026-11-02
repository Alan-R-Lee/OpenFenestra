using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Threading;
using OpenFenestra.Core;
using Forms = System.Windows.Forms;

namespace OpenFenestra.App;

internal sealed class WorkspaceController : IDisposable
{
    internal Layout Layout { get; private set; }
    internal LayoutStore Store { get; }
    internal MainWindow Manager { get; }
    internal string Status { get; private set; } = "布局保存在本机 · 所有分组只记录文件引用";
    internal event Action? Changed;
    private readonly Dictionary<string, FenceWindow> _panels = [];
    private readonly List<FileSystemWatcher> _desktopWatchers = [];
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _organize = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _anchor = new() { Interval = TimeSpan.FromSeconds(2) };
    private Forms.NotifyIcon? _tray;
    private HwndSource? _source;
    private bool _disposed;
    private bool _peeking;
    internal bool Exiting { get; private set; }
    internal IEnumerable<FenceWindow> Panels => _panels.Values;
    internal static string[] DesktopPaths => new[] {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal WorkspaceController(LayoutStore store)
    {
        Store = store; Layout = Store.Load();
        Manager = new MainWindow(this);
        _save.Tick += (_, _) => { _save.Stop(); SaveNow(); };
        _organize.Tick += (_, _) => { _organize.Stop(); if (Layout.AutoOrganize) OrganizeDesktop(); };
        _anchor.Tick += (_, _) =>
        {
            if (Layout.PinToDesktop && !_peeking)
                foreach (var panel in _panels.Values.Where(p => p.IsVisible && !p.IsActive)) NativeDesktop.Anchor(panel);
        };
    }

    internal void Start(bool showManager)
    {
        var appIcon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;
        _tray = new Forms.NotifyIcon { Icon = appIcon ?? System.Drawing.SystemIcons.Application, Text = "OpenFenestra · 桌面，自有条理", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开管理窗口", null, (_, _) => ShowManager());
        menu.Items.Add("显示所有分组", null, (_, _) => ShowAll());
        menu.Items.Add("临时隐藏所有分组", null, (_, _) => { foreach (var p in Panels) p.Hide(); });
        menu.Items.Add("速览 / 取消置顶   Ctrl+Alt+Space", null, (_, _) => Peek());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 OpenFenestra", null, (_, _) => Exit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowManager();
        var hwnd = new WindowInteropHelper(Manager).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        if (!NativeDesktop.RegisterHotKey(hwnd, 7, 0x0001 | 0x0002 | 0x4000, 0x20))
            Status = "Ctrl+Alt+Space 已被其他程序占用，可通过托盘使用速览。";
        if (Store.RecoveredFromBackup) Status = "已从备份恢复布局；损坏的原文件已单独保留。";
        SyncPanels(); ConfigureAutoOrganize(); _anchor.Start();
        if (showManager) ShowManager();
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == 7) { Peek(); handled = true; }
        return 0;
    }

    internal void ShowManager() { Manager.Show(); Manager.WindowState = WindowState.Normal; Manager.Activate(); }
    internal void ShowAll()
    {
        foreach (var group in Layout.Groups) group.Visible = true;
        Notify("已显示所有分组");
    }
    internal void Peek()
    {
        _peeking = !_peeking;
        foreach (var panel in Panels)
        {
            if (!panel.Model.Visible) continue;
            panel.Topmost = _peeking;
            panel.Show();
            if (_peeking) panel.Activate(); else if (Layout.PinToDesktop) NativeDesktop.Anchor(panel);
        }
        Status = _peeking ? "速览已开启 · 再按 Ctrl+Alt+Space 取消置顶" : "速览已关闭";
        Changed?.Invoke();
    }

    internal void AddGroup(FenceGroup group)
    {
        if (Layout.Groups.Count >= 100) throw new InvalidOperationException("最多支持 100 个分组。");
        Layout.Groups.Add(group); Notify("已创建分组「" + group.Name + "」");
    }

    internal void RemoveGroup(FenceGroup group)
    {
        Store.Snapshot(Layout);
        Layout.Groups.Remove(group); Notify("已移除分组；原文件保留，可从快照恢复分组。");
    }

    internal void OrganizeDesktop()
    {
        var count = FileCatalog.Organize(Layout, DesktopPaths);
        Notify($"已归类 {count} 个新桌面项目 · 原文件位置保持不变");
    }

    internal void UpdateSettings(bool pin, bool auto)
    {
        Layout.PinToDesktop = pin; Layout.AutoOrganize = auto;
        ConfigureAutoOrganize(); Notify("设置已保存");
    }

    private void ConfigureAutoOrganize()
    {
        foreach (var watcher in _desktopWatchers) watcher.Dispose();
        _desktopWatchers.Clear();
        if (!Layout.AutoOrganize) return;
        foreach (var path in DesktopPaths)
        {
            try
            {
                var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                watcher.Created += OnDesktopChanged; watcher.Renamed += OnDesktopChanged;
                watcher.Error += (_, _) => QueueOrganize();
                watcher.EnableRaisingEvents = true; _desktopWatchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Report(e); }
        }
    }
    private void OnDesktopChanged(object sender, FileSystemEventArgs e) => QueueOrganize();
    private void QueueOrganize() => Application.Current.Dispatcher.BeginInvoke(new Action(() =>
    { if (!_disposed) { _organize.Stop(); _organize.Start(); } }));

    internal void Notify(string? status = null)
    {
        if (status is not null) Status = status;
        SyncPanels(); ScheduleSave(); Changed?.Invoke();
    }
    internal void ScheduleSave() { if (_disposed) return; _save.Stop(); _save.Start(); }
    internal void SaveNow()
    {
        try { Store.Save(Layout); }
        catch (Exception error)
        {
            Report(error); Status = "布局保存失败：" + error.Message; Changed?.Invoke();
        }
    }

    private void SyncPanels()
    {
        foreach (var obsolete in _panels.Keys.Where(k => Layout.Groups.All(g => g.Id != k)).ToArray())
        { _panels[obsolete].DisposePanel(); _panels.Remove(obsolete); }
        foreach (var group in Layout.Groups)
        {
            if (!_panels.TryGetValue(group.Id, out var panel))
            { panel = new FenceWindow(this, group); _panels.Add(group.Id, panel); }
            panel.Refresh();
            if (group.Visible) { if (!panel.IsVisible) panel.Show(); } else panel.Hide();
        }
    }

    internal void Restore(string path)
    {
        var next = LayoutStore.Read(path);
        Store.Snapshot(Layout);
        foreach (var panel in Panels.ToArray()) panel.DisposePanel();
        _panels.Clear(); Layout = next;
        ConfigureAutoOrganize(); Notify("布局已恢复 · 恢复前的布局已自动保存为快照");
    }

    internal void Report(Exception error)
    {
        try { Directory.CreateDirectory(Store.DirectoryPath); File.AppendAllText(Path.Combine(Store.DirectoryPath, "errors.log"), DateTimeOffset.Now + " " + error + Environment.NewLine); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(e); }
    }
    internal void Exit() { Exiting = true; SaveNow(); Application.Current.Shutdown(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Exiting = true; _save.Stop(); _organize.Stop(); _anchor.Stop();
        foreach (var watcher in _desktopWatchers) watcher.Dispose();
        if (_source is not null) { NativeDesktop.UnregisterHotKey(_source.Handle, 7); _source.RemoveHook(WndProc); }
        foreach (var panel in Panels.ToArray()) panel.DisposePanel();
        var icon = _tray?.Icon;
        _tray?.Dispose();
        if (icon is not null && icon != System.Drawing.SystemIcons.Application) icon.Dispose();
    }
}
