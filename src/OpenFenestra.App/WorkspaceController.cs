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
    private readonly Dictionary<nint, string> _pinErrors = [];
    private readonly HashSet<nint> _pinnedPanels = [];
    private readonly DesktopIntegration _desktop;
    internal bool IsDragging { get; private set; }
    internal DesktopIntegration Desktop => _desktop;
    internal bool Exiting { get; private set; }
    internal IEnumerable<FenceWindow> Panels => _panels.Values;
    internal static string[] DesktopPaths => new[] {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal WorkspaceController(LayoutStore store)
    {
        Store = store; DesktopRecovery.RecoverStale(DesktopRecovery.PathFor(store.DirectoryPath)); Layout = Store.Load();
        _desktop = new DesktopIntegration(this);
        Manager = new MainWindow(this);
        _save.Tick += (_, _) => { _save.Stop(); SaveNow(); };
        _organize.Tick += (_, _) => { _organize.Stop(); if (Layout.AutoOrganize) OrganizeDesktop(); };
        _anchor.Tick += (_, _) =>
        {
            _desktop.Tick();
            if (!_peeking) ApplyDesktopZOrder();
        };
    }

    internal void Start(bool showManager)
    {
        var appIcon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;
        _tray = new Forms.NotifyIcon { Icon = appIcon ?? System.Drawing.SystemIcons.Application, Text = "OpenFenestra · 桌面，自有条理", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开管理窗口", null, (_, _) => ShowManager());
        menu.Items.Add("显示所有分组", null, (_, _) => ShowAll());
        menu.Items.Add("整理分栏位置", null, (_, _) => ArrangePanels());
        menu.Items.Add("折叠所有分栏", null, (_, _) => CollapseAll(true));
        menu.Items.Add("展开所有分栏", null, (_, _) => CollapseAll(false));
        menu.Items.Add("临时隐藏所有分组", null, (_, _) => { foreach (var p in Panels) p.Hide(); });
        menu.Items.Add("速览 / 取消置顶   Ctrl+Alt+Space", null, (_, _) => Peek());
        menu.Items.Add("恢复 Windows 原生桌面图标", null, (_, _) => SetDesktopIntegration(false));
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
        SyncPanels();
        // Render the saved layout before Shell enumeration/bridge setup or registration.
        // Explorer may still be creating its desktop when a logon task starts us.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_disposed) return;
            try { _ = ShellFiles.DesktopItems(); } catch (Exception error) { Report(error); }
            ConfigureAutoOrganize(); ConfigureDesktopIntegration(retryOnFailure: true); _anchor.Start();
            if (Layout.StartWithWindows)
            {
                try { StartupRegistration.SetEnabled(true); }
                catch (Exception error) { Report(error); }
            }
            SaveNow();
        }));
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
            if (_peeking) panel.Activate();
        }
        Status = _peeking ? "速览已开启 · 再按 Ctrl+Alt+Space 取消置顶" : "速览已关闭";
        if (!_peeking) ApplyDesktopZOrder();
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
        foreach (var path in group.Tabs.Where(t => !t.IsPortal).SelectMany(t => t.Items))
            if (!Layout.DesktopItems.Contains(path, StringComparer.OrdinalIgnoreCase)) Layout.DesktopItems.Add(path);
        Layout.Groups.Remove(group); Notify("已移除分组；原文件保留，可从快照恢复分组。");
    }

    internal void OrganizeDesktop()
    {
        Store.Snapshot(Layout);
        var count = FileCatalog.OrganizeEntries(Layout, ShellFiles.DesktopItems());
        Notify($"已归类 {count} 个新桌面项目 · 原文件位置保持不变");
    }

    internal void UpdateSettings(bool pin, bool auto)
    {
        Layout.PinToDesktop = pin; Layout.AutoOrganize = auto;
        ConfigureAutoOrganize(); Notify("设置已保存");
    }

    internal void SetDesktopIntegration(bool enabled)
    {
        Store.Snapshot(Layout);
        Layout.DesktopIntegrationEnabled = enabled;
        if (enabled) Layout.PinToDesktop = true;
        ConfigureDesktopIntegration();
        Notify(Layout.DesktopIntegrationEnabled ? "原生桌面整合已开启 · 已收纳图标只在分栏中绘制" : enabled ? Status : "已恢复所有 Windows 原生桌面图标");
    }
    internal void SetStartup(bool enabled)
    {
        StartupRegistration.SetEnabled(enabled); Layout.StartWithWindows = enabled;
        Notify(enabled ? StartupRegistration.LastFallbackReason is null
            ? "已开启登录后立即启动，并恢复上次布局"
            : "系统未允许登录任务，已使用兼容自启动方式（可能被 Windows 延迟）"
            : "已关闭开机自动运行");
    }
    private void ConfigureDesktopIntegration(bool retryOnFailure = false)
    {
        if (!Layout.DesktopIntegrationEnabled) { _desktop.Dispose(); return; }
        try { _desktop.Enable(); }
        catch (Exception error)
        {
            Report(error);
            if (!retryOnFailure) Layout.DesktopIntegrationEnabled = false;
            Status = retryOnFailure ? "正在等待 Explorer 桌面就绪，将自动连接。" : "原生桌面整合未开启：" + error.Message;
        }
    }
    internal void ApplyDesktopZOrder()
    {
        if (!Layout.PinToDesktop && !_desktop.Enabled) return;
        var windows = _panels.Values.Where(p => p.IsVisible && !p.Topmost).Cast<Window>().ToArray();
        NativeDesktop.AnchorWindows(windows);
        foreach (var panel in windows)
        {
            var handle = new WindowInteropHelper(panel).Handle;
            if (VirtualDesktopPinning.TryPin(handle, out var error)) { _pinErrors.Remove(handle); _pinnedPanels.Add(handle); }
            else if (error is not null && (!_pinErrors.TryGetValue(handle, out var previous) || previous != error))
            { _pinnedPanels.Remove(handle); _pinErrors[handle] = error; Report(new InvalidOperationException(error)); }
        }
    }
    internal void Collect(FenceTab tab, IEnumerable<string> paths)
    {
        var count = DesktopAssignments.Collect(Layout, tab, paths);
        Notify($"已收纳 {count} 个图标 · 原文件位置保持不变");
    }
    internal void ReturnToDesktop(IEnumerable<string> paths, DesktopPosition position)
    {
        var items = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var native = NativeDesktopShell.ReadSnapshot().Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        // A reference to a file outside the Desktop needs a real desktop link.
        // Never pretend that an entry in our JSON creates an Explorer icon.
        var desktopItems = items.Select(path => native.ContainsKey(path) ? path : CreateDesktopShortcut(path)).ToArray();
        var count = DesktopAssignments.ReturnToDesktop(Layout, items, position);
        for (var index = 0; index < desktopItems.Length; index++)
            Layout.DesktopPositions[desktopItems[index]] = new(position.X + index * 90, position.Y);
        Notify($"已将 {count} 个图标放回原生桌面");
        var destinations = desktopItems.Select((path, index) => (path, point: NativeDesktopShell.ScreenToView(VisibleDesktopPoint(position.X + index * 90, position.Y))))
            .ToDictionary(item => item.path, item => item.point, StringComparer.OrdinalIgnoreCase);
        NativeDesktopShell.PositionItems(destinations);
    }
    internal void DragItems(DependencyObject source, string[] paths)
    {
        if (paths.Length == 0) return;
        IsDragging = true;
        NativeDesktopPoint? desktopDrop = null;
        QueryContinueDragEventHandler continuation = (_, e) =>
        {
            if (e.EscapePressed) { desktopDrop = null; e.Action = DragAction.Cancel; return; }
            if ((e.KeyStates & DragDropKeyStates.LeftMouseButton) != 0) return;
            var cursor = Forms.Cursor.Position;
            var point = new NativeDesktopPoint(cursor.X, cursor.Y);
            if (!NativeDesktopShell.IsDesktopAtScreenPoint(point)) return;
            desktopDrop = point; e.Action = DragAction.Cancel; e.Handled = true;
        };
        DragDrop.AddQueryContinueDragHandler(source, continuation);
        try { DragDrop.DoDragDrop(source, new DesktopDrag(paths).Data(false), DragDropEffects.Link | DragDropEffects.Copy); }
        finally { IsDragging = false; DragDrop.RemoveQueryContinueDragHandler(source, continuation); }
        if (desktopDrop is { } drop) ReturnToDesktop(paths, new(drop.X, drop.Y));
    }

    private static NativeDesktopPoint VisibleDesktopPoint(double x, double y)
    {
        var candidate = new System.Drawing.Point((int)x, (int)y);
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.Contains(candidate)) ?? Forms.Screen.PrimaryScreen!;
        return new(Math.Clamp(candidate.X, screen.WorkingArea.Left + 12, screen.WorkingArea.Right - 90),
            Math.Clamp(candidate.Y, screen.WorkingArea.Top + 12, screen.WorkingArea.Bottom - 100));
    }

    private string CreateDesktopShortcut(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("原项目不可用，未移除分栏引用。", path);
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var extension = Path.GetExtension(path);
        var copyLink = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
        var name = Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var suffix = copyLink ? extension : ".lnk";
        var staging = Path.Combine(Store.DirectoryPath, "shortcut-staging");
        Directory.CreateDirectory(staging);
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + suffix);
        object? shell = null, link = null;
        try
        {
            if (copyLink) File.Copy(path, temporary, false);
            else
            {
                var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("无法创建桌面快捷方式。");
                shell = Activator.CreateInstance(type); link = ((dynamic)shell!).CreateShortcut(temporary);
                ((dynamic)link).TargetPath = path; ((dynamic)link).Save();
            }
            for (var index = 1; index <= 1000; index++)
            {
                var target = Path.Combine(directory, name + (index == 1 ? "" : " (" + index + ")") + suffix);
                try { File.Move(temporary, target, false); return target; }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
            }
            throw new IOException("桌面同名快捷方式过多，未覆盖任何已有项目。");
        }
        finally
        {
            if (link is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
            if (shell is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException error) { System.Diagnostics.Debug.WriteLine(error); }
        }
    }

    internal void ArrangePanels()
    {
        Store.Snapshot(Layout);
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(Manager);
        var width = area.Width / dpi.DpiScaleX; var height = area.Height / dpi.DpiScaleY;
        var columns = Math.Clamp((int)((width - 32) / 310), 1, Math.Max(1, Math.Min(4, Layout.Groups.Count)));
        var rows = Math.Max(1, (int)Math.Ceiling((double)Layout.Groups.Count / columns));
        var panelWidth = Math.Clamp((width - (columns + 1) * 16) / columns, 260, 420);
        var panelHeight = Math.Clamp((height - 56 - (rows + 1) * 16) / rows, 180, 420);
        for (var index = 0; index < Layout.Groups.Count; index++)
        {
            var group = Layout.Groups[index];
            group.X = area.Left / dpi.DpiScaleX + 16 + index % columns * (panelWidth + 16);
            group.Y = area.Top / dpi.DpiScaleY + 32 + index / columns * (panelHeight + 16);
            group.Width = panelWidth; group.Height = panelHeight; group.Visible = true;
        }
        Notify("已按屏幕可用空间排列分栏，原分类和标签页保留");
    }

    internal void ReportStatus(string status) { Status = status; Changed?.Invoke(); }

    internal void CollapseAll(bool collapsed)
    {
        foreach (var group in Layout.Groups) group.Collapsed = collapsed;
        Notify(collapsed ? "已折叠所有分栏；单击栏头展开" : "已展开所有分栏");
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
        SyncPanels(); _desktop.Refresh(); ApplyDesktopZOrder(); ScheduleSave(); Changed?.Invoke();
    }
    internal void ScheduleSave() { if (_disposed) return; _save.Stop(); _save.Start(); }
    internal void SaveNow()
    {
        try
        {
            Store.Save(Layout);
            try
            {
                var runtime = new { processId = Environment.ProcessId, packageIdentity = RuntimePaths.PackageIdentity,
                    executable = Environment.ProcessPath, nativeEnabled = _desktop.Enabled,
                    hiddenCount = _desktop.HiddenCount, nativeCapabilities = _desktop.Capabilities,
                    visiblePanels = Panels.Count(p => p.IsVisible),
                    desktopOwnedPanels = Panels.Count(p => NativeDesktop.IsDesktopOwned(p)),
                    pinnedPanels = Panels.Count(p => _pinnedPanels.Contains(new WindowInteropHelper(p).Handle)),
                    startupEnabled = StartupRegistration.IsEnabled(), updated = DateTimeOffset.Now };
                File.WriteAllText(Path.Combine(Store.DirectoryPath, "runtime-state.json"), System.Text.Json.JsonSerializer.Serialize(runtime));
            }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }
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
        ConfigureAutoOrganize(); ConfigureDesktopIntegration(); Notify("布局已恢复 · 恢复前的布局已自动保存为快照");
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
        SaveNow(); // Windows logoff/shutdown must also flush the debounce queue.
        _disposed = true; Exiting = true; _save.Stop(); _organize.Stop(); _anchor.Stop();
        _desktop.Dispose();
        foreach (var watcher in _desktopWatchers) watcher.Dispose();
        if (_source is not null) { NativeDesktop.UnregisterHotKey(_source.Handle, 7); _source.RemoveHook(WndProc); }
        foreach (var panel in Panels.ToArray()) panel.DisposePanel();
        var icon = _tray?.Icon;
        _tray?.Dispose();
        if (icon is not null && icon != System.Drawing.SystemIcons.Application) icon.Dispose();
    }
}
