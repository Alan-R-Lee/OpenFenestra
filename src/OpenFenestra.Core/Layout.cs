using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFenestra.Core;

public sealed class Layout
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public bool PinToDesktop { get; set; }
    public bool AutoOrganize { get; set; }
    public bool DesktopIntegrationEnabled { get; set; }
    public bool StartWithWindows { get; set; }
    public List<string> DesktopItems { get; set; } = [];
    public Dictionary<string, DesktopPosition> DesktopPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FenceGroup> Groups { get; set; } = [];
    public Dictionary<string, ItemActivity> ItemActivity { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PanelArrangementMode Arrangement { get; set; }
    public bool AutoCollapseIdle { get; set; } = true;
    public Dictionary<string, PanelBounds> FreePlacements { get; set; } = new(StringComparer.Ordinal);

    public Layout Clone() => LayoutCodec.Decode(LayoutCodec.Encode(this));

    public bool ReorderGroup(string sourceId, string targetId, bool insertAfter)
    {
        var source = Groups.FindIndex(g => g.Id == sourceId);
        var target = Groups.FindIndex(g => g.Id == targetId);
        if (source < 0 || target < 0 || source == target) return false;
        var destination = target + (insertAfter ? 1 : 0);
        if (source < destination) destination--;
        if (destination == source) return false;
        var group = Groups[source];
        Groups.RemoveAt(source); Groups.Insert(destination, group);
        return true;
    }
}

public sealed class UnsupportedLayoutVersionException(int version)
    : Exception($"不支持布局版本 {version}；请使用相应版本的 OpenFenestra，现有布局未改动。");

public sealed class FenceGroup
{
    public bool IsRecent { get; set; }
    public ItemSortOrder ItemSort { get; set; } = ItemSortOrder.FrequentFirst;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新分组";
    public string Color { get; set; } = "#78B9A1";
    public double Opacity { get; set; } = 0.58;
    public double CollapsedOpacity { get; set; } = 0.20;
    public int IconSize { get; set; } = 48;
    public double X { get; set; } = 120;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 340;
    public bool Collapsed { get; set; }
    public bool Locked { get; set; }
    public bool Visible { get; set; } = true;
    public string? SelectedTabId { get; set; }
    public List<FenceTab> Tabs { get; set; } = [new()];

    [JsonIgnore] public FenceTab ActiveTab => Tabs.FirstOrDefault(t => t.Id == SelectedTabId) ?? Tabs[0];
}

// Physical desktop pixels. Each monitor window transforms these into its own DIPs.
public sealed record DesktopPosition(double X, double Y);

public sealed class FenceTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "常用";
    public string? FolderPath { get; set; }
    public List<string> Items { get; set; } = [];
    [JsonIgnore] public bool IsPortal => !string.IsNullOrWhiteSpace(FolderPath);
}

public static class LayoutCodec
{
    public const int MaxBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Encode(Layout layout) => JsonSerializer.Serialize(layout, Options);

    public static Layout Decode(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new InvalidDataException("布局文件超过 8 MB。");
        var layout = JsonSerializer.Deserialize<Layout>(json, Options)
            ?? throw new InvalidDataException("布局文件为空。");
        if (layout.Version is < 1 or > Layout.CurrentVersion)
            throw new UnsupportedLayoutVersionException(layout.Version);
        layout.Version = Layout.CurrentVersion;
        if (!Enum.IsDefined(layout.Arrangement)) layout.Arrangement = PanelArrangementMode.Free;
        if (layout.FreePlacements is null || layout.FreePlacements.Count > 100)
            throw new InvalidDataException("自由布局记录无效。");
        layout.FreePlacements = layout.FreePlacements.Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value is not null)
            .ToDictionary(p => p.Key, p => new PanelBounds(Finite(p.Value.X, 120, -100000, 100000),
                Finite(p.Value.Y, 100, -100000, 100000), Finite(p.Value.Width, 340, PanelArrangement.MinimumWidth, 2000),
                Finite(p.Value.Height, 340, PanelArrangement.MinimumHeight, 2000), p.Value.Collapsed), StringComparer.Ordinal);
        if (layout.ItemActivity is null || layout.ItemActivity.Count > ItemActivityTracking.MaximumEntries)
            throw new InvalidDataException("项目使用记录无效或超过上限。");
        layout.ItemActivity = layout.ItemActivity.Where(p => DesktopAssignments.NormalizeItem(p.Key) is not null && p.Value is not null)
            .GroupBy(p => DesktopAssignments.NormalizeItem(p.Key)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key, p => new ItemActivity { AddedAtUtc = p.Last().Value.AddedAtUtc,
                LastOpenedAtUtc = p.Last().Value.LastOpenedAtUtc, OpenCount = Math.Max(0, p.Last().Value.OpenCount) }, StringComparer.OrdinalIgnoreCase);
        if (layout.DesktopItems is null || layout.DesktopItems.Count > 10000 || layout.DesktopPositions is null || layout.DesktopPositions.Count > 20000)
            throw new InvalidDataException("桌面项目或位置数据无效。");
        layout.DesktopItems = layout.DesktopItems.Select(DesktopAssignments.NormalizeItem).Where(p => p is not null)
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        layout.DesktopPositions = layout.DesktopPositions.Where(p => DesktopAssignments.NormalizeItem(p.Key) is not null && p.Value is not null)
            .GroupBy(p => DesktopAssignments.NormalizeItem(p.Key)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key, p => new DesktopPosition(Finite(p.Last().Value.X, 0, -100000, 100000), Finite(p.Last().Value.Y, 0, -100000, 100000)), StringComparer.OrdinalIgnoreCase);
        if (layout.Groups is null || layout.Groups.Count > 100)
            throw new InvalidDataException("布局最多支持 100 个分组。");
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var tabIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in layout.Groups)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id) || !groupIds.Add(group.Id))
                throw new InvalidDataException("分组标识无效或重复。");
            group.Name = CleanName(group.Name, "新分组");
            if (!Enum.IsDefined(group.ItemSort)) group.ItemSort = ItemSortOrder.FrequentFirst;
            if (group.Color is null || !System.Text.RegularExpressions.Regex.IsMatch(group.Color, "^#[0-9a-fA-F]{6}$"))
                group.Color = "#78B9A1";
            group.X = Finite(group.X, 120, -100000, 100000);
            group.Y = Finite(group.Y, 100, -100000, 100000);
            group.Width = Finite(group.Width, 340, PanelArrangement.MinimumWidth, 2000);
            group.Height = Finite(group.Height, 340, 180, 2000);
            group.Opacity = Finite(group.Opacity, 0.58, 0.15, 1);
            group.CollapsedOpacity = Finite(group.CollapsedOpacity, 0.20, 0.06, 0.65);
            group.IconSize = Math.Clamp(group.IconSize, 32, 80);
            if (group.Tabs is null || group.Tabs.Count is < 1 or > 30)
                throw new InvalidDataException("每个分组需要 1 至 30 个标签页。");
            foreach (var tab in group.Tabs)
            {
                if (tab is null || string.IsNullOrWhiteSpace(tab.Id) || !tabIds.Add(tab.Id))
                    throw new InvalidDataException("标签页标识无效或重复。");
                tab.Name = CleanName(tab.Name, "常用");
                if (tab.Items is null || tab.Items.Count > 10000)
                    throw new InvalidDataException("标签页项目数量无效。");
                tab.Items = tab.Items.Select(DesktopAssignments.NormalizeItem).Where(p => p is not null)
                    .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!string.IsNullOrWhiteSpace(tab.FolderPath))
                    tab.FolderPath = FileCatalog.NormalizePath(tab.FolderPath)
                        ?? throw new InvalidDataException("文件夹路径必须是绝对文件系统路径。");
            }
            group.SelectedTabId = group.ActiveTab.Id;
        }
        if (layout.Groups.Count(g => g.IsRecent) > 1 || layout.Groups.Any(g => g.IsRecent && g.Tabs.Any(t => t.IsPortal)))
            throw new InvalidDataException("最近分栏只能有一个，且不能映射文件夹。");
        return layout;
    }

    private static string CleanName(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim()[..Math.Min(name.Trim().Length, 80)];

    private static double Finite(double v, double fallback, double min, double max) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}
