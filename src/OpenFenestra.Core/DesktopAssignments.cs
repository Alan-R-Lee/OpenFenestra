namespace OpenFenestra.Core;

/// <summary>All transfers change display assignments only, never filesystem contents.</summary>
public static class DesktopAssignments
{
    public static bool IsVirtual(string? path) => path is { Length: 40 }
        && path.StartsWith("::{", StringComparison.Ordinal) && path[^1] == '}'
        && Guid.TryParseExact(path.AsSpan(3, 36), "D", out _);
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
        var destinationItems = destination.Items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (destination.Items.Count + items.Count(p => !destinationItems.Contains(p)) > 10000)
            throw new InvalidOperationException("标签页最多支持 10,000 个项目。");
        var moved = items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (moved.Count == 0) return 0;
        foreach (var tab in layout.Groups.SelectMany(g => g.Tabs).Where(t => !t.IsPortal && !ReferenceEquals(t, destination)))
            tab.Items.RemoveAll(moved.Contains);
        destination.Items.RemoveAll(moved.Contains);
        destination.Items.AddRange(items);
        layout.DesktopItems.RemoveAll(moved.Contains);
        return items.Count;
    }

    public static int ReturnToDesktop(Layout layout, IEnumerable<string> paths, DesktopPosition position)
    {
        var items = paths.Select(NormalizeItem).Where(p => p is not null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = layout.DesktopItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (layout.DesktopItems.Count + items.Count(p => !known.Contains(p)) > 10000)
            throw new InvalidOperationException("桌面最多支持 10,000 个额外项目。");
        var moved = items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (moved.Count == 0) return 0;
        foreach (var tab in layout.Groups.SelectMany(g => g.Tabs).Where(t => !t.IsPortal))
            tab.Items.RemoveAll(moved.Contains);
        for (var i = 0; i < items.Count; i++)
        {
            var path = items[i];
            if (known.Add(path)) layout.DesktopItems.Add(path);
            layout.DesktopPositions[path] = new(position.X + i * 100, position.Y);
        }
        return items.Count;
    }
}
