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
            workspace.Layout.AutoCollapseIdle = false;
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
            var tabBar = (DockPanel)panel.FindName("TabBar");
            var addTab = (Button)panel.FindName("AddTabButton");
            Check(group.Tabs.Count == 1 && tabBar.IsVisible && addTab.IsVisible && addTab.IsEnabled,
                "单标签页仍显示可用的添加按钮", checks);
            var otherGroup = new FenceGroup { Name = "透明度验证", Collapsed = true, X = 100000, Y = 100000 };
            workspace.AddGroup(otherGroup);
            await Task.Delay(100);
            var recoveredPanel = workspace.Panels.Single(p => p.Model == otherGroup);
            workspace.Notify();
            Check(otherGroup.X < 100000 && otherGroup.Y < 100000 && recoveredPanel.Left == otherGroup.X
                && recoveredPanel.Top == otherGroup.Y, "屏幕外分栏恢复位置写回布局且刷新后仍可见", checks);
            using (var nativeMenu = new ShellContextMenu.Session(note))
                Check(nativeMenu.CommandCount > 0, "真实文件的 Windows Shell 菜单可构建", checks);
            using (var nativeMenu = new ShellContextMenu.Session(folder))
                Check(nativeMenu.CommandCount > 0, "文件夹的 Windows Shell 菜单可构建", checks);
            using (var nativeMenu = new ShellContextMenu.Session("::{645FF040-5081-101B-9F08-00AA002F954E}"))
                Check(nativeMenu.CommandCount > 0, "虚拟回收站的 Windows Shell 菜单可构建", checks);
            var merged = ShellFiles.MergeDesktopEntries([samples, folder], [new DesktopEntry(note, "Shell 名称", false)]);
            Check(merged.Count(e => e.Path == note) == 1 && merged.Single(e => e.Path == note).Name == "Shell 名称"
                && merged.Any(e => e.Path == folder), "目录先于 Explorer 更新时仍能发现文件与文件夹并按完整路径消重", checks);
            var container = (ListBoxItem)((ListBox)panel.FindName("Items")).ItemContainerGenerator.ContainerFromIndex(0);
            for (var click = 0; click < 2; click++)
                container.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            Check(panel.AppItemMenu?.IsOpen == true && panel.AppItemMenu.Items.OfType<MenuItem>()
                .Any(i => (string)i.Header == "放回桌面（保留原文件）"), "双击右键路由打开本应用项目菜单", checks);
            panel.AppItemMenu!.IsOpen = false;
            var previousPosition = new Point(panel.Left, panel.Top);
            workspace.ReorderGroup(otherGroup.Id, group.Id, false);
            Check(workspace.Layout.Groups[0].Id == otherGroup.Id
                && ((ItemsControl)workspace.Manager.FindName("GroupCards")).Items.Cast<GroupCard>().First().Id == otherGroup.Id
                && panel.Left == previousPosition.X && panel.Top == previousPosition.Y,
                "调整分组顺序同步控制台且不立即移动桌面分栏", checks);
            workspace.ArrangePanels();
            Check(otherGroup.Y < group.Y || (otherGroup.Y == group.Y && otherGroup.X < group.X),
                "整理分栏位置遵循调整后的分组顺序", checks);
            var originalItems = ((ListBox)panel.FindName("Items")).ItemsSource;
            var expandedSlider = (Slider)workspace.Manager.FindName("ExpandedTransparency");
            var collapsedSlider = (Slider)workspace.Manager.FindName("CollapsedTransparency");
            expandedSlider.Value = 65;
            collapsedSlider.Value = 89;
            Check(workspace.Layout.Groups.All(g => Math.Abs(g.Opacity - 0.35) < 0.001 && Math.Abs(g.CollapsedOpacity - 0.11) < 0.001),
                "控制台透明度滑块同步展开与折叠分栏", checks);
            Check(workspace.Panels.All(p => Math.Abs(((SolidColorBrush)((Border)p.FindName("Surface")).Background).Color.A
                    - (p.Model.Collapsed ? 0.11 : 0.35) * 255) < 1.1)
                && ReferenceEquals(originalItems, ((ListBox)panel.FindName("Items")).ItemsSource),
                "透明度实时生效且不重建图标列表", checks);
            workspace.SaveNow();
            Check(workspace.Store.Load().Groups.All(g => Math.Abs(g.Opacity - 0.35) < 0.001 && Math.Abs(g.CollapsedOpacity - 0.11) < 0.001),
                "统一透明度保存到布局", checks);
            expandedSlider.Value = 42; collapsedSlider.Value = 80;
            workspace.RemoveGroup(otherGroup);
            group.Width = 350; group.Height = 370; workspace.Notify();
            var startupXml = System.Xml.Linq.XElement.Parse(StartupRegistration.BuildTaskXml(@"C:\测试 & tools\OpenFenestra.exe", "--tray --portable", "S-1-5-21-123"));
            System.Xml.Linq.XNamespace ns = startupXml.Name.Namespace;
            Check(startupXml.Descendants(ns + "Command").Single().Value == @"C:\测试 & tools\OpenFenestra.exe"
                && !startupXml.Descendants(ns + "Delay").Any()
                && startupXml.Descendants(ns + "LogonType").Single().Value == "InteractiveToken"
                && startupXml.Descendants(ns + "ExecutionTimeLimit").Single().Value == "PT0S",
                "登录任务无延迟、交互会话、无超时且安全保存路径", checks);
            group.X = 440; group.Y = 180; workspace.Notify();
            Check(panel.Left == 440 && panel.Top == 180 && group.X == 440 && group.Y == 180,
                "同时更新横纵位置不被窗口事件覆盖", checks);
            panel.HandleHeaderClick(2); await Task.Delay(220);
            Trace($"folded: model={group.Collapsed}, height={panel.Height}, actual={panel.ActualHeight}");
            Check(group.Collapsed && Math.Abs(panel.Height - 34) < 1, "折叠标题栏", checks);
            Check(!tabBar.IsVisible, "折叠时隐藏标签栏", checks);
            Check(panel.Opacity < 1 && ((SolidColorBrush)((Border)panel.FindName("Surface")).Background).Color.A < group.Opacity * 255, "折叠后提高透明度", checks);
            panel.HandleHeaderClick(1); await Task.Delay(220);
            Check(!group.Collapsed && Math.Abs(panel.Height - 370) < 1, "展开恢复高度", checks);
            Check(addTab.IsVisible, "展开后恢复添加标签页按钮", checks);
            Check(Math.Abs(panel.Opacity - 1) < 0.01, "单击展开状态恢复透明度", checks);
            Trace("before high-resolution extraction");
            var shellIcon = await Task.Run(() => ShellFiles.Icon(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\explorer.exe", 128)).WaitAsync(TimeSpan.FromSeconds(8));
            Trace("after high-resolution extraction");
            Check(shellIcon is System.Windows.Media.Imaging.BitmapSource { PixelWidth: >= 96 }, "高分辨率 Shell 图标提取", checks);
            Check(ReferenceEquals(shellIcon, await ShellFiles.IconAsync(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\explorer.exe", 128)),
                "异步图标请求复用已加载的高清图标", checks);
            var cancelled = false;
            try { await ShellFiles.IconAsync(note, 128, new System.Threading.CancellationToken(true)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "过期图标请求可以在进入 Shell 队列前取消", checks);
            var portal = new FenceTab { Name = "灵感资料", FolderPath = folder };
            group.Tabs.Add(portal); group.SelectedTabId = portal.Id; workspace.Notify();
            Check(((StackPanel)panel.FindName("Tabs")).Children.Count == 2, "标签页切换", checks);
            Check(addTab.IsVisible, "多标签页仍保留添加按钮", checks);
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
            ((RadioButton)workspace.Manager.FindName("SettingsNavigation")).IsChecked = true;
            Capture(workspace.Manager, Path.Combine(workspace.Store.DirectoryPath, "settings-preview.png"));
            ((RadioButton)workspace.Manager.FindName("GroupsNavigation")).IsChecked = true;
            Capture(workspace.Manager, Path.Combine(workspace.Store.DirectoryPath, "manager-preview.png"));
            var cards = (ItemsControl)workspace.Manager.FindName("GroupCards");
            var originalCards = cards.ItemsSource;
            var groupScroll = (ScrollViewer)workspace.Manager.FindName("GroupScroll");
            cards.ItemsSource = Enumerable.Range(1, 9).Select(i => new GroupCard(i.ToString(), "滚动预览分组 " + i, "#78B9A1", "1 个标签页 · 拖动排序预览", "隐藏")).ToArray();
            workspace.Manager.UpdateLayout(); groupScroll.ScrollToBottom(); workspace.Manager.UpdateLayout();
            Capture(workspace.Manager, Path.Combine(workspace.Store.DirectoryPath, "groups-scrolled-preview.png"));
            cards.ItemsSource = originalCards; groupScroll.ScrollToTop(); workspace.Manager.UpdateLayout();
            Capture(workspace.Panels.Single(), Path.Combine(workspace.Store.DirectoryPath, "panel-preview.png"));
            workspace.Layout.Groups[0].Name = "退出保存验证";
            workspace.ScheduleSave(); workspace.Dispose();
            Check(workspace.Store.Load().Groups[0].Name == "退出保存验证", "退出时立即保存待处理布局", checks);
            using (var loginWorkspace = new WorkspaceController(workspace.Store))
            {
                loginWorkspace.Start(false, collapseOnStart: true, captureDesktop: false);
                await Task.Delay(250);
                var loginPanel = loginWorkspace.Panels.Single();
                Check(loginPanel.Model.Collapsed && loginPanel.Height == 34 && !loginWorkspace.Manager.IsVisible,
                    "登录启动时分栏直接折叠且管理窗口不弹出", checks);
                Check(((ListBox)loginPanel.FindName("Items")).Items.Count == 0,
                    "折叠启动不提前加载不可见的图标列表", checks);
                loginPanel.HandleHeaderClick(1);
                Check(!loginPanel.Model.Collapsed && loginPanel.Height == 370 && ((ListBox)loginPanel.FindName("Items")).Items.Count == 4,
                    "登录折叠后单击恢复原展开高度", checks);
                var inboxFile = Path.Combine(samples, "最近新增.txt"); File.WriteAllText(inboxFile, "inbox fixture");
                var recent = RecentItems.Ensure(loginWorkspace.Layout)!;
                loginWorkspace.Layout.AutoOrganize = true;
                var groupCount = loginWorkspace.Layout.Groups.Count;
                Check(loginWorkspace.CaptureRecent([FileCatalog.Entry(inboxFile)]) == 1
                    && recent.ActiveTab.Items.Contains(inboxFile) && loginWorkspace.Layout.Groups.Count == groupCount,
                    "旧自动归类设置开启时新增项目仍留在最近且不创建类型分栏", checks);
                Check(loginWorkspace.CaptureRecent([FileCatalog.Entry(inboxFile)]) == 0,
                    "周期补扫不重复收纳最近项目", checks);
                await Task.Delay(150);
                var recentPanel = loginWorkspace.Panels.Single(p => p.Model.IsRecent);
                var organize = (Button)recentPanel.FindName("OrganizeRecentButton");
                Check(organize.IsVisible && ((ListBox)recentPanel.FindName("Items")).Items.Count == 1,
                    "最近分栏展示新增项目和一键归类按钮", checks);
                Capture(recentPanel, Path.Combine(workspace.Store.DirectoryPath, "recent-preview.png"));
                organize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(recent.ActiveTab.Items.Count == 0 && loginWorkspace.Layout.Groups.Any(g => !g.IsRecent && g.ActiveTab.Items.Contains(inboxFile))
                    && File.ReadAllText(inboxFile) == "inbox fixture",
                    "一键归类按钮转移引用且保留原文件", checks);
                var firstDestination = loginWorkspace.Panels.Single(p => !p.Model.IsRecent && p.Model.ActiveTab.Items.Contains(inboxFile));
                Check(!firstDestination.Model.Collapsed && firstDestination.IsVisible
                    && ((ListBox)firstDestination.FindName("Items")).SelectedItem is EntryView { IsLocated: true } located && located.Path == inboxFile,
                    "一键归类后自动展开目标分栏并选中高亮新图标", checks);
                var document = Path.Combine(samples, "第二批文档.txt"); File.WriteAllText(document, "document fixture");
                var picture = Path.Combine(samples, "第二批图片.png"); File.WriteAllBytes(picture, [1, 2, 3]);
                var link = Path.Combine(samples, "第二批快捷方式.url"); File.WriteAllText(link, "[InternetShortcut]\nURL=https://example.com");
                var unavailable = Path.Combine(samples, "尚未同步.txt");
                var apps = new FenceGroup { Name = "应用与快捷方式", Visible = false, Collapsed = true, X = 500, Y = 120, ItemSort = ItemSortOrder.Original };
                apps.ActiveTab.Name = "应用";
                apps.ActiveTab.Items.AddRange(Enumerable.Range(0, 301).Select(i => Path.Combine(samples, $"旧引用-{i}.lnk")));
                var alternateTab = new FenceTab { Name = "另一页" }; apps.Tabs.Add(alternateTab);
                loginWorkspace.AddGroup(apps);
                firstDestination.Model.Collapsed = true;
                ((TextBox)firstDestination.FindName("Search")).Text = "此前的搜索条件";
                loginWorkspace.CaptureRecent(new[] { document, picture, link }.Select(FileCatalog.Entry));
                recent.ActiveTab.Items.Add(unavailable);
                organize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150);
                var destinationLinks = (WrapPanel)recentPanel.FindName("DestinationLinks");
                Check(destinationLinks.Children.Count == 3 && recent.ActiveTab.Items.SequenceEqual([unavailable])
                    && ((TextBlock)recentPanel.FindName("LocationText")).Text.Contains("1 个留在最近"),
                    "混合类型批次显示各自去向且未成功归类的引用留在最近", checks);
                var appsPanel = loginWorkspace.Panels.Single(p => p.Model == apps);
                var appItems = (ListBox)appsPanel.FindName("Items");
                Check(appsPanel.IsVisible && !apps.Collapsed && appItems.Items.Count == 300
                    && appItems.Items[0] is EntryView { IsLocated: true } newApp && newApp.Path == link
                    && apps.ActiveTab.Items[^1] == link,
                    "超过300项的隐藏折叠分栏展开后优先展示新增图标且保存顺序保持", checks);
                Check(((ListBox)firstDestination.FindName("Items")).Items.OfType<EntryView>().Where(v => v.IsLocated)
                    .Select(v => v.Path).SequenceEqual([document]) && ((TextBox)firstDestination.FindName("Search")).Text == "",
                    "新批次替换旧高亮并清除妨碍定位的搜索条件", checks);
                Capture(recentPanel, Path.Combine(workspace.Store.DirectoryPath, "organization-destinations-preview.png"));
                Capture(appsPanel, Path.Combine(workspace.Store.DirectoryPath, "located-items-preview.png"));
                var appLink = destinationLinks.Children.OfType<Button>().Single(b => b.Content.ToString()!.StartsWith("应用与快捷方式 /"));
                apps.SelectedTabId = alternateTab.Id; apps.Collapsed = true; apps.Visible = false;
                ((TextBox)appsPanel.FindName("Search")).Text = "不会匹配";
                loginWorkspace.Notify();
                appLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(apps.Visible && !apps.Collapsed && apps.ActiveTab != alternateTab
                    && ((TextBox)appsPanel.FindName("Search")).Text == ""
                    && appItems.SelectedItem is EntryView { IsLocated: true } selectedApp && selectedApp.Path == link,
                    "点击去向重新显示目标分栏并切回对应标签页和图标", checks);
                ((Button)appsPanel.FindName("ClearLocationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((Border)appsPanel.FindName("LocationArea")).Visibility == Visibility.Collapsed
                    && appItems.Items.OfType<EntryView>().All(v => !v.IsLocated)
                    && appItems.Items.OfType<EntryView>().First().Path == apps.ActiveTab.Items[0],
                    "清除定位恢复原显示顺序并移除高亮提示", checks);
                DesktopAssignments.Collect(loginWorkspace.Layout, firstDestination.Model.ActiveTab, [link]);
                appLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(loginWorkspace.Status.Contains("归属已变化") && File.ReadAllText(document) == "document fixture",
                    "旧去向中的项目改属后提示变化并保留源文件", checks);
                var documentItems = (ListBox)firstDestination.FindName("Items");
                Check(documentItems.Items.OfType<EntryView>().Single(v => v.Path == document).IsNew,
                    "新加入的图标持续显示新增标记", checks);
                var sortButton = (Button)firstDestination.FindName("SortButton");
                sortButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var frequentOption = sortButton.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "常用优先");
                frequentOption.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); sortButton.ContextMenu.IsOpen = false;
                ((Button)firstDestination.FindName("ClearLocationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                loginWorkspace.RecordItemOpened(inboxFile); loginWorkspace.RecordItemOpened(inboxFile);
                await Task.Delay(150);
                Trace("activity sort: " + firstDestination.Model.ItemSort + " collapsed=" + firstDestination.Model.Collapsed + " views="
                    + JsonSerializer.Serialize(documentItems.Items.OfType<EntryView>().Select(v => new { v.Name, v.OpenCount, v.IsNew, v.IsLocated })));
                Check(firstDestination.Model.ItemSort == ItemSortOrder.FrequentFirst
                    && documentItems.Items[0] is EntryView { OpenCount: 2, IsNew: false } popular && popular.Path == inboxFile
                    && documentItems.Items.OfType<EntryView>().Single(v => v.Path == document).IsNew,
                    "常用优先按本机打开次数排序且未使用的新图标保留新增标记", checks);
                sortButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                sortButton.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "新加入优先")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); sortButton.ContextMenu.IsOpen = false;
                Check(documentItems.Items[0] is EntryView { IsNew: true }, "排序菜单可切换到新加入优先", checks);
                try { loginWorkspace.OpenItem(unavailable); }
                catch (FileNotFoundException) { }
                Check(!loginWorkspace.Layout.ItemActivity.ContainsKey(unavailable), "打开失败不会计入使用次数", checks);
                loginWorkspace.SaveNow();
                var savedActivity = loginWorkspace.Store.Load();
                Check(savedActivity.ItemActivity[inboxFile].OpenCount == 2
                    && savedActivity.Groups.Single(g => g.Id == firstDestination.Model.Id).ItemSort == ItemSortOrder.NewestFirst,
                    "使用次数和分栏排序偏好可保存恢复", checks);
                Capture(firstDestination, Path.Combine(workspace.Store.DirectoryPath, "new-items-and-sort-preview.png"));
                loginWorkspace.SetArrangement(PanelArrangementMode.Free);
                {
                    var sizeDialog = new GroupDialog(firstDestination.Model);
                    sizeDialog.WidthInput.Text = "224"; sizeDialog.HeightInput.Text = "280";
                    sizeDialog.Apply(firstDestination.Model); loginWorkspace.Notify();
                    Check(firstDestination.Width == 224 && firstDestination.Height == 280,
                        "分栏编辑器可精确设置独立宽高并支持更窄分栏", checks);
                    sizeDialog.WidthInput.Text = "NaN";
                    Check(!sizeDialog.TryDimensions(out _, out _), "分栏尺寸拒绝无效数值", checks);
                    sizeDialog.Close();
                }
                ((System.Windows.Controls.Primitives.Thumb)firstDestination.FindName("ResizeGrip")).RaiseEvent(
                    new System.Windows.Controls.Primitives.DragDeltaEventArgs(16, 20) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
                Check(firstDestination.Model.Width == 240 && firstDestination.Model.Height == 300,
                    "右下角拖拽缩放独立更新并保存分栏尺寸", checks);
                var freeGeometry = loginWorkspace.Layout.Groups.ToDictionary(g => g.Id, g => new PanelBounds(g.X, g.Y, g.Width, g.Height, g.Collapsed));
                var arrangement = (ComboBox)loginWorkspace.Manager.FindName("ArrangementSetting");
                arrangement.SelectedIndex = (int)PanelArrangementMode.CompactGrid;
                Check(loginWorkspace.Layout.Arrangement == PanelArrangementMode.CompactGrid
                    && loginWorkspace.Layout.FreePlacements[firstDestination.Model.Id] == freeGeometry[firstDestination.Model.Id],
                    "设置页切换紧凑网格且单独保存自由布局", checks);
                loginWorkspace.ShowManager(); loginWorkspace.Manager.NavigateToSettings();
                await Task.Delay(150);
                Capture(loginWorkspace.Manager, Path.Combine(workspace.Store.DirectoryPath, "arrangement-settings-preview.png"));
                loginWorkspace.Manager.Hide();
                arrangement.SelectedIndex = (int)PanelArrangementMode.Shelf;
                Check(loginWorkspace.Shelf?.IsVisible == true && loginWorkspace.Panels.All(p => !p.IsVisible)
                    && loginWorkspace.Shelf.GroupButtons.Count() == loginWorkspace.Layout.Groups.Count(g => g.Visible),
                    "折叠栏架保留所有标题并收起内容窗口", checks);
                loginWorkspace.Shelf!.GroupButtons.First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(loginWorkspace.Panels.Count(p => p.IsVisible) == 1,
                    "点击栏架标题只展开一个对应分栏", checks);
                loginWorkspace.Shelf.GroupButtons.Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var openPanel = loginWorkspace.Panels.Single(p => p.IsVisible);
                Check(openPanel.Model == loginWorkspace.Layout.Groups.Last(g => g.Visible)
                    && openPanel.Left + openPanel.Width < loginWorkspace.Shelf.Left,
                    "栏架切换时收起上一栏且内容在架旁展开", checks);
                Capture(loginWorkspace.Shelf, Path.Combine(workspace.Store.DirectoryPath, "shelf-preview.png"));
                loginWorkspace.SaveNow();
                Check(loginWorkspace.Store.Load().Arrangement == PanelArrangementMode.Shelf,
                    "栏架模式和自由布局备份可保存恢复", checks);
                arrangement.SelectedIndex = (int)PanelArrangementMode.Free;
                Trace("restore free geometry: " + JsonSerializer.Serialize(loginWorkspace.Layout.Groups.Select(g => new { g.Name, Expected = freeGeometry[g.Id], Actual = new PanelBounds(g.X, g.Y, g.Width, g.Height, g.Collapsed) })));
                Check(loginWorkspace.Layout.Groups.All(g => freeGeometry[g.Id] == new PanelBounds(g.X, g.Y, g.Width, g.Height, g.Collapsed))
                    && loginWorkspace.Shelf.IsVisible == false,
                    "切回自由排布恢复原位置宽高与折叠状态", checks);
                for (var index = 0; index < 30; index++) loginWorkspace.Layout.Groups.Add(new FenceGroup { Name = "示例分栏 " + (index + 1) });
                loginWorkspace.SetArrangement(PanelArrangementMode.Shelf);
                Check(loginWorkspace.Shelf.Height <= SystemParameters.WorkArea.Height - 24 && loginWorkspace.Shelf.GroupButtons.Count() > 12,
                    "超过十二分栏时栏架通过滚动列表保持窗口在工作区内", checks);
                Capture(loginWorkspace.Shelf, Path.Combine(workspace.Store.DirectoryPath, "shelf-many-preview.png"));
                loginWorkspace.SetArrangement(PanelArrangementMode.Free);
                loginWorkspace.Layout.AutoCollapseIdle = true;
                loginWorkspace.SetArrangement(PanelArrangementMode.Shelf);
                loginWorkspace.Shelf!.GroupButtons.First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(loginWorkspace.Panels.Count(p => p.IsVisible) == 1, "闲置收起启用后栏架标题仍可正常展开", checks);
                await Task.Delay(5500);
                Check(loginWorkspace.Panels.All(p => !p.IsVisible) && loginWorkspace.Shelf.IsVisible,
                    "栏架展开后五秒无操作自动收起并保留标题列表", checks);


            }
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
