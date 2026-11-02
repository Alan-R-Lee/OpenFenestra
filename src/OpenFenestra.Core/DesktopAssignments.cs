using System.Text.RegularExpressions;

namespace OpenFenestra.Core;

/// <summary>All transfers change display assignments only, never filesystem contents.</summary>
public static partial class DesktopAssignments
{
    [GeneratedRegex(@"^::\{[0-9a-fA-F-]{36}\}$")]
    private static partial Regex VirtualItemPattern();

    public static bool IsVirtual(string path) => VirtualItemPattern().IsMatch(path);
    public static string? NormalizeItem(string? path) => path is not null && IsVirtual(path)
        ? path.ToUpperInvariant() : FileCatalog.NormalizePath(path);

    public static HashSet<string> Assigned(Layout layout) => layout.Groups.SelectMany(g => g.Tabs)
        .Where(t => !t.IsPortal).SelectMany(t => t.Items).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DesktopEntry> VisibleDesktop(Layout layout, IEnumerable<DesktopEntry> shellItems)
    {
        var assigned = Assigned(layout);
        return shellItems.Concat(layout.DesktopItems.Select(FileCatalog.Entry))
            .Where(e => !assigned.Contains(e.Path))
            .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static int Collect(Layout layout, FenceTab destination, IEnumerable<string> paths)
    {
        if (destination.IsPortal) return 0;
        var items = paths.Select(NormalizeItem).Where(p => p is not null).Cast<string>()
            .Where(p => IsVirtual(p) || File.Exists(p) || Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Validate capacity before removing anything from another tab.
        if (destination.Items.Count + items.Count(p => !destination.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) > 10000)
            throw new InvalidOperationException("标签页最多支持 10,000 个项目。");
        foreach (var path in items)
        {
            foreach (var tab in layout.Groups.SelectMany(g => g.Tabs).Where(t => !t.IsPortal))
                tab.Items.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            destination.Items.Add(path);
            layout.DesktopItems.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
        return items.Count;
    }

    public static int ReturnToDesktop(Layout layout, IEnumerable<string> paths, DesktopPosition position)
    {
        var items = paths.Select(NormalizeItem).Where(p => p is not null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (layout.DesktopItems.Count + items.Count(p => !layout.DesktopItems.Contains(p, StringComparer.OrdinalIgnoreCase)) > 10000)
            throw new InvalidOperationException("桌面最多支持 10,000 个额外项目。");
        for (var i = 0; i < items.Count; i++)
        {
            var path = items[i];
            foreach (var tab in layout.Groups.SelectMany(g => g.Tabs).Where(t => !t.IsPortal))
                tab.Items.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!layout.DesktopItems.Contains(path, StringComparer.OrdinalIgnoreCase)) layout.DesktopItems.Add(path);
            layout.DesktopPositions[path] = new(position.X + i * 100, position.Y);
        }
        return items.Count;
    }
}
