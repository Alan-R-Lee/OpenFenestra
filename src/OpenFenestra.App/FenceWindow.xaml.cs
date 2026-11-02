using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OpenFenestra.Core;

namespace OpenFenestra.App;

internal sealed class EntryView(DesktopEntry entry, int iconSize = 48) : INotifyPropertyChanged
{
    public string Path => entry.Path;
    public string Name => entry.Name;
    public double ItemOpacity => entry.Exists ? 1 : 0.45;
    public int IconSize => iconSize;
    public int TileHeight => iconSize + 55;
    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class FenceWindow : Window
{
    private readonly WorkspaceController _workspace;
    internal FenceGroup Model { get; }
    private FileSystemWatcher? _watcher;
    private string? _watchedPath;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _ready, _closing;
    private Point _dragStart;
    private int _generation;
    private bool _headerArmed, _searchVisible;
    private Point _headerStart;
    private DateTime _expandedAt;
    private HwndSource? _hwndSource;

    internal FenceWindow(WorkspaceController workspace, FenceGroup model)
    {
        _workspace = workspace; Model = model;
        InitializeComponent();
        ShowInTaskbar = App.DebugWindows;
        SourceInitialized += (_, _) =>
        {
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(OnNativeMessage);
        };
        _refresh.Tick += (_, _) => { _refresh.Stop(); RefreshItems(); };
        Loaded += (_, _) =>
        {
            ClampToScreens(); _ready = true; RefreshItems();
            _workspace.ApplyDesktopZOrder();
        };
        LocationChanged += (_, _) => { if (_ready) { Model.X = Left; Model.Y = Top; _workspace.ScheduleSave(); } };
        Deactivated += (_, _) => { if (!Topmost) _workspace.ApplyDesktopZOrder(); };
        Closing += (_, e) => { if (!_closing && !_workspace.Exiting) { e.Cancel = true; Model.Visible = false; _workspace.Notify(); } };
        Search.Text = "";
        Refresh();
    }

    private nint OnNativeMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // A child visual's routed DpiChanged must never rebuild its own item tree.
        // Only the top-level HWND's monitor transition needs a fresh icon resolution.
        if (message == 0x02E0) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RefreshItems));
        return 0;
    }

    internal void Refresh()
    {
        Title = "OpenFenestra · " + Model.Name;
        GroupTitle.Text = Model.Name;
        Accent.Background = Brush(Model.Color);
        var color = (Color)ColorConverter.ConvertFromString("#151C22");
        Surface.Background = new SolidColorBrush(Color.FromArgb((byte)((Model.Collapsed ? Model.CollapsedOpacity : Model.Opacity) * 255), color.R, color.G, color.B));
        Left = Model.X; Top = Model.Y; Width = Model.Width;
        var target = Model.Collapsed ? 34 : Model.Height;
        MinHeight = Model.Collapsed ? 34 : 180;
        BeginAnimation(HeightProperty, null); Height = target;
        var opacity = Model.Collapsed ? 0.68 : 1;
        BeginAnimation(OpacityProperty, null); Opacity = opacity;
        TabBar.Visibility = !Model.Collapsed && Model.Tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SearchArea.Visibility = !Model.Collapsed && _searchVisible ? Visibility.Visible : Visibility.Collapsed;
        ContentArea.Visibility = ResizeGrip.Visibility = Model.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = Model.Collapsed ? "+" : "−";
        ResizeGrip.IsEnabled = !Model.Locked;
        Header.Cursor = Model.Locked ? Cursors.Arrow : Cursors.SizeAll;
        Tabs.Children.Clear();
        foreach (var tab in Model.Tabs)
        {
            var button = new Button { Content = tab.Name, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 5, 0),
                FontSize = 11, Foreground = Brushes.White, Background = tab.Id == Model.ActiveTab.Id ? Brush(Model.Color, 0.32) : Brush("#FFFFFF", 0.04) };
            button.Click += (_, _) => { Model.SelectedTabId = tab.Id; Search.Text = ""; _workspace.Notify(); };
            button.ContextMenu = new ContextMenu();
            AddMenu(button.ContextMenu, "编辑标签页", () => EditTab(tab));
            if (Model.Tabs.Count > 1) AddMenu(button.ContextMenu, "移除标签页（保留原文件）", () =>
            {
                _workspace.Store.Snapshot(_workspace.Layout); Model.Tabs.Remove(tab); _workspace.Notify();
            });
            Tabs.Children.Add(button);
        }
        ConfigureWatcher(); RefreshItems();
    }

    private void ClampToScreens()
    {
        // WPF positions are DIPs. Use the matching monitor's scale for physical bounds.
        var dpi = VisualTreeHelper.GetDpi(this);
        var rect = new System.Drawing.Rectangle((int)(Left * dpi.DpiScaleX), (int)(Top * dpi.DpiScaleY), (int)(Width * dpi.DpiScaleX), 48);
        if (System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) return;
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
        if (area is not null) { Left = (area.Value.Left + 60) / dpi.DpiScaleX; Top = (area.Value.Top + 60) / dpi.DpiScaleY; }
    }

    private void ConfigureWatcher()
    {
        var path = Model.ActiveTab.FolderPath;
        if (_watchedPath == path && _watcher is not null) return;
        _watcher?.Dispose(); _watcher = null; _watchedPath = path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            _watcher = new FileSystemWatcher(path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            _watcher.Created += OnFolderChanged; _watcher.Deleted += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged; _watcher.Changed += OnFolderChanged;
            _watcher.Error += (_, _) => QueueRefresh();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { _workspace.Report(e); }
    }
    private void OnFolderChanged(object sender, FileSystemEventArgs e) => QueueRefresh();
    private void QueueRefresh() => Dispatcher.BeginInvoke(new Action(() =>
    { if (!_closing) { _refresh.Stop(); _refresh.Start(); } }));

    private async void RefreshItems()
    {
        if (!_ready && Items is null) return;
        var generation = ++_generation;
        var tab = Model.ActiveTab;
        var search = Search.Text.Trim();
        var entries = FileCatalog.ReadTab(tab).Where(e => e.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var views = entries.Take(300).Select(e => new EntryView(e, Model.IconSize)).ToArray();
        Items.ItemsSource = views;
        Empty.Text = tab.IsPortal && !Directory.Exists(tab.FolderPath) ? "文件夹暂不可用\n请检查路径或网络连接" : search.Length > 0 ? "没有匹配的项目" : tab.IsPortal ? "这个文件夹还没有项目" : "把文件、文件夹或快捷方式拖到这里\n从这里开始整理桌面";
        Empty.Visibility = views.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Footnote.Text = $"{entries.Count} 个项目" + (entries.Count > 300 ? " · 仅显示前 300 项，请搜索缩小范围" : tab.IsPortal ? " · 文件夹映射 · 只读浏览" : " · 文件引用");
        try
        {
            foreach (var view in views)
            {
                var pixels = Math.Clamp((int)Math.Ceiling(Model.IconSize * VisualTreeHelper.GetDpi(this).DpiScaleX * 2), 96, 256);
                var icon = await Task.Run(() => ShellFiles.Icon(view.Path, pixels));
                if (generation != _generation || _closing) return;
                view.Icon = icon;
            }
        }
        catch (Exception error) { _workspace.Report(error); }
    }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (Items is not null) RefreshItems(); }
    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (HandleHeaderClick(e.ClickCount)) { e.Handled = true; return; }
        _headerStart = e.GetPosition(this); _headerArmed = !Model.Locked;
        if (_headerArmed) Header.CaptureMouse();
    }
    internal bool HandleHeaderClick(int clickCount)
    {
        if (Model.Collapsed) { _expandedAt = DateTime.UtcNow; SetCollapsed(false); return true; }
        if (clickCount == 2)
        {
            if ((DateTime.UtcNow - _expandedAt).TotalMilliseconds > System.Windows.Forms.SystemInformation.DoubleClickTime) SetCollapsed(true);
            _headerArmed = false; Header.ReleaseMouseCapture(); return true;
        }
        return false;
    }
    private void OnHeaderMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (!_headerArmed || e.LeftButton != MouseButtonState.Pressed || Math.Abs(point.X - _headerStart.X) + Math.Abs(point.Y - _headerStart.Y) < 10) return;
        _headerArmed = false; Header.ReleaseMouseCapture();
        try { DragMove(); } catch (InvalidOperationException) { }
        Model.X = Left; Model.Y = Top; _workspace.ScheduleSave();
    }
    private void OnHeaderUp(object sender, MouseButtonEventArgs e) { _headerArmed = false; Header.ReleaseMouseCapture(); }
    private void OnSearchToggle(object sender, RoutedEventArgs e)
    {
        _searchVisible = !_searchVisible; SearchArea.Visibility = _searchVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_searchVisible) Search.Focus(); else Search.Text = "";
    }
    private void OnCollapse(object sender, RoutedEventArgs e) => ToggleCollapse();
    private void ToggleCollapse() => SetCollapsed(!Model.Collapsed);
    internal void SetCollapsed(bool collapsed) { Model.Collapsed = collapsed; _workspace.Notify(); }
    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        if (Model.Locked || Model.Collapsed) return;
        BeginAnimation(HeightProperty, null);
        Width = Math.Clamp(Width + e.HorizontalChange, 260, 2000);
        Height = Math.Clamp(Height + e.VerticalChange, 180, 2000);
        Model.Width = Width; Model.Height = Height; _workspace.ScheduleSave();
    }
    private void OnMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "编辑分组", () => EditGroup());
        AddMenu(menu, "收纳文件或快捷方式…", AddFiles);
        AddMenu(menu, "添加标签页", () => OnAddTab(this, new RoutedEventArgs()));
        AddMenu(menu, "编辑当前标签页 / 映射文件夹", () => EditTab(Model.ActiveTab));
        AddMenu(menu, Model.Locked ? "解锁位置与大小" : "锁定位置与大小", () => { Model.Locked = !Model.Locked; _workspace.Notify(); });
        AddMenu(menu, "刷新", () => { ConfigureWatcher(); RefreshItems(); });
        AddMenu(menu, "隐藏分组", () => { Model.Visible = false; _workspace.Notify(); });
        AddMenu(menu, "打开管理窗口", _workspace.ShowManager);
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    internal void EditGroup()
    {
        var dialog = new GroupDialog(Model) { Owner = _workspace.Manager.IsVisible ? _workspace.Manager : this };
        if (dialog.ShowDialog() == true) { dialog.Apply(Model); _workspace.Notify("分组设置已保存"); }
    }
    private void EditTab(FenceTab tab)
    {
        var dialog = new TabDialog(tab) { Owner = this };
        if (dialog.ShowDialog() == true) { dialog.Apply(tab); _workspace.Notify(); }
    }
    private void OnAddTab(object sender, RoutedEventArgs e)
    {
        if (Model.Tabs.Count >= 30) { MessageBox.Show(this, "最多支持 30 个标签页。", "OpenFenestra"); return; }
        var tab = new FenceTab { Name = "新标签页" };
        var dialog = new TabDialog(tab) { Owner = this };
        if (dialog.ShowDialog() == true) { dialog.Apply(tab); Model.Tabs.Add(tab); Model.SelectedTabId = tab.Id; _workspace.Notify(); }
    }
    private void AddFiles()
    {
        if (Model.ActiveTab.IsPortal) { MessageBox.Show(this, "文件夹映射展示文件夹的实时内容。请切换普通标签页添加引用。", "OpenFenestra"); return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "添加文件引用（原文件保留在原位置）" };
        if (dialog.ShowDialog(this) == true) _workspace.Collect(Model.ActiveTab, dialog.FileNames);
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Model.ActiveTab.IsPortal && DesktopDrag.Read(e.Data).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Model.ActiveTab.IsPortal) return;
        var files = DesktopDrag.Read(e.Data);
        if (files.Length == 0) return;
        e.Effects = DragDropEffects.Link;
        _workspace.Collect(Model.ActiveTab, files);
    }
    private void OnOpen(object sender, MouseButtonEventArgs e) => OpenSelected();
    private void OpenSelected()
    {
        if (Items.SelectedItem is not EntryView item) return;
        try { ShellFiles.Open(item.Path); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "无法打开项目", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void OnItemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        if (e.Key == Key.Delete && !Model.ActiveTab.IsPortal && Items.SelectedItem is EntryView item)
        { ReturnItem(item.Path); e.Handled = true; }
    }
    private void OnItemContext(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: EntryView item } container) return;
        container.IsSelected = true;
        var menu = new ContextMenu();
        AddMenu(menu, "打开", OpenSelected);
        AddMenu(menu, "在资源管理器中定位", () => ShellFiles.Reveal(item.Path));
        if (!Model.ActiveTab.IsPortal)
        {
            var move = new MenuItem { Header = "移到分组" };
            foreach (var group in _workspace.Layout.Groups)
                foreach (var tab in group.Tabs.Where(t => !t.IsPortal && t.Id != Model.ActiveTab.Id))
                {
                    var target = new MenuItem { Header = group.Name + " / " + tab.Name };
                    target.Click += (_, _) =>
                    {
                        _workspace.Collect(tab, [item.Path]);
                    };
                    move.Items.Add(target);
                }
            menu.Items.Add(move);
            AddMenu(menu, "放回桌面（保留原文件）", () => ReturnItem(item.Path));
        }
        container.ContextMenu = menu;
        menu.PlacementTarget = container;
        menu.IsOpen = true;
        e.Handled = true;
    }
    private void OnItemMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(Items);
    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Items);
        if (e.LeftButton != MouseButtonState.Pressed || Items.SelectedItem is not EntryView item
            || Math.Abs(point.X - _dragStart.X) + Math.Abs(point.Y - _dragStart.Y) < 12) return;
        var paths = Items.SelectedItems.Cast<EntryView>().Select(v => v.Path).ToArray();
        _workspace.DragItems(Items, paths.Length > 0 ? paths : [item.Path]);
    }
    private void ReturnItem(string path)
    {
        var point = PointToScreen(new Point(Width + 20, 50));
        _workspace.ReturnToDesktop([path], new(point.X, point.Y));
    }
    internal void DisposePanel() { _closing = true; _generation++; _refresh.Stop(); _watcher?.Dispose(); _hwndSource?.RemoveHook(OnNativeMessage); Close(); }
    internal static SolidColorBrush Brush(string hex, double opacity = 1) => new((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
    private static void AddMenu(ContextMenu menu, string title, Action action)
    { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
}
