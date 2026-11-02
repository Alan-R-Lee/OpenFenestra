using System.Text.Json;
using System.Threading.Tasks;
using OpenFenestra.Core;

namespace OpenFenestra.App;

internal static class SmokeTest
{
    private static string? _tracePath;
    private static void Trace(string text) { if (_tracePath is not null) File.AppendAllText(_tracePath, DateTimeOffset.Now + " " + text + Environment.NewLine); }
    internal static async void Run(WorkspaceController workspace)
    {
        var checks = new List<string>();
        var exitCode = 0;
        Directory.CreateDirectory(workspace.Store.DirectoryPath);
        _tracePath = Path.Combine(workspace.Store.DirectoryPath, "smoke-progress.log");
        Trace("begin");
        try
        {
            if (workspace.Layout.Groups.Count > 0) throw new InvalidOperationException("请为测试提供一个空的数据目录。");
            var samples = Path.Combine(workspace.Store.DirectoryPath, "sample-files");
            var folder = Path.Combine(samples, "灵感资料");
            Directory.CreateDirectory(folder);
            var note = Path.Combine(samples, "欢迎使用 OpenFenestra.txt");
            File.WriteAllText(note, "OpenFenestra UI smoke test fixture.");
            File.WriteAllText(Path.Combine(samples, "项目草稿.md"), "# 项目草稿\n桌面，自有条理。");
            var group = new FenceGroup { Name = "我的工作台", X = 90, Y = 90, Width = 350, Height = 370 };
            FileCatalog.AddReferences(group.ActiveTab, Directory.GetFileSystemEntries(samples));
            FileCatalog.AddReferences(group.ActiveTab, [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")]);
            Trace("before add group"); workspace.AddGroup(group); Trace("after add group");
            await Task.Delay(600);
            Trace("after first delay"); var panel = workspace.Panels.Single();
            Check(panel.IsVisible, "面板窗口创建与显示", checks);
            Check(((ListBox)panel.FindName("Items")).Items.Count == 4, "引用图标列表渲染", checks);
            Check(File.Exists(note), "添加引用保留原文件", checks);
            group.X = 440; group.Y = 180; workspace.Notify();
            Check(panel.Left == 440 && panel.Top == 180 && group.X == 440 && group.Y == 180,
                "同时更新横纵位置不被窗口事件覆盖", checks);
            panel.HandleHeaderClick(2); await Task.Delay(220);
            Trace($"folded: model={group.Collapsed}, height={panel.Height}, actual={panel.ActualHeight}");
            Check(group.Collapsed && Math.Abs(panel.Height - 34) < 1, "折叠标题栏", checks);
            Check(panel.Opacity < 1 && ((SolidColorBrush)((Border)panel.FindName("Surface")).Background).Color.A < group.Opacity * 255, "折叠后提高透明度", checks);
            panel.HandleHeaderClick(1); await Task.Delay(220);
            Check(!group.Collapsed && Math.Abs(panel.Height - 370) < 1, "展开恢复高度", checks);
            Check(Math.Abs(panel.Opacity - 1) < 0.01, "单击展开状态恢复透明度", checks);
            Trace("before high-resolution extraction");
            var shellIcon = await Task.Run(() => ShellFiles.Icon(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\explorer.exe", 128)).WaitAsync(TimeSpan.FromSeconds(8));
            Trace("after high-resolution extraction");
            Check(shellIcon is System.Windows.Media.Imaging.BitmapSource { PixelWidth: >= 96 }, "高分辨率 Shell 图标提取", checks);
            var portal = new FenceTab { Name = "灵感资料", FolderPath = folder };
            group.Tabs.Add(portal); group.SelectedTabId = portal.Id; workspace.Notify();
            Check(((StackPanel)panel.FindName("Tabs")).Children.Count == 2, "标签页切换", checks);
            File.WriteAllText(Path.Combine(folder, "实时更新.md"), "folder watcher fixture");
            await Task.Delay(1500);
            Check(((ListBox)panel.FindName("Items")).Items.Count == 1, "FileSystemWatcher 实时刷新", checks);
            group.SelectedTabId = group.Tabs[0].Id; workspace.Notify();
            var search = (TextBox)panel.FindName("Search"); search.Text = "项目草稿";
            Check(((ListBox)panel.FindName("Items")).Items.Count == 1, "搜索筛选项目", checks);
            search.Text = "";
            Check(NativeDesktop.Anchor(panel), "Win32 桌面层定位调用", checks);
            Check(NativeDesktop.IsDesktopOwned(panel), "分栏窗口归属于原生桌面宿主", checks);
            workspace.Store.Save(workspace.Layout);
            Check(workspace.Store.Load().Groups.Single().Tabs.Count == 2, "布局磁盘往返", checks);
            var snapshot = workspace.Store.Snapshot(workspace.Layout);
            group.Name = "临时修改"; workspace.Restore(snapshot);
            Check(workspace.Layout.Groups.Single().Name == "我的工作台", "快照恢复", checks);
            Check(workspace.Store.Snapshots().Count >= 2, "恢复前自动快照", checks);
            await Task.Delay(500);
            var restoredPanel = workspace.Panels.Single();
            workspace.Manager.Hide(); restoredPanel.Topmost = true; restoredPanel.Activate();
            await Task.Delay(500); restoredPanel.UpdateLayout();
            var renderedItems = (ListBox)restoredPanel.FindName("Items");
            var area = (Grid)restoredPanel.FindName("ContentArea");
            Trace($"render: items={renderedItems.Items.Count}, visible={renderedItems.IsVisible}, size={renderedItems.ActualWidth}x{renderedItems.ActualHeight}, area={area.Visibility}/{area.ActualHeight}, collapsed={restoredPanel.Model.Collapsed}, tab={restoredPanel.Model.ActiveTab.Name}, container={renderedItems.ItemContainerGenerator.Status}");
            DumpVisual(renderedItems, 0);
            Capture(restoredPanel, Path.Combine(workspace.Store.DirectoryPath, "panel-preview.png"));
            Check(renderedItems.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem { ActualHeight: > 50 }, "图标项目实际布局", checks);
            workspace.Manager.Show();
            Capture(workspace.Manager, Path.Combine(workspace.Store.DirectoryPath, "manager-preview.png"));
            Capture(workspace.Panels.Single(), Path.Combine(workspace.Store.DirectoryPath, "panel-preview.png"));
            workspace.Layout.Groups[0].Name = "退出保存验证";
            workspace.ScheduleSave(); workspace.Dispose();
            Check(workspace.Store.Load().Groups[0].Name == "退出保存验证", "退出时立即保存待处理布局", checks);
        }
        catch (Exception error) { workspace.Report(error); checks.Add("FAILED: " + error); exitCode = 1; }
        finally
        {
            File.WriteAllText(Path.Combine(workspace.Store.DirectoryPath, "smoke-result.json"), JsonSerializer.Serialize(new { passed = exitCode == 0, checks }, new JsonSerializerOptions { WriteIndented = true }));
            workspace.SaveNow();
            Application.Current.Shutdown(exitCode);
        }
    }
    private static void Check(bool condition, string label, List<string> results)
    { if (!condition) throw new InvalidOperationException(label); results.Add("PASS: " + label); Trace("PASS: " + label); }
    private static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void DumpVisual(DependencyObject value, int depth)
    {
        if (depth > 10) return;
        if (value is FrameworkElement element)
            Trace(new string(' ', depth) + element.GetType().Name + $" {element.ActualWidth}x{element.ActualHeight} desired={element.DesiredSize} visible={element.Visibility} data={element.DataContext?.GetType().Name}");
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) DumpVisual(VisualTreeHelper.GetChild(value, i), depth + 1);
    }
}
