namespace OpenFenestra.Core;

public sealed record DesktopEntry(string Path, string Name, bool IsDirectory)
{
    public bool Exists => DesktopAssignments.IsVirtual(Path) || File.Exists(Path) || Directory.Exists(Path);
}

public static class FileCatalog
{
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".tex" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".svg", ".webp", ".bmp", ".psd", ".ai" };
    private static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".mp4", ".wav", ".flac", ".mkv", ".avi", ".mov" };

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return null;
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static DesktopEntry Entry(string path) => CreateEntry(path, Directory.Exists(path));

    private static DesktopEntry CreateEntry(string path, bool isDirectory)
    {
        var name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url")
            name = System.IO.Path.GetFileNameWithoutExtension(path);
        return new(path, name, isDirectory);
    }

    // Materialize inside the try: EnumerateFileSystemEntries can throw while iterating.
    public static IReadOnlyList<string> ReadFolder(string path) => ReadFolderEntries(path).Select(entry => entry.Path).ToArray();

    private static IReadOnlyList<DesktopEntry> ReadFolderEntries(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Select(info => (info.FullName, info.Attributes))
                .Where(info => (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(info => CreateEntry(info.FullName, (info.Attributes & FileAttributes.Directory) != 0))
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => System.IO.Path.GetFileName(entry.Path), StringComparer.CurrentCultureIgnoreCase)
                .Take(10000).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return []; }
    }

    public static IReadOnlyList<DesktopEntry> ReadTab(FenceTab tab) =>
        tab.IsPortal ? ReadFolderEntries(tab.FolderPath!) : tab.Items.Select(Entry).ToList();

    public static int AddReferences(FenceTab tab, IEnumerable<string> paths)
    {
        if (tab.IsPortal) return 0;
        var known = tab.Items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths)
        {
            var normalized = NormalizePath(path);
            if (normalized is null || (!File.Exists(normalized) && !Directory.Exists(normalized))) continue;
            if (tab.Items.Count >= 10000) break;
            if (known.Add(normalized)) { tab.Items.Add(normalized); added++; }
        }
        return added;
    }

    public static string Category(string path)
    {
        if (DesktopAssignments.IsVirtual(path)) return "系统入口";
        if (Directory.Exists(path)) return "文件夹";
        var ext = System.IO.Path.GetExtension(path);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) return "应用与快捷方式";
        if (Documents.Contains(ext)) return "文档与笔记";
        if (Images.Contains(ext)) return "图片与设计";
        return Media.Contains(ext) ? "影音与媒体" : "其他项目";
    }

    public static int Organize(Layout layout, IEnumerable<string> desktopPaths) =>
        OrganizeEntries(layout, desktopPaths.SelectMany(ReadFolderEntries));

    /// <summary>Organizes Shell entries by reference, preserving assignments in ordinary tabs.</summary>
    public static int OrganizeEntries(Layout layout, IEnumerable<DesktopEntry> entries)
    {
        // A folder portal is a live view, not exclusive ownership of its contents.
        var known = DesktopAssignments.Assigned(layout);
        var targets = new Dictionary<string, Queue<FenceGroup>>(StringComparer.Ordinal);
        foreach (var existing in layout.Groups)
        {
            if (existing.ActiveTab.IsPortal || existing.ActiveTab.Items.Count >= 10000) continue;
            if (!targets.TryGetValue(existing.Name, out var queue)) targets[existing.Name] = queue = new();
            queue.Enqueue(existing);
        }
        var added = 0;
        foreach (var entry in entries)
        {
            var path = DesktopAssignments.NormalizeItem(entry.Path);
            if (path is null || (!DesktopAssignments.IsVirtual(path) && !File.Exists(path) && !Directory.Exists(path))
                || known.Contains(path)) continue;
            var category = Category(path);
            if (!targets.TryGetValue(category, out var candidates)) targets[category] = candidates = new();
            if (candidates.Count == 0)
            {
                if (layout.Groups.Count >= 100) continue;
                var index = layout.Groups.Count;
                var created = new FenceGroup { Name = category, X = 90 + index % 3 * 370, Y = 90 + index / 3 * 350,
                    Color = new[] { "#78B9A1", "#8CA9DB", "#C0A2D1", "#D6AE75", "#D98F9D", "#8BBFC8" }[index % 6] };
                layout.Groups.Add(created); candidates.Enqueue(created);
            }
            var group = candidates.Peek();
            // known already includes every ordinary tab. New entries cannot need
            // a transfer scan; append once instead of rescanning the growing layout.
            group.ActiveTab.Items.Add(path);
            if (group.ActiveTab.Items.Count >= 10000) candidates.Dequeue();
            known.Add(path); added++;
        }
        layout.DesktopItems.RemoveAll(known.Contains);
        return added;
    }
}
