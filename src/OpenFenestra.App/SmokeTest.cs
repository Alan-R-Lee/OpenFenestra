using System.Text.Json;
using System.Threading.Tasks;
using OpenFenestra.Core;

namespace OpenFenestra.App;

internal static class SmokeTest
{
    internal static async void Run(WorkspaceController workspace)
    {
        var checks = new List<string>();
        var exitCode = 0;
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
            workspace.AddGroup(group);
            await Task.Delay(600);
            var panel = workspace.Panels.Single();
            Check(panel.IsVisible, "面板窗口创建与显示", checks);
            Check(((ListBox)panel.FindName("Items")).Items.Count == 3, "引用图标列表渲染", checks);
            Check(File.Exists(note), "添加引用保留原文件", checks);
            group.Collapsed = true; workspace.Notify();
            Check(panel.Height == 48, "折叠标题栏", checks);
            group.Collapsed = false; workspace.Notify();
            Check(panel.Height == 370, "展开恢复高度", checks);
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
            workspace.Store.Save(workspace.Layout);
            Check(workspace.Store.Load().Groups.Single().Tabs.Count == 2, "布局磁盘往返", checks);
            var snapshot = workspace.Store.Snapshot(workspace.Layout);
            group.Name = "临时修改"; workspace.Restore(snapshot);
            Check(workspace.Layout.Groups.Single().Name == "我的工作台", "快照恢复", checks);
            Check(workspace.Store.Snapshots().Count >= 2, "恢复前自动快照", checks);
            await Task.Delay(500);
            Capture(workspace.Manager, Path.Combine(workspace.Store.DirectoryPath, "manager-preview.png"));
            Capture(workspace.Panels.Single(), Path.Combine(workspace.Store.DirectoryPath, "panel-preview.png"));
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
    { if (!condition) throw new InvalidOperationException(label); results.Add("PASS: " + label); }
    private static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
