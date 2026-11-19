using OpenFenestra.Core;

namespace OpenFenestra.App;

internal abstract class EditorDialog : Window
{
    internal static int OpenDialogCount { get; private set; }
    private bool _registered;
    protected readonly StackPanel Fields = new() { Margin = new Thickness(26) };
    protected EditorDialog(string title)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = title; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40);
        Content = new ScrollViewer { Content = Fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => { if (!_registered) { _registered = true; OpenDialogCount++; } };
        Closed += (_, _) => { if (_registered) { _registered = false; OpenDialogCount--; } };
        Fields.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
    }
    protected void Label(string text) => Fields.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 7) });
    protected TextBox Input(string text) { var box = new TextBox { Text = text }; Fields.Children.Add(box); return box; }
    protected void Hint(string text) => Fields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = FenceWindow.Brush("#7B887C"), FontSize = 11, Margin = new Thickness(0, 9, 0, 0) });
    protected void Buttons(Action accept, Action? remove = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 24, 0, 0), LastChildFill = false };
        var save = new Button { Content = "保存", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        save.Click += (_, _) => accept(); DockPanel.SetDock(save, Dock.Right); row.Children.Add(save);
        var cancel = new Button { Content = "取消", IsCancel = true }; DockPanel.SetDock(cancel, Dock.Right); row.Children.Add(cancel);
        if (remove is not null)
        {
            var delete = new Button { Content = "移除分组", Foreground = Brushes.White, Background = FenceWindow.Brush("#B94040") };
            delete.Click += (_, _) => remove(); row.Children.Add(delete);
        }
        Fields.Children.Add(row);
    }
}

internal sealed class GroupDialog : EditorDialog
{
    private readonly TextBox _name;
    private readonly ComboBox _color;
    private readonly Slider _opacity, _collapsedOpacity;
    private readonly ComboBox _iconSize;
    private readonly CheckBox _locked;
    internal TextBox WidthInput { get; }
    internal TextBox HeightInput { get; }
    internal bool DeleteRequested { get; private set; }
    private static readonly (string Name, string Color)[] Colors = [("苔绿", "#78B9A1"), ("雾蓝", "#8CA9DB"), ("丁香", "#C0A2D1"), ("麦黄", "#D6AE75"), ("玫瑰", "#D98F9D"), ("海盐", "#8BBFC8")];
    internal GroupDialog(FenceGroup group, bool allowDelete = false) : base(allowDelete ? "编辑分组" : "分组设置")
    {
        Label("分组名称"); _name = Input(group.Name); _name.MaxLength = 80;
        Label("强调色");
        _color = new ComboBox { ItemsSource = Colors.Select(c => c.Name).ToArray(), SelectedIndex = Math.Max(0, Array.FindIndex(Colors, c => c.Color == group.Color)), Padding = new Thickness(9) };
        Fields.Children.Add(_color);
        Label("展开时背景不透明度"); _opacity = new Slider { Minimum = 0.15, Maximum = 1, Value = group.Opacity, TickFrequency = 0.05, IsSnapToTickEnabled = true }; Fields.Children.Add(_opacity);
        Label("折叠时背景不透明度"); _collapsedOpacity = new Slider { Minimum = 0.06, Maximum = 0.65, Value = group.CollapsedOpacity, TickFrequency = 0.02, IsSnapToTickEnabled = true }; Fields.Children.Add(_collapsedOpacity);
        Label("图标大小"); _iconSize = new ComboBox { ItemsSource = new[] { 32, 40, 48, 64, 80 }, SelectedItem = group.IconSize, Padding = new Thickness(8) }; Fields.Children.Add(_iconSize);
        Label("展开尺寸（宽 × 高，随显示缩放）");
        var dimensions = new StackPanel { Orientation = Orientation.Horizontal };
        WidthInput = new TextBox { Text = Math.Round(group.Width).ToString(), Width = 105 };
        HeightInput = new TextBox { Text = Math.Round(group.Height).ToString(), Width = 105 };
        dimensions.Children.Add(WidthInput); dimensions.Children.Add(new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center });
        dimensions.Children.Add(HeightInput); Fields.Children.Add(dimensions);
        var presets = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        foreach (var preset in new[] { ("小", 240, 200), ("中", 340, 340), ("大", 480, 420) })
        {
            var button = new Button { Content = preset.Item1, Padding = new Thickness(14, 5, 14, 5) };
            button.Click += (_, _) => { WidthInput.Text = preset.Item2.ToString(); HeightInput.Text = preset.Item3.ToString(); };
            presets.Children.Add(button);
        }
        Fields.Children.Add(presets);
        Hint("宽度 220–2000，高度 180–2000；也可拖动分栏右下角调整。折叠时保留展开尺寸。");
        _locked = new CheckBox { Content = "锁定分组的位置与大小", IsChecked = group.Locked }; Fields.Children.Add(_locked);
        Hint("创建后可通过面板的 + 添加标签页，或映射文件夹。");
        Buttons(() =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show(this, "请填写分组名称。", "OpenFenestra"); return; }
            if (!TryDimensions(out _, out _)) { MessageBox.Show(this, "请输入有效尺寸：宽 220–2000，高 180–2000。", "OpenFenestra"); return; }
            DialogResult = true;
        }, allowDelete && !group.IsRecent ? () => { DeleteRequested = true; DialogResult = false; } : null);
    }
    internal bool TryDimensions(out double width, out double height)
    {
        var validWidth = double.TryParse(WidthInput.Text, out width);
        var validHeight = double.TryParse(HeightInput.Text, out height);
        return validWidth && validHeight && double.IsFinite(width) && double.IsFinite(height)
            && width is >= 220 and <= 2000 && height is >= 180 and <= 2000;
    }
    internal void Apply(FenceGroup group)
    { if (!TryDimensions(out var width, out var height)) throw new InvalidOperationException("分栏尺寸无效。");
      group.Width = width; group.Height = height; group.Name = _name.Text.Trim(); group.Color = Colors[_color.SelectedIndex].Color; group.Opacity = _opacity.Value; group.CollapsedOpacity = _collapsedOpacity.Value; group.IconSize = _iconSize.SelectedItem is int size ? size : 48; group.Locked = _locked.IsChecked == true; }
}

internal sealed class TabDialog : EditorDialog
{
    private readonly TextBox _name, _folder;
    internal TabDialog(FenceTab tab, bool allowPortal = true) : base("编辑标签页")
    {
        Label("标签页名称"); _name = Input(tab.Name); _name.MaxLength = 80;
        Label("映射文件夹（可选）"); _folder = Input(tab.FolderPath ?? "");
        var browse = new Button { Content = "浏览文件夹…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        browse.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFolderDialog(); if (picker.ShowDialog(this) == true) _folder.Text = picker.FolderName; };
        Fields.Children.Add(browse);
        _folder.IsEnabled = browse.IsEnabled = allowPortal;
        Hint("留空：收纳文件引用。选择文件夹：只读展示实时内容。切换映射不会删除此前保存的引用。");
        Buttons(() =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show(this, "请填写标签页名称。", "OpenFenestra"); return; }
            if (!string.IsNullOrWhiteSpace(_folder.Text) && (FileCatalog.NormalizePath(_folder.Text) is null || !Directory.Exists(_folder.Text)))
            { MessageBox.Show(this, "请选择一个可以访问的文件夹。", "OpenFenestra"); return; }
            DialogResult = true;
        });
    }
    internal void Apply(FenceTab tab) { tab.Name = _name.Text.Trim(); tab.FolderPath = string.IsNullOrWhiteSpace(_folder.Text) ? null : FileCatalog.NormalizePath(_folder.Text); }
}
