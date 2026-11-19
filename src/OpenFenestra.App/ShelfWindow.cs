using OpenFenestra.Core;

namespace OpenFenestra.App;

internal sealed class ShelfWindow : Window
{
    private readonly WorkspaceController _workspace;
    private readonly StackPanel _groups = new();
    private readonly ScrollViewer _scroll;
    private bool _closing;
    internal IEnumerable<Button> GroupButtons => _groups.Children.OfType<Button>();
    internal PanelBounds Bounds => new(Left, Top, Width, Height);

    internal ShelfWindow(WorkspaceController workspace)
    {
        _workspace = workspace;
        Title = "OpenFenestra · 折叠栏架";
        Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = App.DebugWindows; ShowActivated = false; Width = 220;
        var root = new DockPanel { Margin = new Thickness(7) };
        var header = new DockPanel { Margin = new Thickness(5, 3, 2, 8) };
        var manage = new Button { Content = "设置", Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0), FontSize = 11 };
        manage.Click += (_, _) => { workspace.ShowManager(); workspace.Manager.NavigateToSettings(); }; DockPanel.SetDock(manage, Dock.Right); header.Children.Add(manage);
        header.Children.Add(new TextBlock { Text = "分栏架", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _scroll = new ScrollViewer { Content = _groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(_scroll);
        Content = new Border { Background = FenceWindow.Brush("#18242B", 0.92), BorderBrush = FenceWindow.Brush("#FFFFFF", 0.18), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = root };
        Loaded += (_, _) => workspace.ApplyDesktopZOrder();
        Closing += (_, e) => { if (!_closing && !workspace.Exiting) e.Cancel = true; };
    }

    internal void Refresh()
    {
        var area = SystemParameters.WorkArea;
        var visible = _workspace.Layout.Groups.Where(g => g.Visible).ToArray();
        Height = Math.Min(Math.Max(88, visible.Length * 38 + 52), Math.Max(88, area.Height - 24));
        Left = area.Right - Width - 12; Top = area.Top + 12;
        var offset = _scroll.VerticalOffset;
        _groups.Children.Clear();
        foreach (var group in visible)
        {
            var row = new DockPanel();
            var count = new TextBlock { Text = group.ActiveTab.IsPortal ? "映射" : group.ActiveTab.Items.Count.ToString(), Foreground = FenceWindow.Brush("#B4C8BC"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), FontSize = 10 };
            DockPanel.SetDock(count, Dock.Right); row.Children.Add(count);
            row.Children.Add(new Border { Width = 3, Background = FenceWindow.Brush(group.Color), Margin = new Thickness(0, 2, 8, 2), CornerRadius = new CornerRadius(1) });
            row.Children.Add(new TextBlock { Text = group.Name, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, Height = 34, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(9, 6, 9, 6), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = FenceWindow.Brush(group.Color, group.Collapsed ? 0.10 : 0.35), ToolTip = group.Name + " · 点击展开 / 收起" };
            // The shared button template centers content; give the row a stable rack width.
            row.Width = 160;
            button.Click += (_, _) => _workspace.ToggleShelfGroup(group.Id);
            _groups.Children.Add(button);
        }
        _scroll.ScrollToVerticalOffset(offset);
    }

    internal void DisposeShelf() { _closing = true; Close(); }
}
