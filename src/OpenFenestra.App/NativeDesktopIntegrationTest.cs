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
        var original = NativeDesktopShell.ReadSnapshot();
        var resultPath = Path.Combine(workspace.Store.DirectoryPath, "integration-result.json");
        try
        {
            var group = new FenceGroup { Name = "原生整合验证", X = 160, Y = 100 };
            workspace.AddGroup(group);
            workspace.SetDesktopIntegration(true);
            await Task.Delay(250);
            Check(workspace.Desktop.Enabled && workspace.Desktop.Capabilities == 15, "绘制、选择、输入和 OLE 保护全部就绪", checks);
            Check(NativeDesktop.HasVisibleStyle(NativeDesktop.IconList()) == before, "Explorer 原生图标列表保持原可见状态", checks);
            Check(Application.Current.Windows.Cast<Window>().All(w => !w.Title.EndsWith("· 桌面图标", StringComparison.Ordinal)), "没有全屏桌面图标替代窗口", checks);
            var source = original.Items.FirstOrDefault(i => File.Exists(i.Path) && Path.GetExtension(i.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("没有可验证的桌面快捷方式。");
            var bytes = File.ReadAllBytes(source.Path);
            workspace.Collect(group.ActiveTab, [source.Path]);
            await WaitForCount(workspace.Desktop, 1);
            Check(workspace.Desktop.HiddenCount == 1, "收纳后同一原生项目逐项停止绘制", checks);
            Check(NativeDesktopShell.ReadSnapshot().Items.Any(i => i.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase)), "原 Shell 项目和原文件仍存在", checks);
            var payload = new DesktopDrag([source.Path]).Data(false);
            Check(DesktopDrag.Read(payload).Single() == source.Path && !payload.GetDataPresent(DataFormats.FileDrop), "内部拖拽不触发 Explorer 复制或删除源文件", checks);
            var screenPoint = NativeDesktopShell.ViewToScreen(source.Position);
            workspace.ReturnToDesktop([source.Path], new(screenPoint.X, screenPoint.Y));
            await WaitForCount(workspace.Desktop, 0);
            Check(group.ActiveTab.Items.Count == 0 && workspace.Desktop.HiddenCount == 0, "放回桌面恢复原生项目绘制", checks);
            Check(bytes.SequenceEqual(File.ReadAllBytes(source.Path)), "原快捷方式路径与字节保持不变", checks);
            Check(NativeDesktopShell.ReadSnapshot().FolderFlags == original.FolderFlags, "自动排列和网格设置保持原值", checks);
            workspace.SetDesktopIntegration(false);
            Check(!workspace.Desktop.Enabled && NativeDesktop.HasVisibleStyle(NativeDesktop.IconList()) == before, "关闭原生整合恢复桌面并脱离组件", checks);
            if (waitForCrash)
            {
                workspace.SetDesktopIntegration(true); workspace.Collect(group.ActiveTab, [source.Path]); workspace.SaveNow();
                await WaitForCount(workspace.Desktop, 1);
                Check(workspace.Desktop.HiddenCount == 1, "异常恢复测试准备完成", checks);
                File.WriteAllText(Path.Combine(workspace.Store.DirectoryPath, "integration-ready.json"), JsonSerializer.Serialize(new { processId = Environment.ProcessId, originalVisible = before, checks }));
                return;
            }
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            workspace.SaveNow(); Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            workspace.Report(error); workspace.Desktop.Dispose(); checks.Add("FAILED: " + error);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(1);
        }
    }
    private static void Check(bool condition, string label, List<string> checks)
    { if (!condition) throw new InvalidOperationException(label); checks.Add("PASS: " + label); }
    private static async Task WaitForCount(DesktopIntegration desktop, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (desktop.HiddenCount != count && DateTime.UtcNow < deadline) await Task.Delay(80);
    }
}
