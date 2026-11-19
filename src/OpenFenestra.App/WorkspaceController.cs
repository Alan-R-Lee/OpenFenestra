using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Threading;
using OpenFenestra.Core;
using Forms = System.Windows.Forms;

namespace OpenFenestra.App;

internal sealed record OrganizationDestination(FenceGroup Group, FenceTab Tab, string[] Paths);

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
    private bool _anchorQueued;
    private bool _peeking;
    private bool _captureDesktop = true;
    private DateTime _nextRecentScan;
    private string? _locatedPanelId;
    private ShelfWindow? _shelf;
    internal ShelfWindow? Shelf => _shelf;
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
        _organize.Tick += (_, _) => { _organize.Stop(); RefreshRecent(); };
        _anchor.Tick += (_, _) =>
        {
            _desktop.Tick();
            if (_captureDesktop && DateTime.UtcNow >= _nextRecentScan) RefreshRecent();
            if (!_peeking) ApplyDesktopZOrder();
        };
    }

    internal void Start(bool showManager, bool collapseOnStart = false, bool captureDesktop = true)
    {
        _captureDesktop = captureDesktop;
        if (_captureDesktop) RecentItems.Ensure(Layout);
        // Apply before creating windows so expanded content never flashes at logon.
        // Keep each group's expanded height for the user's next click.
        if (collapseOnStart)
            foreach (var group in Layout.Groups) group.Collapsed = true;
        var appIcon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;
        _tray = new Forms.NotifyIcon { Icon = appIcon ?? System.Drawing.SystemIcons.Application, Text = "OpenFenestra · 桌面，自有条理", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开管理窗口", null, (_, _) => ShowManager());
        menu.Items.Add("显示所有分组", null, (_, _) => ShowAll());
        menu.Items.Add("整理分栏位置", null, (_, _) => ArrangePanels());
        menu.Items.Add("切换折叠栏架", null, (_, _) => SetArrangement(PanelArrangementMode.Shelf));
        menu.Items.Add("恢复自由排布", null, (_, _) => SetArrangement(PanelArrangementMode.Free));
        menu.Items.Add("折叠所有分栏", null, (_, _) => CollapseAll(true));
        menu.Items.Add("展开所有分栏", null, (_, _) => CollapseAll(false));
        menu.Items.Add("临时隐藏所有分组", null, (_, _) => { foreach (var p in Panels) p.Hide(); _shelf?.Hide(); });
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
            try { RefreshRecent(); } catch (Exception error) { Report(error); }
            foreach (var panel in Panels) panel.RefreshNames();
            ConfigureAutoOrganize(); ConfigureDesktopIntegration(retryOnFailure: true); _anchor.Start();
            if (Layout.StartWithWindows)
            {
                try
                {
                    StartupRegistration.SetEnabled(true);
                    if (StartupRegistration.LastFallbackReason is not null)
                        ReportStatus("系统未允许登录任务，已使用兼容自启动方式（可能被 Windows 延迟）");
                }
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
            if (!panel.Model.Visible || (Layout.Arrangement == PanelArrangementMode.Shelf && panel.Model.Collapsed)) continue;
            panel.Topmost = _peeking;
            panel.Show();
            if (_peeking) panel.Activate();
        }
        if (_shelf?.IsVisible == true) _shelf.Topmost = _peeking;
        Status = _peeking ? "速览已开启 · 再按 Ctrl+Alt+Space 取消置顶" : "速览已关闭";
        if (!_peeking) ApplyDesktopZOrder();
        Changed?.Invoke();
    }

    internal void AddGroup(FenceGroup group)
    {
        if (Layout.Groups.Count >= 100) throw new InvalidOperationException("最多支持 100 个分组。");
        Layout.Groups.Add(group); Notify("已创建分组「" + group.Name + "」");
    }

    internal void ReorderGroup(string sourceId, string targetId, bool insertAfter)
    {
        if (!Layout.ReorderGroup(sourceId, targetId, insertAfter)) return;
        Status = "分组顺序已保存；整理分栏位置时将按此顺序排列。";
        ScheduleSave(); Changed?.Invoke();
    }

    internal void RemoveGroup(FenceGroup group)
    {
        if (group.IsRecent) { ReportStatus("最近分栏用于接收新项目，可通过隐藏分组暂时隐藏。"); return; }
        Store.Snapshot(Layout);
        foreach (var path in group.Tabs.Where(t => !t.IsPortal).SelectMany(t => t.Items))
            if (!Layout.DesktopItems.Contains(path, StringComparer.OrdinalIgnoreCase)) Layout.DesktopItems.Add(path);
        Layout.Groups.Remove(group); Notify("已移除分组；原文件保留，可从快照恢复分组。");
    }

    internal void OrganizeDesktop()
    {
        Store.Snapshot(Layout);
        var count = 0;
        foreach (var recent in Layout.Groups.Where(g => g.IsRecent).ToArray())
            count += RecentItems.Organize(Layout, recent);
        count += FileCatalog.OrganizeEntries(Layout, ShellFiles.DesktopItems());
        Notify($"已归类 {count} 个新桌面项目 · 原文件位置保持不变");
    }

    internal void UpdateSettings(bool pin, bool auto)
    {
        Layout.PinToDesktop = pin; Layout.AutoOrganize = auto;
        ConfigureAutoOrganize(); Notify("设置已保存");
    }

    internal void OrganizeRecent(FenceGroup recent)
    {
        Store.Snapshot(Layout);
        var moved = recent.Tabs.Where(t => !t.IsPortal).SelectMany(t => t.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = RecentItems.Organize(Layout, recent);
        moved.ExceptWith(recent.Tabs.SelectMany(t => t.Items));
        var destinations = Layout.Groups.Where(g => !g.IsRecent)
            .SelectMany(g => g.Tabs.Where(t => !t.IsPortal)
                .Select(t => new OrganizationDestination(g, t, t.Items.Where(moved.Contains).ToArray())))
            .Where(d => d.Paths.Length > 0).ToArray();
        foreach (var destination in destinations)
        {
            destination.Group.Visible = true;
            destination.Group.Collapsed = false;
            destination.Group.SelectedTabId = destination.Tab.Id;
        }
        foreach (var panel in Panels) panel.ClearLocatedItems();
        _locatedPanelId = destinations.FirstOrDefault()?.Group.Id;
        Notify(count > 0 ? $"已归类最近分栏中的 {count} 个项目 · 点击最近分栏中的去向可再次定位" : "最近分栏中暂无可归类的项目");
        foreach (var destination in destinations) _panels[destination.Group.Id].LocateItems(destination.Tab, destination.Paths);
        if (_panels.TryGetValue(recent.Id, out var inbox)) inbox.ShowOrganizationResults(destinations);
        if (destinations.Length > 0) _panels[destinations[0].Group.Id].Activate();
    }

    internal void LocateDestination(OrganizationDestination destination)
    {
        var group = Layout.Groups.FirstOrDefault(g => g.Id == destination.Group.Id);
        var tab = group?.Tabs.FirstOrDefault(t => t.Id == destination.Tab.Id && !t.IsPortal);
        var paths = tab is null ? [] : destination.Paths.Where(tab.Items.ToHashSet(StringComparer.OrdinalIgnoreCase).Contains).ToArray();
        if (group is null || tab is null || paths.Length == 0)
        { ReportStatus("这些项目的归属已变化，请查看当前分组。"); return; }
        group.Visible = true; group.Collapsed = false; group.SelectedTabId = tab.Id;
        _locatedPanelId = group.Id;
        Notify($"已定位：{group.Name} / {tab.Name} · {paths.Length} 个项目");
        _panels[group.Id].LocateItems(tab, paths);
        _panels[group.Id].Activate();
    }

    private void RefreshRecent()
    {
        if (_disposed || !_captureDesktop) return;
        if (IsDragging) { QueueOrganize(); return; }
        _nextRecentScan = DateTime.UtcNow.AddSeconds(10);
        try
        {
            CaptureRecent(ShellFiles.DesktopItemsForCapture());
        }
        catch (Exception error) { Report(error); }
    }

    internal int CaptureRecent(IEnumerable<DesktopEntry> entries)
    {
        var count = RecentItems.Capture(Layout, entries);
        // Old AutoOrganize preferences must not drain the inbox or spawn category panels.
        if (count > 0) Notify($"最近分栏新增 {count} 个桌面项目");
        return count;
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
        if (_disposed || _anchorQueued) return;
        _anchorQueued = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _anchorQueued = false;
            if (!_disposed) ApplyDesktopZOrderCore();
        }));
    }
    private void ApplyDesktopZOrderCore()
    {
        if (!Layout.PinToDesktop && !_desktop.Enabled && _pinnedPanels.Count == 0) return;
        var windows = _panels.Values.Where(p => p.IsVisible && !p.Topmost)
            .OrderByDescending(p => p.Model.Id == _locatedPanelId).Cast<Window>()
            .Concat(_shelf is { IsVisible: true, Topmost: false } ? new Window[] { _shelf } : []).ToArray();
        if (Layout.PinToDesktop || _desktop.Enabled) NativeDesktop.AnchorWindows(windows);
        var pinsChanged = false;
        foreach (var panel in windows)
        {
            var handle = new WindowInteropHelper(panel).Handle;
            if (!Layout.PinToDesktop && !_pinnedPanels.Contains(handle)) continue;
            var success = Layout.PinToDesktop ? VirtualDesktopPinning.TryPin(handle, out var error) : VirtualDesktopPinning.TryUnpin(handle, out error);
            if (success)
            {
                _pinErrors.Remove(handle);
                pinsChanged |= Layout.PinToDesktop ? _pinnedPanels.Add(handle) : _pinnedPanels.Remove(handle);
            }
            else if (error is not null && (!_pinErrors.TryGetValue(handle, out var previous) || previous != error))
            { _pinErrors[handle] = error; Report(new InvalidOperationException(error)); }
        }
        if (pinsChanged) ScheduleSave();
    }
    internal void Collect(FenceTab tab, IEnumerable<string> paths)
    {
        var count = DesktopAssignments.Collect(Layout, tab, paths);
        Notify($"已收纳 {count} 个图标 · 原文件位置保持不变");
    }
    internal void OpenItem(string path)
    {
        ShellFiles.Open(path);
        RecordItemOpened(path);
    }
    internal void RecordItemOpened(string path)
    {
        ItemActivityTracking.RecordOpened(Layout, path);
        ScheduleSave();
        // Defer reordering until the input/menu handler has returned.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_disposed) foreach (var panel in Panels) panel.RefreshItemActivity(path);
        }));
    }
    internal void ReturnToDesktop(IEnumerable<string> paths, DesktopPosition position)
    {
        var items = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var native = NativeDesktopShell.ReadSnapshot().Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        // A reference to a file outside the Desktop needs a real desktop link.
        // Never pretend that an entry in our JSON creates an Explorer icon.
        var desktopItems = items.Select(path => native.ContainsKey(path) ? path : CreateDesktopShortcut(path)).ToArray();
        // Also pin newly created desktop links outside the inbox. Otherwise the
        // directory watcher would immediately collect a link we just returned.
        DesktopAssignments.ReturnToDesktop(Layout, items.Concat(desktopItems), position);
        for (var index = 0; index < desktopItems.Length; index++)
            Layout.DesktopPositions[desktopItems[index]] = new(position.X + index * 90, position.Y);
        Notify($"已将 {items.Length} 个图标放回原生桌面");
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
        try { DragDrop.DoDragDrop(source, new DesktopDrag(paths).Data(), DragDropEffects.Link | DragDropEffects.Copy); }
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
        if (Layout.Arrangement == PanelArrangementMode.Shelf) { Notify("已刷新折叠栏架"); return; }
        SetArrangement(PanelArrangementMode.CompactGrid);
    }

    internal void SetArrangement(PanelArrangementMode mode)
    {
        if (mode == PanelArrangementMode.Free && Layout.Arrangement == PanelArrangementMode.Free) return;
        var groups = Layout.Groups.Where(g => g.Visible).ToArray();
        if (mode != PanelArrangementMode.Free && groups.Any(g => g.Locked))
        { ReportStatus("请先解锁需要重新排布的分栏，再切换排列方式。"); return; }
        var area = SystemParameters.WorkArea;
        var plan = PanelArrangement.Grid(groups.Length, new(area.X, area.Y, area.Width, area.Height));
        if (mode == PanelArrangementMode.CompactGrid && groups.Length > plan.Capacity)
        { ReportStatus($"当前屏幕紧凑网格最多容纳 {plan.Capacity} 个可见分栏，可选择支持滚动的折叠栏架。"); return; }
        Store.Snapshot(Layout);
        if (Layout.Arrangement == PanelArrangementMode.Free && mode != PanelArrangementMode.Free)
            Layout.FreePlacements = Layout.Groups.ToDictionary(g => g.Id, g => new PanelBounds(g.X, g.Y, g.Width, g.Height, g.Collapsed), StringComparer.Ordinal);
        Layout.Arrangement = mode;
        if (mode == PanelArrangementMode.Free)
        {
            foreach (var group in Layout.Groups)
                if (Layout.FreePlacements.TryGetValue(group.Id, out var saved))
                { group.X = saved.X; group.Y = saved.Y; group.Width = saved.Width; group.Height = saved.Height; group.Collapsed = saved.Collapsed; }
        }
        else if (mode == PanelArrangementMode.Shelf)
            foreach (var group in groups) group.Collapsed = true;
        else
            for (var i = 0; i < groups.Length; i++)
            { var cell = plan.Cells[i]; groups[i].X = cell.X; groups[i].Y = cell.Y; groups[i].Width = cell.Width; groups[i].Height = cell.Height; }
        Notify(mode switch { PanelArrangementMode.Shelf => "折叠栏架已开启：点击标题展开，切换分栏时自动收起上一栏", PanelArrangementMode.CompactGrid => "紧凑网格已排列；自由布局已单独保留", _ => "已恢复自由排布的位置、大小与折叠状态" });
    }

    internal PanelBounds PanelGeometry(FenceGroup group)
    {
        if (Layout.Arrangement != PanelArrangementMode.Shelf || _shelf is null) return new(group.X, group.Y, group.Width, group.Height);
        var area = SystemParameters.WorkArea;
        return PanelArrangement.ShelfPopup(new(area.X, area.Y, area.Width, area.Height), _shelf.Bounds, group);
    }

    internal void ToggleShelfGroup(string id)
    {
        var selected = Layout.Groups.FirstOrDefault(g => g.Id == id && g.Visible);
        if (selected is null) return;
        var expand = selected.Collapsed;
        foreach (var group in Layout.Groups) group.Collapsed = group != selected || !expand;
        _locatedPanelId = expand ? selected.Id : null;
        Notify(expand ? "已展开「" + selected.Name + "」" : "已收起分栏");
        if (expand) _panels[selected.Id].Activate();
    }

    internal void ReportStatus(string status) { Status = status; Changed?.Invoke(); }

    internal void CollapseAll(bool collapsed)
    {
        foreach (var group in Layout.Groups) group.Collapsed = collapsed;
        Notify(collapsed ? "已折叠所有分栏；单击栏头展开" : "已展开所有分栏");
    }

    internal void SetAllTransparency(double transparency, bool collapsed)
    {
        if (!double.IsFinite(transparency)) return;
        var opacity = 1 - Math.Clamp(transparency, collapsed ? 35 : 0, collapsed ? 94 : 85) / 100;
        foreach (var group in Layout.Groups)
        {
            if (collapsed) group.CollapsedOpacity = opacity;
            else group.Opacity = opacity;
        }
        // Appearance-only updates keep icons, selection and geometry intact while dragging.
        foreach (var panel in Panels) panel.RefreshAppearance();
        Status = "已统一调整所有分栏的" + (collapsed ? "折叠" : "展开") + "背景透明度，自动保存。";
        ScheduleSave();
    }

    private void ConfigureAutoOrganize()
    {
        foreach (var watcher in _desktopWatchers) watcher.Dispose();
        _desktopWatchers.Clear();
        if (!_captureDesktop) return;
        foreach (var path in DesktopPaths)
        {
            try
            {
                var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                watcher.Created += OnDesktopChanged; watcher.Renamed += OnDesktopChanged; watcher.Deleted += OnDesktopChanged;
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
                    arrangement = Layout.Arrangement.ToString(), shelfVisible = _shelf?.IsVisible == true,
                    visiblePanels = Panels.Count(p => p.IsVisible),
                    collapsedPanels = Panels.Count(p => p.IsVisible && p.Model.Collapsed),
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
        if (Layout.Arrangement == PanelArrangementMode.Shelf)
        {
            _shelf ??= new ShelfWindow(this);
            var expanded = Layout.Groups.Where(g => g.Visible && !g.Collapsed).ToArray();
            var active = expanded.FirstOrDefault(g => g.Id == _locatedPanelId) ?? expanded.FirstOrDefault();
            foreach (var group in expanded) if (group != active) group.Collapsed = true;
            _shelf.Topmost = _peeking;
            _shelf.Refresh(); if (!_shelf.IsVisible) _shelf.Show();
        }
        else _shelf?.Hide();
        var groupIds = Layout.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var obsolete in _panels.Keys.Where(k => !groupIds.Contains(k)).ToArray())
        {
            var handle = new WindowInteropHelper(_panels[obsolete]).Handle;
            _pinnedPanels.Remove(handle); _pinErrors.Remove(handle);
            _panels[obsolete].DisposePanel(); _panels.Remove(obsolete);
        }
        foreach (var group in Layout.Groups)
        {
            if (!_panels.TryGetValue(group.Id, out var panel))
            { panel = new FenceWindow(this, group); _panels.Add(group.Id, panel); }
            panel.Refresh();
            if (group.Visible && (Layout.Arrangement != PanelArrangementMode.Shelf || !group.Collapsed))
            { if (!panel.IsVisible) panel.Show(); } else panel.Hide();
        }
    }

    internal void Restore(string path)
    {
        var next = LayoutStore.Read(path);
        Store.Snapshot(Layout);
        foreach (var panel in Panels.ToArray()) panel.DisposePanel();
        _shelf?.DisposeShelf(); _shelf = null;
        _panels.Clear(); _pinnedPanels.Clear(); _pinErrors.Clear(); Layout = next;
        if (_captureDesktop) RecentItems.Ensure(Layout);
        ConfigureAutoOrganize(); ConfigureDesktopIntegration(); Notify("布局已恢复 · 恢复前的布局已自动保存为快照");
        QueueOrganize();
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
        _shelf?.DisposeShelf();
        var icon = _tray?.Icon;
        _tray?.Dispose();
        if (icon is not null && icon != System.Drawing.SystemIcons.Application) icon.Dispose();
    }
}
