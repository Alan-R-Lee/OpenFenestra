using OpenFenestra.Core;

namespace OpenFenestra.App;

internal sealed record GroupCard(string Id, string Name, string Color, string Detail, string VisibilityLabel);
internal sealed record SnapshotCard(string Path, string Name);

public partial class MainWindow : Window
{
    private readonly WorkspaceController _workspace;
    private bool _rendering;
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
            SnapshotList.ItemsSource = _workspace.Store.Snapshots().Select(p => new SnapshotCard(p, Path.GetFileNameWithoutExtension(p))).ToList();
        }
        finally { _rendering = false; }
    }
    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        var page = (string)((Button)sender).Tag;
        GroupsPage.Visibility = page == "groups" ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsPage.Visibility = page == "snapshots" ? Visibility.Visible : Visibility.Collapsed;
        GuidePage.Visibility = page == "guide" ? Visibility.Visible : Visibility.Collapsed;
        Render();
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
    private void OnOrganize(object sender, RoutedEventArgs e) => _workspace.OrganizeDesktop();
    private void OnTray(object sender, RoutedEventArgs e) => Hide();
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
