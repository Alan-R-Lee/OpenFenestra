using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Interop;
using OpenFenestra.Core;

namespace OpenFenestra.App;

internal sealed class DesktopSurfaceWindow : Window
{
    private readonly WorkspaceController _workspace;
    private readonly Canvas _canvas = new();
    internal System.Drawing.Rectangle MonitorBounds { get; }
    private bool _closing;
    private HwndSource? _source;
    private Point _dragStart;
    private DesktopEntry? _pressed;
    private Border? _selection;
    private int _generation;
    internal int IconCount => _canvas.Children.Count;

    internal DesktopSurfaceWindow(WorkspaceController workspace, System.Drawing.Rectangle bounds)
    {
        _workspace = workspace; MonitorBounds = bounds;
        Title = "OpenFenestra · 桌面图标";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = App.DebugWindows; ShowActivated = false; AllowDrop = true;
        Left = 0; Top = 0; Width = Math.Max(100, bounds.Width); Height = Math.Max(100, bounds.Height);
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WndProc);
            NativeDesktop.SetBounds(this, MonitorBounds);
        };
        Loaded += (_, _) => NativeDesktop.SetBounds(this, MonitorBounds);
        Closing += (_, e) => { if (!_closing) e.Cancel = true; };
        DragOver += (_, e) => { e.Effects = DesktopDrag.Read(e.Data).Length > 0 ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            var paths = DesktopDrag.Read(e.Data);
            if (paths.Length == 0) return;
            var point = PointToScreen(e.GetPosition(this));
            _workspace.ReturnToDesktop(paths, new(point.X - 38, point.Y - 24));
            e.Effects = DragDropEffects.Link; e.Handled = true;
        };
    }

    internal void UpdateDragSurface() => _canvas.Background = _workspace.IsDragging
        ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : null;

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
        if (message != 0x0084) return 0; // WM_NCHITTEST
        if (_workspace.IsDragging) { handled = true; return 1; }
        var screen = new Point((short)((long)lParam & 0xffff), (short)(((long)lParam >> 16) & 0xffff));
        var point = PointFromScreen(screen);
        var hit = InputHitTest(point) as DependencyObject;
        while (hit is not null)
        {
            if (hit is Border { Tag: DesktopEntry }) return 0;
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : null;
        }
        handled = true; return -1; // Blank wallpaper remains clickable by Explorer.
    }

    internal async void RenderItems(IReadOnlyList<DesktopEntry> entries)
    {
        var generation = ++_generation;
        _canvas.Children.Clear(); _selection = null;
        var pending = new List<(Image Image, string Path)>();
        foreach (var entry in entries.Take(1000))
        {
            if (!_workspace.Layout.DesktopPositions.TryGetValue(entry.Path, out var stored)) continue;
            var physical = new System.Drawing.Point((int)stored.X, (int)stored.Y);
            if (!MonitorBounds.Contains(physical)) continue;
            var local = PointFromScreen(new(stored.X, stored.Y));
            var image = new Image { Width = 48, Height = 48, Margin = new Thickness(12, 4, 12, 4) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var label = new TextBlock { Text = entry.Name, Foreground = Brushes.White, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 12,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis,
                Background = FenceWindow.Brush("#101820", 0.20) };
            var stack = new StackPanel { Width = 86, Height = 98 }; stack.Children.Add(image); stack.Children.Add(label);
            var border = new Border { Child = stack, Tag = entry, Background = Brushes.Transparent, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), ToolTip = entry.Name };
            Canvas.SetLeft(border, Math.Clamp(local.X, 0, Math.Max(0, ActualWidth - 90)));
            Canvas.SetTop(border, Math.Clamp(local.Y, 0, Math.Max(0, ActualHeight - 100)));
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (_selection is not null) { _selection.Background = Brushes.Transparent; _selection.BorderBrush = null; }
                _selection = border; border.Background = FenceWindow.Brush("#78A6D6", 0.24); border.BorderBrush = FenceWindow.Brush("#B6D4F3", 0.5);
                _pressed = entry; _dragStart = e.GetPosition(this);
                if (e.ClickCount == 2)
                {
                    try { ShellFiles.Open(entry.Path); } catch (Exception error) { MessageBox.Show(error.Message, "OpenFenestra"); }
                    _pressed = null;
                }
                e.Handled = true;
            };
            border.MouseMove += (_, e) =>
            {
                var point = e.GetPosition(this);
                if (e.LeftButton != MouseButtonState.Pressed || _pressed is null || Math.Abs(point.X - _dragStart.X) + Math.Abs(point.Y - _dragStart.Y) < 10) return;
                var path = _pressed.Path; _pressed = null;
                _workspace.DragItems(border, [path]);
            };
            border.ContextMenu = new ContextMenu();
            var open = new MenuItem { Header = "打开" }; open.Click += (_, _) => ShellFiles.Open(entry.Path); border.ContextMenu.Items.Add(open);
            var collect = new MenuItem { Header = "收纳到分栏" };
            foreach (var group in _workspace.Layout.Groups)
                foreach (var tab in group.Tabs.Where(t => !t.IsPortal))
                {
                    var menu = new MenuItem { Header = group.Name + " / " + tab.Name };
                    menu.Click += (_, _) => _workspace.Collect(tab, [entry.Path]); collect.Items.Add(menu);
                }
            border.ContextMenu.Items.Add(collect);
            _canvas.Children.Add(border); pending.Add((image, entry.Path));
        }
        try
        {
            var pixels = Math.Clamp((int)Math.Ceiling(48 * VisualTreeHelper.GetDpi(this).DpiScaleX * 2), 96, 256);
            foreach (var (image, path) in pending)
            {
                var icon = await Task.Run(() => ShellFiles.Icon(path, pixels));
                if (_closing || generation != _generation) return;
                image.Source = icon;
            }
        }
        catch (Exception error) { _workspace.Report(error); }
    }
    internal void DisposeSurface() { _closing = true; _generation++; _source?.RemoveHook(WndProc); Close(); }
}
