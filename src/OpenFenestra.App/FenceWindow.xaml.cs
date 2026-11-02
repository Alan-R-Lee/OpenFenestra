using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenFenestra.Core;

namespace OpenFenestra.App;

internal sealed class EntryView(DesktopEntry entry) : INotifyPropertyChanged
{
    public string Path => entry.Path;
    public string Name => entry.Name;
    public double ItemOpacity => entry.Exists ? 1 : 0.45;
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

    internal FenceWindow(WorkspaceController workspace, FenceGroup model)
    {
        _workspace = workspace; Model = model;
        InitializeComponent();
        _refresh.Tick += (_, _) => { _refresh.Stop(); RefreshItems(); };
        Loaded += (_, _) =>
        {
            ClampToScreens(); _ready = true;
            if (_workspace.Layout.PinToDesktop) NativeDesktop.Anchor(this);
        };
        LocationChanged += (_, _) => { if (_ready) { Model.X = Left; Model.Y = Top; _workspace.ScheduleSave(); } };
        Deactivated += (_, _) => { if (_workspace.Layout.PinToDesktop && !Topmost) NativeDesktop.Anchor(this); };
        Closing += (_, e) => { if (!_closing && !_workspace.Exiting) { e.Cancel = true; Model.Visible = false; _workspace.Notify(); } };
        Search.Text = "";
        Refresh();
    }

    internal void Refresh()
    {
        Title = "OpenFenestra · " + Model.Name;
        GroupTitle.Text = Model.Name;
        Accent.Background = Brush(Model.Color);
        var color = (Color)ColorConverter.ConvertFromString("#1F302C");
        Surface.Background = new SolidColorBrush(Color.FromArgb((byte)(Model.Opacity * 255), color.R, color.G, color.B));
        Left = Model.X; Top = Model.Y; Width = Model.Width;
        MinHeight = Model.Collapsed ? 48 : 180;
        Height = Model.Collapsed ? 48 : Model.Height;
        foreach (var element in new UIElement[] { TabBar, SearchArea, ContentArea, Footnote, ResizeGrip })
            element.Visibility = Model.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        ResizeGrip.IsEnabled = !Model.Locked;
        Header.Cursor = Model.Locked ? Cursors.Arrow : Cursors.SizeAll;
        Tabs.Children.Clear();
        foreach (var tab in Model.Tabs)
        {
            var button = new Button { Content = tab.Name, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 5, 0),
                Foreground = Brushes.White, Background = tab.Id == Model.ActiveTab.Id ? Brush(Model.Color, 0.5) : Brush("#FFFFFF", 0.06) };
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
        var views = entries.Take(300).Select(e => new EntryView(e)).ToArray();
        Items.ItemsSource = views;
        Empty.Text = tab.IsPortal && !Directory.Exists(tab.FolderPath) ? "文件夹暂不可用\n请检查路径或网络连接" : search.Length > 0 ? "没有匹配的项目" : tab.IsPortal ? "这个文件夹还没有项目" : "把文件、文件夹或快捷方式拖到这里\n从这里开始整理桌面";
        Empty.Visibility = views.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Footnote.Text = $"{entries.Count} 个项目" + (entries.Count > 300 ? " · 仅显示前 300 项，请搜索缩小范围" : tab.IsPortal ? " · 文件夹映射 · 只读浏览" : " · 文件引用");
        try
        {
            foreach (var view in views)
            {
                var icon = await Task.Run(() => ShellFiles.Icon(view.Path));
                if (generation != _generation || _closing) return;
                view.Icon = icon;
            }
        }
        catch (Exception error) { _workspace.Report(error); }
    }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (Items is not null) RefreshItems(); }
    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleCollapse(); return; }
        if (Model.Locked) return;
        try { DragMove(); } catch (InvalidOperationException) { /* button was released before the drag started */ }
        Model.X = Left; Model.Y = Top; _workspace.ScheduleSave();
    }
    private void OnCollapse(object sender, RoutedEventArgs e) => ToggleCollapse();
    private void ToggleCollapse() { Model.Collapsed = !Model.Collapsed; _workspace.Notify(); }
    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        if (Model.Locked || Model.Collapsed) return;
        Width = Math.Clamp(Width + e.HorizontalChange, 260, 2000);
        Height = Math.Clamp(Height + e.VerticalChange, 180, 2000);
        Model.Width = Width; Model.Height = Height; _workspace.ScheduleSave();
    }
    private void OnMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddMenu(menu, "编辑分组", () => EditGroup());
        AddMenu(menu, "添加文件引用…", AddFiles);
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
        if (dialog.ShowDialog(this) == true) { FileCatalog.AddReferences(Model.ActiveTab, dialog.FileNames); _workspace.Notify(); }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Model.ActiveTab.IsPortal && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Model.ActiveTab.IsPortal || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var added = FileCatalog.AddReferences(Model.ActiveTab, files);
        e.Effects = DragDropEffects.Link;
        _workspace.Notify($"已添加 {added} 个文件引用");
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
        { Model.ActiveTab.Items.Remove(item.Path); _workspace.Notify("已移除引用 · 原文件保留"); e.Handled = true; }
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
                        FileCatalog.AddReferences(tab, [item.Path]);
                        // Keep the source when a missing file cannot be added to the destination.
                        if (tab.Items.Contains(item.Path, StringComparer.OrdinalIgnoreCase)) Model.ActiveTab.Items.Remove(item.Path);
                        _workspace.Notify();
                    };
                    move.Items.Add(target);
                }
            menu.Items.Add(move);
            AddMenu(menu, "移除引用（保留原文件）", () => { Model.ActiveTab.Items.Remove(item.Path); _workspace.Notify(); });
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
        DragDrop.DoDragDrop(Items, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Link | DragDropEffects.Copy);
    }
    internal void DisposePanel() { _closing = true; _generation++; _refresh.Stop(); _watcher?.Dispose(); Close(); }
    internal static SolidColorBrush Brush(string hex, double opacity = 1) => new((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
    private static void AddMenu(ContextMenu menu, string title, Action action)
    { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
}
