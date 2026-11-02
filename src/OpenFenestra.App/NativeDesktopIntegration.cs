using System.Threading;
using System.Windows.Interop;
using OpenFenestra.Core;

namespace OpenFenestra.App;

/// <summary>Coordinates per-item ownership without replacing the desktop.</summary>
internal sealed class DesktopIntegration(WorkspaceController workspace) : IDisposable
{
    private Mutex? _lease;
    private NativeShellBridgeClient? _bridge;
    private bool _ownsLease;
    private DateTime _retryAfter;
    internal bool Enabled => _bridge?.Connected == true;
    internal int HiddenCount => _bridge?.HiddenCount ?? 0;
    internal uint Capabilities => _bridge?.Capabilities ?? 0;
    internal void Enable()
    {
        if (Enabled) return;
        Dispose();
        _lease = new Mutex(false, "Local\\OpenFenestra-NativeDesktop");
        try { _ownsLease = _lease.WaitOne(0); } catch (AbandonedMutexException) { _ownsLease = true; }
        if (!_ownsLease) { _lease.Dispose(); _lease = null; throw new InvalidOperationException("另一个 OpenFenestra 实例正在管理原生桌面。"); }
        try
        {
            _bridge = new NativeShellBridgeClient(new WindowInteropHelper(workspace.Manager).EnsureHandle());
            _bridge.Connect(); _retryAfter = DateTime.MinValue; Refresh();
        }
        catch { Dispose(); throw; }
    }
    internal void Refresh()
    {
        if (Enabled) _bridge!.Update(DesktopAssignments.Assigned(workspace.Layout));
    }
    internal void Tick()
    {
        if (!workspace.Layout.DesktopIntegrationEnabled || DateTime.UtcNow < _retryAfter) return;
        try
        {
            if (!Enabled) { Enable(); workspace.ReportStatus("已重新连接 Explorer 原生桌面"); }
            _bridge!.Heartbeat();
            if ((_bridge.Capabilities & 15) != 15) throw new InvalidOperationException("原生桌面的选择或拖放保护已改变，停止隐藏图标。");
        }
        catch (Exception error)
        {
            workspace.Report(error); Dispose();
            _retryAfter = DateTime.UtcNow.AddSeconds(10);
            workspace.ReportStatus("原生桌面组件已断开，已停止隐藏图标；稍后自动重试。");
        }
    }
    public void Dispose()
    {
        _bridge?.Dispose(); _bridge = null;
        if (_lease is not null)
        {
            if (_ownsLease) _lease.ReleaseMutex();
            _lease.Dispose(); _lease = null; _ownsLease = false;
        }
    }
}
