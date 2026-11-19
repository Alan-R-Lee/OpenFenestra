namespace OpenFenestra.Core;

public enum ItemSortOrder { FrequentFirst, NewestFirst, Original }

public sealed class ItemActivity
{
    public DateTimeOffset? AddedAtUtc { get; set; }
    public int OpenCount { get; set; }
    public DateTimeOffset? LastOpenedAtUtc { get; set; }
}

/// <summary>Local, path-based activity. Historic usage and arrival dates are never inferred.</summary>
public static class ItemActivityTracking
{
    public const int MaximumEntries = 20000;

    private static ItemActivity? Ensure(Layout layout, string path)
    {
        if (layout.ItemActivity.TryGetValue(path, out var activity)) return activity;
        if (layout.ItemActivity.Count >= MaximumEntries) return null;
        return layout.ItemActivity[path] = new();
    }

    public static void RecordAdded(Layout layout, string path)
    {
        var normalized = DesktopAssignments.NormalizeItem(path);
        if (normalized is not null && Ensure(layout, normalized) is { AddedAtUtc: null } activity)
            activity.AddedAtUtc = DateTimeOffset.UtcNow;
    }

    public static void RecordOpened(Layout layout, string path)
    {
        var normalized = DesktopAssignments.NormalizeItem(path);
        if (normalized is null || Ensure(layout, normalized) is not { } activity) return;
        if (activity.OpenCount < int.MaxValue) activity.OpenCount++;
        activity.LastOpenedAtUtc = DateTimeOffset.UtcNow;
    }

    public static bool IsNew(Layout layout, string path) =>
        layout.ItemActivity.TryGetValue(path, out var activity) && activity.AddedAtUtc.HasValue && activity.OpenCount == 0;

    public static IEnumerable<DesktopEntry> Sort(Layout layout, ItemSortOrder order, IEnumerable<DesktopEntry> entries)
    {
        ItemActivity? Activity(DesktopEntry entry) => layout.ItemActivity.GetValueOrDefault(entry.Path);
        return order switch
        {
            ItemSortOrder.FrequentFirst => entries.OrderByDescending(e => Activity(e)?.OpenCount ?? 0)
                .ThenByDescending(e => Activity(e)?.AddedAtUtc)
                .ThenByDescending(e => Activity(e)?.LastOpenedAtUtc),
            ItemSortOrder.NewestFirst => entries.OrderByDescending(e => Activity(e)?.AddedAtUtc),
            _ => entries
        };
    }
}
