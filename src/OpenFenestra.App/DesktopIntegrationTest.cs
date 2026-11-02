using System.Text.Json;
using System.Threading.Tasks;
using OpenFenestra.Core;

namespace OpenFenestra.App;

internal static class DesktopIntegrationTest
{
    internal static async void Run(WorkspaceController workspace, bool waitForCrash)
    {
        var checks = new List<string>();
        var before = NativeDesktop.HasVisibleStyle(NativeDesktop.IconList());
        var resultPath = Path.Combine(workspace.Store.DirectoryPath, "integration-result.json");
        try
        {
            var group = new FenceGroup { Name = "桌面整合验证", X = 160, Y = 100 };
            workspace.AddGroup(group);
            workspace.SetDesktopIntegration(true);
            await Task.Delay(500);
            Check(workspace.Desktop.Enabled && workspace.Desktop.Surfaces.Any(), "桌面图标层创建", checks);
            Check(!NativeDesktop.HasVisibleStyle(NativeDesktop.IconList()), "Windows 原生重复图标临时隐藏", checks);
            var source = WorkspaceController.DesktopPaths.SelectMany(FileCatalog.ReadFolder).FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("没有可验证的桌面文件。");
            var bytes = File.ReadAllBytes(source);
            workspace.Collect(group.ActiveTab, [source]);
            Check(workspace.Desktop.VisibleItems.All(i => !i.Path.Equals(source, StringComparison.OrdinalIgnoreCase)), "收纳后不在桌面重复显示", checks);
            var payload = new DesktopDrag([source]).Data();
            Check(DesktopDrag.Read(payload).Single() == source, "拖拽数据同时支持分栏和桌面", checks);
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            workspace.ReturnToDesktop([source], new(screen.Left + 20, screen.Top + 20));
            Check(group.ActiveTab.Items.Count == 0 && workspace.Desktop.VisibleItems.Any(i => i.Path.Equals(source, StringComparison.OrdinalIgnoreCase)), "从分栏放回桌面", checks);
            Check(bytes.SequenceEqual(File.ReadAllBytes(source)), "原快捷方式内容和位置保持不变", checks);
            workspace.SetDesktopIntegration(false);
            Check(NativeDesktop.HasVisibleStyle(NativeDesktop.IconList()) == before, "关闭接管恢复原生图标", checks);
            if (waitForCrash)
            {
                workspace.SetDesktopIntegration(true);
                workspace.SaveNow();
                File.WriteAllText(Path.Combine(workspace.Store.DirectoryPath, "integration-ready.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId, originalVisible = before, checks }));
                return; // The external test harness terminates this process to test the guard.
            }
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            workspace.SaveNow(); Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            workspace.Report(error); workspace.Desktop.Dispose();
            checks.Add("FAILED: " + error);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(1);
        }
    }
    private static void Check(bool condition, string label, List<string> checks)
    { if (!condition) throw new InvalidOperationException(label); checks.Add("PASS: " + label); }
}
