namespace OpenFenestra.Core;

/// <summary>An inbox of references. Explicitly returned desktop items remain outside it.</summary>
public static class RecentItems
{
    public static FenceGroup? Ensure(Layout layout)
    {
        var recent = layout.Groups.FirstOrDefault(g => g.IsRecent);
        if (recent is not null || layout.Groups.Count >= 100) return recent;
        recent = new FenceGroup { Name = "最近", IsRecent = true, X = 24, Y = 60,
            Tabs = [new FenceTab { Name = "待归类" }] };
        layout.Groups.Insert(0, recent);
        return recent;
    }

    public static int Capture(Layout layout, IEnumerable<DesktopEntry> entries)
    {
        var recent = Ensure(layout);
        var tab = recent?.Tabs.FirstOrDefault(t => !t.IsPortal);
        if (tab is null) return 0;
        var known = DesktopAssignments.Assigned(layout);
        known.UnionWith(layout.DesktopItems);
        var added = new List<string>();
        foreach (var entry in entries)
        {
            var path = DesktopAssignments.NormalizeItem(entry.Path);
            if (path is null || !entry.Exists || known.Contains(path)) continue;
            if (tab.Items.Count + added.Count >= 10000) break;
            added.Add(path); known.Add(path);
            ItemActivityTracking.RecordAdded(layout, path);
        }
        tab.Items.InsertRange(0, added);
        return added.Count;
    }

    public static int Organize(Layout layout, FenceGroup recent)
    {
        if (!recent.IsRecent || !layout.Groups.Contains(recent)) return 0;
        var tabs = recent.Tabs.Where(t => !t.IsPortal).ToArray();
        var originals = tabs.Select(t => t.Items.ToArray()).ToArray();
        var paths = originals.SelectMany(p => p).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var tab in tabs) tab.Items.Clear();
        try { return FileCatalog.OrganizeEntries(layout, paths.Select(FileCatalog.Entry)); }
        finally
        {
            // Capacity limits or unavailable files must not lose inbox references.
            var assigned = DesktopAssignments.Assigned(layout);
            for (var i = 0; i < tabs.Length; i++)
                foreach (var path in originals[i])
                    if (assigned.Add(path)) tabs[i].Items.Add(path);
        }
    }
}
