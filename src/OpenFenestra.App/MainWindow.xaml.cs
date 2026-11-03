using OpenFenestra.Core;
using System.Windows.Input;
using System.Windows.Controls.Primitives;

namespace OpenFenestra.App;

internal sealed record GroupCard(string Id, string Name, string Color, string Detail, string VisibilityLabel);
internal sealed record SnapshotCard(string Path, string Name);

public partial class MainWindow : Window
{
    private readonly WorkspaceController _workspace;
    private bool _rendering;
    private const string GroupOrderFormat = "OpenFenestra.GroupOrder.v1";
    private readonly string _groupOrderToken = Guid.NewGuid().ToString("N") + ":";
    private Point _groupDragStart;
    private string? _armedGroup;
    private Border? _dropCue;
    private DateTime _lastDragScroll;
    internal MainWindow(WorkspaceController workspace)
    {
        _workspace = workspace; InitializeComponent();
        workspace.Changed += Render;
        Closing += (_, e) => { if (!workspace.Exiting) { e.Cancel = true; Hide(); } };
        Loaded += (_, _) => Render();
    }
    private void Render()
    {
        _rendering = true;
        try
        {
            var groups = _workspace.Layout.Groups;
            GroupCount.Text = groups.Count.ToString("D2");
            ReferenceCount.Text = groups.Sum(g => g.Tabs.Sum(t => t.Items.Count)).ToString("D2");
            PortalCount.Text = groups.Sum(g => g.Tabs.Count(t => t.IsPortal)).ToString("D2");
            var query = GroupSearch.Text.Trim();
            GroupCards.ItemsSource = groups.Where(g => g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || g.Tabs.Any(t => t.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                .Select(g => new GroupCard(g.Id, g.Name, g.Color,
                    $"{g.Tabs.Count} 个标签页  ·  {g.Tabs.Sum(t => t.Items.Count)} 个引用  ·  " + (g.Locked ? "已锁定" : "可自由移动"), g.Visible ? "隐藏" : "显示")).ToList();
            EmptyState.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PinSetting.IsChecked = _workspace.Layout.PinToDesktop;
            AutoSetting.IsChecked = _workspace.Layout.AutoOrganize;
            DesktopSetting.IsChecked = _workspace.Layout.DesktopIntegrationEnabled;
            StartupSetting.IsChecked = StartupRegistration.IsEnabled();
            StatusText.Text = _workspace.Status;
            var expanded = groups.Select(g => g.Opacity).ToArray();
            var collapsed = groups.Select(g => g.CollapsedOpacity).ToArray();
            ExpandedTransparency.Value = 100 * (1 - (expanded.Length > 0 ? expanded.Average() : 0.58));
            CollapsedTransparency.Value = 100 * (1 - (collapsed.Length > 0 ? collapsed.Average() : 0.20));
            ExpandedTransparency.IsEnabled = CollapsedTransparency.IsEnabled = groups.Count > 0;
            ExpandedTransparencyValue.Text = TransparencyLabel(ExpandedTransparency.Value, expanded);
            CollapsedTransparencyValue.Text = TransparencyLabel(CollapsedTransparency.Value, collapsed);
            SnapshotList.ItemsSource = _workspace.Store.Snapshots().Select(p => new SnapshotCard(p, Path.GetFileNameWithoutExtension(p))).ToList();
        }
        finally { _rendering = false; }
    }
    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (GroupsPage is null || SnapshotsPage is null || GuidePage is null) return;
        var page = (string)((RadioButton)sender).Tag;
        GroupsPage.Visibility = page == "groups" ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsPage.Visibility = page == "snapshots" ? Visibility.Visible : Visibility.Collapsed;
        GuidePage.Visibility = page == "guide" ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }
    private static string TransparencyLabel(double value, double[] opacities)
        => $"{value:0}%" + (opacities.Select(v => Math.Round(v, 4)).Distinct().Skip(1).Any() ? " · 各分栏不同" : "");
    private void OnTransparencyChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_rendering || _workspace is null || ExpandedTransparencyValue is null || CollapsedTransparencyValue is null) return;
        var collapsed = ReferenceEquals(sender, CollapsedTransparency);
        _workspace.SetAllTransparency(e.NewValue, collapsed);
        (collapsed ? CollapsedTransparencyValue : ExpandedTransparencyValue).Text = $"{e.NewValue:0}%";
        StatusText.Text = _workspace.Status;
    }
    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var index = _workspace.Layout.Groups.Count;
        var group = new FenceGroup { X = 90 + index % 3 * 370, Y = 90 + index / 3 * 350 };
        var dialog = new GroupDialog(group) { Owner = this };
        if (dialog.ShowDialog() == true) { dialog.Apply(group); _workspace.AddGroup(group); }
    }
    private void OnEditGroup(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        var group = _workspace.Layout.Groups.First(g => g.Id == id);
        var dialog = new GroupDialog(group, true) { Owner = this };
        var result = dialog.ShowDialog();
        if (dialog.DeleteRequested) _workspace.RemoveGroup(group);
        else if (result == true) { dialog.Apply(group); _workspace.Notify("分组设置已保存"); }
    }
    private void OnToggleGroup(object sender, RoutedEventArgs e)
    {
        var group = _workspace.Layout.Groups.First(g => g.Id == (string)((Button)sender).Tag);
        group.Visible = !group.Visible; _workspace.Notify();
    }

    private void OnGroupDragStart(object sender, MouseButtonEventArgs e)
    {
        _armedGroup = null;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, sender); node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase) return;
        if (sender is Border { DataContext: GroupCard card })
        { _armedGroup = card.Id; _groupDragStart = e.GetPosition(GroupCards); }
    }
    private void OnGroupDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _armedGroup = null; return; }
        if (_armedGroup is not { } id || sender is not Border { DataContext: GroupCard card } border || card.Id != id) return;
        var point = e.GetPosition(GroupCards);
        if (Math.Abs(point.X - _groupDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _groupDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _armedGroup = null;
        var data = new DataObject(); data.SetData(GroupOrderFormat, _groupOrderToken + id);
        try { DragDrop.DoDragDrop(border, data, DragDropEffects.Move); }
        finally { ClearGroupDropCue(); }
        e.Handled = true;
    }
    private string? GroupDragId(IDataObject data)
    {
        if (!data.GetDataPresent(GroupOrderFormat, false) || data.GetData(GroupOrderFormat, false) is not string value
            || !value.StartsWith(_groupOrderToken, StringComparison.Ordinal)) return null;
        var id = value[_groupOrderToken.Length..];
        return _workspace.Layout.Groups.Any(g => g.Id == id) ? id : null;
    }
    private void OnGroupDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (sender is not Border { DataContext: GroupCard target } border || GroupDragId(e.Data) is not { } source) { ClearGroupDropCue(); return; }
        var scrollPoint = e.GetPosition(GroupScroll);
        if ((DateTime.UtcNow - _lastDragScroll).TotalMilliseconds > 70)
        {
            if (scrollPoint.Y < 32) GroupScroll.ScrollToVerticalOffset(GroupScroll.VerticalOffset - 16);
            else if (scrollPoint.Y > GroupScroll.ActualHeight - 32) GroupScroll.ScrollToVerticalOffset(GroupScroll.VerticalOffset + 16);
            _lastDragScroll = DateTime.UtcNow;
        }
        if (source == target.Id) { ClearGroupDropCue(); return; }
        e.Effects = DragDropEffects.Move;
        ClearGroupDropCue(); _dropCue = border;
        var after = e.GetPosition(border).Y >= border.ActualHeight / 2;
        border.BorderBrush = FenceWindow.Brush("#3D8264");
        border.BorderThickness = after ? new Thickness(1, 1, 1, 3) : new Thickness(1, 3, 1, 1);
    }
    private void OnGroupDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border && !new Rect(border.RenderSize).Contains(e.GetPosition(border))) ClearGroupDropCue();
    }
    private void OnGroupDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (sender is Border { DataContext: GroupCard target } border && GroupDragId(e.Data) is { } source)
        {
            var after = e.GetPosition(border).Y >= border.ActualHeight / 2;
            ClearGroupDropCue();
            _workspace.ReorderGroup(source, target.Id, after);
            e.Effects = DragDropEffects.Move;
        }
        ClearGroupDropCue();
    }
    private void ClearGroupDropCue()
    {
        if (_dropCue is null) return;
        _dropCue.BorderBrush = FenceWindow.Brush("#DEE4DA"); _dropCue.BorderThickness = new Thickness(1);
        _dropCue = null;
    }
    private void OnOrganize(object sender, RoutedEventArgs e) => _workspace.OrganizeDesktop();
    private void OnTray(object sender, RoutedEventArgs e) => Hide();
    private void OnArrange(object sender, RoutedEventArgs e) => _workspace.ArrangePanels();
    private void OnCollapseAll(object sender, RoutedEventArgs e) => _workspace.CollapseAll(true);
    private void OnExpandAll(object sender, RoutedEventArgs e) => _workspace.CollapseAll(false);
    private void OnExitApp(object sender, RoutedEventArgs e) => _workspace.Exit();
    private void OnSearch(object sender, TextChangedEventArgs e) { if (GroupCards is not null) Render(); }
    private void OnSettings(object sender, RoutedEventArgs e)
    { if (!_rendering) _workspace.UpdateSettings(PinSetting.IsChecked == true, AutoSetting.IsChecked == true); }
    private void OnDesktopSetting(object sender, RoutedEventArgs e)
    { if (!_rendering) _workspace.SetDesktopIntegration(DesktopSetting.IsChecked == true); }
    private void OnStartupSetting(object sender, RoutedEventArgs e)
    { if (!_rendering) _workspace.SetStartup(StartupSetting.IsChecked == true); }
    private void OnSnapshot(object sender, RoutedEventArgs e)
    { _workspace.Store.Snapshot(_workspace.Layout); _workspace.Notify("当前布局已保存为快照"); }
    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "OpenFenestra 布局 (*.json)|*.json", FileName = "OpenFenestra-layout.json" };
        if (dialog.ShowDialog(this) == true) { LayoutStore.Export(dialog.FileName, _workspace.Layout); _workspace.Notify("布局已导出"); }
    }
    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "OpenFenestra 布局 (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) _workspace.Restore(dialog.FileName);
    }
    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotCard selected) return;
        _workspace.Restore(selected.Path);
    }
    private void OnOpenData(object sender, RoutedEventArgs e)
    { Directory.CreateDirectory(_workspace.Store.DirectoryPath); ShellFiles.Open(_workspace.Store.DirectoryPath); }
}
